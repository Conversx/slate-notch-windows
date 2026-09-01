using System.Windows;
// Aliased rather than imported wholesale: a plain `using System.Windows.Forms` beside
// `using System.Windows` makes Point, Size and Application ambiguous in this very file.
// Two types is all this needs.
using WinScreen = System.Windows.Forms.Screen;
using WinControl = System.Windows.Forms.Control;

namespace Slate.Support;

/// <summary>
/// Where the panel lives.
/// </summary>
/// <remarks>
/// A Windows laptop has no camera cutout, so there is no hardware rect to read — the
/// notch is invented. It is placed at the top centre of whichever screen the cursor is
/// on, which is the closest thing to "the notch is where you are looking".
/// </remarks>
public sealed class NotchMetrics
{
    /// <summary>Notch size in device-independent units.</summary>
    public double Width { get; init; } = 200;
    public double Height { get; init; } = 34;

    /// <summary>Working area of the target screen, in DIUs.</summary>
    public Rect ScreenBounds { get; init; }

    public bool IsVirtual => true;

    public double NotchLeft => ScreenBounds.Left + (ScreenBounds.Width - Width) / 2;
    public double NotchTop => ScreenBounds.Top;

    public static NotchMetrics ForCursorScreen(double dpiScale)
    {
        var screen = WinScreen.FromPoint(WinControl.MousePosition) ?? WinScreen.PrimaryScreen!;
        return FromScreen(screen, dpiScale);
    }

    public static NotchMetrics Primary(double dpiScale) => FromScreen(WinScreen.PrimaryScreen!, dpiScale);

    private static NotchMetrics FromScreen(WinScreen screen, double dpiScale)
    {
        // Screen bounds arrive in physical pixels; WPF lays out in DIUs.
        var b = screen.Bounds;
        return new NotchMetrics
        {
            ScreenBounds = new Rect(b.Left / dpiScale, b.Top / dpiScale,
                                    b.Width / dpiScale, b.Height / dpiScale)
        };
    }
}

public static class Layout
{
    public const double OpenWidth = 560;
    public const double OpenHeight = 214;
    /// <summary>Transparent room around the panel so shadows and springs never clip.</summary>
    public const double WindowPadding = 140;

    public const double ClosedCornerRadius = 10;
    public const double OpenCornerRadius = 26;
    /// <summary>Radius of the concave flares where the panel meets the screen edge.</summary>
    public const double FlareRadius = 9;
}
