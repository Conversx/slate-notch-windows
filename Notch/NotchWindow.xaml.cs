using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Slate.Clipboard;
using Slate.Media;
using Slate.Shelf;
using Slate.Support;
using Forms = System.Windows.Forms;

namespace Slate.Notch;

public partial class NotchWindow : Window
{
    private readonly NotchState _state = new();
    private readonly MediaModel _media = new();
    private readonly ShelfStore _shelf = new();
    private readonly ClipboardStore _clipboard = new();
    private readonly VolumeController _volume = new();

    private NotchMetrics _metrics = null!;
    private Forms.NotifyIcon? _tray;
    private MouseHook? _clickHook;
    private bool _artworkHovered;

    /// Which volume the slider moves. The app's own is the default because that is the
    /// one the keyboard cannot reach.
    private bool _volumeTargetsApp = true;
    private bool _draggingVolume;
    /// Remembers where an app was before it was muted, so unmuting returns to it.
    private double? _mutedAppLevel;

    /// <summary>Extra slack so the cursor does not have to be pixel-perfect on the bar.</summary>
    private const double HoverSlack = 8;

    public NotchWindow()
    {
        InitializeComponent();

        Width = Layout.OpenWidth + Layout.WindowPadding * 2;
        Height = Layout.OpenHeight + Layout.WindowPadding;

        _state.PhaseChanged += OnPhaseChanged;
        _media.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(RenderMedia);
        _shelf.Items.CollectionChanged += (_, _) => RenderShelf();
        _clipboard.Items.CollectionChanged += (_, _) => RenderClipboard();
        _volume.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(RenderVolume);

        Loaded += OnLoaded;
    }

    // MARK: - Lifecycle

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionOnScreen();
        WireInteractions();
        InstallTray();

        _clipboard.Attach(this);
        _clickHook = new MouseHook(OnGlobalClick);

        ApplyPhaseVisuals(NotchPhase.Closed, animate: false);
        RenderMedia();
        RenderVolume();
        RenderShelf();
        RenderClipboard();
        SelectTab(NotchTab.Now);

        await _media.StartAsync();

        if (Diagnostics.OpensOnLaunch)
        {
            Diagnostics.Log($"window at {Left},{Top} size {Width}x{Height}");
            await Task.Delay(400);
            _state.Open();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(HitTestHook);
    }

    public void Shutdown()
    {
        _clickHook?.Dispose();
        _clipboard.Dispose();
        _volume.Dispose();
        _media.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
    }

    private void PositionOnScreen()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _metrics = NotchMetrics.Primary(dpi.DpiScaleX);
        Left = _metrics.ScreenBounds.Left + (_metrics.ScreenBounds.Width - Width) / 2;
        Top = _metrics.ScreenBounds.Top;

        // The centre band exists so the layout matches the Mac build, where it hides the
        // camera. Here it is simply breathing room.
        CentreBand.Width = new GridLength(_metrics.Width - 24);
    }

    // MARK: - Hit testing

    /// <summary>
    /// Everything outside the panel falls through to whatever is behind.
    /// </summary>
    /// <remarks>
    /// A WPF layered window still swallows clicks over its transparent areas, so this
    /// answers <c>WM_NCHITTEST</c> with <c>HTTRANSPARENT</c> the way the macOS build
    /// overrides <c>hitTest</c>.
    /// </remarks>
    private IntPtr HitTestHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_NCHITTEST) return IntPtr.Zero;

        // Two signed 16-bit values packed into lParam. Read it as 64-bit and mask:
        // IntPtr.ToInt32() throws once the high half is set, which it is off the left
        // or top of the virtual desktop.
        long packed = lParam.ToInt64();
        double px = (short)(packed & 0xFFFF);
        double py = (short)((packed >> 16) & 0xFFFF);

        var dpi = VisualTreeHelper.GetDpi(this);
        var local = new Point(px / dpi.DpiScaleX - Left, py / dpi.DpiScaleY - Top);

        handled = true;
        return InteractiveRect().Contains(local) ? new IntPtr(HTCLIENT) : new IntPtr(HTTRANSPARENT);
    }

    /// <summary>Panel rect in window coordinates, grown into the region the cursor may
    /// be "on" it. One definition, used for hit testing and for judging outside clicks.</summary>
    private Rect InteractiveRect()
    {
        var size = SizeFor(_state.Phase);
        double x = (Width - size.Width) / 2;
        var rect = new Rect(x, 0, size.Width, size.Height);
        if (_state.Phase != NotchPhase.Open)
        {
            rect = new Rect(rect.X - HoverSlack, 0,
                            rect.Width + HoverSlack * 2, rect.Height + HoverSlack);
        }
        return rect;
    }

    private Size SizeFor(NotchPhase phase) => phase switch
    {
        NotchPhase.Closed => new Size(_metrics?.Width ?? 200, _metrics?.Height ?? 34),
        NotchPhase.Hinted => new Size((_metrics?.Width ?? 200) + 10, (_metrics?.Height ?? 34) + 4),
        _ => new Size(Layout.OpenWidth, Layout.OpenHeight)
    };

    // MARK: - Interaction wiring

    private void WireInteractions()
    {
        Panel.MouseEnter += (_, _) => _state.Hover(true);
        Panel.MouseLeave += (_, _) => _state.Hover(false);
        Panel.MouseLeftButtonUp += (_, _) => _state.TapOnBar();

        Panel.DragEnter += (_, e) => { if (HasFiles(e)) _state.DropTargeted(true); };
        Panel.DragOver += (_, e) => { e.Effects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Panel.DragLeave += (_, _) => _state.DropTargeted(false);
        Panel.Drop += OnDrop;

        TabNow.MouseLeftButtonUp += (_, e) => { SelectTab(NotchTab.Now); e.Handled = true; };
        TabShelf.MouseLeftButtonUp += (_, e) => { SelectTab(NotchTab.Shelf); e.Handled = true; };
        TabClip.MouseLeftButtonUp += (_, e) => { SelectTab(NotchTab.Clipboard); e.Handled = true; };
        CloseButton.MouseLeftButtonUp += (_, e) => { _state.Close(); e.Handled = true; };

        PrevButton.MouseLeftButtonUp += (_, e) => { _media.Previous(); e.Handled = true; };
        PlayButton.MouseLeftButtonUp += (_, e) => { _media.Toggle(); e.Handled = true; };
        NextButton.MouseLeftButtonUp += (_, e) => { _media.Next(); e.Handled = true; };

        ScrubTrack.MouseLeftButtonUp += (_, e) =>
        {
            var x = e.GetPosition(ScrubTrack).X;
            if (ScrubTrack.ActualWidth > 0) _media.Seek(x / ScrubTrack.ActualWidth);
            e.Handled = true;
        };

        ArtworkHost.MouseEnter += (_, _) => { _artworkHovered = true; UpdateArtworkVeil(); };
        ArtworkHost.MouseLeave += (_, _) => { _artworkHovered = false; UpdateArtworkVeil(); };
        ArtworkHost.MouseLeftButtonUp += (_, e) =>
        {
            Launcher.ActivateBySourceId(_media.Current.SourceId);
            e.Handled = true;
        };

        VolumeButton.MouseLeftButtonUp += (_, e) => { ToggleVolumeMute(); e.Handled = true; };
        VolumeTag.MouseLeftButtonUp += (_, e) =>
        {
            _volumeTargetsApp = !_volumeTargetsApp;
            RenderVolume();
            e.Handled = true;
        };
        VolumeHit.MouseLeftButtonDown += (s, e) =>
        {
            _draggingVolume = true;
            VolumeHit.CaptureMouse();
            ApplyVolumeFromPointer(e.GetPosition(VolumeHit).X);
            e.Handled = true;
        };
        VolumeHit.MouseMove += (_, e) =>
        {
            if (_draggingVolume) ApplyVolumeFromPointer(e.GetPosition(VolumeHit).X);
        };
        VolumeHit.MouseLeftButtonUp += (_, e) =>
        {
            _draggingVolume = false;
            VolumeHit.ReleaseMouseCapture();
            e.Handled = true;
        };

        ShelfClear.MouseLeftButtonUp += (_, e) => { _shelf.Clear(); e.Handled = true; };
        ClipClear.MouseLeftButtonUp += (_, e) => { _clipboard.Clear(); e.Handled = true; };
    }

    private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    private void OnDrop(object sender, DragEventArgs e)
    {
        _state.DropTargeted(false);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            _shelf.Add(paths);
            SelectTab(NotchTab.Shelf);
            _state.Open();
        }
        e.Handled = true;
    }

    /// <summary>A click anywhere outside an open panel dismisses it. Moves are
    /// deliberately not hooked — WPF's own enter/leave handles hover for free.</summary>
    private void OnGlobalClick(Point screenPoint)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_state.Phase != NotchPhase.Open) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            var local = new Point(screenPoint.X / dpi.DpiScaleX - Left,
                                  screenPoint.Y / dpi.DpiScaleY - Top);
            if (!InteractiveRect().Contains(local))
            {
                Diagnostics.Log($"close: click outside at {local}");
                _state.Close();
            }
        });
    }

    // MARK: - Phase

    private void OnPhaseChanged(NotchPhase phase)
    {
        ApplyPhaseVisuals(phase, animate: true);
        _media.SetPanelOpen(phase == NotchPhase.Open);
    }

    private void ApplyPhaseVisuals(NotchPhase phase, bool animate)
    {
        var size = SizeFor(phase);
        bool open = phase == NotchPhase.Open;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(open ? 260 : 200);

        if (animate)
        {
            Panel.BeginAnimation(WidthProperty,
                new DoubleAnimation(size.Width, duration) { EasingFunction = ease });
            Panel.BeginAnimation(HeightProperty,
                new DoubleAnimation(size.Height, duration) { EasingFunction = ease });
            PanelContent.BeginAnimation(OpacityProperty,
                new DoubleAnimation(open ? 1 : 0, TimeSpan.FromMilliseconds(open ? 200 : 120)));
        }
        else
        {
            Panel.Width = size.Width;
            Panel.Height = size.Height;
            PanelContent.Opacity = open ? 1 : 0;
        }

        Panel.CornerRadius = new CornerRadius(0, 0,
            open ? Layout.OpenCornerRadius : Layout.ClosedCornerRadius,
            open ? Layout.OpenCornerRadius : Layout.ClosedCornerRadius);

        // The glow hugs the panel and blooms past it.
        Glow.Width = size.Width * (open ? 1.02 : 1.10);
        Glow.Height = size.Height * (open ? 1.10 : 1.35);
        Glow.BeginAnimation(OpacityProperty,
            new DoubleAnimation(phase == NotchPhase.Closed ? 0 : (open ? 0.40 : 0.72),
                                TimeSpan.FromMilliseconds(180)));

        PanelContent.IsHitTestVisible = open;
    }

    // MARK: - Tabs

    private void SelectTab(NotchTab tab)
    {
        _state.Tab = tab;

        // Only the selected tab carries its label; three full labels crowd the bar.
        Style(TabNow, TabNowIcon, TabNowLabel, tab == NotchTab.Now);
        Style(TabShelf, TabShelfIcon, TabShelfLabel, tab == NotchTab.Shelf);
        Style(TabClip, TabClipIcon, TabClipLabel, tab == NotchTab.Clipboard);

        NowPane.Visibility = tab == NotchTab.Now && _media.HasContent ? Visibility.Visible : Visibility.Collapsed;
        NowIdlePane.Visibility = tab == NotchTab.Now && !_media.HasContent ? Visibility.Visible : Visibility.Collapsed;
        ShelfPane.Visibility = tab == NotchTab.Shelf ? Visibility.Visible : Visibility.Collapsed;
        ClipPane.Visibility = tab == NotchTab.Clipboard ? Visibility.Visible : Visibility.Collapsed;

        static void Style(Border pill, TextBlock icon, TextBlock label, bool selected)
        {
            pill.Background = selected
                ? new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF))
                : Brushes.Transparent;
            var brush = selected
                ? Brushes.White
                : new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
            icon.Foreground = brush;
            label.Foreground = brush;
            label.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateArtworkVeil()
        => ArtworkHoverVeil.Visibility = _artworkHovered ? Visibility.Visible : Visibility.Collapsed;

    // MARK: - Rendering (deliberately explicit rather than bound)

    private void RenderMedia()
    {
        var track = _media.Current;
        var accent = _media.Accent;

        GlowBrush.Color = accent;
        ScrubBrush.Color = accent;
        BadgeBrush.Color = Color.FromArgb(0x59, accent.R, accent.G, accent.B);
        ArtworkPlaceholder.Color = Color.FromArgb(0x66, accent.R, accent.G, accent.B);

        // Tint only below the header band, so the top strip stays true black.
        Panel.Background = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Colors.Black, 0),
                new(Colors.Black, 0.17),
                new(Blend(accent, 0.10), 0.37),
                new(Blend(accent, 0.02), 1)
            },
            new Point(0.5, 0), new Point(0.5, 1));

        if (_state.Tab == NotchTab.Now)
        {
            NowPane.Visibility = track.HasContent ? Visibility.Visible : Visibility.Collapsed;
            NowIdlePane.Visibility = track.HasContent ? Visibility.Collapsed : Visibility.Visible;
        }

        SourceLabel.Text = track.SourceName;
        TitleLabel.Text = track.Title;
        ArtistLabel.Text = string.IsNullOrEmpty(track.Artist) ? track.Album : track.Artist;
        // Segoe Fluent Icons: E769 pause, E768 play. Escaped rather than pasted so the
        // file survives being moved between machines with different encodings.
        PlayGlyph.Text = track.IsPlaying ? "\uE769" : "\uE768";
        ElapsedLabel.Text = TimeFormat.Clock(track.Elapsed);
        DurationLabel.Text = TimeFormat.Clock(track.Duration);
        ArtworkImage.Source = track.Artwork;
        WashImage.Source = track.Artwork;
        ArtworkWash.Visibility = track.Artwork is null ? Visibility.Collapsed : Visibility.Visible;

        if (ScrubTrack.ActualWidth > 0)
            ScrubFill.Width = ScrubTrack.ActualWidth * track.Progress;

        RenderVolume();
    }

    // MARK: - Volume

    /// True when the slider is moving the playing app's own mixer level rather than the
    /// system output. Windows exposes a real per-process mixer, so unlike the Mac build
    /// this works for anything that makes noise, not only players Slate can talk to.
    private bool UsingAppVolume =>
        _volumeTargetsApp && !string.IsNullOrWhiteSpace(_media.Current.SourceId);

    private double CurrentVolumeLevel()
    {
        if (UsingAppVolume)
            return _volume.GetApp(_media.Current.SourceId) ?? 0;
        return _volume.SystemMuted ? 0 : _volume.SystemLevel;
    }

    private void ApplyVolumeFromPointer(double x)
    {
        if (VolumeHit.ActualWidth <= 0) return;
        var level = Math.Clamp(x / VolumeHit.ActualWidth, 0, 1);
        _mutedAppLevel = null;
        if (UsingAppVolume) _volume.SetApp(_media.Current.SourceId, level);
        else _volume.SetSystem(level);
        RenderVolume();
    }

    private void ToggleVolumeMute()
    {
        if (!UsingAppVolume)
        {
            _volume.ToggleSystemMute();
            RenderVolume();
            return;
        }

        var level = _volume.GetApp(_media.Current.SourceId) ?? 0;
        if (level > 0.001)
        {
            _mutedAppLevel = level;
            _volume.SetApp(_media.Current.SourceId, 0);
        }
        else
        {
            _volume.SetApp(_media.Current.SourceId, _mutedAppLevel ?? 0.5);
            _mutedAppLevel = null;
        }
        RenderVolume();
    }

    private void RenderVolume()
    {
        VolumeRow.Visibility = _volume.IsAvailable ? Visibility.Visible : Visibility.Collapsed;
        if (!_volume.IsAvailable) return;

        bool app = UsingAppVolume;
        VolumeTag.Visibility = string.IsNullOrWhiteSpace(_media.Current.SourceId)
            ? Visibility.Hidden
            : Visibility.Visible;
        VolumeTagText.Text = app ? "APP" : "SYS";
        VolumeTag.ToolTip = app
            ? $"Moving {_media.Current.SourceName}'s own level — click for system output"
            : "Moving system output — click for this app only";

        var level = CurrentVolumeLevel();
        // Segoe Fluent Icons: E74F mute, E992..E995 rising levels.
        VolumeGlyph.Text = level <= 0.001 ? "\uE74F"
            : level < 0.34 ? "\uE993"
            : level < 0.67 ? "\uE994"
            : "\uE995";

        VolumeBrush.Color = level <= 0.001
            ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)
            : _media.Accent;
        VolumeFill.Width = Math.Max(0, VolumeHit.Width * level);
    }

    private static Color Blend(Color accent, double alpha)
        => Color.FromArgb((byte)(alpha * 255), accent.R, accent.G, accent.B);

    private void RenderShelf()
    {
        ShelfBadge.Visibility = _shelf.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfBadgeText.Text = _shelf.Items.Count.ToString();

        bool any = _shelf.Items.Count > 0;
        ShelfDropZone.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ShelfList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ShelfCount.Text = $"{_shelf.Items.Count} item{(_shelf.Items.Count == 1 ? "" : "s")}";

        ShelfItems.Items.Clear();
        foreach (var item in _shelf.Items) ShelfItems.Items.Add(ChipFactory.Shelf(item, _shelf));
    }

    private void RenderClipboard()
    {
        bool any = _clipboard.Items.Count > 0;
        ClipEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ClipList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ClipCount.Text = $"{_clipboard.Items.Count} item{(_clipboard.Items.Count == 1 ? "" : "s")}";

        ClipItems.Items.Clear();
        foreach (var item in _clipboard.Items)
            ClipItems.Items.Add(ChipFactory.Clip(item, _clipboard, _media.Accent));
    }

    // MARK: - Tray

    private void InstallTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => Dispatcher.BeginInvoke(() => _state.Open()));
        menu.Items.Add(new Forms.ToolStripSeparator());

        var record = new Forms.ToolStripMenuItem("Record Clipboard") { Checked = true };
        record.Click += (_, _) =>
        {
            record.Checked = !record.Checked;
            _clipboard.SetEnabled(record.Checked);
        };
        record.ToolTipText = "History is kept in memory only and is never written to disk.";
        menu.Items.Add(record);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Slate", null, (_, _) => Application.Current.Shutdown());

        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Slate",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Dispatcher.BeginInvoke(() => _state.Open());
        };
    }

    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;
    private const int HTCLIENT = 1;
}
