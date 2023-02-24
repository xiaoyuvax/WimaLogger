using Microsoft.Extensions.Logging;
using System;
using CM = Common.Logging;

namespace Wima.Log
{
    public sealed class WimaLogger : WimaLoggerBase, ILogger
    {
        public WimaLogger(Type t) : base(t) { }

        public WimaLogger(object o) : base(o) { }

        public WimaLogger(string logName, CM.LogLevel? logLevel = null, bool showLevel = true, bool showDateTime = true, bool showLogName = true, string dateTimeFormat = "yy-MM-dd HH:mm:ss")
            : base(logName, logLevel, showLevel, showDateTime, showLogName, dateTimeFormat) { }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => default!;

        public bool IsEnabled(LogLevel logLevel) => LogLevel.HasFlag(logLevel.Map2CommonLogLevel());

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter = null)
            => WriteInternal(logLevel.Map2CommonLogLevel(),
                             $"[{eventId.Id}]\t{(string.IsNullOrEmpty(eventId.Name) ? "" : "[" + eventId.Name + "]")}\t{(formatter == null ? state.ToString() : formatter(state, exception))}",
                             exception);
    }
}