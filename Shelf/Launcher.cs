using System.Diagnostics;

namespace Slate.Shelf;

/// <summary>Opening things the way Explorer would.</summary>
public static class Launcher
{
    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Support.Diagnostics.Log($"open failed: {ex.Message}");
        }
    }

    public static void Reveal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Support.Diagnostics.Log($"reveal failed: {ex.Message}");
        }
    }

    /// <summary>Brings the app that owns the current media session forward.</summary>
    public static void ActivateBySourceId(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) return;
        try
        {
            // "Spotify.exe" style ids map to a running process we can raise directly.
            var exe = sourceId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? sourceId[..^4]
                : null;
            if (exe is not null)
            {
                var process = Process.GetProcessesByName(exe).FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
                if (process is not null)
                {
                    WindowRaiser.Raise(process.MainWindowHandle);
                    return;
                }
            }

            // Otherwise it is a packaged app; the shell knows how to launch an AUMID.
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:appsFolder\\{sourceId}")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Support.Diagnostics.Log($"activate failed: {ex.Message}");
        }
    }
}
