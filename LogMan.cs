using Common.Logging;
using Common.Logging.Factory;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;

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
        public static string DEFAULT_LOGFILE_NAME_TIME_FORMAT = "yyMMdd_HH";
        public static string DEFAULT_LOGLINE_TIME_FORMAT = "yy-MM-dd_HH:mm:ss";
        public static int DefaultMaxBufferLength { get; set; } = 1024 * 64;
        public static LogMode GlobalLogModes { get; set; } = LogMode.Console;

        public string Name { get; set; } = "";

        public LogMode LogModes { get; set; }

        /// <summary>
        /// In-memory buffer of recent log, for quick query of rencent logs.
        /// </summary>
        public string LogBuf { get; set; } = "";

        private ILog CommonLogger { get; set; } = null;

        /// <summary>
        /// Log root path
        /// </summary>
        public string LogRoot = Path.GetFullPath(Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + @"Logs" + Path.DirectorySeparatorChar);

        /// <summary>
        /// Writing lock,prevent race condition.
        /// </summary>
        private readonly object logLock = new object();

        /// <summary>
        /// Date format for log files
        /// </summary>
        public string LogFileNameTimeFormat { get; set; } = DEFAULT_LOGFILE_NAME_TIME_FORMAT;

        /// <summary>
        /// Date format for log lines
        /// </summary>
        public string LogLineTimeFormat { get; set; } = DEFAULT_LOGLINE_TIME_FORMAT;

        /// <summary>
        /// Path for current LogMan instance
        /// </summary>
        public string LogPath { get; private set; }

        /// <summary>
        /// LogStream for writing
        /// </summary>
        private StreamWriter _logWriter { get; set; }

        /// <summary>
        /// Reggistered loggers
        /// </summary>
        public static ConcurrentDictionary<string, LogMan> Loggers { get; private set; } = new ConcurrentDictionary<string, LogMan>();

        public override bool IsTraceEnabled => true;

        public override bool IsDebugEnabled => true;

        public override bool IsInfoEnabled => true;

        public override bool IsWarnEnabled => true;

        public override bool IsErrorEnabled => true;

        public override bool IsFatalEnabled => true;

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
            if (!Loggers.TryAdd(logName, this)) Error("Failed to registered LogMan!");

            Info("LogMan - Ready!");
        }

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
                        LogBuf = "Unable to create log files,Console mode only！Error：" + ex.Message;
                        LogModes = LogMode.Console;
                    }
                }
            }
        }

        private string GetNextLogPath() => LogRoot + Name + "_" + DateTime.Now.ToString(LogFileNameTimeFormat) + ".log";

        public LogMan(Type type) : this(type.Name) //LogLevel.All, true, true, true, DEFAULT_LOGFILE_NAME_TIME_FORMAT
        { }

        public LogMan(object obj) : this(obj.GetType().Name) //LogLevel.All, true, true, true, DEFAULT_LOGFILE_NAME_TIME_FORMAT
        { }

        private static ILog GetLogger(string key) => LogManager.GetLogger(key);

        public void Error(Exception ex) => Error(ex.TargetSite + ":" + ex.Message);

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

            string logText = $"[{level.ToString()}]{logName}:" + message?.ToString() +
                (ex == null ? "" : " - " + ex.Message + " - " + ex.InnerException?.Message);

            string stackChain = "";
            if (LogModes.HasFlag(LogMode.StackTrace))
            {
                StackTrace callStack = new StackTrace();
                callStack.GetFrames().Select(i => i.GetMethod().Name).Where(i => !i.StartsWith(".")).ToList().ForEach(i => stackChain += "/" + i);
                stackChain = " <- " + stackChain + "\r\n\r\n";
            }

            string logLine = DateTime.Now.ToString(LogLineTimeFormat) + logText + "\r\n" + stackChain;

            lock (logLock)
            {
                if (LogBuf.Length > DefaultMaxBufferLength) LogBuf = LogBuf.Remove(DefaultMaxBufferLength - 4096);
                LogBuf = LogBuf.Insert(0, logLine);
            }

            //Renew LogStreamWriter in case log path changes
            RenewLogWriter();
            if (LogModes.HasFlag(LogMode.Native) && _logWriter != null)
            {
                try
                {
                    lock (_logWriter) { _logWriter.Write(logLine); }
                }
                catch (Exception excpt) { LogBuf.Insert(0, "!!!Failure writing log stream：" + excpt.Message); }
            }

            if (LogModes.HasFlag(LogMode.Console)) Console.Write(logLine);
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
    }
}