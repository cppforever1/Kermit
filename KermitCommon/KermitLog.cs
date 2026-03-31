using NLog;

namespace KermitCommon;

public static class KermitLog
{
    static KermitLog()
    {
        if (LogManager.Configuration is null)
        {
            var configuration = new NLog.Config.LoggingConfiguration();
            var consoleTarget = new NLog.Targets.ColoredConsoleTarget("console")
            {
                Layout = "${longdate}|${level:uppercase=true}|${logger}|${message}${onexception:${newline}${exception:format=tostring}}"
            };

            configuration.AddRule(LogLevel.Info, LogLevel.Fatal, consoleTarget);
            LogManager.Configuration = configuration;
        }
    }

    public static Logger For<T>() => LogManager.GetLogger(typeof(T).FullName ?? typeof(T).Name);

    public static Logger For(string name) => LogManager.GetLogger(name);
}