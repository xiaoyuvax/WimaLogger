namespace Wima.Log
{
    public record WimaLoggerConfiguration(Common.Logging.LogLevel? LogLevel = null, bool ShowLevel = true, bool ShowDateTime = true, bool ShowLogName = true, string DateTimeFormat = "yy-MM-dd HH:mm:ss")
    {
        public WimaLoggerConfiguration() : this(Common.Logging.LogLevel.All, true, true, true, "yy-MM-dd HH:mm:ss") { }
    };
}