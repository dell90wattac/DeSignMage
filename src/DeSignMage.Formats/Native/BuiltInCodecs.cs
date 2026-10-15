using System.Text.Json;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;

namespace DeSignMage.Formats.Native;

// One codec per object kind. To add a new object type: copy one of these, pick a NEW stable Kind string,
// and register it in DsmCodecRegistry.CreateDefault(). Only fields that are not the common ones go here.

internal sealed class SolidPaintCodec : PaintCodec<SolidPaint>
{
    public override string Type => "solid";
    protected override void WriteFields(SolidPaint paint, DsmWriteContext ctx) => ctx.Json.WriteColor("color", paint.Color);
    public override Paint Read(JsonElement e, DsmReadContext ctx) =>
        new SolidPaint(DsmJson.ReadColor(e.TryGet("color", out var c) ? c : throw new DsmFormatException("A solid fill needs a 'color'.")));
}

internal sealed class RectangleCodec : ObjectCodec<RectangleObject>
{
    public override string Kind => "rect";

    protected override void WriteFields(RectangleObject o, DsmWriteContext ctx)
    {
        ctx.Json.WriteNum("width", o.Width);
        ctx.Json.WriteNum("height", o.Height);
        if (o.CornerRadius != 0) ctx.Json.WriteNum("cornerRadius", o.CornerRadius);
    }

    protected override RectangleObject ReadFields(JsonElement e, DsmReadContext ctx) =>
        new(e.Num("width"), e.Num("height"), e.Num("cornerRadius"));
}

internal sealed class EllipseCodec : ObjectCodec<EllipseObject>
{
    public override string Kind => "ellipse";

    protected override void WriteFields(EllipseObject o, DsmWriteContext ctx)
    {
        ctx.Json.WriteNum("width", o.Width);
        ctx.Json.WriteNum("height", o.Height);
    }

    protected override EllipseObject ReadFields(JsonElement e, DsmReadContext ctx) => new(e.Num("width"), e.Num("height"));
}

internal sealed class PolygonCodec : ObjectCodec<PolygonObject>
{
    public override string Kind => "polygon";

    protected override void WriteFields(PolygonObject o, DsmWriteContext ctx)
    {
        ctx.Json.WriteNumber("sides", o.Sides);
        ctx.Json.WriteNum("radius", o.Radius);
        if (o.InnerRatio != 0) ctx.Json.WriteNum("innerRatio", o.InnerRatio);
        ctx.Json.WriteNum("startAngle", o.StartAngle);
    }

    protected override PolygonObject ReadFields(JsonElement e, DsmReadContext ctx) =>
        new(e.Int("sides", 5), e.Num("radius"), e.Num("innerRatio"), e.Num("startAngle", -Math.PI / 2));
}

internal sealed class ArrowCodec : ObjectCodec<ArrowObject>
{
    public override string Kind => "arrow";

    protected override void WriteFields(ArrowObject o, DsmWriteContext ctx)
    {
        ctx.Json.WriteNum("length", o.Length);
        ctx.Json.WriteNum("headWidth", o.HeadWidth);
        ctx.Json.WriteNum("headFraction", o.HeadFraction);
        ctx.Json.WriteNum("shaftFraction", o.ShaftFraction);
    }

    protected override ArrowObject ReadFields(JsonElement e, DsmReadContext ctx) =>
        new(e.Num("length"), e.Num("headWidth"), e.Num("headFraction", 0.4), e.Num("shaftFraction", 0.4));
}

internal sealed class PathCodec : ObjectCodec<PathObject>
{
    public override string Kind => "path";
    protected override void WriteFields(PathObject o, DsmWriteContext ctx) => ctx.Json.WritePath("path", o.Geometry);
    protected override PathObject ReadFields(JsonElement e, DsmReadContext ctx) => new(DsmJson.ReadPath(e.Obj("path")));
}

/// <summary>
/// A group (format 2): only its own properties (name, transform, the group around it). Its members say which group they belong to
/// (<c>group</c>), so the members can be anywhere in the drawing order. Format 1 files nested the members in <c>children</c>; those are still read.
/// </summary>
internal sealed class GroupCodec : ObjectCodec<GroupObject>
{
    public override string Kind => "group";
    protected override void WriteFields(GroupObject o, DsmWriteContext ctx) { }
    protected override GroupObject ReadFields(JsonElement e, DsmReadContext ctx) => new();

    protected override void ReadChildFields(GroupObject o, JsonElement e, DsmReadContext ctx)
    {
        if (e.TryGet("children", out var children)) ctx.ReadObjectsInto(o, children);   // format 1
    }
}

/// <summary>Combined object: its parts are complete solo objects, kept so Break Apart still works after reopening.</summary>
internal sealed class CombinedCodec : ObjectCodec<CombinedObject>
{
    public override string Kind => "combined";
    protected override void WriteFields(CombinedObject o, DsmWriteContext ctx) => ctx.WriteObjectList("parts", o.Parts);

    protected override CombinedObject ReadFields(JsonElement e, DsmReadContext ctx)
    {
        var parts = ctx.ReadDetached(e.Arr("parts"));
        if (parts.Count == 0) throw new DsmFormatException("A combined object has no readable parts.");
        return new CombinedObject(parts);
    }
}

/// <summary>
/// Live text: the string and everything that decides its look (font as requested, size, tracking, per-gap kerning,
/// alignment, line spacing). Glyph outlines are never stored; they come from the font. <c>fontMissing</c> /
/// <c>fontResolved</c> only record what the font service did when the file was written (for people reading the file
/// and for diagnostics); on load the real state is worked out again.
/// </summary>
internal sealed class TextCodec : ObjectCodec<TextObject>
{
    public override string Kind => "text";

    protected override void WriteFields(TextObject o, DsmWriteContext ctx)
    {
        var w = ctx.Json;
        w.WriteString("text", o.Text);
        w.WriteString("fontFamily", o.FontFamily);
        w.WriteNum("fontSize", o.FontSize);
        if (o.FontWeight != 400) w.WriteNumber("fontWeight", o.FontWeight);
        if (o.Italic) w.WriteBoolean("italic", true);
        if (o.Tracking != 0) w.WriteNum("tracking", o.Tracking);
        if (o.Kerning.Count > 0) w.WriteNumbers("kerning", o.Kerning);
        if (o.Alignment != TextAlignment.Left) w.WriteEnum("alignment", o.Alignment);
        if (o.LineSpacing != 1) w.WriteNum("lineSpacing", o.LineSpacing);
        if (o.IsFontMissing)
        {
            w.WriteBoolean("fontMissing", true);
            w.WriteString("fontResolved", o.ResolvedFontFamily);
        }
        if (o.OnPath is { } onPath) WriteOnPath(w, onPath);
    }

    /// <summary>
    /// Text on a Path: the path (in the text's own space) and how the text follows it. An optional field: a reader that does not know it shows the
    /// text straight, and nothing else changes.
    /// </summary>
    private static void WriteOnPath(System.Text.Json.Utf8JsonWriter w, TextOnPath p)
    {
        w.WriteStartObject("onPath");
        w.WritePath("path", p.Path);
        w.WriteNum("curveCompensation", p.CurveCompensation);
        if (p.Spacing != TextPathSpacing.Fit) w.WriteEnum("spacing", p.Spacing);
        if (p.StartOffset != 0) w.WriteNum("startOffset", p.StartOffset);
        if (p.Side != TextPathSide.Above) w.WriteEnum("side", p.Side);
        if (p.Flip) w.WriteBoolean("flip", true);
        if (p.Raise != 0) w.WriteNum("raise", p.Raise);
        w.WriteEndObject();
    }

    private static TextOnPath? ReadOnPath(JsonElement e, DsmReadContext ctx)
    {
        if (!e.TryGet("onPath", out var p) || !p.TryGet("path", out _)) return null;
        return new TextOnPath(DsmJson.ReadPath(p.Obj("path")))
        {
            CurveCompensation = p.Num("curveCompensation", TextOnPath.DefaultCompensation),
            Spacing = p.EnumOf("spacing", TextPathSpacing.Fit, ctx),
            StartOffset = p.Num("startOffset"),
            Side = p.EnumOf("side", TextPathSide.Above, ctx),
            Flip = p.Bool("flip"),
            Raise = p.Num("raise"),
        };
    }

    protected override TextObject ReadFields(JsonElement e, DsmReadContext ctx) =>
        new(e.Str("text") ?? "", e.Str("fontFamily") ?? "Arial", e.Num("fontSize", 12))
        {
            FontWeight = e.Int("fontWeight", 400),
            Italic = e.Bool("italic"),
            Tracking = e.Num("tracking"),
            Kerning = e.Doubles("kerning"),
            Alignment = e.EnumOf("alignment", TextAlignment.Left, ctx),
            LineSpacing = e.Num("lineSpacing", 1),
            OnPath = ReadOnPath(e, ctx),
        };
}

/// <summary>Placed bitmap: a frame plus the id of an embedded asset (the bytes are stored under assets/).</summary>
internal sealed class BitmapCodec : ObjectCodec<ImageObject>
{
    public override string Kind => "image";

    protected override void WriteFields(ImageObject o, DsmWriteContext ctx)
    {
        ctx.Json.WriteString("asset", o.AssetId);
        ctx.Json.WriteNum("width", o.Width);
        ctx.Json.WriteNum("height", o.Height);
        if (o.PixelWidth != 0) ctx.Json.WriteNumber("pixelWidth", o.PixelWidth);
        if (o.PixelHeight != 0) ctx.Json.WriteNumber("pixelHeight", o.PixelHeight);
        if (o.SourceName is { } source) ctx.Json.WriteString("sourceName", source);
        if (o.SourceAssetId is { } original && o.Recipe is { } recipe)
        {
            ctx.Json.WriteString("source", original);
            PictureRecipeJson.Write(ctx.Json, "recipe", recipe);
        }
    }

    protected override ImageObject ReadFields(JsonElement e, DsmReadContext ctx)
    {
        var stored = e.Str("asset") ?? throw new DsmFormatException("A bitmap has no 'asset'.");
        var id = ctx.ResolveAssetId(stored);
        if (ctx.Document.Assets.Get(id) is null) ctx.Warn($"A bitmap's picture data is missing from the file (asset {stored[..Math.Min(8, stored.Length)]}…); a placeholder is shown.");
        var picture = new ImageObject(id, e.Num("width"), e.Num("height"), e.Int("pixelWidth"), e.Int("pixelHeight"), e.Str("sourceName"));
        if (e.Str("source") is { } storedSource && e.TryGet("recipe", out var recipeJson))
        {
            var sourceId = ctx.ResolveAssetId(storedSource);
            if (ctx.Document.Assets.Get(sourceId) is null) ctx.Warn($"A bitmap's original picture is missing from the file (asset {storedSource[..Math.Min(8, storedSource.Length)]}…); it can no longer be edited from its original.");
            else
            {
                picture.SourceAssetId = sourceId;
                picture.Recipe = PictureRecipeJson.Read(recipeJson);
            }
        }
        return picture;
    }
}



