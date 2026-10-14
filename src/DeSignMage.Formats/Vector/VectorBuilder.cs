using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Vector;

/// <summary>
/// Collects what an importer reads (objects in drawing order, in groups, on layers, with clips) and builds the <see cref="Document"/> at the end.
/// Groups are made from scopes: a scope with fewer than two objects below it is not a group.
/// </summary>
internal sealed class VectorBuilder
{
    private sealed class Scope(string? name, Scope? parent)
    {
        public string? Name = name;
        public readonly Scope? Parent = parent;
        public readonly List<object> Items = [];   // DocObject or Scope, in drawing order
        public int LeafCount;
    }

    private sealed record Leaf(DocObject Object, string? LayerName);

    private readonly Scope _root = new(null, null);
    private Scope _scope;
    private string? _layer;

    public VectorBuilder(PageSetup page, VectorImportOptions options)
    {
        Page = page;
        Options = options;
        _scope = _root;
    }

    public PageSetup Page { get; }
    public VectorImportOptions Options { get; }
    public List<string> Warnings { get; } = [];
    public AssetStore Assets { get; } = new();

    // warnings that are counted and written once
    private readonly Dictionary<string, int> _counted = [];

    public void Count(string message) => _counted[message] = _counted.GetValueOrDefault(message) + 1;

    /// <summary>"3 shapes ..." style summaries: the message is a format with {0} for the count.</summary>
    public void Warn(string message)
    {
        if (!Warnings.Contains(message)) Warnings.Add(message);
    }

    public void BeginGroup(string? name) => _scope = new Scope(name, _scope);

    /// <summary>Closes the current group; one with fewer than two objects in it is not a group, its object stays in the group around it.</summary>
    public void EndGroup()
    {
        var scope = _scope;
        if (scope.Parent is null) return;
        _scope = scope.Parent;
        if (scope.LeafCount == 0) return;
        if (scope.LeafCount == 1) _scope.Items.AddRange(scope.Items);
        else _scope.Items.Add(scope);
    }

    private void AddItem(object item, int leaves)
    {
        _scope.Items.Add(item);
        for (var s = _scope; s is not null; s = s.Parent) s.LeafCount += leaves;
    }

    /// <summary>The layer new objects go on (null = the first layer).</summary>
    public void SetLayer(string? name) => _layer = name;

    public void Add(DocObject obj) => AddItem(new Leaf(obj, _layer), 1);

    public Document Build()
    {
        var doc = new Document(Page);
        foreach (var asset in Assets.All) doc.Assets.Add(asset.Data, asset.MediaType);

        var layers = new Dictionary<string, Layer>();
        Layer LayerFor(string? name)
        {
            if (name is null) return doc.Layers[0];
            if (layers.TryGetValue(name, out var existing)) return existing;
            var layer = new Layer(name);
            doc.InsertLayer(doc.Layers.Count, layer);
            return layers[name] = layer;
        }

        var made = new Dictionary<Scope, GroupObject>();
        GroupObject? GroupFor(Scope? scope, Layer layer)
        {
            if (scope is null || scope == _root) return null;
            if (made.TryGetValue(scope, out var g)) return g;
            var parent = GroupFor(scope.Parent, layer);
            g = new GroupObject { Name = scope.Name };
            doc.Insert(layer, -1, g);
            if (parent is not null) doc.SetGroup(g, parent);
            made[scope] = g;
            return g;
        }

        void Walk(Scope scope)
        {
            foreach (var item in scope.Items)
            {
                if (item is Scope child) { Walk(child); continue; }
                var leaf = (Leaf)item;
                var layer = LayerFor(leaf.LayerName);
                doc.Insert(layer, -1, leaf.Object);
                if (GroupFor(scope, layer) is { } group) doc.SetGroup(leaf.Object, group);
            }
        }
        Walk(_root);

        // an unused extra layer (the default one when everything is on named layers) goes
        foreach (var l in doc.Layers.ToList())
            if (doc.Layers.Count > 1 && l.Children.Count == 0) doc.RemoveLayer(l);
        doc.ActiveLayer = doc.Layers.FirstOrDefault(l => l.Children.Count > 0) ?? doc.Layers[0];

        foreach (var (message, count) in _counted.OrderBy(kv => kv.Key))
            Warnings.Add(string.Format(message, count));
        return doc;
    }


    // ---------------------------------------------------------------- shapes

    /// <summary>
    /// Clips <paramref name="shape"/> to every clip in <paramref name="clips"/> (document points). A clip that is a rectangle holding the shape
    /// changes nothing; others need <see cref="VectorImportOptions.BooleanService"/>, else the shape stays unclipped (counted). Null = nothing is left.
    /// </summary>
    public PathGeometry? ApplyClips(PathGeometry shape, IReadOnlyList<PathGeometry> clips)
    {
        if (clips.Count == 0) return shape;
        var bounds = shape.GetBounds();
        var result = shape;
        foreach (var clip in clips)
        {
            var cb = clip.GetBounds();
            if (cb.IsEmpty) return null;
            if (!cb.Intersects(bounds)) return null;
            if (IsRectangle(clip) && Contains(cb, bounds)) continue;
            if (Options.BooleanService is { } booleans)
            {
                result = booleans.Intersect(result, clip);
                if (result.IsEmpty) return null;
                bounds = result.GetBounds();
            }
            else Count("{0} shape(s) are cut by a clipping path that was ignored (clipping needs the path boolean service).");
        }
        return result;
    }

    private static bool Contains(Rect2 outer, Rect2 inner, double eps = 0.01) =>
        inner.Left >= outer.Left - eps && inner.Right <= outer.Right + eps && inner.Top >= outer.Top - eps && inner.Bottom <= outer.Bottom + eps;

    /// <summary>True when the path is one closed axis-aligned rectangle (what clip rectangles are).</summary>
    public static bool IsRectangle(PathGeometry g)
    {
        var loops = PathEditing.ToLoops(g);
        if (loops.Count != 1) return false;
        var l = loops[0];
        if (!l.Closed || l.Segments.Any(s => s.Kind != SegmentKind.Line)) return false;
        var pts = Enumerable.Range(0, l.Segments.Count).Select(i => l.Segments[i].End).ToList();
        if (pts.Count is < 4 or > 5) return false;
        if (pts.Count == 5 && Vec2.Distance(pts[0], pts[4]) > 1e-6) return false;
        var b = g.GetBounds();
        return pts.Take(4).All(p => (Math.Abs(p.X - b.Left) < 1e-6 || Math.Abs(p.X - b.Right) < 1e-6) && (Math.Abs(p.Y - b.Top) < 1e-6 || Math.Abs(p.Y - b.Bottom) < 1e-6))
            && pts.Take(4).Select(p => (p.X > b.Center.X, p.Y > b.Center.Y)).Distinct().Count() == 4;
    }
}
