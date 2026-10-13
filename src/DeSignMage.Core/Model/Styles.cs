using System.Globalization;

namespace DeSignMage.Core.Model;

/// <summary>How a color was specified. The display RGB of a CMYK color is derived from its components.</summary>
public enum ColorModel : byte { Rgb, Cmyk }

/// <summary>CMYK components in percent (0..100), as palettes and layouts store them.</summary>
public readonly record struct Cmyk(double C, double M, double Y, double K);

/// <summary>
/// sRGB color with alpha that keeps its identity (architecture D7): the vinyl/spot <see cref="Name"/> (e.g. "Golden Yellow"),
/// the <see cref="Vendor"/> or series it belongs to ("Oracal 651") and the <see cref="Model"/> it was specified in. A
/// CMYK color also keeps its <see cref="Cmyk"/> components; R/G/B are then the on-screen conversion.
/// Two colors with the same RGB but different names, vendors or models are different colors.
/// </summary>
public readonly record struct DocColor(
    byte R, byte G, byte B, byte A = 255, string? Name = null,
    string? Vendor = null, ColorModel Model = ColorModel.Rgb, Cmyk? Cmyk = null)
{
    public static readonly DocColor Black = new(0, 0, 0);
    public static readonly DocColor White = new(255, 255, 255);
    public static readonly DocColor Transparent = new(0, 0, 0, 0);

    /// <summary>A CMYK color (percent components); R/G/B get the display conversion.</summary>
    public static DocColor FromCmyk(double c, double m, double y, double k, string? name = null, string? vendor = null)
    {
        c = Math.Clamp(c, 0, 100); m = Math.Clamp(m, 0, 100); y = Math.Clamp(y, 0, 100); k = Math.Clamp(k, 0, 100);
        var (r, g, b) = ColorMath.CmykToRgb(c, m, y, k);
        return new DocColor(r, g, b, 255, name, vendor, ColorModel.Cmyk, new Cmyk(c, m, y, k));
    }

    /// <summary>The CMYK components: the stored ones for a CMYK color, else a plain conversion of R/G/B.</summary>
    public Cmyk ToCmyk() => Cmyk ?? ColorMath.RgbToCmyk(R, G, B);

    /// <summary>Hue (0..360), saturation and value (0..100).</summary>
    public (double H, double S, double V) ToHsv() => ColorMath.RgbToHsv(R, G, B);

    /// <summary>Same color without its identity (name, vendor), as after the user edits it by hand. Keeps the model.</summary>
    public DocColor Anonymous() => this with { Name = null, Vendor = null };

    /// <summary>The name shown to the user: "Name" or "Vendor Name"; null for unnamed colors.</summary>
    public string? DisplayName => string.IsNullOrWhiteSpace(Name) ? null
        : string.IsNullOrWhiteSpace(Vendor) ? Name : $"{Vendor} {Name}";

    /// <summary>Parses "#RRGGBB" or "#AARRGGBB".</summary>
    public static DocColor FromHex(string hex, string? name = null)
    {
        var s = hex.TrimStart('#');
        uint v = uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return s.Length switch
        {
            6 => new((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255, name),
            8 => new((byte)(v >> 16), (byte)(v >> 8), (byte)v, (byte)(v >> 24), name),
            _ => throw new FormatException($"Bad color '{hex}'."),
        };
    }

    public string ToHex() => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{A:X2}{R:X2}{G:X2}{B:X2}";
    public override string ToString() => Name is null ? ToHex() : $"{Name} ({ToHex()})";
}

/// <summary>How an area or outline is painted. Gradients, image fills, etc. are added as subclasses.</summary>
public abstract record Paint;

public sealed record SolidPaint(DocColor Color) : Paint;

public enum LineJoin : byte { Miter, Round, Bevel }
public enum LineCap : byte { Butt, Round, Square }

public sealed record StrokeStyle(Paint Paint, double Width, LineJoin Join = LineJoin.Miter, LineCap Cap = LineCap.Butt, double MiterLimit = 4)
{
    public static StrokeStyle Solid(DocColor color, double width) => new(new SolidPaint(color), width);
}

/// <summary>Which soft effect made a <see cref="BlurStyle"/> object.</summary>
public enum SoftKind : byte { SoftShadow, Glow }

/// <summary>
/// "Soft paint" of a Soft Shadow or Glow object (R8): the object's shape, drawn blurred, in its fill color times <see cref="Opacity"/>. Nothing is
/// rasterized in the document; the renderer blurs the vector shape when it draws (<c>SoftLayers</c>). The blur is
/// <c>0.068 × Softness × Reference</c> (a Gaussian sigma in the object's own units; <see cref="Reference"/> is the height of the source the
/// effect was made from, in this object's units, so the blur scales with the object when it is scaled).
/// <para>The rest of the fields are what the effect was made with (shift, cast shadow slant and height, inset or glow thickness), kept so
/// the tool can open on the source again and change them. Hs/Vs are in document points with Vs positive UP (the layout's convention; the
/// default shadow is down and to the right: Hs > 0, Vs &lt; 0). <see cref="Inset"/> is a fraction of the source height, negative = grows
/// (a glow's thickness is stored as a negative inset, as the layout format does).</para>
/// </summary>
public sealed record BlurStyle(SoftKind Kind, double Opacity, double Softness, double Reference,
    double Hs = 0, double Vs = 0, double Slant = 0, double HeightScale = 1, double Inset = 0)
{
    /// <summary>Gaussian sigma per unit of (Softness × Reference): the legacy program's stack blur of radius (H/3 × Softness)/2 is a triangle kernel of sigma about 0.41 × radius.</summary>
    public const double SigmaPerSoftness = 0.068;

    /// <summary>Sigma of the blur in this object's own units.</summary>
    public double Sigma => SigmaPerSoftness * Math.Max(Softness, 0) * Math.Max(Reference, 0);
}

/// <summary>
/// Feather (R8): the object's own paint fades out at its edge. <see cref="Amount"/> is a fraction of the object's smaller side (the width or the
/// height of its bounds; for live text the height), <see cref="Inset"/> (0..1) pulls the 50 % contour of the fade inwards so a hard-edged picture loses its
/// edge. 0 amount = off. Drawn by <c>SoftLayers</c> from the object's own coverage; nothing is rasterized in the document.
/// </summary>
public sealed record FeatherStyle(double Amount, double Inset)
{
    public const double SigmaPerPixel = 0.164, ErodeWidthPerPixel = 1.5;
}

/// <summary>Fill and stroke of an object, plus the soft effects that change how it is drawn. Immutable; change by replacing the whole style (one undo step).</summary>
public sealed record ObjectStyle(Paint? Fill, StrokeStyle? Stroke, BlurStyle? Blur = null, FeatherStyle? Feather = null)
{
    public static readonly ObjectStyle None = new(null, null);
    public static ObjectStyle FillOnly(DocColor c) => new(new SolidPaint(c), null);

    public ObjectStyle WithFill(Paint? fill) => this with { Fill = fill };
    public ObjectStyle WithStroke(StrokeStyle? stroke) => this with { Stroke = stroke };
    public ObjectStyle WithBlur(BlurStyle? blur) => this with { Blur = blur };
    public ObjectStyle WithFeather(FeatherStyle? feather) => this with { Feather = feather is { Amount: > 0 } ? feather : null };

    /// <summary>True when something other than fill and stroke is set (the file writer needs to know).</summary>
    public bool HasSoftEffects => Blur is not null || Feather is not null;

    /// <summary>Half the stroke width, i.e. how far paint extends beyond the geometry.</summary>
    public double StrokeOutset => Stroke is null ? 0 : Stroke.Width / 2;
}
