namespace Feige.App.Services;

/// <summary>
/// Append-only text log in the package's local folder.
/// </summary>
/// <remarks>
/// A packaged WinUI app has no console, so a start-up failure would otherwise leave nothing behind but a status line
/// the user has already clicked away. This is deliberately tiny: timestamp + message, one line each, capped so it
/// cannot grow without bound. Every operation is best effort — logging must never be the reason something fails.
/// </remarks>
public static class AppLog
{
    private const string FileName = "app.log";
    private const long MaxBytes = 256 * 1024;

    private static readonly Lock Gate = new();

    /// <summary>Full path of the log file.</summary>
    public static string FilePath => Path.Combine(AppSettings.AppDataFolder, FileName);

    /// <summary>Appends one line. Never throws.</summary>
    /// <param name="message">What happened; keep secrets out of it.</param>
    public static void Write(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    // Keep the tail rather than growing forever; startup is logged first and matters most.
                    string[] lines = File.ReadAllLines(path);
                    File.WriteAllLines(path, lines.Skip(lines.Length / 2));
                }

                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Nothing useful can be done if even the log cannot be written.
        }
    }
}
