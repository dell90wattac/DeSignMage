using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

public sealed partial class EditorSession
{
    /// <summary>
    /// What a refused action does to get the user's attention: the shell plays the system's default beep (a Vector Edit box that holds a
    /// loop's start/end point deletes nothing and beeps). Does nothing by default, so tests and headless runs stay quiet.
    /// </summary>
    public Action Beep { get; set; } = static () => { };

    private sealed record SmallEnds(int Version, Affine2 World, double Threshold, Vec2[] Ends);

    private readonly Dictionary<DocObject, SmallEnds> _smallEnds = [];

    /// <summary>
    /// View ▸ Highlight Small Vectors: adds a mark at the end of every small vector (chord shorter than the threshold) of the objects that are
    /// on screen. Each object is measured once and again only after it changes, moves or the threshold does.
    /// </summary>
    private void AddSmallVectorMarks(List<OverlayItem> items)
    {
        if (!VectorEditOptions.HighlightSmallVectors) return;
        var view = Viewport.VisibleDocRect;
        double threshold = SmallVectors.ThresholdPoints;
        var ends = new List<Vec2>();
        foreach (var layer in Document.Layers)
        {
            if (!layer.Visible) continue;
            foreach (var o in layer.Children)
            {
                if (!o.IsShown || o.GetLocalGeometry() is null) continue;
                if (!o.GetWorldBounds().Intersects(view)) continue;
                var world = o.GetWorldTransform();
                if (!_smallEnds.TryGetValue(o, out var cached) || cached.Version != o.Version || cached.World != world || cached.Threshold != threshold)
                {
                    var found = new List<Vec2>();
                    double scale = world.AverageScale;
                    foreach (var loop in PathEditing.ToLoops(o.GetLocalGeometry()!))
                        foreach (var end in SmallVectors.SmallEnds(loop, scale)) found.Add(world.Transform(end));
                    cached = new SmallEnds(o.Version, world, threshold, found.ToArray());
                    if (_smallEnds.Count > 20000) _smallEnds.Clear();
                    _smallEnds[o] = cached;
                }
                ends.AddRange(cached.Ends);
            }
        }
        if (ends.Count > 0) items.Add(new SmallVectorMarksOverlay(ends));
    }
}
