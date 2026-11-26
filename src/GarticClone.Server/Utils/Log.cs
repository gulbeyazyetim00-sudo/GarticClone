namespace GarticClone.Server.Utils;

public enum LogLevel { Debug = 10, Info = 20, Warn = 30, Error = 40, Silent = 100 }

/// <summary>Tiny scoped console logger: Log.For("transport").Info("...")</summary>
public static class Log
{
    /// <summary>From LOG_LEVEL env (debug/info/warn/error/silent), default info.</summary>
    public static LogLevel Level { get; set; } =
        Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("LOG_LEVEL"), ignoreCase: true, out var l) ? l : LogLevel.Info;

    public static Logger For(string scope) => new(scope);
}

public sealed class Logger(string scope)
{
    public void Debug(string msg) => Write(LogLevel.Debug, msg);
    public void Info(string msg) => Write(LogLevel.Info, msg);
    public void Warn(string msg) => Write(LogLevel.Warn, msg);
    public void Error(string msg, Exception? ex = null) => Write(LogLevel.Error, ex is null ? msg : $"{msg}: {ex}");

    private void Write(LogLevel level, string msg)
    {
        if (level < Log.Level) return;
        string line = $"{DateTime.UtcNow:O} {level.ToString().ToUpperInvariant(),-5} [{scope}] {msg}";
        if (level >= LogLevel.Warn) Console.Error.WriteLine(line);
        else Console.WriteLine(line);
    }
}
