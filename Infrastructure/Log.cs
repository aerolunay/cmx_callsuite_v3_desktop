using System.IO;

namespace CmxDialer.Infrastructure;

/// <summary>Plain daily log files in %LOCALAPPDATA%\CmxDialer\logs — useful for SIP/registration support.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CmxDialer", "logs");

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var file = Path.Combine(Directory, $"{DateTime.Now:yyyyMMdd}.log");
            lock (Gate)
            {
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }
}
