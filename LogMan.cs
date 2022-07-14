using Common.Logging;
using Common.Logging.Factory;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Wima.Log
{
    [Flags]
    public enum LogMode : byte
    {
        None = 0,
        CommonLog = 0b1,
        Native = 0b10,
        Console = 0b100,
        StackTrace = 0b1000,
        Verbose = 0b10000,
        ElasticSearch = 0b100000
    }

    public class LogMan : AbstractLogger, IDisposable
    {
        public const string DEFAULT_LOGFILE_NAME_TIME_FORMAT = "yyMMdd_HH";
        public const string DEFAULT_LOGLINE_TIME_FORMAT = "yy-MM-dd HH:mm:ss";
        public const string DEFAULT_LOGROOT_NAME = "Logs";
        public const string LINE_REPLACEMENT_PREFIX = "<< ";
        public const string INTERNAL_ERROR_STR = "[LogSvc Internal Error]";

        /// <summary>
        /// Preserve Period in Hour, 0 = forever
        /// </summary>
        public static int LogPreservePeriodInHour = 0;

        /// <summary>
        /// Logfile Renewal Period(in hour)
        /// </summary>
        public static int LogRenewalPeriodInHour = 2;

        /// <summary>
        /// For preventing race condition during accessing LogBuf
        /// </summary>
        protected readonly object syncLogBuf = new();

        /// <summary>
        /// For preventing race condition during writing log to file
        /// </summary>
        protected readonly object syncLogWriter = new();

        protected StringBuilder _logBuf = new(DefaultMaxBufferLength);







        /// <summary>
        /// Name of the Log,should be unique among other instances.
        /// </summary>
        ///
        private string _name;

        /// <summary>
        /// Determine the details level of log
        /// </summary>
        public LogLevel LogLevel { get; set; }

        /// <summary>
        /// Show LogLevel in loglines or not
        /// </summary>
        public bool ShowLevel { get; set; }

        /// <summary>
        /// Show Datetime in loglines or not
        /// </summary>
        public bool ShowDateTime { get; set; }

        /// <summary>
        /// DateTimeFormat for loglines
        /// </summary>
        public string DateTimeFormat { get; set; }

        /// <summary>
        /// whether show LogName at beginning of line in console mode. LogName at each line will not be written to disk.
        /// </summary>
        public bool ShowLogName { get; set; }

        public LogMan(string logName, LogLevel logLevel = LogLevel.All, bool showLevel = true, bool showDateTime = true, bool showLogName = true, string dateTimeFormat = DEFAULT_LOGLINE_TIME_FORMAT)
        {
            StartedAt = DateTime.Now;
            LogModes = GlobalLogMode;

            if (LogModes.HasFlag(LogMode.CommonLog))
            {
                try { CommonLogger = GetLogger(logName); }
                catch (Exception ex)
                {
                    LogModes = (GlobalLogMode ^ LogMode.CommonLog) | LogMode.Native;
                    Info(INTERNAL_ERROR_STR + "Failure initalizing CommonLog,use native mode instead!", ex);
                }
            }

            LogLevel = logLevel;
            ShowLevel = showLevel;
            ShowDateTime = showDateTime;
            ShowLogName = showLogName;
            DateTimeFormat = dateTimeFormat;

            Name = logName;

            RenewLogWriter();

            //Register this Logman instance to a global static dictionary, if new instance use exisiting name, the old record would be overwritten.
            Loggers.AddOrUpdate(logName, this, (k, v) =>
            {
                v.Dispose();
                return this;
            });

            Info("[LogMan]\tOK!");
        }

        public LogMan(Type type, LogLevel logLevel = LogLevel.All, bool showLevel = true, bool showDateTime = true, bool showLogName = true, string dateTimeFormat = DEFAULT_LOGLINE_TIME_FORMAT)
            : this(type.Name, logLevel, showLevel, showDateTime, showLogName, dateTimeFormat)
        { }

        public LogMan(object obj, LogLevel logLevel = LogLevel.All, bool showLevel = true, bool showDateTime = true, bool showLogName = true, string dateTimeFormat = DEFAULT_LOGLINE_TIME_FORMAT)
            : this(obj.GetType().Name, logLevel, showLevel, showDateTime, showLogName, dateTimeFormat)
        { }

        public static int DefaultMaxBufferLength { get; set; } = 1024 * 64;

        /// <summary>
        /// This property evaluates default LogModes property of new instance.
        /// </summary>
        public static LogMode GlobalLogMode { get; set; } = LogMode.Console;

        /// <summary>
        /// Reggistered loggers
        /// </summary>
        public static ConcurrentDictionary<string, LogMan> Loggers { get; private set; } = new ConcurrentDictionary<string, LogMan>();

        /// <summary>
        /// Global log root path
        /// </summary>
        public static string LogRoot { get; set; } = ResetLogRoot();

        public static DateTime StartedAt { get; private set; }
        public override bool IsDebugEnabled => LogLevel.HasFlag(LogLevel.All) || LogLevel.HasFlag(LogLevel.Debug);

        public override bool IsErrorEnabled => LogLevel.HasFlag(LogLevel.All) || LogLevel.HasFlag(LogLevel.Error);

        public override bool IsFatalEnabled => LogLevel.HasFlag(LogLevel.All) || LogLevel.HasFlag(LogLevel.Fatal);

        public override bool IsInfoEnabled => LogLevel.HasFlag(LogLevel.All) || LogLevel.HasFlag(LogLevel.Info);

        public override bool IsTraceEnabled => LogLevel.HasFlag(LogLevel.All) || LogLevel.HasFlag(LogLevel.Trace);

        public override bool IsWarnEnabled => LogLevel.HasFlag(LogLevel.All) || LogLevel.HasFlag(LogLevel.Warn);

        /// <summary>
        /// In-memory buffer of recent logs, for quick query of rencent logs.
        /// </summary>
        public string LogBuf
        {
            get
            {
                lock (syncLogBuf) return _logBuf.ToString();
            }
        }

        /// <summary>
        /// Time format for log file
        /// </summary>
        public string LogFileNameTimeFormat { get; set; } = DEFAULT_LOGFILE_NAME_TIME_FORMAT;

        /// <summary>
        /// DateTime format for log lines
        /// </summary>
        public string LogLineTimeFormat { get; set; } = DEFAULT_LOGLINE_TIME_FORMAT;

        /// <summary>
        /// Logmodes for current instance, it takes effect instantly.
        /// </summary>
        public LogMode LogModes { get; set; }

        /// <summary>
        /// Path for current instance
        /// </summary>
        public string LogPath { get; private set; }

        public string Name
        {
            get => _name;
            set
            {
                var v = value;
                Path.GetInvalidFileNameChars()
                    .Where(i => i != Path.DirectorySeparatorChar)
                    .ToList().ForEach(i => v = v.Replace(i, '_'));
                _name = v;
                v = ESGlobalIndexPrefix + ESIndexPrefix + _name;

                foreach (char c in invalidUrlChar) v = v.Replace(c, '_');
                _esIndexName = v.ToLower(); ;
            }
        }

        /// <summary>
        /// 无效的URL字符
        /// </summary>
        private static string invalidUrlChar { get; } = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());

        private string _esIndexName;

        /// <summary>
        /// StreamWriter for writing
        /// </summary>
        private StreamWriter _logWriter { get; set; }

        private static ESService _eSService { get; set; }

        private ILog CommonLogger { get; set; } = null;

        /// <summary>
        /// 初始化全局的ElasticSearch客户端
        /// </summary>
        /// <param name="config"></param>
        /// <returns></returns>
        public static bool InitElasticSearch(ESConfig config, string globalIndexPrefix = null)
        {
            if (globalIndexPrefix != null) ESGlobalIndexPrefix = globalIndexPrefix;
            return (_eSService = new ESService(config)).Client != null;
        }

        /// <summary>
        /// Get LogRoot path once
        /// </summary>
        public static string ResetLogRoot() => LogRoot = Path.GetFullPath(Environment.CurrentDirectory + Path.DirectorySeparatorChar + DEFAULT_LOGROOT_NAME + Path.DirectorySeparatorChar);

        /// <summary>
        /// Set LogRoot to specified path
        /// </summary>
        /// <param name="workingPath"></param>
        public static void SetGlobalLogRoot(string workingPath) => LogRoot = Path.GetFullPath(workingPath + Path.DirectorySeparatorChar + DEFAULT_LOGROOT_NAME + Path.DirectorySeparatorChar);

        /// <summary>
        /// Set LogRoot to CodeBase path, used for .net core when it was deployed as a service
        /// </summary>
        public static void SetLogRoot2CodeBase() => SetGlobalLogRoot(AppDomain.CurrentDomain.BaseDirectory);

        /// <summary>
        /// Custom Elastic Search IndexName Prefix for all instance,will be prefixed to all instance if not null or empty,which will be directly added to the front of each indexName;
        /// Change this property will not timely change the EsIndexName property.
        /// </summary>
        public static string ESGlobalIndexPrefix { get; set; }


        /// <summary>
        /// Elastic Search IndexName Prefix for current instance, will be prefixed after GlobalIndexPrefix.
        /// Default value = "log_", and can be changed per instance.
        /// 
        /// </summary>
        public string ESIndexPrefix { get; set; } = "log_";

        /// <summary>
        /// Cached Elastic Search IndexName,to avoid calculating the IndexName from time to time.
        /// This property would not be updated after ESIndexPrefix, but will be updated upon setting Name value.
        /// </summary>
        public string EsIndexName => _esIndexName;

        /// <summary>
        /// Unregister Logman from Loggers Dictionary, call this method when dispose the object associated with a logman instance.
        /// </summary>
        /// <returns></returns>
        public void Dispose()
        {
            var done = Loggers.TryRemove(Name, out _);
            if (done) Info("LogMan Disposed!");
            _logWriter?.Dispose();
            _logWriter = null;
        }

        protected override void WriteInternal(LogLevel level, object message, Exception ex)
        {
            bool useCommonLog() => LogModes.HasFlag(LogMode.CommonLog) && CommonLogger != null;

            switch (level)
            {
                case LogLevel.Trace when IsTraceEnabled:
                    if (useCommonLog())
                        if (ex == null) CommonLogger.Trace(message);
                        else CommonLogger.Trace(message, ex);
                    break;

                case LogLevel.Debug when IsDebugEnabled:
                    if (useCommonLog())
                        if (ex == null) CommonLogger.Debug(message);
                        else CommonLogger.Debug(message, ex);
                    break;

                case LogLevel.Info when IsInfoEnabled:
                    if (useCommonLog())
                        if (ex == null) CommonLogger.Info(message);
                        else CommonLogger.Info(message, ex);
                    break;

                case LogLevel.Warn when IsWarnEnabled:
                    if (useCommonLog())
                        if (ex == null) CommonLogger.Warn(message);
                        else CommonLogger.Warn(message, ex);
                    break;

                case LogLevel.Error when IsErrorEnabled:
                    if (useCommonLog())
                        if (ex == null) CommonLogger.Error(message);
                        else CommonLogger.Error(message, ex);
                    break;

                case LogLevel.Fatal when IsFatalEnabled:
                    if (useCommonLog())
                        if (ex == null) CommonLogger.Fatal(message);
                        else CommonLogger.Fatal(message, ex);
                    break;

                default:
                    return;
            }

            var posSep = Name.LastIndexOf(Path.DirectorySeparatorChar) + 1;
            var logName = posSep >= 0 ? Name.Substring(posSep, Name.Length - posSep) : Name;

            //Construction of logline 
            StringBuilder _logLineBuilder = new();

            _logLineBuilder.Append($"{(ShowDateTime ? DateTime.Now.ToString(LogLineTimeFormat) : "")} {(ShowLevel ? level.ToString().ToUpper() : "")}\t{message?.ToString()}" +
                $"{(LogModes.HasFlag(LogMode.Verbose) ? "\r\n-> " + ex?.Message + "\r\n-> " + ex?.InnerException?.Message : "") + Environment.NewLine}");

            StringBuilder _stackChain = null;
            if (LogModes.HasFlag(LogMode.StackTrace))
            {
                _stackChain = new StringBuilder();
                _stackChain.Append(" <- ");
                new StackTrace().GetFrames().Select(i => i.GetMethod().Name).Where(i => !i.StartsWith(".")).ToList().ForEach(i => _stackChain.Append("/" + i));
                _stackChain.Append(Environment.NewLine + Environment.NewLine);
                _logLineBuilder.Append(_stackChain);
            }

            string _logLine = _logLineBuilder.ToString();

            //Post to ElasticSearch
            if (LogModes.HasFlag(LogMode.ElasticSearch) && _eSService != null)
                Task.Run(() => _eSService.CreateDocument(
                  new LogLine(DateTime.Now.Ticks, DateTime.Now,
                  level.ToString(),
                  message?.ToString(),
                  ex?.Message + "\r\n-> " + ex?.InnerException?.Message,
                  LogModes.HasFlag(LogMode.StackTrace) ? _stackChain?.ToString() : null), EsIndexName));


            //Update LogBuf:Cut tail and process Replacement Mark "<<" in _logBuf
            int _firstNL;
            lock (syncLogBuf)
            {
                if (_logBuf.Length > DefaultMaxBufferLength) _logBuf.Remove(DefaultMaxBufferLength - 4096, 4096);
                if (_logLine.Contains(LINE_REPLACEMENT_PREFIX) && (_firstNL = _logBuf.ToString().IndexOf(Environment.NewLine)) > 0)
                    _logBuf.Remove(0, _firstNL + Environment.NewLine.Length);  //remove first line from begining of _logBuf
                _logBuf.Insert(0, _logLine);
            }

            //Renew logwriter conditionally
            RenewLogWriter();
            //Renew LogStreamWriter in case log path changes
            if (LogModes.HasFlag(LogMode.Native) && _logWriter != null)
                for (int i = 0; i < 2; i++)
                {
                    //if happens to fail writing during _logwriter renewal(occasionally in case threads pile up), relock new _logWriter for anothter trial.
                    try
                    {
                        lock (_logWriter) _logWriter.Write(_logLine);
                        break;
                    }
                    catch (Exception ex2)
                    {
                        if (i > 0)
                        {
                            lock (syncLogBuf) _logBuf.Insert(0, INTERNAL_ERROR_STR + "Bad log stream:" + ex2.Message);
                            break;
                        }
                    }
                }

            if (LogModes.HasFlag(LogMode.Console)) Console.Write((ShowLogName ? Name + "\t" : "") + _logLine);
        }

        /// <summary>
        /// Directly put object to Elastic Search, if activated.
        /// </summary>
        public async Task<Nest.CreateResponse> ESPut<T>(T obj) where T : class => await _eSService?.CreateDocument<T>(obj, EsIndexName);

        /// <summary>
        /// Directly put object to Elastic Search, if activated.
        /// This method sort with default field of "@timestamp" which is a compulsory field for ES datastream.
        /// </summary>
        public async Task<Nest.ISearchResponse<T>> ESGet<T>(string indexName, int startIndex = 0, int size = 10, bool sortDescending = false, string sortField = "@timestamp") where T : class => await _eSService?.GetDocument<T>(indexName, startIndex, size);


        private static ILog GetLogger(string key) => LogManager.GetLogger(key);

        private string GetNextLogPath(DateTime? now = null) => LogRoot + Name + "_" + (now ?? DateTime.Now).ToString(LogFileNameTimeFormat) + ".log";

        /// <summary>
        /// Thread-safely updating LogWriter
        /// </summary>
        private void RenewLogWriter()
        {
            DateTime now = DateTime.Now;
            if (LogRenewalPeriodInHour == 1 || ((int)(now - StartedAt).TotalHours) % LogRenewalPeriodInHour == 0 || _logWriter == null)
                lock (syncLogWriter)
                {
                    string nextLogPath = GetNextLogPath(now);
                    if (LogModes.HasFlag(LogMode.Native) && LogPath != nextLogPath)
                    {
                        LogPath = nextLogPath;
                        try
                        {
                            var logPath = Path.GetDirectoryName(LogPath);
                            Directory.CreateDirectory(logPath);
                            var writer = new StreamWriter(new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                            if (_logWriter != null)
                            {
                                _logWriter.Flush();
                                _logWriter.Dispose();
                            }
                            _logWriter = writer;

                            //Clean outdated log files,if necessary
                            if (LogPreservePeriodInHour > 0)
                                try
                                {
                                    Directory.GetFiles(logPath, "*.log")
                                    .Where(i => (now - File.GetLastWriteTime(i)).TotalHours >= LogPreservePeriodInHour)
                                    .ToList().ForEach(i => File.Delete(i));
                                }
                                catch (Exception ex)
                                {
                                    lock (syncLogBuf) _logBuf.Append(INTERNAL_ERROR_STR + "Unable to remove log files, will try next time:" + ex.Message + "\r\n");
                                }
                        }
                        catch (Exception ex)
                        {
                            lock (syncLogBuf) _logBuf.Append(INTERNAL_ERROR_STR + "Unable to create log files, Console Mode only！Error:" + ex.Message + "\r\n");
                            LogModes = LogMode.Console;
                        }
                    }
                }
        }
    }
}