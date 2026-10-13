using System.Diagnostics;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;
using SkiaSharp;

namespace DeSignMage.Rendering;

/// <summary>
/// Turns the document into Skia pictures. Must be used from one thread (the UI thread).
/// <list type="bullet">
/// <item>The document (minus objects being dragged) is recorded once per <see cref="Document.Revision"/> into a
/// picture in document coordinates, so pan/zoom only replays it with a different matrix.</item>
/// <item>Objects being dragged are recorded every frame into a small separate picture.</item>
/// <item>World-space SKPaths are cached per object, keyed on (object version, world transform).</item>
/// </list>
/// </summary>
public sealed class SceneRenderer : IDisposable
{
    private sealed class CachedPath(int version, Affine2 world, SKPath path)
    {
        public int Version = version;
        public Affine2 World = world;
        public SKPath Path = path;
        public int LastUsed;
    }

    private readonly Dictionary<ObjectId, CachedPath> _paths = [];
    private readonly Dictionary<string, ImageSource> _images = [];
    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private readonly SKPaint _imagePaint = new() { IsAntialias = true };
    private int _generation;

    // Soft paint (Soft Shadow, Glow, Feather): blurred masks made from the objects' own shapes, kept per object (SoftLayers.cs).
    private readonly SoftLayers _soft = new();
    private DocObject? _feathering;   // the object whose feathered layer is being drawn (so drawing it inside does not feather it again)

    /// <summary>How many soft masks (blurred shapes for shadows, glows and feathers) were made so far (tests, profiling).</summary>
    public int SoftMasksMade => _soft.MasksMade;

    /// <summary>How many soft masks are kept right now.</summary>
    public int SoftMasksKept => _soft.Cached;

    /// <summary>
    /// 0 (the default): a feathered object is drawn into a layer that is multiplied by its mask, which a bitmap canvas does exactly. Above 0: it is drawn into a
    /// bitmap of this many dots per inch that is placed as one picture, for a PDF canvas (a PDF cannot multiply by a mask).
    /// </summary>
    public double FeatherPixelDpi { get; set; }

    private SharedPicture? _docPicture;
    private long _docRevision = -1;
    private IReadOnlyList<DocObject>? _docExcluded;

    private readonly Document _doc;

    public SceneRenderer(Document document) => _doc = document;

    /// <summary>
    /// Screen DIPs per document point (<see cref="Viewport.Zoom"/>). Pictures are drawn smoothed, mipmapped, from a small
    /// copy, or as crisp pixels depending on it, so the document picture is re-recorded when it moves to another
    /// half-octave while the document holds pictures. 0 (the default) means drawing for export: always full quality.
    /// </summary>
    public double ViewScale { get; set; }

    private int ScaleBucket => ViewScale > 0 ? (int)Math.Floor(Math.Log2(ViewScale) * 2) : int.MinValue;
    private int _docScaleBucket;
    private bool _docHasImages;

    /// <summary>Milliseconds spent on the last full document recording.</summary>
    public double LastRecordMs { get; private set; }

    /// <summary>
    /// "Show Fill Colors" off: draw only vectors, as 1-pixel lines (border loops blue, interior red, broken black).
    /// </summary>
    public bool Wireframe { get; set; }
    private bool _docWireframe;

    public static readonly SKColor BorderColor = CanvasTheme.Light.Border;
    public static readonly SKColor InteriorColor = CanvasTheme.Light.Interior;
    public static readonly SKColor OpenColor = CanvasTheme.Light.Open;

    public static SKColor ColorOf(LoopKind kind) => CanvasTheme.Light.ColorOf(kind);

    /// <summary>
    /// The colors the outline view draws with (R19). Only the on-screen canvas sets another theme; every other use (export, thumbnails,
    /// the clipboard) keeps the default, so Dark Canvas never reaches a file. Changing it re-records the document picture.
    /// </summary>
    public CanvasTheme Theme { get; set; } = CanvasTheme.Light;
    private CanvasTheme _docTheme = CanvasTheme.Light;

    /// <summary>
    /// The document picture, rebuilt only when the document or the excluded set changed.
    /// The caller owns one reference and must <see cref="SharedPicture.Release"/> it.
    /// </summary>
    public SharedPicture GetDocumentPicture(IReadOnlyList<DocObject>? excluded)
    {
        if (_docPicture is null || _docRevision != _doc.Revision || !SameObjects(_docExcluded, excluded) || _docWireframe != Wireframe ||
            !ReferenceEquals(_docTheme, Theme) || (_docHasImages && _docScaleBucket != ScaleBucket))
        {
            _docWireframe = Wireframe;
            _docTheme = Theme;
            _docHasImages = false;
            _docScaleBucket = ScaleBucket;
            var watch = Stopwatch.StartNew();
            var pic = RecordDocument(excluded is { Count: > 0 } ? new HashSet<DocObject>(excluded) : null);
            LastRecordMs = watch.Elapsed.TotalMilliseconds;
            _docPicture?.Release();
            _docPicture = new SharedPicture(pic);
            _docRevision = _doc.Revision;
            _docExcluded = excluded?.ToArray();
        }
        return _docPicture.AddRef();
    }

    /// <summary>
    /// Records objects displaced by <paramref name="worldDelta"/> (drag preview), in document coordinates. A
    /// dragged object is drawn as outlines only (the "Show Fill Colors" off look), so whatever lies underneath stays
    /// visible through it until the drop.
    /// </summary>
    public SharedPicture RecordPreview(IEnumerable<DocObject> objects, Affine2 worldDelta)
    {
        using var rec = new SKPictureRecorder();
        var canvas = rec.BeginRecording(SKRect.Create(-1e7f, -1e7f, 2e7f, 2e7f));
        // The loops of each dragged object are classified (border / interior) where the object stands and kept; every frame of the
        // drag only moves them with the canvas matrix. Classification does not change when the shapes are moved, scaled or turned,
        // and redoing it per frame cost 10 ms for a group of a hundred shapes.
        canvas.Concat(worldDelta.ToSK());
        foreach (var o in objects)
            if (o.Visible) DrawOutlines(canvas, o, Affine2.Identity, cache: true);
        return new SharedPicture(rec.EndRecording());
    }

    private SKPicture RecordDocument(HashSet<DocObject>? exclude)
    {
        _generation++;
        using var rec = new SKPictureRecorder();
        var cull = _doc.GetContentBounds().Union(_doc.Page.Bounds).Inflate(10_000).ToSK();
        var canvas = rec.BeginRecording(cull, useRTree: true);
        foreach (var layer in _doc.Layers)
        {
            if (!layer.Visible) continue;
            // The layer's list is the drawing order: every drawn object, grouped or not, in the order it is painted. A group has no place in
            // it (R22), so what a group does to its members is only its transform (which GetWorldTransform applies) and hiding them.
            foreach (var o in layer.Children)
            {
                if (!o.IsShown || (exclude is not null && IsOrIsIn(o, exclude))) continue;
                DrawObject(canvas, o, Affine2.Identity, null, cache: true);
            }
        }
        SweepCaches();
        return rec.EndRecording();
    }

    /// <summary>True when <paramref name="o"/>, or a group it belongs to, is in <paramref name="set"/> (a dragged group takes its members along).</summary>
    private static bool IsOrIsIn(DocObject o, HashSet<DocObject> set)
    {
        for (DocObject? x = o; x is not null; x = x.Parent as DocObject)
            if (set.Contains(x)) return true;
        return false;
    }

    /// <summary>
    /// Draws one object in document coordinates. A group is drawn member by member in the order they were put in, which is not necessarily
    /// the drawing order: to draw a document or a selection, draw its drawn objects in the order of the layers (as <see cref="RecordDocument"/> does).
    /// </summary>
    public void DrawObject(SKCanvas canvas, DocObject o, Affine2 worldDelta, HashSet<DocObject>? exclude, bool cache)
    {
        if (!o.Visible || (exclude is not null && exclude.Contains(o))) return;
        if (Wireframe)
        {
            DrawOutlines(canvas, o, worldDelta, cache);
            return;
        }
        // Feather (R8): the object is drawn as usual into a layer, then its edge is faded with the mask from its own shape.
        if (o.Style.Feather is { Amount: > 0 } feather && o is not GroupObject && _paintMode == PaintMode.All && !ReferenceEquals(_feathering, o))
        {
            var previous = _feathering;
            _feathering = o;
            bool done;
            try
            {
                done = _soft.DrawFeathered(canvas, o, o.GetWorldTransform() * worldDelta, feather, ViewScale, _generation, FeatherPixelDpi,
                    layer => DrawObject(layer, o, worldDelta, null, cache));
            }
            finally { _feathering = previous; }
            if (done)
            {
                _docHasImages = true;   // the mask depends on the zoom bucket: record again when it changes
                return;
            }
        }
        switch (o)
        {
            case GroupObject g:
                foreach (var c in g.Children) DrawObject(canvas, c, worldDelta, null, cache);
                break;
            case ImageObject img:
                if (_paintMode != PaintMode.StrokeOnly) DrawImage(canvas, img, img.GetWorldTransform() * worldDelta);
                break;
            case CombinedObject combined:
            {
                // Pieces keep their own colors, but fills are clipped to the even-odd area of all pieces, so
                // overlaps are knocked out like combined graphics (the cue that sets them apart from groups).
                // Outlines are drawn afterwards, unclipped.
                var cw = combined.GetWorldTransform() * worldDelta;
                using var area = combined.GetLocalGeometry().Transform(cw).ToSKPath();
                canvas.Save();
                canvas.ClipPath(area, SKClipOperation.Intersect, antialias: true);
                _paintMode = PaintMode.FillOnly;
                foreach (var part in combined.Parts) DrawObject(canvas, part, cw, null, cache: false);
                canvas.Restore();
                _paintMode = PaintMode.StrokeOnly;
                foreach (var part in combined.Parts) DrawObject(canvas, part, cw, null, cache: false);
                _paintMode = PaintMode.All;
                break;
            }
            default:
                DrawShape(canvas, o, worldDelta, cache);
                break;
        }
    }

    private sealed class OutlineEntry(int version, long stamp, Affine2 world, List<(SKPath Path, LoopKind Kind)> paths)
    {
        public readonly int Version = version;
        public readonly long Stamp = stamp;
        public readonly Affine2 World = world;
        public readonly List<(SKPath Path, LoopKind Kind)> Paths = paths;
        public int LastUsed;

        public void Dispose() { foreach (var (p, _) in Paths) p.Dispose(); }
    }

    /// <summary>A text's classified loops in its own coordinates; independent of where the text is, so moving it keeps them.</summary>
    private sealed class TextLoopSet(int layoutStamp, int generation, List<(PathGeometry Loop, LoopKind Kind)> loops)
    {
        public readonly int LayoutStamp = layoutStamp;
        public readonly int Generation = generation;
        public readonly List<(PathGeometry Loop, LoopKind Kind)> Loops = loops;
    }

    private readonly Dictionary<ObjectId, OutlineEntry> _outlines = [];
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextObject, TextLoopSet> _textLoops = [];

    /// <summary>
    /// Like vector view: each letter's outer loops blue, its holes (the inside of o, e, b...) red. Classified per
    /// letter, so long texts stay cheap and touching letters don't nest into each other. Cached until the text's layout changes.
    /// </summary>
    private List<(PathGeometry Loop, LoopKind Kind)> ClassifiedLoops(TextObject t)
    {
        if (_textLoops.TryGetValue(t, out var set) && set.LayoutStamp == t.LayoutStamp && set.Generation == TextEngine.Generation)
            return set.Loops;
        var result = new List<(PathGeometry, LoopKind)>();
        foreach (var letter in t.Layout.Letters)
        {
            if (letter.Outline.IsEmpty) continue;
            var loops = PathEditing.ToLoops(letter.Outline);
            var kinds = PathEditing.Classify(loops);
            for (int i = 0; i < loops.Count; i++)
                result.Add((PathEditing.FromLoops([loops[i]], letter.Outline.FillRule), kinds[i]));
        }
        _textLoops.AddOrUpdate(t, new TextLoopSet(t.LayoutStamp, TextEngine.Generation, result));
        return result;
    }

    private List<(SKPath Path, LoopKind Kind)> BuildOutlinePaths(DocObject o, Affine2 worldDelta)
    {
        var list = new List<(SKPath, LoopKind)>();
        if (o is TextObject t)
        {
            var tw = t.GetWorldTransform() * worldDelta;
            foreach (var (loop, kind) in ClassifiedLoops(t)) list.Add((loop.Transform(tw).ToSKPath(), kind));
        }
        else
        {
            foreach (var loop in OutlineView.Loops(o, worldDelta)) list.Add((loop.Path.ToSKPath(), loop.Kind));
        }
        return list;
    }

    /// <summary>
    /// Outline view: vectors only, hairlines colored by loop kind; text as glyph outlines, images as frames. With
    /// <paramref name="cache"/> (recording the document) the world-space paths of each object are kept until it changes or moves.
    /// </summary>
    private void DrawOutlines(SKCanvas canvas, DocObject o, Affine2 worldDelta, bool cache = false)
    {
        switch (o)
        {
            case GroupObject g:
                foreach (var c in g.Children) if (c.Visible) DrawOutlines(canvas, c, worldDelta, cache);
                return;
            case ImageObject img:
            {
                using var frame = img.GetLocalGeometry().Transform(img.GetWorldTransform() * worldDelta).ToSKPath();
                StrokeHairline(canvas, frame, SKColors.Gray);
                return;
            }
            case CombinedObject c:
            {
                var cw = c.GetWorldTransform() * worldDelta;
                foreach (var part in c.Parts)
                    if (part is TextObject or ImageObject) DrawOutlines(canvas, part, cw);
                break;   // shape parts below, classified together
            }
        }

        if (cache && !o.Id.IsNone)
        {
            var world = o.GetWorldTransform() * worldDelta;
            if (!_outlines.TryGetValue(o.Id, out var entry) || entry.Version != o.Version || entry.Stamp != o.DependencyStamp || entry.World != world)
            {
                entry?.Dispose();
                entry = new OutlineEntry(o.Version, o.DependencyStamp, world, BuildOutlinePaths(o, worldDelta));
                _outlines[o.Id] = entry;
            }
            entry.LastUsed = _generation;
            foreach (var (path, kind) in entry.Paths) StrokeHairline(canvas, path, Theme.ColorOf(kind));
            return;
        }

        var paths = BuildOutlinePaths(o, worldDelta);
        foreach (var (path, kind) in paths)
        {
            StrokeHairline(canvas, path, Theme.ColorOf(kind));
            path.Dispose();
        }
    }

    private void StrokeHairline(SKCanvas canvas, SKPath path, SKColor color)
    {
        _stroke.Color = color;
        _stroke.StrokeWidth = 0;   // hairline: one device pixel at any zoom
        _stroke.StrokeJoin = SKStrokeJoin.Miter;
        _stroke.StrokeCap = SKStrokeCap.Butt;
        canvas.DrawPath(path, _stroke);
    }

    private void DrawShape(SKCanvas canvas, DocObject o, Affine2 worldDelta, bool cache)
    {
        var style = o.Style;
        if (style.Blur is { } blur && style.Fill is SolidPaint blurFill)
        {
            // A Soft Shadow or Glow object (R8): its shape, blurred. The soft layer makes and keeps the mask; there is no stroke.
            if (_paintMode != PaintMode.StrokeOnly)
            {
                _soft.DrawBlurred(canvas, o, o.GetWorldTransform() * worldDelta, blur, blurFill.Color.ToSK(), ViewScale, _generation, out bool usedMask);
                if (usedMask) _docHasImages = true;
            }
            return;
        }
        if (style.Fill is null && style.Stroke is null)
        {
            // Uncolored objects (e.g. a Weld result, or after "remove all color") show on screen as their plain vector outline,
            // (user, 2026-10-12). Exports (ViewScale 0) leave them out: there is no color to print. In a combined
            // object the outline is drawn once, in the stroke pass.
            if (ViewScale > 0 && _paintMode != PaintMode.FillOnly) DrawOutlines(canvas, o, worldDelta, cache);
            return;
        }

        SKPath path;
        bool dispose = false;
        var world = o.GetWorldTransform() * worldDelta;
        if (cache && _paths.TryGetValue(o.Id, out var cp) && cp.Version == o.Version && cp.World == world)
        {
            path = cp.Path;
            cp.LastUsed = _generation;
        }
        else
        {
            var geom = o.GetLocalGeometry();
            if (geom is null) return;
            // Paths are flattened into world space so strokes keep their width under non-uniform scaling.
            path = geom.Transform(world).ToSKPath();
            if (cache)
            {
                if (_paths.Remove(o.Id, out var old)) old.Path.Dispose();
                _paths[o.Id] = new CachedPath(o.Version, world, path) { LastUsed = _generation };
            }
            else dispose = true;
        }

        Paint(canvas, path, style, o, world);
        if (dispose) path.Dispose();
    }

    private enum PaintMode { All, FillOnly, StrokeOnly }

    /// <summary>Lets combined objects paint all fills (clipped) first, then all strokes.</summary>
    private PaintMode _paintMode = PaintMode.All;

    private void Paint(SKCanvas canvas, SKPath path, ObjectStyle style, DocObject o, Affine2 world)
    {
        if (style.Fill is SolidPaint sf && _paintMode != PaintMode.StrokeOnly)
        {
            _fill.Color = sf.Color.ToSK();
            canvas.DrawPath(path, _fill);
        }
        else if (style.Fill is GradientPaint gradient && _paintMode != PaintMode.StrokeOnly)
        {
            // The gradient is built in the object's own space (its control points are relative to the object's bounds) and carried
            // to the page by the object's world transform, so it follows every move, scale, rotation and skew.
            var bounds = o.GetLocalGeometry()?.GetBounds() ?? Rect2.Empty;
            using var shader = bounds.IsEmpty ? null : GradientShaders.Create(gradient, bounds, world);
            if (shader is null) _fill.Color = GradientShaders.FallbackColor(gradient);
            else { _fill.Color = SKColors.Black; _fill.Shader = shader; _fill.IsDither = true; }
            canvas.DrawPath(path, _fill);
            _fill.Shader = null;
            _fill.IsDither = false;
        }
        if (style.Stroke is { Paint: SolidPaint sp } stroke && stroke.Width > 0 && _paintMode != PaintMode.FillOnly)
        {
            _stroke.Color = sp.Color.ToSK();
            _stroke.StrokeWidth = (float)stroke.Width;
            _stroke.StrokeJoin = stroke.Join.ToSK();
            _stroke.StrokeCap = stroke.Cap.ToSK();
            _stroke.StrokeMiter = (float)stroke.MiterLimit;
            canvas.DrawPath(path, _stroke);
        }
    }

    private void DrawImage(SKCanvas canvas, ImageObject img, Affine2 world)
    {
        if (!_images.TryGetValue(img.AssetId, out var source))
        {
            source = ImageSource.Load(_doc.Assets.Get(img.AssetId));
            _images[img.AssetId] = source;
        }
        source.LastUsed = _generation;
        _docHasImages = true;

        // Screen pixels per picture pixel decides how the pixels are sampled.
        double across = world.TransformVector(new Vec2(img.Width, 0)).Length;
        double perPixel = source.PixelWidth > 0 ? ViewScale * across / source.PixelWidth : 0;
        var dest = SKRect.Create(0, 0, (float)img.Width, (float)img.Height);
        canvas.Save();
        canvas.Concat(world.ToSK());
        if (source.Choose(perPixel) is not { } choice)
        {
            _stroke.Color = SKColors.Gray;
            _stroke.StrokeWidth = 0;
            canvas.DrawRect(dest, _stroke);
            canvas.DrawLine(dest.Left, dest.Top, dest.Right, dest.Bottom, _stroke);
            canvas.DrawLine(dest.Right, dest.Top, dest.Left, dest.Bottom, _stroke);
        }
        else
        {
            canvas.DrawImage(choice.Image, dest, choice.Sampling, _imagePaint);
        }
        canvas.Restore();
    }

    private void SweepCaches()
    {
        // Pictures not drawn for two records are released (an excluded, dragged picture is still drawn by the preview).
        List<string>? staleImages = null;
        foreach (var (id, src) in _images)
            if (src.LastUsed < _generation - 1) (staleImages ??= []).Add(id);
        if (staleImages is not null)
            foreach (var id in staleImages)
            {
                _images[id].Dispose();
                _images.Remove(id);
            }

        _soft.Sweep(_generation);

        List<ObjectId>? staleOutlines = null;
        foreach (var (id, oe) in _outlines)
            if (oe.LastUsed != _generation) (staleOutlines ??= []).Add(id);
        if (staleOutlines is not null)
            foreach (var id in staleOutlines)
            {
                _outlines[id].Dispose();
                _outlines.Remove(id);
            }

        List<ObjectId>? stale = null;
        foreach (var (id, cp) in _paths)
            if (cp.LastUsed != _generation) (stale ??= []).Add(id);
        if (stale is null) return;
        foreach (var id in stale)
        {
            _paths[id].Path.Dispose();
            _paths.Remove(id);
        }
    }

    private static bool SameObjects(IReadOnlyList<DocObject>? a, IReadOnlyList<DocObject>? b)
    {
        int na = a?.Count ?? 0, nb = b?.Count ?? 0;
        if (na != nb) return false;
        for (int i = 0; i < na; i++)
            if (!ReferenceEquals(a![i], b![i])) return false;
        return true;
    }

    public void Dispose()
    {
        _docPicture?.Release();
        foreach (var cp in _paths.Values) cp.Path.Dispose();
        foreach (var oe in _outlines.Values) oe.Dispose();
        foreach (var img in _images.Values) img.Dispose();
        _soft.Dispose();
        _fill.Dispose();
        _stroke.Dispose();
        _imagePaint.Dispose();
    }
}
