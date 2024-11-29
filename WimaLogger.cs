using Microsoft.Extensions.Logging;
using CM = Common.Logging;

namespace Wima.Log
{
    public class WimaLogger : WimaLoggerBase, ILogger
    {
        public WimaLogger(Type t) : base(t) { }

        public WimaLogger(object o) : base(o) { }

        public WimaLogger(string logName, CM.LogLevel? logLevel = null, bool showLevel = true, bool showDateTime = true, bool showLogName = true, string dateTimeFormat = DEFAULT_LOGLINE_TIME_FORMAT, LogMode? logMode = null)
            : base(logName, logLevel, showLevel, showDateTime, showLogName, dateTimeFormat, logMode) { }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => default!;

        public bool IsEnabled(LogLevel logLevel) => LogLevel.HasFlag(logLevel.Map2CommonLogLevel());

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter = null)
            => WriteInternal(logLevel.Map2CommonLogLevel(),
                             $"[{eventId.Id}]\t{(string.IsNullOrEmpty(eventId.Name) ? "" : "[" + eventId.Name + "]")}\t{(formatter == null ? state.ToString() : formatter(state, exception))}",
                             exception);
    }
}