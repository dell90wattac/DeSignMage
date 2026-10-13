using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Lyt;

/// <summary>What <see cref="LytWriter"/> does with a soft shadow or glow (a shape drawn blurred): the legacy layout has no such object.</summary>
public enum LytSoftEffects
{
    /// <summary>The shape is written as an ordinary object in its fill color (hard edged, no blur, no transparency).</summary>
    Plain,

    /// <summary>The soft shadow or glow object is left out.</summary>
    Skip,
}

/// <summary>Settings of <see cref="LytWriter"/>. Every property has a working default.</summary>
public sealed record LytWriteOptions
{
    /// <summary>Title stored in the file (40 characters at most).</summary>
    public string Title { get; init; } = "";

    /// <summary>
    /// Preview picture stored at the start of the file: a complete JFIF JPEG file, about 400 pixels on its long side as legacy layouts
    /// store it. The writer has no image encoder of its own, so the caller renders it. Null writes a file without preview.
    /// </summary>
    public byte[]? ThumbnailJpeg { get; init; }

    /// <summary>Keep text that a TrueType font can draw as live text (editable in the legacy program). False writes every text as outlines.</summary>
    public bool LiveText { get; init; } = true;

    /// <summary>Write gradients and fades as legacy fills. False writes them as a plain color (the first stop).</summary>
    public bool Gradients { get; init; } = true;

    /// <summary>
    /// Both services are needed to turn a stroke into a shape of its own (the legacy objects have a fill and no stroke). Without them a
    /// stroke is dropped when the object has a fill, and an object that is only a stroke is written as an unfilled hairline outline.
    /// </summary>
    public IOffsetService? OffsetService { get; init; }

    public IBooleanService? BooleanService { get; init; }

    public LytSoftEffects SoftEffects { get; init; } = LytSoftEffects.Plain;

    /// <summary>
    /// Re-encodes a picture the writer cannot store as it is (only JPEG and PNG are stored unchanged) into a JPEG or PNG file.
    /// Null, or a null result, leaves the picture out.
    /// </summary>
    public Func<Asset, byte[]?>? ImageEncoder { get; init; }

    /// <summary>Also write objects that are hidden, or on a hidden layer. The default leaves them out, as printing and exporting do.</summary>
    public bool IncludeHidden { get; init; }
}

/// <summary>What <see cref="LytWriter"/> wrote, and everything it had to leave out or simplify.</summary>
public sealed class LytWriteResult
{
    /// <summary>Things that were simplified or left out; shown to the user, never fatal.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Size of the file in bytes.</summary>
    public long Length { get; internal set; }

    /// <summary>Objects (parts) in the file, not counting groups.</summary>
    public int PartCount { get; internal set; }

    public int GroupCount { get; internal set; }

    /// <summary>Texts written as live TrueType text.</summary>
    public int LiveTextCount { get; internal set; }

    /// <summary>Texts written as outlines because live text could not carry them.</summary>
    public int TextAsOutlinesCount { get; internal set; }

    public int PictureCount { get; internal set; }
    public int GradientCount { get; internal set; }
    public int FadeCount { get; internal set; }

    /// <summary>Strokes written as shapes of their own (a ring around the outline).</summary>
    public int StrokeShapeCount { get; internal set; }

    /// <summary>Soft shadows and glows written as plain objects (or left out, see <see cref="LytWriteOptions.SoftEffects"/>).</summary>
    public int SoftEffectCount { get; internal set; }

    /// <summary>Objects not written because they are hidden.</summary>
    public int HiddenSkippedCount { get; internal set; }

    /// <summary>Objects that could not be written at all (no outline, a picture without bytes, ...).</summary>
    public int SkippedCount { get; internal set; }
}
