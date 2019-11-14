using Common.Logging;
using Common.Logging.Simple;
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
        CommonLog = 0b1,
        Native = 0b10,
        Console = 0b100,
        StackTrace = 0b1000,
        Verbose = 0b10000
    }


    public class LogMan : AbstractSimpleLogger
    {

        private static int DefaultMaxBufferLength { get; set; } = 1024 * 64;

        public static LogMode LogModes { get; set; } = LogMode.Console;


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
        public static string LogFileNameTimeFormat { get; } = "yyMMdd_HHmmss";


        /// <summary>
        /// Date format for log lines
        /// </summary>
        public static string LogLineTimeFormat { get; } = "yy-MM-dd_HH:mm:ss";


        /// <summary>
        /// Path for current LogMan instance
        /// </summary>
        public string LogPath { get; private set; }

        /// <summary>
        /// LogStream for writing 
        /// </summary>
        private StreamWriter LogStreamWriter { get; set; }

        /// <summary>
        /// Reggistered loggers
        /// </summary>
        public static ConcurrentBag<LogMan> Loggers { get; private set; } = new ConcurrentBag<LogMan>();


        public LogMan(string logName, LogLevel logLevel, bool showlevel, bool showDateTime, bool showLogName, string dateTimeFormat) : base(logName, logLevel, showlevel, showDateTime, showLogName, dateTimeFormat)
        {
            LogPath = LogRoot + logName + "_" + DateTime.Now.ToString(LogFileNameTimeFormat) + ".log";

            if (LogModes.HasFlag(LogMode.CommonLog))
            {
                try { CommonLogger = GetLogger(logName); }
                catch (Exception ex)
                {
                    LogModes = (LogModes ^ LogMode.CommonLog) | LogMode.Native;
                    Info("Failure initalizing CommonLog,use native mode instead!", ex);
                }
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                LogStreamWriter = LogModes.HasFlag(LogMode.Native) ? new StreamWriter(LogPath, true) { AutoFlush = true } : null;

            }
            catch (Exception ex)
            {
                LogBuf = "Unable to create log files,Console mode only！Error：" + ex.Message;
                LogModes = LogMode.Console;
            }

            //Register this Logman instance to a global static Bag
            Loggers.Add(this);

            Info($"LogMan is working!");
        }

        public LogMan(string Key) : this(Key, LogLevel.All, true, true, true, LogFileNameTimeFormat)
        { }

        public LogMan(Type type) : this(type.Name, LogLevel.All, true, true, true, LogFileNameTimeFormat)
        { }

        public LogMan(object obj) : this(obj.GetType().ToString(), LogLevel.All, true, true, true, LogFileNameTimeFormat)
        { }

        private static ILog GetLogger(string key) => LogManager.GetLogger(key);

        public void Error(Exception ex)
        {
            Error(ex.TargetSite + ":" + ex.Message);
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
                }
            };


            string logText = $"[{level.ToString()}]{this.Name}:" + message.ToString() +
                (ex == null ? "" : " - " + ex.Message + " - " + ex.InnerException?.Message);

            string methodName = "";
            if (LogModes.HasFlag(LogMode.StackTrace))
            {
                StackTrace callStack = new StackTrace();
                callStack.GetFrames().Select(i => i.GetMethod().Name).Where(i => !i.StartsWith(".")).ToList().ForEach(i => methodName += "/" + i);
                methodName = " <- " + methodName + "\r\n\r\n";
            }

            if (LogBuf.Length > DefaultMaxBufferLength) LogBuf = LogBuf.Remove(DefaultMaxBufferLength - 4096);
            string logLine = DateTime.Now.ToString(LogLineTimeFormat) + logText + "\r\n" + methodName;
            LogBuf = LogBuf.Insert(0, logLine);

            if (LogModes.HasFlag(LogMode.Native) && LogStreamWriter != null)
            {
                try
                {
                    lock (logLock) { LogStreamWriter.Write(logLine); }
                }
                catch (Exception excpt) { LogBuf.Insert(0, "!!!Failure writing log stream：" + excpt.Message); }
            }

            if (LogModes.HasFlag(LogMode.Console)) Console.Write(logLine);
        }
    }

}
