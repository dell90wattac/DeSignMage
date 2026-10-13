using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// Placing a paste. A pasted item is not dropped at once: it hangs on the cursor, drawn as the outline-only drag preview centered on
/// the pointer (and snapping to guides like a move), and the next left click puts it there, as the one undo step of the paste.
/// Esc (or Undo) abandons it and nothing is added. The right button keeps zooming (the view navigation never reaches tools), so the
/// view can be changed before placing; the preview then keeps following the pointer's screen position.
/// Until the pointer enters the canvas the item hangs at the center of the view.
/// </summary>
public sealed class PlaceTool : Tool
{
    private List<DocObject>? _items;
    private string _description = "Paste";
    private Rect2 _bounds;          // the items' bounds as they are (before placing)
    private Vec2 _screen;           // where the pointer is (screen), or the view center before it has entered
    private bool _pressed;

    public override string Id => "place";
    public override string DisplayName => "Place";
    public override string Hint => "Click to place · Esc to cancel";
    public override CursorKind Cursor => CursorKind.Move;

    /// <summary>True while an item hangs on the cursor.</summary>
    public bool IsPlacing => _items is not null;

    /// <summary>The items waiting to be placed (not in the document yet).</summary>
    public IReadOnlyList<DocObject> Items => _items ?? [];

    /// <summary>Where the items' center would land now (document points), for tests and feedback.</summary>
    public Vec2? TargetCenter => _items is null ? null : Snapped(Session.Viewport.ToDoc(_screen)).Center;

    /// <summary>Starts placing <paramref name="items"/> (detached objects). <paramref name="hoverScreen"/> is where the pointer is, if it is over the canvas.</summary>
    internal void Begin(IReadOnlyList<DocObject> items, string description, Vec2? hoverScreen)
    {
        Clear();
        _items = items.ToList();
        _description = description;
        _bounds = Rect2.Empty;
        foreach (var o in _items) _bounds = _bounds.Union(o.GetWorldBounds());
        _screen = hoverScreen ?? Session.Viewport.ViewSize / 2;
        _pressed = false;
        Session.Viewport.Changed += OnViewChanged;
        UpdatePreview();
    }

    // ---- Pointer ----

    public override void PointerMove(PointerInput e) => Follow(e.Screen);

    /// <summary>The pointer is at <paramref name="screen"/> (the session reports every move, also while a right-button zoom box is drawn).</summary>
    internal void Follow(Vec2 screen)
    {
        if (_items is null) return;
        _screen = screen;
        UpdatePreview();
    }

    public override void PointerDown(PointerInput e)
    {
        if (_items is null) return;
        _screen = e.Screen;
        _pressed = true;
        UpdatePreview();
    }

    public override void PointerUp(PointerInput e)
    {
        if (_items is null || !_pressed) return;
        _pressed = false;
        _screen = e.Screen;
        Place();
    }

    private void OnViewChanged(object? sender, EventArgs e) => UpdatePreview();

    // ---- Keys ----

    public override bool KeyDown(EditorKey key, ModifierKeys modifiers)
    {
        if (_items is null) return false;
        switch (key)
        {
            case EditorKey.Escape:
                Cancel();
                return true;
            case EditorKey.Delete or EditorKey.Back:
                return true;   // there is nothing placed yet to delete; never the selection behind it
        }
        return false;
    }

    /// <summary>Abandons the paste: nothing is added and no undo step is made (Esc, Undo, switching tools).</summary>
    public override void Cancel()
    {
        if (_items is null) return;
        Clear();
        if (Session.ActiveTool == this) Session.SetTool("select");
    }

    public override void Deactivate() => Clear();   // the session is already switching away

    public override void BuildOverlay(List<OverlayItem> items) { }   // no selection frame: the preview is the feedback

    // ---- Work ----

    /// <summary>The items' bounds if they were placed with their center under the pointer, after snapping to guides.</summary>
    private Rect2 Snapped(Vec2 pointerDoc)
    {
        var delta = Session.SnapMoveDelta(_bounds, pointerDoc - _bounds.Center);
        return _bounds.Transform(Affine2.Translation(delta));
    }

    private Vec2 DeltaFor(Vec2 pointerDoc) => Snapped(pointerDoc).TopLeft - _bounds.TopLeft;

    private void UpdatePreview()
    {
        if (_items is null) return;
        var delta = DeltaFor(Session.Viewport.ToDoc(_screen));
        // Copy: true = the document is not drawn without them (they are not in it).
        Session.Preview = new TransformPreview(_items.ToArray(), Affine2.Translation(delta), Copy: true);
    }

    private void Place()
    {
        var items = _items!;
        var delta = DeltaFor(Session.Viewport.ToDoc(_screen));
        Clear();
        // The items are still detached, so moving them is not an edit yet; the Add commands are the undo step.
        foreach (var o in items) o.Transform = o.Transform * Affine2.Translation(delta);
        using (Session.History.BeginTransaction(_description))
            Session.History.Execute(new AddObjectsCommand(Session.Document.ActiveLayer, items));
        Session.SetTool("select");
        Session.Selection.Set(items);
    }

    private void Clear()
    {
        if (_items is null) return;
        _items = null;
        _pressed = false;
        Session.Viewport.Changed -= OnViewChanged;
        Session.Preview = null;
    }
}
