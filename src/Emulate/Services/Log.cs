namespace Emulate.Services;

public static class Log
{
    private static readonly object Gate = new();

    /// <summary>Raised for user-facing status lines (shown in the status bar).</summary>
    public static event Action<string>? Status;

    public static string FilePath => Path.Combine(AppPaths.LogDir, $"emulate-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

    /// <summary>Logs and shows the message in the status bar.</summary>
    public static void Say(string message)
    {
        Info(message);
        Status?.Invoke(message);
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
