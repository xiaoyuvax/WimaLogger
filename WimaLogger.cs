﻿using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.ObjectPool;
using Common.Logging;
using Common.Logging.Factory;
using MS = Microsoft.Extensions.Logging;

namespace Wima.Log
{
    public sealed class WimaLogger : AbstractLogger, IDisposable, MS.ILogger
    {
        public const string DEFAULT_LOGFILE_NAME_TIME_FORMAT = "yyMMdd_HH";

        public const string DEFAULT_LOGLINE_TIME_FORMAT = "yy-MM-dd HH:mm:ss";

        public const string DEFAULT_LOGROOT_NAME = "logs";

        public const char ES_INDEX_SEPARATOR = '-';

        public const string INTERNAL_ERROR_STR = "[LogMan Internal Error]";

        public const string LINE_REPLACEMENT_PREFIX = "<< ";

        /// <summary>
        /// A default logger who logs nothing
        /// </summary>
        public static readonly WimaLogger NullLogger;

        private const char INVALID_CHAR_REPLACER = '-';

        private const int LOGBUFFER_LENGTH = 4096;

        private static readonly DefaultObjectPool<LogLine> logLinePool = new(new DefaultPooledObjectPolicy<LogLine>());

        private static readonly DefaultObjectPool<StringBuilder> stringBuilderPool = new(new StringBuilderPooledObjectPolicy());

        private readonly StringBuilder _logBuf = new(DefaultMaxBufferLength);

        /// <summary>
        /// For preventing race condition during accessing LogBuf
        /// </summary>
        private readonly object syncLogBuf = new();

        /// <summary>
        /// For preventing race condition during writing log to file
        /// </summary>
        private readonly object syncLogWriter = new();

        private string _esIndexName;

        /// <summary>
        /// StreamWriter for writing
        /// </summary>
        private StreamWriter _logWriter;

        /// <summary>
        /// Name of the Log,should be unique among other instances.
        /// </summary>
        ///
        private string _name;

        static WimaLogger()
        {
            //assure Nullogger initialized after all statics.
            NullLogger = new("_", LogLevel.Off, logMode: LogMode.None);
        }

        public WimaLogger(string logName, LogLevel? logLevel = null, bool showLevel = true, bool showDateTime = true, bool showLogName = true, string dateTimeFormat = DEFAULT_LOGLINE_TIME_FORMAT, LogMode? logMode = null)
        {
            StartedAt = DateTime.Now;
            LogModes = logMode ?? GlobalLogMode;
            foreach (char c in Path.GetInvalidFileNameChars()) logName = logName.Replace(c, INVALID_CHAR_REPLACER);

            if (LogModes.HasFlag(LogMode.CommonLog))
                try { _commonLogger = getExternalLogger(logName); }
                catch (Exception ex)
                {
                    LogModes = (GlobalLogMode ^ LogMode.CommonLog) | LogMode.Native;
                    Info(INTERNAL_ERROR_STR + "Failure initalizing CommonLog,use native mode instead!", ex);
                }

            LogLevel = logLevel ?? GlobalLogLevel;

            ShowLevel = showLevel;
            ShowDateTime = showDateTime;
            ShowLogName = showLogName;
            DateTimeFormat = dateTimeFormat;

            Name = logName;

            //Init LogWriter.
            renewLogWriter();

            //LogMan use a self-registration Model, and will be unregisted in its Dispose() method.
            //Call this.Dispose() when class who instantiates LogMan disposes, so that logWriter and ESService instances can be released.
            //Register this Logman instance to a global static dictionary, if new instance use exisiting name, the old record would be overwritten.
            LogBook.AddOrUpdate(logName, this, (k, v) =>
            {
                v.Dispose();
                return this;
            });

            Info($"[LogMan:{Name}]\tON!");
        }

        public WimaLogger(Type type, LogLevel? logLevel = null, bool showLevel = true, bool showDateTime = true, bool showLogName = true, string dateTimeFormat = DEFAULT_LOGLINE_TIME_FORMAT, LogMode? logMode = null)
                                   : this(type.Name, logLevel, showLevel, showDateTime, showLogName, dateTimeFormat, logMode)
        { }

        public WimaLogger(object obj, LogLevel? logLevel = null, bool showLevel = true, bool showDateTime = true, bool showLogName = true, string dateTimeFormat = DEFAULT_LOGLINE_TIME_FORMAT, LogMode? logMode = null)
                                     : this(obj.GetType().Name, logLevel, showLevel, showDateTime, showLogName, dateTimeFormat, logMode)
        { }

        public static int DefaultMaxBufferLength { get; set; } = 1024 * 128;

        /// <summary>
        /// Custom ElasticSearch IndexName Prefix for all instance,will be prefixed to all instance if not null or empty,which will be directly added to the front of each indexName;
        /// Change this property will not timely change the EsIndexName property.
        /// </summary>
        public static string ESGlobalIndexPrefix { get; set; }

        /// <summary>
        /// Reference to shared ElasticSearchService, once initialized all Logs share this service instance.
        /// </summary>
        public static ElasticSearchService ESService { get; private set; }

        /// <summary>
        /// This property evaluates default LogLevel property of new instance.
        /// </summary>
        public static LogLevel GlobalLogLevel { get; set; } = LogLevel.All;

        /// <summary>
        /// This property evaluates default LogModes property of new instance.
        /// </summary>
        public static LogMode GlobalLogMode { get; set; } = LogMode.Console;

        /// <summary>
        /// Reggistered loggers
        /// </summary>
        public static ConcurrentDictionary<string, WimaLogger> LogBook { get; set; } = new();

        /// <summary>
        /// Preserve Period in Hour, 0 = forever
        /// </summary>
        public static int LogPreservePeriodInHour { get; set; } = 0;

        /// <summary>
        /// Logfile Renewal Period(in hour)
        /// </summary>
        public static int LogRenewalPeriodInHour { get; set; } = 2;

        /// <summary>
        /// Global log root path
        /// </summary>
        public static string LogRoot { get; set; } = ResetLogRoot();

        public static DateTime StartedAt { get; private set; }

        /// <summary>
        /// DateTime format for log lines
        /// </summary>
        public string DateTimeFormat { get; set; } = DEFAULT_LOGLINE_TIME_FORMAT;

        /// <summary>
        /// Cached ElasticSearch IndexName,to avoid calculating the IndexName from time to time.
        /// This property would not be updated after ESIndexPrefix, but will be updated upon setting Name value.
        /// </summary>
        public string EsIndexName => _esIndexName;

        /// <summary>
        /// Elastic Search IndexName Prefix for current instance, will be prefixed after GlobalIndexPrefix.
        /// Default value = "log-", and can be changed per instance.
        ///
        /// </summary>
        public string ESIndexPrefix { get; set; } = "logs" + ES_INDEX_SEPARATOR;

        public override bool IsDebugEnabled => LogLevel == LogLevel.All || LogLevel.HasFlag(LogLevel.Debug);

        public override bool IsErrorEnabled => LogLevel == LogLevel.All || LogLevel.HasFlag(LogLevel.Error);

        public override bool IsFatalEnabled => LogLevel == LogLevel.All || LogLevel.HasFlag(LogLevel.Fatal);

        public override bool IsInfoEnabled => LogLevel == LogLevel.All || LogLevel.HasFlag(LogLevel.Info);

        public override bool IsTraceEnabled => LogLevel == LogLevel.All || LogLevel.HasFlag(LogLevel.Trace);

        public override bool IsWarnEnabled => LogLevel == LogLevel.All || LogLevel.HasFlag(LogLevel.Warn);

        /// <summary>
        /// In-memory buffer of recent logs, for quick query of rencent logs.
        /// </summary>
        public string LogBuf
        {
            get
            {
                lock (syncLogBuf) return _logBuf.ToString();  //StringBuilder is not thread-safe.
            }
        }

        /// <summary>
        /// Time format for log file
        /// </summary>
        public string LogFileNameTimeFormat { get; set; } = DEFAULT_LOGFILE_NAME_TIME_FORMAT;

        /// <summary>
        /// Determine the details level of log
        /// </summary>
        public LogLevel LogLevel { get; set; }

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
            protected set
            {
                var v = value;
                foreach (char c in invalidUrlChar.Except([Path.DirectorySeparatorChar])) v = v.Replace(c, '_');
                _name = v;
                RefreshEsIndex();
            }
        }

        /// <summary>
        /// Show Datetime in loglines or not
        /// </summary>
        public bool ShowDateTime { get; set; }

        /// <summary>
        /// Show LogLevel in loglines or not
        /// </summary>
        public bool ShowLevel { get; set; }

        /// <summary>
        /// whether show LogName at beginning of line in console mode. LogName at each line will not be written to disk.
        /// </summary>
        public bool ShowLogName { get; set; }

        public bool UseVerbose => LogModes.HasFlag(LogMode.Verbose);

        /// <summary>
        /// 无效的URL字符
        /// </summary>
        private static string invalidUrlChar { get; } = new string([.. Path.GetInvalidFileNameChars(), .. Path.GetInvalidPathChars()]);

        private ILog _commonLogger { get; set; } = null;

        /// <summary>
        /// Intialized Shared ElasticSearch Client, which is used by all LogMan instances.
        /// </summary>
        /// <param name="config"></param>
        /// <returns></returns>
        /// <remarks>An index template of ESGlobalIndexPrefix must be created in ES for this log mode to work.</remarks>
        public static Task<bool> InitElasticSearch(ESConfig config, string globalIndexPrefix = null) => Task.Run(() =>
            {
                if (globalIndexPrefix != null) ESGlobalIndexPrefix = globalIndexPrefix + ES_INDEX_SEPARATOR;
                return (ESService = new ElasticSearchService(config)).Client != null;
            });

        /// <summary>
        /// This method updates the key in LogBook too
        /// </summary>
        /// <param name="oldLogName"></param>
        /// <param name="newLogName"></param>
        /// <returns></returns>
        public static bool Rename(string oldLogName, string newLogName) => LogBook.TryRemove(oldLogName, out WimaLogger l) && LogBook.TryAdd(l.Name = newLogName, l);

        /// <summary>
        /// Set LogRoot path to default
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

        public static void Trace(Action<string, Exception> logAction, object message, Exception ex = null, [CallerMemberName] string caller = null) => logAction?.Invoke($"[{caller}]\t{message}", ex);

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => default!;

        /// <summary>
        /// Unregister Logman from Loggers Dictionary, call this method when dispose the object associated with a logman instance.
        /// </summary>
        /// <returns></returns>
        public void Dispose()
        {
            //LogMan instance is suggested to be disposed with the class which uses it, so it is convenient to unregister itself from static LogBook.
            if (LogBook.TryRemove(Name, out _)) Info($"LogMan:{Name} Disposed!");
            _logWriter?.Dispose();
            _logWriter = null;

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Del datastream from Elastic Search, if enabled.
        /// </summary>
        public async Task<Nest.DeleteDataStreamResponse> ESDelDataStreams(IEnumerable<string> dsNames) => ESService.IsOnline ? await ESService?.Client.Indices.DeleteDataStreamAsync(new Nest.Names(dsNames)).ContinueWith(i =>
        {
            if (!i.Result.IsValid) _logBuf.AppendLine("[ESDel]Fail!\t" + i.Result.OriginalException?.Message);
            return i.Result;
        }) : ESService.FakeDeleteDataStreamResponseFalse;

        /// <summary>
        /// Get object from Elastic Search, if enabled.
        /// This method sort with default field of "@timestamp" which is a compulsory field for ES datastream.
        /// </summary>
        public async Task<Nest.ISearchResponse<T>> ESGet<T>(string indexName, int startIndex = 0, int size = 10, bool sortDescending = false, string sortField = "@timestamp", DateTime startTime = default, DateTime endTime = default) where T : class
            => ESService.IsOnline ? await ESService?.GetDocument<T>(indexName, startIndex, size, sortDescending, sortField, startTime, endTime) : ElasticSearchService.GetFakeTaskSearchResponseFalse<T>();

        /// <summary>
        /// Put object to Elastic Search, if enabled.
        /// </summary>
        public async Task<Exception> ESPut<T>(T obj) where T : class => ESService.IsOnline ? await ESService?.IndexDSBuffered(obj, EsIndexName).ContinueWith(i =>
        {
            if (i.Result != null) _logBuf.AppendLine("[ESPut]Fail!\t" + i.Result.Message);
            return i.Result;
        }) : null;

        /// <summary>
        /// Method exposed for external procedure to construct ES Index string for querying ES.
        /// </summary>
        /// <param name="logName"></param>
        /// <returns></returns>
        public string GetESIndexName(string logName) => (ESGlobalIndexPrefix + ESIndexPrefix + logName.Replace(Path.DirectorySeparatorChar, ES_INDEX_SEPARATOR)).ToLower();

        public bool IsEnabled(MS.LogLevel logLevel) => LogLevel.HasFlag(logLevel.Map2CommonLogLevel());

        public void Log<TState>(MS.LogLevel logLevel, MS.EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter = null)
            => WriteInternal(logLevel.Map2CommonLogLevel(),
                             $"[{eventId.Id}]\t{(string.IsNullOrEmpty(eventId.Name) ? "" : "[" + eventId.Name + "]")}\t{(formatter == null ? state.ToString() : formatter(state, exception))}",
                             exception);

        /// <summary>
        /// Refresh ESIndexName属性
        /// </summary>
        public void RefreshEsIndex()
        {
            var v = GetESIndexName(_name);   //TODO:因为要到读取配置的时候才会给ES索引名前缀赋值，所以之前初始化的日志的名称可能会有问题。
            foreach (char c in invalidUrlChar) v = v.Replace(c, '_');
            _esIndexName = v;
        }

        /// <summary>
        /// In order to keep the log key consistent, always rename by this method.
        /// </summary>
        /// <param name="newLogName"></param>
        /// <returns></returns>
        public bool Rename(string newLogName) => Rename(Name, newLogName);

        #region Log Actions

        public Action<string, Exception> DEBUG => Debug;

        public Action<string, Exception> ERROR => Error;

        public Action<string, Exception> FATAL => Fatal;

        //Expose Actions for LogExtention
        public Action<string, Exception> INFO => Info;

        public Action<string, Exception> TRACE => Trace;

        /// <summary>
        /// Allow calling chain like LogMan.VERBOSE.WARN() and to work together with any Extention Methods which may format log texts.
        /// </summary>
        public WimaLogger VERBOSE => UseVerbose ? this : NullLogger;

        public Action<string, Exception> WARN => Warn;

        #endregion Log Actions

        public void Trace(object message, Exception ex = null, [CallerMemberName] string caller = null) => base.Trace($"[{caller}]\t{message}", ex);

        public void Verbose(string logText, Exception ex = null, Action<string, Exception> logAction = null)
        {
            if (UseVerbose)
                if (logAction == null) Info(logText, ex);
                else logAction.Invoke(logText, ex);
        }

        protected override void WriteInternal(LogLevel level, object message, Exception ex)
        {
            //multiple threads cannot share the same logLineBuilder, so it has to be Get() from stringBuilderPool and Return() before exit the procedure.
            StringBuilder logLineBuilder = stringBuilderPool.Get();
            if (ShowDateTime) logLineBuilder.Append(DateTime.Now.ToString(DateTimeFormat));
            if (ShowLevel)
            {
                logLineBuilder.Append('\t');
                logLineBuilder.Append(level.ToString().ToUpper());
            }
            logLineBuilder.Append('\t');
            logLineBuilder.Append(message);
            if (ex != null && UseVerbose)
            {
                logLineBuilder.Append('\t');
                logLineBuilder.Append(ex.Message);
                if (ex.InnerException != null)
                {
                    logLineBuilder.Append('\t');
                    logLineBuilder.Append(ex.InnerException.Message);
                }
            }
            logLineBuilder.Append(Environment.NewLine);

            StringBuilder stackChain = null;

            if (LogModes.HasFlag(LogMode.StackTrace))
            {
                stackChain = stringBuilderPool.Get();
                stackChain.Append(" <- ");
                stackChain.Append(string.Join("/", new System.Diagnostics.StackTrace().GetFrames()
                    .Select(i => i.ToString())  //use StackFrame.ToString() to be native-aot compatible
                    .Where(i => !i.StartsWith("."))));
                stackChain.Append(Environment.NewLine);
                stackChain.Append(Environment.NewLine);
                logLineBuilder.Append(stackChain);
            }

            var _logLine = logLineBuilder.ToString();

            //Update LogBuf:Cut tail and process Replacement Mark "<<" in _logBuf
            lock (syncLogBuf)
            {
                if (_logBuf.Length > DefaultMaxBufferLength) _logBuf.Remove(DefaultMaxBufferLength - LOGBUFFER_LENGTH, LOGBUFFER_LENGTH);

                int _firstNL;
                if (_logLine.Contains(LINE_REPLACEMENT_PREFIX) && (_firstNL = _logBuf.ToString().IndexOf(Environment.NewLine)) > 0)
                {
                    _logBuf.Remove(0, _firstNL + Environment.NewLine.Length);  //remove first line from begining of _logBuf
                }
                _logBuf.Insert(0, _logLine);
            }

            //Log Native(Disk)
            if (LogModes.HasFlag(LogMode.Native))
            {
                renewLogWriter(); //Renew LogStreamWriter in case log path changes
                if (_logWriter != null) for (int i = 0; i < 2; i++)
                    {
                        //if happens to fail writing during _logwriter renewal(occasionally in case threads pile up), lock new _logWriter for anothter trial.
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
            }
            //Post to CommonLogger ,if enabled.
            if (LogModes.HasFlag(LogMode.CommonLog) && _commonLogger != null)
                switch (level)
                {
                    case LogLevel.Trace when IsTraceEnabled:
                        if (ex == null) _commonLogger.Trace(message);
                        else _commonLogger.Trace(message, ex);
                        break;

                    case LogLevel.Debug when IsDebugEnabled:
                        if (ex == null) _commonLogger.Debug(message);
                        else _commonLogger.Debug(message, ex);
                        break;

                    case LogLevel.Info when IsInfoEnabled:
                        if (ex == null) _commonLogger.Info(message);
                        else _commonLogger.Info(message, ex);
                        break;

                    case LogLevel.Warn when IsWarnEnabled:
                        if (ex == null) _commonLogger.Warn(message);
                        else _commonLogger.Warn(message, ex);
                        break;

                    case LogLevel.Error when IsErrorEnabled:
                        if (ex == null) _commonLogger.Error(message);
                        else _commonLogger.Error(message, ex);
                        break;

                    case LogLevel.Fatal when IsFatalEnabled:
                        if (ex == null) _commonLogger.Fatal(message);
                        else _commonLogger.Fatal(message, ex);
                        break;

                    case LogLevel.Trace when !IsTraceEnabled:
                    case LogLevel.Debug when !IsDebugEnabled:
                    case LogLevel.Info when !IsInfoEnabled:
                    case LogLevel.Warn when !IsWarnEnabled:
                    case LogLevel.Error when !IsErrorEnabled:
                    case LogLevel.Fatal when !IsFatalEnabled:
                    default:
                        return;
                }

            //Post to ElasticSearch, if enabled.
            if (LogModes.HasFlag(LogMode.ElasticSearch) && ESService != null)
            {
                var line = logLinePool.Get();
                line.Id = DateTime.Now.Ticks;
                line.Timestamp = DateTime.Now;
                line.LogLevel = level.ToString();
                line.LogMsg = message?.ToString();
                line.VerBoseMsg = ex?.Message + "\r\n-> " + ex?.InnerException?.Message;
                line.StackTrace = stackChain?.ToString();
                ESPut(line).ContinueWith(i => logLinePool.Return(line));
            }

            //Post to Console, as the last output to indicate log accomplishes.
            if (LogModes.HasFlag(LogMode.Console)) Console.Write((ShowLogName ? Name + '\t' : "") + _logLine);

            //Return StringBuilder instance
            //has to explicitly do null check before Return(), see https://github.com/dotnet/aspnetcore/issues/52873#issuecomment-1865443129
            if (stackChain != null) stringBuilderPool.Return(stackChain);
            stringBuilderPool.Return(logLineBuilder);
        }

        private static ILog getExternalLogger(string key) => LogManager.GetLogger(key);

        private string GetNextLogPath(DateTime? now = null) => LogRoot + Name + "_" + (now ?? DateTime.Now).ToString(LogFileNameTimeFormat) + ".log";

        /// <summary>
        /// Thread-safely updating LogWriter
        /// </summary>
        private void renewLogWriter()
        {
            DateTime now = DateTime.Now;
            if (LogRenewalPeriodInHour == 1 || (now.Hour - StartedAt.Hour) % LogRenewalPeriodInHour == 0 || _logWriter == null)
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
                            var oldWriter = Interlocked.Exchange(ref _logWriter, writer);
                            if (oldWriter != null)
                            {
                                oldWriter.Flush();
                                oldWriter.Dispose();
                            }

                            //Clean outdated log files,if necessary
                            if (LogPreservePeriodInHour > 0)
                                try
                                {
                                    foreach (var i in Directory.GetFiles(logPath, "*.log").Where(i => (now - File.GetLastWriteTime(i)).TotalHours >= LogPreservePeriodInHour))
                                        File.Delete(i);
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