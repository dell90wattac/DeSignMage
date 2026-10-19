using DeSignMage.Core.Geometry;

namespace DeSignMage.Core.Model;

/// <summary>Rectangle with optional rounded corners; local geometry spans (0,0)-(Width,Height).</summary>
public sealed class RectangleObject : DocObject
{
    private double _width, _height, _cornerRadius;

    public RectangleObject(double width, double height, double cornerRadius = 0)
    {
        _width = width; _height = height; _cornerRadius = cornerRadius;
    }

    public double Width { get => _width; set { _width = value; Touch(); } }
    public double Height { get => _height; set { _height = value; Touch(); } }
    public double CornerRadius { get => _cornerRadius; set { _cornerRadius = value; Touch(); } }

    public override string KindName => "Rectangle";
    protected override DocObject CloneCore() => new RectangleObject(_width, _height, _cornerRadius);
    public override PathGeometry GetLocalGeometry() => PathGeometry.Rectangle(new Rect2(0, 0, _width, _height), _cornerRadius);
}

/// <summary>Ellipse inscribed in (0,0)-(Width,Height).</summary>
public sealed class EllipseObject : DocObject
{
    private double _width, _height;

    public EllipseObject(double width, double height) { _width = width; _height = height; }

    public double Width { get => _width; set { _width = value; Touch(); } }
    public double Height { get => _height; set { _height = value; Touch(); } }

    public override string KindName => "Ellipse";
    protected override DocObject CloneCore() => new EllipseObject(_width, _height);
    public override PathGeometry GetLocalGeometry() => PathGeometry.Ellipse(new Rect2(0, 0, _width, _height));
}

/// <summary>Free-form bezier path ("Vector Graphic" in object tree).</summary>
public sealed class PathObject : DocObject
{
    private PathGeometry _geometry;

    public PathObject(PathGeometry geometry) => _geometry = geometry;

    public PathGeometry Geometry { get => _geometry; set { _geometry = value; Touch(); } }

    public override string KindName => "Vector Graphic";
    protected override DocObject CloneCore() => new PathObject(_geometry);   // geometry is immutable, safe to share
    public override PathGeometry GetLocalGeometry() => _geometry;
}

/// <summary>
/// Objects that belong together: they are selected, moved, scaled and copied as one, and the group's transform applies to all of them.
/// A group is <b>membership only</b>: it has no place in the drawing order, which belongs to the members (<see cref="Layer.Children"/>).
/// Members may therefore be drawn with other objects between them, and making or breaking a group never changes what is on top of what
/// (R22). Groups nest: a member can be another group.
/// </summary>
public sealed class GroupObject : DocObject, IObjectContainer
{
    private readonly List<DocObject> _children = [];

    /// <summary>The direct members (objects and groups), in the order they were put in. This is not the drawing order.</summary>
    public IReadOnlyList<DocObject> Children => _children;
    List<DocObject> IObjectContainer.ChildList => _children;
    internal List<DocObject> MemberList => _children;

    public override string KindName => "Group";

    /// <summary>Puts a member in a group that is not in a document yet (copies, objects being built). In a document use <see cref="Document"/>.</summary>
    public void AddDetached(DocObject member)
    {
        if (member.Parent is not null) throw new InvalidOperationException($"{member} already has a parent.");
        member.Parent = this;
        _children.Add(member);
    }

    /// <summary>A new group, not in any document yet, holding <paramref name="members"/> (detached objects, bottom to top). For things that are built outside a document and then placed.</summary>
    public static GroupObject Of(IEnumerable<DocObject> members, string? name = null)
    {
        var group = new GroupObject { Name = name };
        foreach (var m in members) group.AddDetached(m);
        return group;
    }

    /// <summary>All objects that are not groups below this group, at any depth, in structure order (not drawing order; see <c>Stacking.LeavesBottomToTop</c>).</summary>
    public IEnumerable<DocObject> Leaves()
    {
        foreach (var c in _children)
        {
            if (c is GroupObject g)
            {
                foreach (var l in g.Leaves()) yield return l;
            }
            else yield return c;
        }
    }

    protected override DocObject CloneCore()
    {
        var g = new GroupObject();
        foreach (var child in _children)
        {
            var c = child.Clone();
            c.Parent = g;
            g._children.Add(c);
        }
        return g;
    }
    public override PathGeometry? GetLocalGeometry() => null;

    public override Rect2 GetWorldBounds()
    {
        var r = Rect2.Empty;
        foreach (var c in _children) r = r.Union(c.GetWorldBounds());
        return r;
    }
}

/// <summary>
/// The result of Combine (Arrange ▸ Combine): ONE object, not a group, so Ungroup (U) does not split it.
/// It sits between "grouped" and "welded" (the user, R2):
/// <list type="bullet">
/// <item><b>Grouped</b>: separate objects, nested group levels, U peels one level.</item>
/// <item><b>Combined</b>: one object; group history is gone. The parts are complete copies of the original solo
/// pieces (any kind, with all their properties), painted in their old stacking order. Break Apart (K) turns them
/// back into those solo pieces.</item>
/// <item><b>Welded</b> (not built): outlines fused into one shape; only Undo reverses it.</item>
/// </list>
/// Parts are detached objects (no id, no parent) whose <see cref="DocObject.Transform"/> is relative to this object.
/// <para>
/// .lyt mapping: "graphic" is one part with several loops and one color, which imports as a
/// <see cref="PathObject"/> (multi-subpath) or, if it must stay a compound, a CombinedObject with one part.
/// </para>
/// </summary>
public sealed class CombinedObject : DocObject
{
    private readonly DocObject[] _parts;

    public CombinedObject(IEnumerable<DocObject> parts)
    {
        _parts = parts.ToArray();
        if (_parts.Length == 0) throw new ArgumentException("A combined object needs at least one part.");
        if (_parts.Any(p => p.Parent is not null || p is GroupObject or CombinedObject))
            throw new ArgumentException("Parts must be detached solo objects.");
        SyncStyle();
    }

    /// <summary>The original solo pieces, bottom to top. Change them only through commands.</summary>
    public IReadOnlyList<DocObject> Parts => _parts;

    /// <summary>Keeps <see cref="DocObject.Style"/> equal to the top part's style (shown in the color UI).</summary>
    internal void SyncStyle() => Style = _parts[^1].Style;

    /// <summary>Swaps one part (vector editing turns a part into a <see cref="PathObject"/>). Use through commands.</summary>
    internal void ReplacePart(int index, DocObject part)
    {
        _parts[index] = part;
        SyncStyle();   // also bumps Version
    }

    public override string KindName => "Combined Graphic";

    /// <summary>
    /// All parts' outlines as one path in this object's local space, with the <b>even-odd</b> fill rule. This is the
    /// combined object's fill area: where pieces overlap, the area is knocked out. That knock-out is the
    /// visual cue that tells combined pieces apart from grouped ones (the user). Each part's fill is clipped to it.
    /// </summary>
    public override PathGeometry GetLocalGeometry()
    {
        var verbs = new List<PathVerb>();
        var points = new List<Vec2>();
        foreach (var p in _parts)
        {
            if (p.GetLocalGeometry() is not { } g) continue;
            var t = g.Transform(p.Transform);
            verbs.AddRange(t.Verbs);
            points.AddRange(t.Points);
        }
        return new PathGeometry(verbs.ToArray(), points.ToArray(), FillRule.EvenOdd);
    }

    /// <summary>Parts are edited through commands that may not touch this object, so their versions are part of the cache key.</summary>
    public override long DependencyStamp
    {
        get
        {
            long sum = 0;
            foreach (var p in _parts) sum += p.Version;
            return sum;
        }
    }

    protected override Rect2 ComputeWorldBounds(Affine2 w)
    {
        var r = Rect2.Empty;
        foreach (var p in _parts)
            if (p.GetLocalGeometry() is { } g) r = r.Union(g.Transform(p.Transform * w).GetBounds());
        return r;
    }

    protected override DocObject CloneCore() => new CombinedObject(_parts.Select(p => p.Clone()));
}

/// <summary>
/// Placed raster image ("Bitmap"). The encoded pixels live in the document's <see cref="AssetStore"/>; the frame is
/// (0,0)-(Width,Height) in points. The pixel size and original file name are metadata kept next to it: they let the
/// editor show the DPI and do crop math without decoding. Imported layouts may not know the pixel size (0) until an
/// image command asks the image service for it.
/// </summary>
public sealed class ImageObject : DocObject
{
    private string _assetId;
    private double _width, _height;
    private int _pixelWidth, _pixelHeight;
    private string? _sourceName;
    private string? _sourceAssetId;
    private Imaging.PictureRecipe? _recipe;

    public ImageObject(string assetId, double width, double height, int pixelWidth = 0, int pixelHeight = 0, string? sourceName = null)
    {
        _assetId = assetId; _width = width; _height = height;
        _pixelWidth = pixelWidth; _pixelHeight = pixelHeight; _sourceName = sourceName;
    }

    public string AssetId { get => _assetId; set { _assetId = value; Touch(); } }

    /// <summary>
    /// Which parts of the picture are visible, for picking (a click on a see-through part does not select the picture); null for a picture without see-through
    /// parts. Worked out on demand by <see cref="HitTesting"/> from the stored pixels, and again whenever <see cref="AssetId"/> is not the one it was made for.
    /// Not saved and not part of undo.
    /// </summary>
    public Imaging.AlphaMask? HitMask { get; set; }

    /// <summary>The <see cref="AssetId"/> <see cref="HitMask"/> was made for (null = not worked out yet).</summary>
    public string? HitMaskAssetId { get; set; }

    public double Width { get => _width; set { _width = value; Touch(); } }
    public double Height { get => _height; set { _height = value; Touch(); } }

    /// <summary>Pixels across the stored picture, or 0 when not known yet.</summary>
    public int PixelWidth { get => _pixelWidth; set { _pixelWidth = value; Touch(); } }
    public int PixelHeight { get => _pixelHeight; set { _pixelHeight = value; Touch(); } }

    /// <summary>The file the picture was imported from (name only), if any. Stays when the object is renamed.</summary>
    public string? SourceName { get => _sourceName; set { _sourceName = value; Touch(); } }

    public bool HasPixelSize => _pixelWidth > 0 && _pixelHeight > 0;

    /// <summary>
    /// With a <see cref="Recipe"/>: the untouched original pixels (an asset). <see cref="AssetId"/> then holds the finished result at full resolution, so
    /// everything that reads the picture (drawing, export, clipboard, Vectorize, the legacy layout writer) needs no knowledge of the recipe.
    /// Null for a picture that was never edited in the Picture Editor.
    /// </summary>
    public string? SourceAssetId { get => _sourceAssetId; set { _sourceAssetId = value; Touch(); } }

    /// <summary>What the Picture Editor did to <see cref="SourceAssetId"/> to make <see cref="AssetId"/> (crop, straighten, colors); null when nothing was.</summary>
    public Imaging.PictureRecipe? Recipe { get => _recipe; set { _recipe = value; Touch(); } }

    /// <summary>Pixels per inch across the image as it lies on the page (world scale included); 0 if the pixel size is unknown.</summary>
    public double EffectiveDpiX => HasPixelSize ? _pixelWidth / Math.Max(WorldFrameLength(new Vec2(_width, 0)), 1e-9) * Units.PointsPerInch : 0;

    /// <summary>Pixels per inch down the image as it lies on the page.</summary>
    public double EffectiveDpiY => HasPixelSize ? _pixelHeight / Math.Max(WorldFrameLength(new Vec2(0, _height)), 1e-9) * Units.PointsPerInch : 0;

    private double WorldFrameLength(Vec2 edge) => GetWorldTransform().TransformVector(edge).Length;

    public override string KindName => "Bitmap";
    protected override DocObject CloneCore() =>
        new ImageObject(_assetId, _width, _height, _pixelWidth, _pixelHeight, _sourceName) { _sourceAssetId = _sourceAssetId, _recipe = _recipe };
    public override PathGeometry GetLocalGeometry() => PathGeometry.Rectangle(new Rect2(0, 0, _width, _height));
}
