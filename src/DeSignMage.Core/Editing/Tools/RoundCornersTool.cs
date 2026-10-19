using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// Layout ▸ Shapes ▸ Convert to Rounded Rectangle: opened by that command on the selected rectangles. The Context Bar sets the corner
/// radius; the rounded outline of every rectangle is previewed live over the canvas; Done (or Enter) applies it as one undo step,
/// Esc leaves without changing anything. Clicks on the canvas do nothing here (right-button zoom still works).
/// </summary>
public sealed class RoundCornersTool : Tool, IModeTool
{
    private double _radius = RoundedRectangleTool.DefaultRadius;

    public override string Id => "roundCorners";
    public override string DisplayName => "Convert to Rounded Rectangle";
    public override string Hint => "To ROUND the corners: set the radius in the Context Bar, then press Done (Enter)   Esc: cancel   To ZOOM: Press right button";

    /// <summary>The corner radius that Done applies, in points; the preview follows it.</summary>
    public double Radius
    {
        get => _radius;
        set
        {
            _radius = Math.Max(0, value);
            Redraw();
        }
    }

    /// <summary>The rectangles the conversion would act on: the selection (groups looked through) that is a rectangle.</summary>
    public List<DocObject> Targets() =>
        Stacking.LeavesBottomToTop(Session.Document, Session.Selection.Items).Where(o => RectangleShapes.TryGet(o, out _)).ToList();

    public bool CanApply => Targets().Count > 0;

    /// <summary>Half of the shortest side among the targets: the largest radius that still makes sense (bigger ones are clamped by the shape).</summary>
    public double MaxRadius
    {
        get
        {
            double min = double.PositiveInfinity;
            foreach (var t in Targets())
                if (RectangleShapes.TryGet(t, out var box)) min = Math.Min(min, Math.Min(box.Width, box.Height) / 2);
            return double.IsInfinity(min) ? 0 : min;
        }
    }

    public override void Activate()
    {
        // Start from the radius the selected rectangle already has, or the usual quarter inch.
        var first = Targets().OfType<RectangleObject>().FirstOrDefault(r => r.CornerRadius > 0);
        _radius = first?.CornerRadius ?? RoundedRectangleTool.DefaultRadius;
    }

    /// <summary>Done: rounds the corners of every target (one undo step) and returns to the Layout tool. The converted shapes stay selected.</summary>
    public void Apply()
    {
        var targets = Targets();
        if (targets.Count == 0) return;
        var cmd = new ConvertToRoundedRectangleCommand(targets, Math.Min(_radius, MaxRadius));
        Session.History.Execute(cmd);
        Session.SetTool("select");
        Session.Selection.Set(Session.Selection.Items.Concat(cmd.Results.Where(r => r.Parent is Layer)).Where(Session.Document.Contains).Distinct());
    }

    // ---- Done and Cancel (the rounded outline is only a preview until Done, so Cancel has nothing in the history to take back) ----

    bool IModeTool.IsInMode => true;
    bool IModeTool.CanFinish => CanApply;
    Task IModeTool.FinishMode() { Apply(); return Task.CompletedTask; }
    void IModeTool.DiscardPending() { }

    public override bool KeyDown(EditorKey key, ModifierKeys modifiers)
    {
        if (key != EditorKey.Enter) return false;
        Apply();
        return true;
    }

    public override void BuildOverlay(List<OverlayItem> items)
    {
        base.BuildOverlay(items);
        double max = MaxRadius;
        foreach (var t in Targets())
        {
            if (!RectangleShapes.TryGet(t, out var box)) continue;
            items.Add(new OutlineOverlay(PathGeometry.Rectangle(box, Math.Min(_radius, max)).Transform(t.GetWorldTransform())));
        }
    }
}
