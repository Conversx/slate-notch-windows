using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Slate.Support;

/// <summary>
/// One vivid colour to represent a piece of artwork, used for the hover glow and the
/// panel tint.
/// </summary>
/// <remarks>
/// A plain average gives muddy greys, so pixels are weighted by their own saturation —
/// a colourful minority beats a grey majority — and the result is then forced up to a
/// usable saturation and brightness.
/// </remarks>
public static class AccentExtractor
{
    public static readonly Color Fallback = Color.FromRgb(107, 143, 255);

    public static Color From(BitmapSource source)
    {
        try
        {
            const int side = 12;
            var scale = new ScaleTransform(
                side / (double)source.PixelWidth,
                side / (double)source.PixelHeight);
            var scaled = new TransformedBitmap(source, scale);
            var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);

            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            if (w == 0 || h == 0) return Fallback;

            int stride = w * 4;
            var pixels = new byte[stride * h];
            bgra.CopyPixels(pixels, stride, 0);

            double r = 0, g = 0, b = 0, weight = 0;
            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                double pb = pixels[i] / 255.0;
                double pg = pixels[i + 1] / 255.0;
                double pr = pixels[i + 2] / 255.0;
                double a = pixels[i + 3] / 255.0;
                if (a < 0.5) continue;

                double max = Math.Max(pr, Math.Max(pg, pb));
                double min = Math.Min(pr, Math.Min(pg, pb));
                double saturation = max <= 0 ? 0 : (max - min) / max;

                double wgt = saturation + 0.08;
                r += pr * wgt; g += pg * wgt; b += pb * wgt; weight += wgt;
            }

            if (weight <= 0) return Fallback;
            return Boost(r / weight, g / weight, b / weight);
        }
        catch
        {
            return Fallback;
        }
    }

    private static Color Boost(double r, double g, double b)
    {
        RgbToHsv(r, g, b, out var h, out var s, out var v);
        s = Math.Clamp(Math.Max(0.55, s * 1.5), 0, 1);
        v = Math.Clamp(Math.Max(0.65, v * 1.25), 0, 1);
        HsvToRgb(h, s, v, out r, out g, out b);
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private static void RgbToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        v = max;
        double d = max - min;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0) { h = 0; return; }
        if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
        else if (max == g) h = ((b - r) / d + 2) / 6;
        else h = ((r - g) / d + 4) / 6;
    }

    private static void HsvToRgb(double h, double s, double v, out double r, out double g, out double b)
    {
        int i = (int)Math.Floor(h * 6) % 6;
        double f = h * 6 - Math.Floor(h * 6);
        double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        (r, g, b) = i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q)
        };
    }
}
