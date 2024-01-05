using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Configuration;
using CM = Common.Logging;

namespace Wima.Log
{
    public static class WimaLoggerExtensions
    {
        public static ILoggingBuilder AddWimaLogger(this ILoggingBuilder builder)
        {
            //builder.AddConfiguration();

            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, WimaLoggerProvider>());

            //LoggerProviderOptions.RegisterProviderOptions<WimaLoggerConfiguration, WimaLoggerProvider>(builder.Services);

            return builder;
        }

        public static ILoggingBuilder AddWimaLogger(this ILoggingBuilder builder, Action<WimaLoggerConfiguration> configure)
        {
            builder.AddWimaLogger();
            builder.Services.Configure(configure);
            return builder;
        }

        public static CM.LogLevel Map2CommonLogLevel(this LogLevel level) => level switch
        {
            LogLevel.Trace => CM.LogLevel.Trace,
            LogLevel.Debug => CM.LogLevel.Debug,
            LogLevel.Warning => CM.LogLevel.Warn,
            LogLevel.Information => CM.LogLevel.Info,
            LogLevel.Error => CM.LogLevel.Error,
            LogLevel.Critical => CM.LogLevel.Fatal,
            _ => CM.LogLevel.Off,
        };

        public static LogLevel Map2MsLogLevel(this CM.LogLevel level) => level switch
        {
            CM.LogLevel.Trace => LogLevel.Trace,
            CM.LogLevel.Debug => LogLevel.Debug,
            CM.LogLevel.Warn => LogLevel.Warning,
            CM.LogLevel.Info => LogLevel.Information,
            CM.LogLevel.Error => LogLevel.Error,
            CM.LogLevel.Fatal => LogLevel.Critical,
            CM.LogLevel.All => LogLevel.Information,
            _ => LogLevel.None,
        };
    }
}