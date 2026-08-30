using System.Runtime.InteropServices;

namespace Slate.Shelf;

/// <summary>
/// Bringing another app's window forward.
/// </summary>
/// <remarks>
/// Windows refuses <c>SetForegroundWindow</c> from a process that is not itself in the
/// foreground — the same restriction that made the macOS build's Spotify activation
/// silently fail. The documented way round it is to attach to the current foreground
/// thread's input queue for the duration of the call.
/// </remarks>
public static class WindowRaiser
{
    public static void Raise(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

        var foreground = GetForegroundWindow();
        var targetThread = GetWindowThreadProcessId(hwnd, out _);
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);

        if (foregroundThread != targetThread)
        {
            AttachThreadInput(foregroundThread, targetThread, true);
            SetForegroundWindow(hwnd);
            AttachThreadInput(foregroundThread, targetThread, false);
        }
        else
        {
            SetForegroundWindow(hwnd);
        }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool fAttach);
}
