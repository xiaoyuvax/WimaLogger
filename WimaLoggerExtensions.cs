using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using MS = Microsoft.Extensions.Logging;

using CM = Common.Logging;

namespace Wima.Log
{
    public static class WimaLoggerExtensions
    {
        public static ILoggingBuilder AddWimaLogger(this ILoggingBuilder builder)
        {
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, WimaLoggerProvider>());
            return builder;
        }

        public static ILoggingBuilder AddWimaLogger(this ILoggingBuilder builder, Action<WimaLoggerConfiguration> configure)
        {
            builder.AddWimaLogger();
            builder.Services.Configure(configure);
            return builder;
        }

        public static CM.LogLevel Map2CommonLogLevel(this MS.LogLevel level) => level switch
        {
            MS.LogLevel.Trace => CM.LogLevel.Trace,
            MS.LogLevel.Debug => CM.LogLevel.Debug,
            MS.LogLevel.Warning => CM.LogLevel.Warn,
            MS.LogLevel.Information => CM.LogLevel.Info,
            MS.LogLevel.Error => CM.LogLevel.Error,
            MS.LogLevel.Critical => CM.LogLevel.Fatal,
            _ => CM.LogLevel.Off,
        };

        public static MS.LogLevel Map2MsLogLevel(this CM.LogLevel level) => level switch
        {
            CM.LogLevel.Trace => MS.LogLevel.Trace,
            CM.LogLevel.Debug => MS.LogLevel.Debug,
            CM.LogLevel.Warn => MS.LogLevel.Warning,
            CM.LogLevel.Info => MS.LogLevel.Information,
            CM.LogLevel.Error => MS.LogLevel.Error,
            CM.LogLevel.Fatal => MS.LogLevel.Critical,
            CM.LogLevel.All => MS.LogLevel.Information,
            _ => MS.LogLevel.None,
        };
    }
}