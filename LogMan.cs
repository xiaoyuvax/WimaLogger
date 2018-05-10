using Common.Logging;
using Common.Logging.Simple;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Wima.Log
{
    [Flags]
    public enum LogMode : byte
    {
        CommonLog = 0b1,
        Disk = 0b10,
        Console = 0b100,
        StackTrace = 0b1000,  //记录堆栈
        Verbose = 0b10000     //高级日志
    }


    public class LogMan : AbstractSimpleLogger
    {

        private static int DefaultMaxBufferLength { get; set; } = 1024 * 64;

        public static LogMode LogModes { get; set; } = LogMode.Console | LogMode.CommonLog;

        
        /// <summary>
        /// 内存日志缓冲。
        /// </summary>
        public string LogBuf { get; set; } = "";

        private ILog CommonLogger { get; set; } = null;


        /// <summary>
        /// 日志默认路径(当前应用域的基目录)
        /// </summary>
        public string LogRoot = AppDomain.CurrentDomain.BaseDirectory + @"\Logs\";

        /// <summary>
        /// 日志写线程锁
        /// </summary>
        private readonly object logLock = new object();

        /// <summary>
        /// 日志文件名日期部分格式
        /// </summary>
        public static string LogFileNameTimeFormat { get; } = "yyMMdd_HHmmss";

        /// <summary>
        /// 日志路径
        /// </summary>
        public string LogPath { get; private set; }

        /// <summary>
        /// 日志流
        /// </summary>
        private StreamWriter LogStreamWriter { get; set; }

        public static List<LogMan> Loggers { get; private set; } = new List<LogMan>();


        public LogMan(string logName, LogLevel logLevel, bool showlevel, bool showDateTime, bool showLogName, string dateTimeFormat) : base(logName, logLevel, showlevel, showDateTime, showLogName, dateTimeFormat)
        {
            LogPath = LogRoot + logName + "_" + DateTime.Now.ToString(LogFileNameTimeFormat);

            if (LogModes.HasFlag(LogMode.CommonLog))
            {
                try
                {
                    CommonLogger = GetLogger(logName);
                }
                catch (Exception ex)
                {
                    LogModes = (LogModes ^ LogMode.CommonLog) | LogMode.Disk;
                    Info("关闭CommonLog, 打开基本日志系统!", ex);
                }
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                LogStreamWriter = LogModes.HasFlag(LogMode.Disk) ? new StreamWriter(LogPath, true) { AutoFlush = true } : null;
            }
            catch (Exception ex)
            {
                LogBuf = "无法创建日志文件！改为内存模式！错误信息：" + ex.Message;
                LogModes = LogMode.Console;
            }

            //添加日志到全局静态列表
            Loggers.Add(this);

            Info("LogMan创建成功!");
        }

        public LogMan(string Key) : this(Key, LogLevel.All, true, true, true, LogFileNameTimeFormat)
        { }

        public LogMan(Type type) : this(type.Name, LogLevel.All, true, true, true, LogFileNameTimeFormat)
        { }

        public LogMan(Object obj) : this(obj.GetType().ToString(), LogLevel.All, true, true, true, LogFileNameTimeFormat)
        { }

        private static ILog GetLogger(string key)
        {
            ILog log = LogManager.GetLogger(key);
            return log;
        }

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


            string logText =
                "[" + level.ToString() + "]{" + this.Name + "}" +
                message.ToString() +
                (ex == null ? "" : " - " + ex.Message + " - " + ex.InnerException?.Message);

            string methodName = "";
            if (LogModes.HasFlag(LogMode.StackTrace))
            {
                StackTrace callStack = new StackTrace();
                callStack.GetFrames().Select(i => i.GetMethod().Name).Where(i => !i.StartsWith(".")).ToList().ForEach(i => methodName += "/" + i);
                methodName = " <- " + methodName + "\r\n\r\n";
            }

            if (LogBuf.Length > DefaultMaxBufferLength) LogBuf = LogBuf.Substring(0, DefaultMaxBufferLength - 2048);
            string logLine = "[" + DateTime.Now.ToString("yyMMdd-HH:mm:ss.fff") + "]" + logText + "\r\n" + methodName;
            LogBuf = LogBuf.Insert(0, logLine);

            if (LogModes.HasFlag(LogMode.Disk))
                try
                {
                    lock (logLock)
                    {
                        LogStreamWriter.Write(logLine);
                    }

                }
                catch (Exception excpt)
                {
                    LogBuf.Insert(0, "!!!日志文件写入错误：" + excpt.Message);
                }

            if (LogModes.HasFlag(LogMode.Console)) Console.Write(logLine);
        }
    }

}
