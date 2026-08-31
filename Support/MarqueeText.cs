using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Slate.Support;

/// <summary>
/// Scrolls its text only when the text is actually wider than the space it has.
/// </summary>
/// <remarks>
/// Short titles stay perfectly still, which is what makes long ones readable — and the
/// titles this ends up showing are long: a YouTube video's full Thai headline does not
/// fit in half a notch panel, and truncating it hides the part that identifies the clip.
///
/// Two copies of the text scroll past with a gap between them, so the line never appears
/// empty mid-cycle. If anything about the measurement looks wrong the control simply
/// stops animating and behaves as a plain, trimmed <see cref="TextBlock"/>.
/// </remarks>
public sealed class MarqueeText : UserControl
{
    private readonly TextBlock _primary = new();
    private readonly TextBlock _echo = new();
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };
    private readonly TranslateTransform _shift = new();

    private const double Gap = 44;
    private const double PointsPerSecond = 26;
    private const double StartDelaySeconds = 1.4;

    public MarqueeText()
    {
        foreach (var block in new[] { _primary, _echo })
        {
            block.TextTrimming = TextTrimming.None;
            block.TextWrapping = TextWrapping.NoWrap;
            block.VerticalAlignment = VerticalAlignment.Center;
        }
        _echo.Margin = new Thickness(Gap, 0, 0, 0);

        _row.Children.Add(_primary);
        _row.Children.Add(_echo);
        _row.RenderTransform = _shift;

        Content = _row;
        ClipToBounds = true;
        HorizontalContentAlignment = HorizontalAlignment.Left;

        SizeChanged += (_, _) => Restart();
    }

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(MarqueeText),
            new PropertyMetadata(string.Empty, OnTextChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MarqueeText marquee) return;
        var text = e.NewValue as string ?? string.Empty;
        marquee._primary.Text = text;
        marquee._echo.Text = text;
        marquee.Restart();
    }

    public void ApplyTypography(double fontSize, FontWeight weight, Brush foreground)
    {
        foreach (var block in new[] { _primary, _echo })
        {
            block.FontSize = fontSize;
            block.FontWeight = weight;
            block.Foreground = foreground;
        }
        Restart();
    }

    private void Restart()
    {
        _shift.BeginAnimation(TranslateTransform.XProperty, null);
        _shift.X = 0;

        double available = ActualWidth;
        if (available <= 0 || string.IsNullOrEmpty(_primary.Text))
        {
            _echo.Visibility = Visibility.Collapsed;
            OpacityMask = null;
            return;
        }

        _primary.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = _primary.DesiredSize.Width;

        if (textWidth <= available + 1)
        {
            // It fits. No scroll, no fade, nothing to cancel later.
            _echo.Visibility = Visibility.Collapsed;
            OpacityMask = null;
            return;
        }

        _echo.Visibility = Visibility.Visible;

        // Scrolling text that stops dead at a hard edge reads as broken layout; fading
        // says "there is more", which is what is actually happening.
        OpacityMask = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Colors.Transparent, 0),
                new(Colors.White, 0.045),
                new(Colors.White, 0.94),
                new(Colors.Transparent, 1)
            },
            new Point(0, 0.5), new Point(1, 0.5));

        double distance = textWidth + Gap;
        var slide = new DoubleAnimation
        {
            From = 0,
            To = -distance,
            Duration = TimeSpan.FromSeconds(distance / PointsPerSecond),
            BeginTime = TimeSpan.FromSeconds(StartDelaySeconds),
            RepeatBehavior = RepeatBehavior.Forever
        };
        _shift.BeginAnimation(TranslateTransform.XProperty, slide);
    }
}
