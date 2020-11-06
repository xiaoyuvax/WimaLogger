using Common.Logging;
using Common.Logging.Factory;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

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
        Verbose = 0b10000
    }

    public class LogMan : AbstractLogger
    {
        public const string LINE_REPLACEMENT_PREFIX = "<< ";
        public static string DEFAULT_LOGFILE_NAME_TIME_FORMAT = "yyMMdd_HH";
        public static string DEFAULT_LOGLINE_TIME_FORMAT = "yy-MM-dd_HH:mm:ss";

        /// <summary>
        /// Global log root path
        /// </summary>
        public static string LogRoot = Path.GetFullPath(Environment.CurrentDirectory + Path.DirectorySeparatorChar + @"Logs" + Path.DirectorySeparatorChar);

        protected StringBuilder _logBuf = new StringBuilder(DefaultMaxBufferLength);

        /// <summary>
        /// Writing lock,prevent race condition.
        /// </summary>
        private readonly object logLock = new object();

        public LogMan(string logName) //LogLevel logLevel, bool showlevel, bool showDateTime, bool showLogName, string dateTimeFormat
        {
            LogModes = GlobalLogModes;

            if (LogModes.HasFlag(LogMode.CommonLog))
            {
                try { CommonLogger = GetLogger(logName); }
                catch (Exception ex)
                {
                    LogModes = (GlobalLogModes ^ LogMode.CommonLog) | LogMode.Native;
                    Info("Failure initalizing CommonLog,use native mode instead!", ex);
                }
            }

            Name = logName;

            RenewLogWriter();

            //Register this Logman instance to a global static Bag
            Loggers.AddOrUpdate(logName, this, (k, v) => this);

            Info("LogMan - Ready!");
        }

        public LogMan(Type type) : this(type.Name) //LogLevel.All, true, true, true, DEFAULT_LOGFILE_NAME_TIME_FORMAT
        { }

        public LogMan(object obj) : this(obj.GetType().Name) //LogLevel.All, true, true, true, DEFAULT_LOGFILE_NAME_TIME_FORMAT
        { }

        public static int DefaultMaxBufferLength { get; set; } = 1024 * 64;

        public static LogMode GlobalLogModes { get; set; } = LogMode.Console;

        /// <summary>
        /// Reggistered loggers
        /// </summary>
        public static ConcurrentDictionary<string, LogMan> Loggers { get; private set; } = new ConcurrentDictionary<string, LogMan>();

        public override bool IsDebugEnabled => true;

        public override bool IsErrorEnabled => true;

        public override bool IsFatalEnabled => true;

        public override bool IsInfoEnabled => true;

        public override bool IsTraceEnabled => true;

        public override bool IsWarnEnabled => true;

        /// <summary>
        /// In-memory buffer of recent log, for quick query of rencent logs.
        /// </summary>
        public string LogBuf
        {
            get
            {
                lock (logLock)
                {
                    return _logBuf.ToString();
                }
            }
        }


        /// <summary>
        /// Date format for log files
        /// </summary>
        public string LogFileNameTimeFormat { get; set; } = DEFAULT_LOGFILE_NAME_TIME_FORMAT;

        /// <summary>
        /// Date format for log lines
        /// </summary>
        public string LogLineTimeFormat { get; set; } = DEFAULT_LOGLINE_TIME_FORMAT;

        public LogMode LogModes { get; set; }

        /// <summary>
        /// Path for current LogMan instance
        /// </summary>
        public string LogPath { get; private set; }

        public string Name { get; set; } = "";

        /// <summary>
        /// LogStream for writing
        /// </summary>
        private StreamWriter _logWriter { get; set; }

        private ILog CommonLogger { get; set; } = null;

        /// <summary>
        /// 用指定的基路径指定日志根路径
        /// </summary>
        /// <param name="workingPath"></param>
        public static void SetGlobalLogRoot(string workingPath) => LogRoot = Path.GetFullPath(workingPath + Path.DirectorySeparatorChar + @"Logs" + Path.DirectorySeparatorChar);

        public void Error(Exception ex)
        {
            if (ex != null) Error(ex.TargetSite + ":" + ex.Message);
        }
        /// <summary>
        /// Unregister Logman from Loggers Dictionary, call this method when dispose the object associated with a logman instance.
        /// </summary>
        /// <returns></returns>
        public bool Unregister()
        {
            var done = Loggers.TryRemove(Name, out _);
            if (done) Info("LogMan - Unregistered!");
            _logWriter?.Dispose();
            _logWriter = null;
            return done;
        }

        protected override void WriteInternal(LogLevel level, object message, Exception ex)
        {
            if (LogModes.HasFlag(LogMode.CommonLog) && CommonLogger != null)
            {
                switch (level)
                {
                    case LogLevel.Trace:
                        if (ex == null) CommonLogger.Trace(message);
                        else CommonLogger.Trace(message, ex);
                        break;

                    case LogLevel.Debug:
                        if (ex == null) CommonLogger.Debug(message);
                        else CommonLogger.Debug(message, ex);
                        break;

                    case LogLevel.Info:
                        if (ex == null) CommonLogger.Info(message);
                        else CommonLogger.Info(message, ex);
                        break;

                    case LogLevel.Warn:
                        if (ex == null) CommonLogger.Warn(message);
                        else CommonLogger.Error(message, ex);
                        break;

                    case LogLevel.Error:
                        if (ex == null) CommonLogger.Error(message);
                        else CommonLogger.Error(message, ex);
                        break;

                    case LogLevel.Fatal:
                        if (ex == null) CommonLogger.Fatal(message);
                        else CommonLogger.Fatal(message, ex);
                        break;

                    default:
                        break;
                }
            };

            var posSep = Name.LastIndexOf(Path.DirectorySeparatorChar) + 1;
            var logName = Name;
            if (posSep >= 0) logName = Name.Substring(posSep, Name.Length - posSep);

            StringBuilder logText = new StringBuilder($"[{level}]{logName}:" + message?.ToString() +
                (ex == null ? "" : " - " + ex.Message + " - " + ex.InnerException?.Message));

            StringBuilder stackChain = new StringBuilder();
            if (LogModes.HasFlag(LogMode.StackTrace))
            {
                StackTrace callStack = new StackTrace();
                callStack.GetFrames().Select(i => i.GetMethod().Name).Where(i => !i.StartsWith(".")).ToList().ForEach(i => stackChain.Append("/" + i));
                stackChain.Insert(0, " <- ");
                logText.AppendLine();
                logText.AppendLine();
            }

            logText.Insert(0, DateTime.Now.ToString(LogLineTimeFormat));
            logText.AppendLine();
            if (stackChain.Length > 0) logText.Append(stackChain);

            lock (logLock)
            {
                if (_logBuf.Length > DefaultMaxBufferLength) _logBuf.Remove(DefaultMaxBufferLength - 4096, 4096);
                string line = logText.ToString();
                if (line.Contains(LINE_REPLACEMENT_PREFIX)) _logBuf.Remove(0, _logBuf.ToString().IndexOf(Environment.NewLine) + Environment.NewLine.Length);
                _logBuf.Insert(0, line);
            }

            //Renew LogStreamWriter in case log path changes
            RenewLogWriter();

            if (LogModes.HasFlag(LogMode.Native) && _logWriter != null)
            {
                try
                {
                    lock (_logWriter) { _logWriter?.Write(logText.ToString()); }
                }
                catch (Exception excpt)
                {
                    lock (logLock)
                    {
                        _logBuf.Insert(0, "!!!Failure writing log stream：" + excpt.Message);
                    }
                }
            }

            if (LogModes.HasFlag(LogMode.Console)) Console.Write(logText.ToString());
        }

        private static ILog GetLogger(string key) => LogManager.GetLogger(key);

        private string GetNextLogPath() => LogRoot + Name + "_" + DateTime.Now.ToString(LogFileNameTimeFormat) + ".log";

        private void RenewLogWriter()
        {
            //默认为当前的日志写入器
            StreamWriter writer = null;

            string nextLogPath = GetNextLogPath();
            if (LogPath != nextLogPath)
            {
                LogPath = nextLogPath;
                if (LogModes.HasFlag(LogMode.Native))
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(LogPath));

                        writer = new StreamWriter(new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                        if (_logWriter != null) _logWriter.Dispose();
                        _logWriter = writer;
                    }
                    catch (Exception ex)
                    {
                        lock (logLock)
                        {
                            _logBuf.Append("Unable to create log files,Console mode only！Error：" + ex.Message);
                        }
                        LogModes = LogMode.Console;
                    }
                }
            }
        }
    }
}