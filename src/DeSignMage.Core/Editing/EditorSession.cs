using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Editing.Tools;

namespace DeSignMage.Core.Editing;

/// <summary>
/// One open document plus everything needed to edit it: undo history, selection, view, tools.
/// The UI shell forwards raw pointer/key input here; this class routes it:
/// right button â†’ marquee zoom / zoom-out stack, middle button or Space+left â†’ pan, left â†’ active tool.
/// </summary>
public enum MiddleButtonUsage { Pan, Zoom }

public sealed partial class EditorSession
{
    private enum NavMode { None, Pan, ZoomMarquee }

    private readonly List<Tool> _tools = [];
    private Tool _activeTool;
    private TransformPreview? _preview;
    private NavMode _nav;
    private PointerButton _navButton;
    private Vec2 _navStart, _navLast;
    private bool _spaceHeld;

    public EditorSession(Document document)
    {
        Document = document;
        History = new CommandHistory(document)
        {
            // Copied pictures are pasted by asset id, so their pixels must outlive the undo steps that made them.
            ExtraLiveAssets = () => UndoMemory.AssetsOf(_clipboard),
        };
        Document.Changed += (_, _) =>
        {
            Selection.Prune(Document);
            RequestRedraw();
        };
        Selection.Changed += (_, _) => RequestRedraw();
        Viewport.Changed += (_, _) => RequestRedraw();

        InitGuides();
        RegisterTool(new SelectTool());
        RegisterTool(new RectangleTool());
        RegisterTool(new EllipseTool());
        RegisterTool(new VectorEditTool());
        RegisterTool(new LoopEditTool());
        RegisterTool(new VectorDrawTool());
        RegisterTool(new TextTool());
        RegisterTool(new EyedropperTool());
        RegisterTool(new CropTool());
        RegisterTool(new OutlineTool());
        RegisterTool(new CookieCutterTool());
        RegisterTool(new KnifeTool());
        RegisterTool(new PlaceTool());
        _activeTool = _tools[0];
    }

    public Document Document { get; }
    public CommandHistory History { get; }
    public Selection Selection { get; } = new();
    public Viewport Viewport { get; } = new();

    /// <summary>Press-release movement below this many DIPs counts as a click, not a drag.</summary>
    public double ClickThresholdPx { get; set; } = 4;
    public double PickTolerancePx { get; set; } = 4;

    /// <summary>
    /// How close (screen pixels, the same at every zoom) a click must be to an object's outline to select it (R10).
    /// Generous to start with; lower it if it grabs too much.
    /// </summary>
    public double HitTolerancePixels { get; set; } = 8;

    /// <summary>The object a click at <paramref name="doc"/> selects under R10's rules, or null (see <see cref="HitTesting.HitTest"/>).</summary>
    public DocObject? HitTestAt(Vec2 doc) => HitTesting.HitTest(Document, doc, Viewport.ToDocLength(HitTolerancePixels));
    public double HandleRadiusPx { get; set; } = 6;

    /// <summary>Arrow-key nudge in points (Shift Ã—5, Ctrl Ã·5).</summary>
    public double NudgeDistance { get; set; } = Units.FromInches(0.1);

    /// <summary>Zoom step per wheel notch (the user's value is 25%).</summary>
    public double WheelZoomFactor { get; set; } = 1.25;

    /// <summary>
    /// "Middle Mouse Button: Zoom / Pan" option. Pan is the default here
    /// until the user decides.
    /// </summary>
    public MiddleButtonUsage MiddleButton { get; set; } = MiddleButtonUsage.Pan;

    /// <summary>Option "Shape tools remain active"; otherwise Alt keeps the tool for the next shape.</summary>
    public bool ShapeToolsRemainActive { get; set; }

    /// <summary>
    /// "Auto Grouping": a rubber band around two or more objects makes a persistent group (requirement R2).
    /// Off = the rubber band only selects (Ctrl then makes a group, "permanent box").
    /// </summary>
    public bool AutoGrouping { get; set; } = true;

    /// <summary>Raised on a left double-click on an object: the shell shows that object's context menu (R1).</summary>
    public event EventHandler<ContextMenuRequest>? ContextMenuRequested;

    internal void RequestContextMenu(DocObject target, Vec2 screen, MenuKind kind = MenuKind.Object) =>
        ContextMenuRequested?.Invoke(this, new ContextMenuRequest(target, screen, kind));

    private bool _showFillColors = true;

    /// <summary>
    /// "Show Fill Colors" (F3, the three-color button). Off = everything drawn as vectors only (border loops blue,
    /// interior red, broken black). Purely a display setting: every tool works the same either way.
    /// </summary>
    public bool ShowFillColors
    {
        get => _showFillColors;
        set
        {
            if (_showFillColors == value) return;
            _showFillColors = value;
            DisplayChanged?.Invoke(this, EventArgs.Empty);
            RequestRedraw();
        }
    }

    public event EventHandler? DisplayChanged;

    /// <summary>Opens the vector tool on <paramref name="o"/> (double-click on a single or combined object).</summary>
    public void EditVectors(DocObject o) => OpenPathEdit(o, "vector");

    /// <summary>Opens <paramref name="o"/> in the Vector Edit tool ("vector") or the Loop Edit tool ("loop").</summary>
    public void OpenPathEdit(DocObject o, string toolId)
    {
        if (o is TextObject text) o = ConvertTextForVectorEditing(text);   // text turns into curves the moment its vectors are edited
        if (!OutlineView.IsEditable(o)) return;
        SetTool(toolId);
        ((PathEditToolBase)_tools.First(t => t.Id == toolId)).SetTarget(o);
    }

    /// <summary>The pointer left the canvas.</summary>
    public void PointerExited()
    {
        HoverScreen = null;
        _activeTool.PointerExited();
    }

    /// <summary>Where the pointer is over the canvas (screen DIPs), or null when it is somewhere else. A paste hangs there.</summary>
    public Vec2? HoverScreen { get; private set; }

    public event EventHandler? RedrawRequested;
    public event EventHandler? ToolChanged;

    public void RequestRedraw() => RedrawRequested?.Invoke(this, EventArgs.Empty);

    // ---- Tools ----

    public IReadOnlyList<Tool> Tools => _tools;
    public Tool ActiveTool => _activeTool;

    public void RegisterTool(Tool tool)
    {
        if (_tools.Any(t => t.Id == tool.Id)) throw new ArgumentException($"Tool '{tool.Id}' already registered.");
        tool.Attach(this);
        _tools.Add(tool);
    }

    public T GetTool<T>() where T : Tool => _tools.OfType<T>().First();

    public void SetTool(string id)
    {
        var tool = _tools.FirstOrDefault(t => t.Id == id) ?? throw new ArgumentException($"Unknown tool '{id}'.");
        if (tool == _activeTool) return;
        _activeTool.Deactivate();
        _activeTool = tool;
        tool.Activate();
        ToolChanged?.Invoke(this, EventArgs.Empty);
        RequestRedraw();
    }

    /// <summary>Objects being dragged; the renderer draws them displaced instead of at their stored position.</summary>
    public TransformPreview? Preview
    {
        get => _preview;
        set { _preview = value; RequestRedraw(); }
    }

    public bool SpaceHeld
    {
        get => _spaceHeld;
        set { _spaceHeld = value; RequestRedraw(); }
    }

    public CursorKind CurrentCursor => _nav switch
    {
        NavMode.Pan => CursorKind.Grabbing,
        NavMode.ZoomMarquee => CursorKind.ZoomIn,
        _ => _spaceHeld ? CursorKind.Hand : GuideCursor() ?? _activeTool.Cursor,
    };

    public string Hint => _activeTool.Hint;

    // ---- Input routing ----

    public void PointerDown(PointerInput e)
    {
        if (_nav != NavMode.None || _guideMode != GuideMode.None) return;   // one gesture at a time; guides ignore the right button
        if (e.Button == PointerButton.Right || (e.Button == PointerButton.Middle && MiddleButton == MiddleButtonUsage.Zoom))
        {
            BeginNav(NavMode.ZoomMarquee, e);
        }
        else if (e.Button == PointerButton.Middle || (e.Button == PointerButton.Left && _spaceHeld))
        {
            BeginNav(NavMode.Pan, e);
        }
        else if (e.Button == PointerButton.Left)
        {
            if (!IsPlacing && GuidePointerDown(e)) return;   // while placing, a click on a guide (where the paste snaps) places it
            _leftDown = true;
            _activeTool.PointerDown(e);
        }
    }

    public void PointerMove(PointerInput e)
    {
        HoverScreen = e.Screen;
        if (_activeTool is PlaceTool placing) placing.Follow(e.Screen);   // also while the right button draws a zoom box
        if (_guideMode != GuideMode.None)
        {
            MoveGuideDrag(e.Screen);
            return;
        }
        switch (_nav)
        {
            case NavMode.Pan:
                Viewport.PanBy(e.Screen - _navLast);
                _navLast = e.Screen;
                return;
            case NavMode.ZoomMarquee:
                _navLast = e.Screen;
                RequestRedraw();
                return;
        }
        UpdateGuideHover(e.Screen, e.Doc);
        _activeTool.PointerMove(e);
    }

    public void PointerUp(PointerInput e)
    {
        if (_guideMode != GuideMode.None)
        {
            if (e.Button == PointerButton.Left) EndGuideDrag(e.Screen);
            return;
        }
        if (_nav != NavMode.None)
        {
            if (e.Button != _navButton) return;
            var mode = _nav;
            _nav = NavMode.None;
            if (mode == NavMode.ZoomMarquee)
            {
                bool click = Vec2.Distance(_navStart, e.Screen) < ClickThresholdPx;
                if (!click) Viewport.PushAndZoomTo(Rect2.FromPoints(Viewport.ToDoc(_navStart), Viewport.ToDoc(e.Screen)));
                else if (e.Shift) ZoomToPage(push: true);                                      // Shift = zoom layout
                else if (e.Alt) Viewport.ZoomAt(Viewport.ViewSize / 2, 0.8);                   // Alt = zoom out 20%
                else ZoomOutStep();
            }
            RequestRedraw();
            return;
        }
        if (e.Button == PointerButton.Left)
        {
            _leftDown = false;
            _activeTool.PointerUp(e);
        }
    }

    /// <summary>Mouse wheel: zoom about the cursor. <paramref name="notches"/> &gt; 0 zooms in.</summary>
    public void Wheel(Vec2 screen, double notches) => Viewport.WheelZoom(screen, Math.Pow(WheelZoomFactor, notches));

    public bool KeyDown(EditorKey key, ModifierKeys modifiers)
    {
        if (key == EditorKey.Escape && (_guideMode != GuideMode.None || _selectedGuide is not null))
        {
            if (_guideMode != GuideMode.None) CancelGuideDrag();
            else SelectedGuide = null;
            return true;
        }
        if (key == EditorKey.Escape && CancelEffect()) return true;   // Esc stops a long effect (Weld, Outline...) that is still computing
        if (_activeTool.KeyDown(key, modifiers)) return true;
        switch (key)
        {
            case EditorKey.Delete:
            case EditorKey.Back:
                DeleteSelection();
                return true;
            case EditorKey.Escape:
                if (_nav != NavMode.None) { _nav = NavMode.None; RequestRedraw(); return true; }
                if (_activeTool is not SelectTool) { SetTool("select"); return true; }
                return false;
        }
        return false;
    }

    private void BeginNav(NavMode mode, PointerInput e)
    {
        _nav = mode;
        _navButton = e.Button;
        _navStart = _navLast = e.Screen;
        RequestRedraw();
    }

    // ---- Overlay ----

    public List<OverlayItem> BuildOverlay()
    {
        var items = new List<OverlayItem>();
        AddGuideOverlays(items);
        _activeTool.BuildOverlay(items);
        if (_nav == NavMode.ZoomMarquee && Vec2.Distance(_navStart, _navLast) >= ClickThresholdPx)
            items.Add(new MarqueeOverlay(Rect2.FromPoints(Viewport.ToDoc(_navStart), Viewport.ToDoc(_navLast)), MarqueeKind.Zoom));
        return items;
    }

    // ---- View commands ----

    // Menu zooms push the current view, so a right-click returns to it (Zoom All help says so).

    public void ZoomToPage(bool push = false) => ZoomTo(Document.Page.Bounds, push);

    public void ZoomToContent(bool push = false)
    {
        var r = Document.GetContentBounds();
        ZoomTo(r.IsEmpty ? Document.Page.Bounds : r.Union(Document.Page.Bounds), push);
    }

    public void ZoomToSelection(bool push = false)
    {
        if (!Selection.IsEmpty) ZoomTo(Selection.GetBounds(), push);
    }

    private void ZoomTo(Rect2 r, bool push)
    {
        if (push) Viewport.PushAndZoomTo(r);
        else Viewport.ZoomToFit(r);
    }

    /// <summary>
    /// Right-click: back through marquee zooms; when none are left, the base view = everything
    /// (page plus pasteboard objects), per the user (requirement R1).
    /// </summary>
    public void ZoomOutStep()
    {
        if (!Viewport.PopZoom()) ZoomToContent();
    }

    // ---- Edit commands ----

    public void Undo()
    {
        // While a Vector Draw drawing is in progress Ctrl+Z takes its last node away (like Backspace) instead of throwing the drawing out.
        if (_activeTool is VectorDrawTool { IsDrawing: true } draw) { draw.RemoveLastNode(); return; }
        _activeTool.Cancel();
        CancelGuideDrag();
        History.Undo();
    }

    public void Redo()
    {
        if (_activeTool is VectorDrawTool { IsDrawing: true }) return;
        _activeTool.Cancel();
        CancelGuideDrag();
        History.Redo();
    }

    /// <summary>The History tab: takes the document to the state after <paramref name="applied"/> steps (0 = the start), as one action.</summary>
    public bool JumpToHistory(int applied)
    {
        _activeTool.Cancel();
        CancelGuideDrag();
        return History.JumpTo(applied);
    }

    public void DeleteSelection()
    {
        if (Selection.IsEmpty && _selectedGuide is { } guide)
        {
            RemoveGuide(guide);   // Delete removes the selected guide
            return;
        }
        if (Selection.IsEmpty) return;
        History.Execute(new DeleteObjectsCommand(Selection.Items));
    }

    public void SelectAll()
    {
        // The top-level objects (a group counts once, where its first member is drawn) that can be clicked: shown and unlocked.
        Selection.Set(Document.Layers
            .Where(l => l.Visible && !l.Locked)
            .SelectMany(l => l.Children)
            .Select(o => o.TopLevel)
            .Where(o => o.Visible && !o.Locked));
    }

    /// <summary>Arrange ▸ To Front / To Back / Forward / Backward: moves the selection in the drawing order (R22: grouping is not touched). One undo step.</summary>
    public void ChangeOrder(StackMove move)
    {
        if (Selection.IsEmpty) return;
        var cmd = new StackOrderCommand(Document, Selection.Items, move);
        if (cmd.ChangesAnything) History.Execute(cmd);
    }

    public void TransformSelection(string description, Affine2 worldDelta)
    {
        if (Selection.IsEmpty || worldDelta.IsIdentity) return;
        History.Execute(TransformObjectsCommand.ApplyWorld(description, Selection.Items, worldDelta));
    }

    public void NudgeSelection(Vec2 delta) => TransformSelection("Nudge", Affine2.Translation(delta));

    /// <summary>Moves/scales the selection so its bounds become <paramref name="target"/> (Context Bar X/Y/W/H).</summary>
    public void SetSelectionBounds(Rect2 target)
    {
        var b = Selection.GetBounds();
        if (b.IsEmpty || target.IsEmpty) return;
        double sx = b.Width > 1e-9 ? target.Width / b.Width : 1;
        double sy = b.Height > 1e-9 ? target.Height / b.Height : 1;
        var delta = Affine2.Translation(-b.TopLeft) * Affine2.Scale(sx, sy) * Affine2.Translation(target.TopLeft);
        TransformSelection("Size/Position", delta);
    }

    // ---- Grouping (requirement R2) ----

    public bool CanGroup => Selection.Count >= 2;
    public bool CanUngroup => Selection.Items.Any(o => o is GroupObject);
    public bool CanCombine => CombineCommand.CanCombine(Selection.Items) &&
                              (Selection.Count >= 2 || Selection.Items[0] is GroupObject);

    /// <summary>Make Group: wraps the selected top-level objects in a new group and selects it.</summary>
    public GroupObject? GroupSelection() => GroupObjects(Selection.Items);

    public GroupObject? GroupObjects(IEnumerable<DocObject> objects)
    {
        var list = objects.ToList();
        if (list.Count < 2) return null;
        var cmd = new GroupObjectsCommand(Document, list);
        History.Execute(cmd);
        Selection.Set(cmd.Group);
        return cmd.Group;
    }

    /// <summary>Ungroup (U): removes one level from each selected group, then deselects everything (user requirement R2).</summary>
    public void UngroupSelection()
    {
        var groups = Selection.Items.OfType<GroupObject>().ToList();
        if (groups.Count == 0) return;
        using (History.BeginTransaction("Ungroup"))
        {
            foreach (var g in groups)
                History.Execute(new UngroupCommand(g));
        }
        Selection.Clear();
    }

    /// <summary>Combine: the selected group (or several objects) becomes one object.</summary>
    public CombinedObject? CombineSelection()
    {
        if (!CanCombine) return null;
        var cmd = new CombineCommand(Document, Selection.Items);
        History.Execute(cmd);
        Selection.Set(cmd.Combined);
        return cmd.Combined;
    }

    public bool CanBreakApart => Selection.Items.Any(IsBreakApartTarget);

    // A group dissolves completely; a combined object splits back into its original pieces; any other multi-loop graphic
    // (e.g. an imported shape made of a pad + toes) splits into its loops, which is what K does.
    private static bool IsBreakApartTarget(DocObject o) => o is GroupObject || o is CombinedObject || BreakIntoLoopsCommand.CanBreak(o);

    /// <summary>
    /// Break Apart (K): a selected group is dissolved at every level (combined objects inside are un-combined first, multi-loop
    /// shapes inside stay whole); a combined object becomes its original solo pieces; any other multi-loop graphic becomes one
    /// object per loop. One undo step; afterwards nothing is selected, like Ungroup (user requirement R2).
    /// </summary>
    public void BreakApartSelection()
    {
        var targets = Selection.Items.Where(IsBreakApartTarget).ToList();
        if (targets.Count == 0) return;
        using (History.BeginTransaction("Break Apart"))
        {
            foreach (var t in targets)
            {
                if (t is GroupObject g) History.Execute(new BreakDownGroupCommand(g));
                else if (t is CombinedObject c) History.Execute(new BreakApartCommand(c));
                else History.Execute(new BreakIntoLoopsCommand(t));
            }
        }
        Selection.Clear();
    }

    // ---- In-app clipboard (paste in place; "place" mode and the system clipboard come later) ----

    private List<DocObject> _clipboard = [];

    public bool CanPaste => _clipboard.Count > 0;

    /// <summary>The objects on the in-app clipboard (copies; read only). The window mirrors a copied picture to the system clipboard.</summary>
    public IReadOnlyList<DocObject> ClipboardObjects => _clipboard;

    /// <summary>
    /// Identifies the current in-app clipboard content; it changes with every Copy/Cut. The window tags the system clipboard with it,
    /// so a later Paste knows whether the system clipboard still holds these objects or something copied elsewhere since.
    /// </summary>
    public Guid ClipboardToken { get; private set; }

    /// <summary>Raised after the in-app clipboard got new content (Copy, Cut, Loop Edit copy).</summary>
    public event EventHandler? ClipboardChanged;

    private void ClipboardUpdated()
    {
        ClipboardToken = Guid.NewGuid();
        ClipboardChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Puts ready-made objects on the in-app clipboard (the Loop Edit tool's Cut/Copy of a loop).</summary>
    internal void SetClipboard(IEnumerable<DocObject> objects)
    {
        _clipboard = objects.ToList();
        ClipboardUpdated();
    }

    public void Copy()
    {
        if (Selection.IsEmpty) return;
        _clipboard = Stacking.CloneForCopy(Document, Stacking.BottomToTop(Document, Selection.Items));
        ClipboardUpdated();
    }

    public void Cut()
    {
        if (Selection.IsEmpty) return;
        Copy();
        DeleteSelection();
    }

    /// <summary>
    /// Paste: copies of the clipboard objects hang on the cursor (<see cref="BeginPlacing"/>) and the next left click puts them down,
    /// as the one undo step. Nothing is added before that.
    /// </summary>
    public void Paste()
    {
        if (_clipboard.Count == 0) return;
        BeginPlacing(_clipboard.Select(o => o.Clone()).ToList(), "Paste");
    }

    /// <summary>
    /// Hangs new, detached objects on the cursor: drawn as the outline-only drag preview centered on the pointer (at the view center
    /// until the pointer is over the canvas); the next left click adds them there as one undo step named <paramref name="description"/>,
    /// Esc cancels. Starting another one replaces the first.
    /// </summary>
    public void BeginPlacing(IReadOnlyList<DocObject> items, string description)
    {
        if (items.Count == 0) return;
        Selection.Clear();
        SetTool("place");
        GetTool<PlaceTool>().Begin(items, description, HoverScreen);
    }

    /// <summary>True while a paste hangs on the cursor.</summary>
    public bool IsPlacing => _activeTool is PlaceTool { IsPlacing: true };
}
