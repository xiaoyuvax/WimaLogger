using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace Wima.Log
{
    public sealed class WimaLoggerProvider : ILoggerProvider
    {
        private readonly IDisposable _onChangeToken;
        public WimaLoggerConfiguration CurrentConfig { get; private set; }

        private readonly ConcurrentDictionary<string, WimaLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);

        public WimaLoggerProvider(IOptionsMonitor<WimaLoggerConfiguration> config)
        {
            CurrentConfig = config.CurrentValue;
            _onChangeToken = config.OnChange(updatedConfig => CurrentConfig = updatedConfig);
        }

        public ILogger CreateLogger(string categoryName) =>
            _loggers.GetOrAdd(categoryName, name => new WimaLogger(name, CurrentConfig.LogLevel, CurrentConfig.ShowLevel, CurrentConfig.ShowDateTime, CurrentConfig.ShowLogName, CurrentConfig.DateTimeFormat));

        public void Dispose()
        {
            foreach (var logger in _loggers) logger.Value.Dispose();
            _loggers.Clear();
            _onChangeToken?.Dispose();
        }
    }
}