using DeSignMage.Core.Geometry;

namespace DeSignMage.Core.Imaging;

/// <summary>A whole-pixel rectangle (x, y from the top-left corner).</summary>
public readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>This rectangle moved and shrunk so it lies inside 0..<paramref name="width"/> x 0..<paramref name="height"/> and has at least one pixel.</summary>
    public RectI ClampedTo(int width, int height)
    {
        int w = Math.Clamp(Width, 1, Math.Max(1, width));
        int h = Math.Clamp(Height, 1, Math.Max(1, height));
        return new RectI(Math.Clamp(X, 0, Math.Max(0, width - w)), Math.Clamp(Y, 0, Math.Max(0, height - h)), w, h);
    }
}

/// <summary>
/// The Adjust tab of the Picture Editor: color corrections applied after cropping. Every number has a neutral value (<see cref="None"/>); the
/// math is in <see cref="AdjustMath"/>.
/// <list type="bullet">
/// <item>Levels (<see cref="LowR"/>..<see cref="HighB"/>): the black and white points per channel, 0..255. Auto levels sets them.</item>
/// <item>Balance (-100..100 per channel): shifts the mid-tones of that channel (shadows and highlights stay).</item>
/// <item><see cref="Gamma"/> (0.1..5, 1 = none): above 1 brightens the mid-tones.</item>
/// <item><see cref="Brightness"/> and <see cref="Contrast"/> (-100..100), <see cref="Saturation"/> (-100 = gray, 100 = twice as colorful), <see cref="Hue"/> (-180..180 degrees).</item>
/// </list>
/// </summary>
public sealed record PictureAdjust
{
    public static readonly PictureAdjust None = new();

    public double Brightness { get; init; }
    public double Contrast { get; init; }
    public double Gamma { get; init; } = 1;
    public double Saturation { get; init; }
    public double Hue { get; init; }
    public double BalanceR { get; init; }
    public double BalanceG { get; init; }
    public double BalanceB { get; init; }
    public byte LowR { get; init; }
    public byte LowG { get; init; }
    public byte LowB { get; init; }
    public byte HighR { get; init; } = 255;
    public byte HighG { get; init; } = 255;
    public byte HighB { get; init; } = 255;

    public bool LevelsAreDefault => LowR == 0 && LowG == 0 && LowB == 0 && HighR == 255 && HighG == 255 && HighB == 255;
    public bool BalanceIsDefault => BalanceR == 0 && BalanceG == 0 && BalanceB == 0;
    public bool ColorIsDefault => Saturation == 0 && Hue == 0;
    public bool IsIdentity =>
        Brightness == 0 && Contrast == 0 && Gamma == 1 && ColorIsDefault && BalanceIsDefault && LevelsAreDefault;

    public byte Low(int channel) => channel switch { 0 => LowR, 1 => LowG, _ => LowB };
    public byte High(int channel) => channel switch { 0 => HighR, 1 => HighG, _ => HighB };
    public double Balance(int channel) => channel switch { 0 => BalanceR, 1 => BalanceG, _ => BalanceB };
}

/// <summary>
/// The Effects tab of the Picture Editor, applied last (after the colors of <see cref="PictureAdjust"/>). Every number has an "off" value
/// (<see cref="None"/>). The order inside this step is fixed: noise reduction, blur, sharpen (the filters), then grayscale, sepia, invert
/// (the looks), then black &amp; white, posterize (the color reducers, so Vectorize traces few flat colors). Filter sizes are in pixels of the original;
/// the editor's preview scales them to its smaller copy.
/// </summary>
public sealed record PictureEffects
{
    public static readonly PictureEffects None = new();

    /// <summary>Despeckle: 0 = off, 1..100 (a 3 x 3 median filter blended in by this much; above 50 it is run twice).</summary>
    public int NoiseReduction { get; init; }

    /// <summary>Gaussian blur radius in pixels of the original, 0 = off, up to <see cref="MaxBlur"/>.</summary>
    public double Blur { get; init; }

    /// <summary>Unsharp mask amount: 0 = off, 1..100 (the difference to a 1-pixel blur is added back, up to twice at 100).</summary>
    public int Sharpen { get; init; }

    public bool Grayscale { get; init; }

    /// <summary>Warm brown tone: 0 = off, 1..100 = how much of the sepia tone replaces the colors.</summary>
    public int Sepia { get; init; }

    public bool Invert { get; init; }

    /// <summary>Black and white: 0 = off; 1..255 = pixels at least this light become white, the others black.</summary>
    public int Threshold { get; init; }

    /// <summary>Posterize: levels per color channel, 1 = off, 2..16.</summary>
    public int Posterize { get; init; } = 1;

    public const double MaxBlur = 50;
    public const int MaxPosterize = 16;

    public bool IsIdentity => NoiseReduction == 0 && Blur <= 0 && Sharpen == 0 && !Grayscale && Sepia == 0 && !Invert && Threshold == 0 && Posterize <= 1;

    /// <summary>The same effects with every number put into its range.</summary>
    public PictureEffects Clamped() => this with
    {
        NoiseReduction = Math.Clamp(NoiseReduction, 0, 100),
        Blur = Math.Clamp(Blur, 0, MaxBlur),
        Sharpen = Math.Clamp(Sharpen, 0, 100),
        Sepia = Math.Clamp(Sepia, 0, 100),
        Threshold = Math.Clamp(Threshold, 0, 255),
        Posterize = Math.Clamp(Posterize, 1, MaxPosterize),
    };
}

/// <summary>
/// What was done to a picture, kept next to its untouched original so every step stays adjustable (non-destructive editing). The steps are applied
/// in this fixed order, and what each one sees is the result of the one before:
/// <list type="number">
/// <item>Orientation: flip left-right (<see cref="Mirror"/>), then turn clockwise by <see cref="Quarter"/> quarter turns. Lossless.</item>
/// <item>Rotate (<see cref="Straighten"/>): any angle, on a canvas big enough to hold the turned picture (also what "straighten" by a level line sets).</item>
/// <item>Crop: a rectangle of that canvas.</item>
/// <item>Adjust: the colors (<see cref="PictureAdjust"/>).</item>
/// <item>Effects: filters, looks and color reducers (<see cref="PictureEffects"/>).</item>
/// </list>
/// Later phases add their own steps (transparency). All sizes are in pixels of the original.
/// </summary>
public sealed record PictureRecipe(int SourceWidth, int SourceHeight)
{
    /// <summary>The original flipped left-right before it is turned (the "flip horizontal" and "flip vertical" buttons fold into this and <see cref="Quarter"/>).</summary>
    public bool Mirror { get; init; }

    /// <summary>Clockwise quarter turns, 0..3, applied after <see cref="Mirror"/>.</summary>
    public int Quarter { get; init; }

    /// <summary>Degrees the picture is turned clockwise on top of the quarter turns, -180..180 (0 = not turned). Straightening by a level line sets this too.</summary>
    public double Straighten { get; init; }

    /// <summary>The part of the (turned) canvas that is kept; null = all of it.</summary>
    public RectI? Crop { get; init; }

    /// <summary>What is made transparent (after the crop, before the colors).</summary>
    public PictureTransparency Transparency { get; init; } = PictureTransparency.None;

    public PictureAdjust Adjust { get; init; } = PictureAdjust.None;

    public PictureEffects Effects { get; init; } = PictureEffects.None;

    /// <summary>True when the recipe changes nothing: the picture is its original.</summary>
    public bool IsIdentity => !Mirror && Quarter == 0 && Math.Abs(Straighten) < 1e-6 && Crop is null && Transparency.IsIdentity && Adjust.IsIdentity && Effects.IsIdentity;

    /// <summary>The original's size after the flip and quarter turns (width and height swap on an odd number of turns).</summary>
    public (int Width, int Height) OrientedSize => (Quarter & 1) == 0 ? (SourceWidth, SourceHeight) : (SourceHeight, SourceWidth);

    /// <summary>Size of the turned canvas (the oriented size when it is not turned further).</summary>
    public (int Width, int Height) CanvasSize
    {
        get
        {
            var (w, h) = OrientedSize;
            return PictureGeometry.CanvasSize(w, h, Straighten);
        }
    }

    /// <summary>The kept rectangle: the crop, limited to the canvas, or the whole canvas.</summary>
    public RectI EffectiveCrop
    {
        get
        {
            var (cw, ch) = CanvasSize;
            return Crop is { } c ? c.ClampedTo(cw, ch) : new RectI(0, 0, cw, ch);
        }
    }

    /// <summary>Pixels of the result.</summary>
    public (int Width, int Height) ResultSize => (EffectiveCrop.Width, EffectiveCrop.Height);

    /// <summary>The same recipe with a crop that equals the whole canvas written as "no crop", and every number in its range.</summary>
    public PictureRecipe Normalized()
    {
        var r = this with
        {
            Quarter = ((Quarter % 4) + 4) % 4,
            Straighten = Math.Abs(Straighten) < 1e-6 ? 0 : PictureGeometry.WrapAngle(Straighten),
            Effects = Effects.Clamped(),
            Transparency = Transparency.IsIdentity ? PictureTransparency.None : Transparency.Clamped(),
        };
        var (cw, ch) = r.CanvasSize;
        return r.Crop is { } c && c.ClampedTo(cw, ch) == new RectI(0, 0, cw, ch) ? r with { Crop = null } : r;
    }

    // ---- Rotate and flip buttons: they change the orientation and carry the crop (and the free angle) along, so the picture looks turned as a whole ----

    /// <summary>Turned a quarter turn clockwise, as the whole picture including its crop.</summary>
    public PictureRecipe RotatedRight() =>
        Reoriented(Mirror, (Quarter + 1) & 3, Straighten, c => c is { } r ? new RectI(CanvasSize.Height - r.Y - r.Height, r.X, r.Height, r.Width) : null);

    /// <summary>Turned a quarter turn counter-clockwise.</summary>
    public PictureRecipe RotatedLeft() =>
        Reoriented(Mirror, (Quarter + 3) & 3, Straighten, c => c is { } r ? new RectI(r.Y, CanvasSize.Width - r.X - r.Width, r.Height, r.Width) : null);

    /// <summary>Mirrored left-right as it is seen now.</summary>
    public PictureRecipe FlippedHorizontally() =>
        Reoriented(!Mirror, (4 - Quarter) & 3, -Straighten, c => c is { } r ? new RectI(CanvasSize.Width - r.X - r.Width, r.Y, r.Width, r.Height) : null);

    /// <summary>Mirrored top-bottom as it is seen now.</summary>
    public PictureRecipe FlippedVertically() =>
        Reoriented(!Mirror, (6 - Quarter) & 3, -Straighten, c => c is { } r ? new RectI(r.X, CanvasSize.Height - r.Y - r.Height, r.Width, r.Height) : null);

    /// <summary>The picture the way it was in the original (no flip, no quarter turns); the free angle and the crop go along, so what is kept stays the same part of the picture.</summary>
    public PictureRecipe InOriginalOrientation()
    {
        var r = this;
        while (r.Quarter != 0) r = r.RotatedLeft();
        return r.Mirror ? r.FlippedHorizontally() : r;
    }

    /// <summary>
    /// The same edits for a replacement original of another size (the picture was changed in another program): the crop and the cut-out outlines
    /// are scaled by the change of size, everything else stays. A replacement of the same size changes nothing.
    /// </summary>
    public PictureRecipe WithNewSource(int width, int height)
    {
        if (width == SourceWidth && height == SourceHeight) return this;
        var (oldW, oldH) = CanvasSize;
        var moved = this with
        {
            SourceWidth = Math.Max(1, width),
            SourceHeight = Math.Max(1, height),
            Transparency = Transparency.ScaledCuts((double)width / SourceWidth, (double)height / SourceHeight),
        };
        var (newW, newH) = moved.CanvasSize;
        if (Crop is { } c)
        {
            double kx = (double)newW / oldW, ky = (double)newH / oldH;
            moved = moved with { Crop = new RectI((int)Math.Round(c.X * kx), (int)Math.Round(c.Y * ky), Math.Max(1, (int)Math.Round(c.Width * kx)), Math.Max(1, (int)Math.Round(c.Height * ky))).ClampedTo(newW, newH) };
        }
        return moved.Normalized();
    }

    private PictureRecipe Reoriented(bool mirror, int quarter, double angle, Func<RectI?, RectI?> crop)
    {
        // The crop is mapped on the canvas as it is now (the canvas of the new orientation is the same pixels, turned); the angle commutes with a quarter turn.
        var moved = crop(Crop is null ? null : EffectiveCrop);
        return (this with { Mirror = mirror, Quarter = quarter, Straighten = angle, Crop = moved }).Normalized();
    }
}

/// <summary>The geometry of a recipe: the turned canvas, the straighten angle from a drawn line, the largest crop without empty corners, the frame on the page.</summary>
public static class PictureGeometry
{
    public const double MaxAngle = 180;

    /// <summary><paramref name="degrees"/> turned into the range -180..180.</summary>
    public static double WrapAngle(double degrees)
    {
        double a = degrees % 360;
        if (a > 180) a -= 360;
        else if (a < -180) a += 360;
        return a;
    }

    /// <summary>The canvas that holds a <paramref name="width"/> x <paramref name="height"/> picture turned by <paramref name="degrees"/> (the same size when not turned).</summary>
    public static (int Width, int Height) CanvasSize(int width, int height, double degrees)
    {
        if (Math.Abs(degrees) < 1e-6) return (width, height);
        double a = degrees * Math.PI / 180, c = Math.Abs(Math.Cos(a)), s = Math.Abs(Math.Sin(a));
        // A hair under the exact size is rounded away, so turning by exactly 90 or 180 does not add a pixel.
        return ((int)Math.Ceiling(width * c + height * s - 1e-6), (int)Math.Ceiling(width * s + height * c - 1e-6));
    }

    /// <summary>
    /// The angle to straighten by so that the line from <paramref name="a"/> to <paramref name="b"/> (drawn on the view of the picture as it is
    /// now, screen directions: x right, y down) becomes level, or plumb when it is steeper than 45 degrees. The new total angle, wrapped into -180..180.
    /// </summary>
    public static double StraightenFromLine(Vec2 a, Vec2 b, double currentDegrees)
    {
        var d = b - a;
        if (d.LengthSquared < 1e-12) return currentDegrees;
        double line = Math.Atan2(d.Y, d.X) * 180 / Math.PI;     // clockwise-positive on screen
        while (line > 45) line -= 90;                            // a plumb line counts as a level one turned a quarter
        while (line <= -45) line += 90;
        return WrapAngle(currentDegrees - line);
    }

    /// <summary>
    /// The largest rectangle with the proportions of the picture that fits inside the turned picture without any of its empty corners, centered
    /// on the canvas (what "remove the empty corners" crops to).
    /// </summary>
    public static RectI InscribedCrop(int width, int height, double degrees)
    {
        var (cw, ch) = CanvasSize(width, height, degrees);
        if (Math.Abs(degrees) < 1e-6) return new RectI(0, 0, width, height);
        // Only the part of the angle beyond a whole number of quarter turns leaves corners: a picture turned by 90 degrees is upright again.
        double quarters = Math.Round(degrees / 90);
        double rest = degrees - quarters * 90;
        if (Math.Abs(rest) < 1e-6) return new RectI(0, 0, cw, ch);
        var (pw, ph) = ((int)Math.Abs(quarters) % 2 == 0) ? (width, height) : (height, width);   // the proportions of the picture as it stands after the quarter turns
        double a = Math.Abs(rest) * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        double scale = Math.Min(pw / (pw * c + ph * s), ph / (pw * s + ph * c));
        // Two pixels in from the exact fit: the edge of the turned picture is smoothed over a pixel, and no half-transparent fringe may be left in the corners.
        int w = Math.Max(1, (int)Math.Floor(pw * scale) - 2), h = Math.Max(1, (int)Math.Floor(ph * scale) - 2);
        return new RectI((cw - w) / 2, (ch - h) / 2, w, h);
    }

    /// <summary>
    /// A point of the original picture (pixels, from its top-left corner, fractions allowed) as it lies on the canvas of the recipe: after the flip, the quarter
    /// turns and the free rotation. The canvas is what the crop frame sits on.
    /// </summary>
    public static Vec2 SourceToCanvas(PictureRecipe recipe, Vec2 p)
    {
        double w = recipe.SourceWidth, h = recipe.SourceHeight, x = p.X, y = p.Y;
        if (recipe.Mirror) x = w - x;
        for (int i = 0; i < ((recipe.Quarter % 4) + 4) % 4; i++)
        {
            (x, y) = (h - y, x);
            (w, h) = (h, w);
        }
        if (Math.Abs(recipe.Straighten) < 1e-6) return new Vec2(x, y);
        var (cw, ch) = CanvasSize((int)w, (int)h, recipe.Straighten);
        double a = recipe.Straighten * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a), dx = x - w / 2, dy = y - h / 2;
        return new Vec2(cw / 2.0 + dx * c - dy * s, ch / 2.0 + dx * s + dy * c);
    }

    /// <summary>The inverse of <see cref="SourceToCanvas"/>: a point of the canvas as a point of the original picture.</summary>
    public static Vec2 CanvasToSource(PictureRecipe recipe, Vec2 p)
    {
        var (w0, h0) = recipe.OrientedSize;
        double w = w0, h = h0, x = p.X, y = p.Y;
        if (Math.Abs(recipe.Straighten) >= 1e-6)
        {
            var (cw, ch) = CanvasSize(w0, h0, recipe.Straighten);
            double a = recipe.Straighten * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a), dx = x - cw / 2.0, dy = y - ch / 2.0;
            x = w / 2 + dx * c + dy * s;
            y = h / 2 - dx * s + dy * c;
        }
        for (int i = 0; i < ((recipe.Quarter % 4) + 4) % 4; i++)
        {
            (x, y) = (y, w - x);
            (w, h) = (h, w);
        }
        if (recipe.Mirror) x = w - x;
        return new Vec2(x, y);
    }

    /// <summary>
    /// Where the edited picture sits on the page. The original frame (<paramref name="baseWidth"/> x <paramref name="baseHeight"/> points, placed
    /// by <paramref name="baseTransform"/>) is turned by the quarter turns about its center (width and height swap, as when the pixels are rotated),
    /// then turned by the free angle like the pixels are and cropped: the kept area stays exactly where it was on the page and keeps its scale
    /// (so the picture's dpi is unchanged), and a picture that is turned without cropping stays centered.
    /// </summary>
    public static ImageFrame FrameFor(PictureRecipe recipe, double baseWidth, double baseHeight, Affine2 baseTransform)
    {
        var oriented = (recipe.Quarter & 1) == 0 ? new ImageFrame(baseWidth, baseHeight, baseTransform) : ImageFrames.Rotate90(baseWidth, baseHeight, baseTransform);
        var (ow, oh) = recipe.OrientedSize;
        double dx = oriented.Width / ow, dy = oriented.Height / oh;   // points per pixel
        var (cw, ch) = recipe.CanvasSize;
        var crop = recipe.EffectiveCrop;
        double canvasX = (oriented.Width - cw * dx) / 2, canvasY = (oriented.Height - ch * dy) / 2;    // the canvas frame in the oriented frame
        return new ImageFrame(crop.Width * dx, crop.Height * dy,
            Affine2.Translation(canvasX + crop.X * dx, canvasY + crop.Y * dy) * oriented.Transform);
    }

    /// <summary>The frame of the original picture, given the current frame of an edited picture (moved, scaled or turned since): the inverse of <see cref="FrameFor"/>.</summary>
    public static ImageFrame BaseFrameFor(PictureRecipe recipe, double currentWidth, double currentHeight, int resultPixelWidth, int resultPixelHeight, Affine2 currentTransform)
    {
        double dx = currentWidth / resultPixelWidth, dy = currentHeight / resultPixelHeight;
        var (cw, ch) = recipe.CanvasSize;
        var (ow, oh) = recipe.OrientedSize;
        var crop = recipe.EffectiveCrop;
        double orientedWidth = ow * dx, orientedHeight = oh * dy;
        double canvasX = (orientedWidth - cw * dx) / 2, canvasY = (orientedHeight - ch * dy) / 2;
        var oriented = new ImageFrame(orientedWidth, orientedHeight, Affine2.Translation(-(canvasX + crop.X * dx), -(canvasY + crop.Y * dy)) * currentTransform);
        // Turning a frame by a quarter is its own inverse.
        return (recipe.Quarter & 1) == 0 ? oriented : ImageFrames.Rotate90(oriented.Width, oriented.Height, oriented.Transform);
    }
}

/// <summary>The per-pixel color math of <see cref="PictureEffects"/> (grayscale, sepia, invert, black and white, posterize), in one place so the editor's fast loop and the tests agree.</summary>
public static class EffectMath
{
    /// <summary>The lightness of a color, 0..255 (the same weights as the saturation matrix).</summary>
    public static double Luma(int r, int g, int b) => 0.213 * r + 0.715 * g + 0.072 * b;

    /// <summary>The colors of one pixel after grayscale, sepia, invert, threshold and posterize, in that order (the filters are not part of this).</summary>
    public static (byte R, byte G, byte B) Look(PictureEffects e, byte r, byte g, byte b)
    {
        double dr = r, dg = g, db = b;
        if (e.Grayscale) { double y = Luma(r, g, b); dr = dg = db = y; }
        if (e.Sepia > 0)
        {
            double y = Luma((int)Math.Round(dr), (int)Math.Round(dg), (int)Math.Round(db)), k = e.Sepia / 100.0;
            dr += (Math.Min(255, y * 1.08) - dr) * k;
            dg += (y * 0.88 - dg) * k;
            db += (y * 0.66 - db) * k;
        }
        if (e.Invert) { dr = 255 - dr; dg = 255 - dg; db = 255 - db; }
        if (e.Threshold > 0)
        {
            double y = Luma((int)Math.Round(dr), (int)Math.Round(dg), (int)Math.Round(db));
            dr = dg = db = y >= e.Threshold ? 255 : 0;
        }
        if (e.Posterize > 1)
        {
            dr = Levels(dr, e.Posterize); dg = Levels(dg, e.Posterize); db = Levels(db, e.Posterize);
        }
        return (AdjustMath.To8(dr), AdjustMath.To8(dg), AdjustMath.To8(db));
    }

    /// <summary>One channel value snapped to <paramref name="levels"/> evenly spaced values between 0 and 255.</summary>
    public static double Levels(double v, int levels)
    {
        double steps = levels - 1;
        return Math.Round(Math.Clamp(v, 0, 255) / 255 * steps) * 255 / steps;
    }

    /// <summary>True when the per-pixel looks (not the filters) change anything.</summary>
    public static bool HasLook(PictureEffects e) => e.Grayscale || e.Sepia > 0 || e.Invert || e.Threshold > 0 || e.Posterize > 1;

    /// <summary>True when one of the filters (noise reduction, blur, sharpen) is on.</summary>
    public static bool HasFilter(PictureEffects e) => e.NoiseReduction > 0 || e.Blur > 0 || e.Sharpen > 0;
}

/// <summary>The color math of <see cref="PictureAdjust"/>, in one place so the editor's fast pixel loop and the tests agree.</summary>
public static class AdjustMath
{
    private const double Luma709R = 0.213, Luma709G = 0.715, Luma709B = 0.072;

    /// <summary>
    /// A 256-entry curve for one channel (0 red, 1 green, 2 blue): levels, then the channel's balance, gamma, brightness and contrast, each
    /// limited to 0..1 before the next one.
    /// </summary>
    public static byte[] Curve(PictureAdjust a, int channel)
    {
        var table = new byte[256];
        double lo = a.Low(channel) / 255.0, hi = a.High(channel) / 255.0;
        if (hi <= lo + 1.0 / 255) hi = Math.Min(1, lo + 1.0 / 255);
        double balance = a.Balance(channel) / 100.0 * 0.25;
        double invGamma = 1.0 / Math.Clamp(a.Gamma, 0.1, 10);
        double brightness = a.Brightness / 100.0;
        double contrast = a.Contrast >= 0 ? 1.0 / (1.0 - Math.Min(a.Contrast, 100) / 100.0 * 0.98) : 1.0 + Math.Max(a.Contrast, -100) / 100.0;
        for (int i = 0; i < 256; i++)
        {
            double v = Math.Clamp((i / 255.0 - lo) / (hi - lo), 0, 1);               // levels
            v = Math.Clamp(v + balance * 4 * v * (1 - v), 0, 1);                     // balance: most at the mid-tones
            v = Math.Pow(v, invGamma);                                               // gamma
            v = Math.Clamp(v + brightness, 0, 1);                                    // brightness
            v = Math.Clamp((v - 0.5) * contrast + 0.5, 0, 1);                        // contrast about the middle gray
            table[i] = (byte)Math.Round(v * 255);
        }
        return table;
    }

    /// <summary>
    /// The 3 x 3 color matrix (rows R, G, B; columns R, G, B) for saturation and then hue, so one multiplication does both. The identity when both are neutral.
    /// Saturation -100 gives gray, 100 twice the color; hue turns the colors about the gray axis.
    /// </summary>
    public static double[] ColorMatrix(PictureAdjust a)
    {
        double k = 1 + Math.Clamp(a.Saturation, -100, 100) / 100.0;
        double[] sat =
        [
            Luma709R * (1 - k) + k, Luma709G * (1 - k), Luma709B * (1 - k),
            Luma709R * (1 - k), Luma709G * (1 - k) + k, Luma709B * (1 - k),
            Luma709R * (1 - k), Luma709G * (1 - k), Luma709B * (1 - k) + k,
        ];
        if (a.Hue == 0) return sat;
        double t = a.Hue * Math.PI / 180, c = Math.Cos(t), s = Math.Sin(t);
        double[] hue =
        [
            Luma709R + c * (1 - Luma709R) - s * Luma709R, Luma709G - c * Luma709G - s * Luma709G, Luma709B - c * Luma709B + s * (1 - Luma709B),
            Luma709R - c * Luma709R + s * 0.143, Luma709G + c * (1 - Luma709G) + s * 0.140, Luma709B - c * Luma709B - s * 0.283,
            Luma709R - c * Luma709R - s * (1 - Luma709R), Luma709G - c * Luma709G + s * Luma709G, Luma709B + c * (1 - Luma709B) + s * Luma709B,
        ];
        return Multiply(hue, sat);   // saturation first, then hue
    }

    private static double[] Multiply(double[] m, double[] n)
    {
        var r = new double[9];
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
                r[row * 3 + col] = m[row * 3] * n[col] + m[row * 3 + 1] * n[3 + col] + m[row * 3 + 2] * n[6 + col];
        return r;
    }

    /// <summary>The reference for one pixel (the fast loop in the editor must give the same result).</summary>
    public static (byte R, byte G, byte B) Apply(PictureAdjust a, byte r, byte g, byte b)
    {
        byte cr = Curve(a, 0)[r], cg = Curve(a, 1)[g], cb = Curve(a, 2)[b];
        if (a.ColorIsDefault) return (cr, cg, cb);
        var m = ColorMatrix(a);
        return (To8(m[0] * cr + m[1] * cg + m[2] * cb), To8(m[3] * cr + m[4] * cg + m[5] * cb), To8(m[6] * cr + m[7] * cg + m[8] * cb));
    }

    public static byte To8(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    /// <summary>
    /// Auto levels from per-channel histograms (256 counts each, of the pixels that are visible): the black point is where the darkest
    /// <paramref name="clip"/> (default 0.5 %) of the pixels end, the white point where the lightest 0.5 % begin, per channel. A channel with almost no range
    /// is left alone. Returns the adjust with the levels set and everything else as given.
    /// </summary>
    public static PictureAdjust AutoLevels(PictureAdjust current, long[][] histograms, double clip = 0.005)
    {
        var low = new byte[3];
        var high = new byte[3] { 255, 255, 255 };
        for (int c = 0; c < 3; c++)
        {
            long total = histograms[c].Sum();
            if (total <= 0) continue;
            long cut = (long)Math.Floor(total * clip), run = 0;
            int lo = 0, hi = 255;
            for (int i = 0; i < 256; i++) { run += histograms[c][i]; if (run > cut) { lo = i; break; } }
            run = 0;
            for (int i = 255; i >= 0; i--) { run += histograms[c][i]; if (run > cut) { hi = i; break; } }
            if (hi - lo < 8) continue;
            low[c] = (byte)lo;
            high[c] = (byte)hi;
        }
        return current with { LowR = low[0], LowG = low[1], LowB = low[2], HighR = high[0], HighG = high[1], HighB = high[2] };
    }
}
