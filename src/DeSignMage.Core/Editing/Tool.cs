namespace DeSignMage.Core.Editing;

/// <summary>
/// An interactive mode of the canvas (select, rectangle, node edit, text, ...). Receives left-button pointer
/// input and keys; the right and middle buttons belong to view navigation and never reach tools.
/// Each tool has a context Context Bar in the shell, looked up by <see cref="Id"/>.
/// </summary>
public abstract class Tool
{
    private EditorSession? _session;

    public abstract string Id { get; }
    public abstract string DisplayName { get; }

    /// <summary>Status bar hint, in the classic style ("To SELECT: Press left button ...").</summary>
    public virtual string Hint => "";

    public virtual CursorKind Cursor => CursorKind.Arrow;

    protected EditorSession Session => _session ?? throw new InvalidOperationException("Tool is not attached to a session.");

    internal void Attach(EditorSession session) => _session = session;

    public virtual void Activate() { }

    /// <summary>Called when switching away; abandon any drag in progress.</summary>
    public virtual void Deactivate() => Cancel();

    public virtual void PointerDown(PointerInput e) { }
    public virtual void PointerMove(PointerInput e) { }
    public virtual void PointerUp(PointerInput e) { }

    /// <summary>A modifier key went down or up while the pointer stayed where it was.</summary>
    public virtual void ModifiersChanged(ModifierKeys modifiers) { }

    /// <summary>The pointer left the canvas (drop any hover feedback).</summary>
    public virtual void PointerExited() { }

    /// <summary>Returns true if the key was consumed.</summary>
    public virtual bool KeyDown(EditorKey key, ModifierKeys modifiers) => false;

    /// <summary>Abort the current gesture without changing the document.</summary>
    public virtual void Cancel() { }

    /// <summary>Adds this tool's overlay. By default shows the selection box without handles.</summary>
    public virtual void BuildOverlay(List<OverlayItem> items)
    {
        if (!Session.Selection.IsEmpty) items.Add(new SelectionOverlay(Session.Selection.GetBounds(), Handles: false));
    }

    protected void Redraw() => Session.RequestRedraw();
}
