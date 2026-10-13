using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// The Color Mixer's eyedropper: click any painted object (also inside groups and combined objects) and the session raises
/// <see cref="EditorSession.ColorPicked"/> with that object's style; the previous tool returns. Nothing is changed here.
/// </summary>
public sealed class EyedropperTool : Tool
{
    public override string Id => "eyedropper";
    public override string DisplayName => "Eyedropper";
    public override string Hint => "To PICK a color: Press left button on an object   Esc: cancel   To ZOOM: Press right button";
    public override CursorKind Cursor => CursorKind.Eyedropper;

    public override void PointerDown(PointerInput e)
    {
        var top = HitTesting.HitPainted(Session.Document, e.Doc, Session.Viewport.ToDocLength(Session.PickTolerancePx));
        if (top is null) return;
        var leaf = HitTesting.HitLeaf(top, e.Doc, Session.Viewport.ToDocLength(Session.PickTolerancePx));
        if (leaf.Style.Fill is null && leaf.Style.Stroke is null) return;   // nothing to pick (e.g. an unfilled bitmap frame)
        Session.RaiseColorPicked(leaf);
    }

    public override bool KeyDown(EditorKey key, ModifierKeys modifiers)
    {
        if (key != EditorKey.Escape) return false;
        Session.CancelEyedropper();
        return true;
    }

}
