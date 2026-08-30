using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Slate.Clipboard;

public sealed class ClipItem
{
    public string? Text { get; init; }
    public BitmapSource? Image { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public string SourceName { get; init; } = "";

    public bool IsImage => Image is not null;

    /// <summary>Collapsed to one flowing string so a chip can show something useful.</summary>
    public string Preview =>
        Text is null ? "" : string.Join(' ', Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public string SizeLabel => IsImage
        ? $"{Image!.PixelWidth}×{Image.PixelHeight}"
        : $"{Text?.Length ?? 0} chars";

    public bool HasSameContent(ClipItem other)
    {
        if (Text is not null && other.Text is not null) return Text == other.Text;
        return false;   // images are never treated as duplicates
    }
}

/// <summary>
/// Clipboard history.
/// </summary>
/// <remarks>
/// <para><strong>Nothing here is written to disk.</strong> A clipboard picks up
/// passwords, tokens and private messages in the course of an ordinary day; keeping the
/// history in memory means quitting Slate ends it and leaves no file behind to leak.</para>
///
/// <para>Unlike the macOS build, this does not poll. Windows has a real notification —
/// <c>AddClipboardFormatListener</c> delivers <c>WM_CLIPBOARDUPDATE</c> — so the cost
/// while nothing is being copied is exactly zero.</para>
/// </remarks>
public sealed class ClipboardStore : IDisposable
{
    public ObservableCollection<ClipItem> Items { get; } = [];
    public bool IsEnabled { get; private set; } = true;

    private const int Limit = 40;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _ignoreNextUpdate;

    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _hwnd = helper.Handle;
        if (_hwnd == IntPtr.Zero)
        {
            Support.Diagnostics.Log("clipboard: window has no handle yet");
            return;
        }

        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        if (!AddClipboardFormatListener(_hwnd))
            Support.Diagnostics.Log("clipboard: AddClipboardFormatListener failed");
    }

    public void SetEnabled(bool enabled) => IsEnabled = enabled;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE) Capture();
        return IntPtr.Zero;
    }

    private void Capture()
    {
        if (!IsEnabled) return;
        if (_ignoreNextUpdate) { _ignoreNextUpdate = false; return; }

        try
        {
            // Apps that handle secrets mark their contents with these, and asking to be
            // left out is a request worth honouring.
            var data = System.Windows.Clipboard.GetDataObject();
            if (data is null) return;
            foreach (var marker in new[] { "ExcludeClipboardContentFromMonitorProcessing",
                                           "CanIncludeInClipboardHistory",
                                           "ClipboardViewerIgnore" })
            {
                if (data.GetDataPresent(marker)) return;
            }

            var source = ForegroundAppName();

            if (System.Windows.Clipboard.ContainsText())
            {
                var text = System.Windows.Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                    Add(new ClipItem { Text = text, SourceName = source });
            }
            else if (System.Windows.Clipboard.ContainsImage())
            {
                var image = System.Windows.Clipboard.GetImage();
                if (image is not null)
                {
                    image.Freeze();
                    Add(new ClipItem { Image = image, SourceName = source });
                }
            }
        }
        catch (Exception ex)
        {
            // The clipboard is shared and another app may hold it open for a moment.
            Support.Diagnostics.Log($"clipboard read failed: {ex.Message}");
        }
    }

    private void Add(ClipItem item)
    {
        if (Items.Count > 0 && Items[0].HasSameContent(item))
        {
            Items[0] = item;
            return;
        }
        for (int i = Items.Count - 1; i >= 0; i--)
            if (Items[i].HasSameContent(item)) Items.RemoveAt(i);

        Items.Insert(0, item);
        while (Items.Count > Limit) Items.RemoveAt(Items.Count - 1);
    }

    /// <summary>Puts the item back on the clipboard and moves it to the front.</summary>
    public void CopyBack(ClipItem item)
    {
        try
        {
            // Our own write must not come back around as a brand new entry.
            _ignoreNextUpdate = true;
            if (item.Text is not null) System.Windows.Clipboard.SetText(item.Text);
            else if (item.Image is not null) System.Windows.Clipboard.SetImage(item.Image);

            Items.Remove(item);
            Items.Insert(0, item);
        }
        catch (Exception ex)
        {
            _ignoreNextUpdate = false;
            Support.Diagnostics.Log($"clipboard write failed: {ex.Message}");
        }
    }

    public void Remove(ClipItem item) => Items.Remove(item);

    public void Clear() => Items.Clear();

    private static string ForegroundAppName()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "";
            GetWindowThreadProcessId(hwnd, out var pid);
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) RemoveClipboardFormatListener(_hwnd);
        _source?.RemoveHook(WndProc);
    }

    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
