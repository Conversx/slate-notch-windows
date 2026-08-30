namespace Slate.Support;

/// <summary>
/// Stderr logging, off unless SLATE_DEBUG_LOG is set.
/// </summary>
/// <remarks>
/// The macOS build was debugged almost entirely through logs like these — a spurious
/// mouse-exit, a tab id that was secretly a string, a window that reported success and
/// did nothing. Expect to need them here too, especially since this port was written
/// without a Windows machine to run it on.
/// </remarks>
public static class Diagnostics
{
    public static readonly bool Enabled =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SLATE_DEBUG_LOG"));

    /// <summary>SLATE_DEBUG_OPEN=1 — expand the panel shortly after launch.</summary>
    public static readonly bool OpensOnLaunch =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SLATE_DEBUG_OPEN"));

    private static readonly object Gate = new();
    private static readonly string? FilePath =
        Environment.GetEnvironmentVariable("SLATE_DEBUG_FILE");

    public static void Log(string message)
    {
        if (!Enabled) return;
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (Gate)
        {
            Console.Error.WriteLine(line);
            if (FilePath is not null)
            {
                try { File.AppendAllText(FilePath, line + Environment.NewLine); }
                catch { /* logging must never be the thing that breaks the app */ }
            }
        }
    }
}
