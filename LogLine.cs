using Nest;

namespace Wima.Log
{
    public class LogLine
    {
        public LogLine()
        {
        }

        public LogLine(long id, DateTime timestamp, string logLevel, string logMsg, string verBoseMsg, string stackTrace = null)
        {
            Id = id;
            Timestamp = timestamp;
            LogLevel = logLevel;
            LogMsg = logMsg;
            VerBoseMsg = verBoseMsg;
            StackTrace = stackTrace;
        }

        public long Id { get; set; }

        public string LogLevel { get; set; }

        public string LogMsg { get; set; }

        public string StackTrace { get; set; }

        [DateNanos(Name = "@timestamp")]
        public DateTime Timestamp { get; set; }
        public string VerBoseMsg { get; set; }
    }
}