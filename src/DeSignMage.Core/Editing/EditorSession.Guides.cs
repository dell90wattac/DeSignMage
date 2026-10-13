using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

/// <summary>A selected or in-flight guide, drawn as a line across the work area (screen-space overlay, never exported).</summary>
public sealed record GuideOverlay(Vec2 Origin, double AngleDegrees, bool Selected, bool Locked) : OverlayItem;

/// <summary>Where the ruler marks the active guide: the x of a vertical guide, the y of a horizontal one, both when angled.</summary>
public readonly record struct GuideMarker(Vec2 Origin, GuideOrientation Orientation);

/// <summary>
/// Ruler guides in the editor (requirement R4): creating one by dragging out of a ruler, selecting and moving
/// guides with the Layout (select) tool, the guide edits behind the guide Context Bar, and snapping.
/// The right button never reaches any of this (it belongs to zoom, R1).
/// </summary>
public sealed partial class EditorSession
{
    private enum GuideMode { None, Pending, Moving, Creating }

    private GuideMode _guideMode;
    private Guide? _dragGuide;                  // the existing guide being pressed/moved; null while creating
    private Vec2 _dragOrigin;                   // live origin of the guide being dragged
    private double _dragAngle;
    private Vec2 _dragStartDoc, _dragStartScreen, _dragStartOrigin;
    private Guide? _selectedGuide, _hoverGuide;
    private bool _showGuides = true, _snapToGuides = true, _leftDown;

    private void InitGuides()
    {
        Selection.Changed += (_, _) =>
        {
            if (!Selection.IsEmpty && _selectedGuide is not null) SelectedGuide = null;
        };
        Document.Changed += (_, _) =>
        {
            if (_selectedGuide is not null && !Document.Guides.Contains(_selectedGuide)) SelectedGuide = null;
        };
    }

    /// <summary>Raised when the selected guide, guide visibility or snapping changes (the shell swaps Context Bars).</summary>
    public event EventHandler? GuideStateChanged;

    /// <summary>View ▸ Show Guidelines. Hidden guides can't be picked and don't snap.</summary>
    public bool ShowGuides
    {
        get => _showGuides;
        set
        {
            if (_showGuides == value) return;
            _showGuides = value;
            if (!value) { CancelGuideDrag(); SelectedGuide = null; }
            GuideStateChanged?.Invoke(this, EventArgs.Empty);
            RequestRedraw();
        }
    }

    /// <summary>Objects being moved or drawn snap to guides (toggle; on by default).</summary>
    public bool SnapToGuides
    {
        get => _snapToGuides;
        set
        {
            if (_snapToGuides == value) return;
            _snapToGuides = value;
            GuideStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Snap distance in screen DIPs ("within a few pixels").</summary>
    public double SnapTolerancePx { get; set; } = 5;

    /// <summary>The selected guide (drawn red, with the guide Context Bar). Selecting one deselects objects and vice versa.</summary>
    public Guide? SelectedGuide
    {
        get => _selectedGuide;
        set
        {
            if (value is not null && (!_showGuides || !Document.Guides.Contains(value))) value = null;
            if (ReferenceEquals(value, _selectedGuide)) return;
            _selectedGuide = value;
            if (value is not null) Selection.Clear();
            GuideStateChanged?.Invoke(this, EventArgs.Empty);
            RequestRedraw();
        }
    }

    public bool IsDraggingGuide => _guideMode != GuideMode.None;

    /// <summary>The guide's position for display: the live value while it is being dragged, otherwise the stored one.</summary>
    public Vec2 GuideOrigin(Guide g) => _guideMode != GuideMode.None && ReferenceEquals(g, _dragGuide) ? _dragOrigin : g.Origin;

    /// <summary>What the rulers mark: the guide being dragged or created, else the selected guide.</summary>
    public GuideMarker? ActiveGuideMarker
    {
        get
        {
            if (!_showGuides) return null;
            if (_guideMode != GuideMode.None) return new GuideMarker(_dragOrigin, Guide.Orient(_dragAngle));
            return _selectedGuide is { } g ? new GuideMarker(g.Origin, g.Orientation) : null;
        }
    }

    private double GuideTolerance => Viewport.ToDocLength(PickTolerancePx);

    // ---- Creating a guide from a ruler ----

    /// <summary>
    /// Left press on a ruler: top ruler → <see cref="GuideOrientation.Horizontal"/>, left ruler →
    /// <see cref="GuideOrientation.Vertical"/>. <paramref name="screen"/> is in canvas coordinates.
    /// </summary>
    public void BeginGuideFromRuler(GuideOrientation orientation, Vec2 screen)
    {
        if (_guideMode != GuideMode.None || _nav != NavMode.None) return;
        if (orientation == GuideOrientation.Angled) throw new ArgumentException("Rulers create horizontal or vertical guides.");
        ShowGuides = true;
        _guideMode = GuideMode.Creating;
        _dragGuide = null;
        _dragAngle = orientation == GuideOrientation.Horizontal ? 0 : 90;
        _dragOrigin = Viewport.ToDoc(screen);
        RequestRedraw();
    }

    /// <summary>The pointer moved while a guide is being created or moved (screen = canvas coordinates).</summary>
    public void MoveGuideDrag(Vec2 screen)
    {
        switch (_guideMode)
        {
            case GuideMode.Creating:
                _dragOrigin = Viewport.ToDoc(screen);
                break;
            case GuideMode.Pending:
                // A locked guide stays pending forever: it can be selected (and unlocked or removed) but not dragged.
                if (_dragGuide is { Locked: true } || Vec2.Distance(screen, _dragStartScreen) < ClickThresholdPx) return;
                _guideMode = GuideMode.Moving;
                goto case GuideMode.Moving;
            case GuideMode.Moving:
                var normal = Guide.NormalOf(_dragAngle);
                _dragOrigin = _dragStartOrigin + normal * Vec2.Dot(Viewport.ToDoc(screen) - _dragStartDoc, normal);
                break;
            default:
                return;
        }
        RequestRedraw();
    }

    /// <summary>
    /// Button released. A new guide is placed when the pointer is over the canvas (released over a ruler it is discarded);
    /// a moved guide gets one undoable move.
    /// </summary>
    public void EndGuideDrag(Vec2 screen)
    {
        if (_guideMode == GuideMode.None) return;
        MoveGuideDrag(screen);
        var mode = _guideMode;
        var guide = _dragGuide;
        var origin = _dragOrigin;
        double angle = _dragAngle;
        ResetGuideDrag();

        if (mode == GuideMode.Creating)
        {
            var view = new Rect2(0, 0, Viewport.ViewSize.X, Viewport.ViewSize.Y);
            if (!view.Contains(screen)) { RequestRedraw(); return; }
            var created = new Guide(origin, angle);
            History.Execute(new AddGuideCommand(created));
            SelectedGuide = created;
        }
        else if (mode == GuideMode.Moving && guide is not null)
        {
            var cmd = SetGuideCommand.Move(guide, origin);
            if (!cmd.IsNoOp) History.Execute(cmd);
        }
        RequestRedraw();
    }

    /// <summary>Abandons a guide drag without changing the document (Escape, undo, switching tools).</summary>
    public void CancelGuideDrag()
    {
        if (_guideMode == GuideMode.None) return;
        ResetGuideDrag();
        RequestRedraw();
    }

    private void ResetGuideDrag()
    {
        _guideMode = GuideMode.None;
        _dragGuide = null;
    }

    // ---- Picking and moving guides with the select tool ----

    /// <summary>
    /// The guide a click at this point would grab, or null. Handles of the selection, and an already selected
    /// object under the pointer, win over a guide so snapped objects can still be dragged where a guide crosses them.
    /// </summary>
    private Guide? PickGuide(Vec2 screen, Vec2 doc)
    {
        if (!_showGuides || _activeTool is not SelectTool) return null;
        var g = Guide.HitTest(Document.Guides, doc, GuideTolerance);
        if (g is null) return null;
        if (!Selection.IsEmpty)
        {
            if (SelectionHandles.HitTest(Viewport, Selection.GetBounds(), screen, HandleRadiusPx) is not null) return null;
            if (HitTesting.HitTest(Document, doc, GuideTolerance) is { } o && Selection.Contains(o)) return null;
        }
        return g;
    }

    /// <summary>Left press on the canvas. Returns true when it grabbed a guide; any other press deselects the guide.</summary>
    private bool GuidePointerDown(PointerInput e)
    {
        var g = PickGuide(e.Screen, e.Doc);
        if (g is null)
        {
            if (_selectedGuide is not null) SelectedGuide = null;
            return false;
        }
        SelectedGuide = g;
        _guideMode = GuideMode.Pending;
        _dragGuide = g;
        _dragAngle = g.AngleDegrees;
        _dragOrigin = _dragStartOrigin = g.Origin;
        _dragStartDoc = e.Doc;
        _dragStartScreen = e.Screen;
        return true;
    }

    private void UpdateGuideHover(Vec2 screen, Vec2 doc) =>
        _hoverGuide = _leftDown ? null : PickGuide(screen, doc);

    private CursorKind? GuideCursor()
    {
        var g = _guideMode switch
        {
            GuideMode.None => _hoverGuide,
            _ => _dragGuide,
        };
        double angle = _guideMode switch
        {
            GuideMode.None => g?.AngleDegrees ?? 0,
            _ => _dragAngle,
        };
        if (g is null && _guideMode != GuideMode.Creating) return null;
        if (g is { Locked: true }) return CursorKind.Arrow;
        return Guide.Orient(angle) switch
        {
            GuideOrientation.Horizontal => CursorKind.SizeNS,
            GuideOrientation.Vertical => CursorKind.SizeWE,
            _ => CursorKind.Move,
        };
    }

    // ---- Guide edits (the Context Bar and Delete key call these) ----

    public void RemoveGuide(Guide guide)
    {
        if (Document.Guides.Contains(guide)) History.Execute(new RemoveGuideCommand(guide));
    }

    public void RemoveAllGuides()
    {
        if (Document.Guides.Count > 0) History.Execute(new RemoveAllGuidesCommand());
    }

    public void SetGuideLocked(Guide guide, bool locked)
    {
        if (guide.Locked != locked) History.Execute(new SetGuideLockedCommand(guide, locked));
    }

    /// <summary>"+": centers the guide on the page along its axis.</summary>
    public void CenterGuideOnPage(Guide guide)
    {
        var cmd = SetGuideCommand.CenterOnPage(guide, Document.Page);
        if (!cmd.IsNoOp) History.Execute(cmd);
    }

    /// <summary>Context Bar edits: new position and/or angle of the guide (one undo step). Works on locked guides too.</summary>
    public void SetGuide(Guide guide, Vec2 origin, double? angleDegrees = null)
    {
        var cmd = new SetGuideCommand("Edit Guide", guide, origin, angleDegrees);
        if (!cmd.IsNoOp) History.Execute(cmd);
    }

    // ---- Snapping ----

    internal bool SnappingActive => _showGuides && _snapToGuides && Document.Guides.Count > 0;

    /// <summary>
    /// A move of <paramref name="startBounds"/> by <paramref name="delta"/>, adjusted so an edge or center lands on a guide
    /// within <see cref="SnapTolerancePx"/>. <paramref name="lockX"/>/<paramref name="lockY"/> keep a Shift-constrained axis untouched.
    /// </summary>
    internal Vec2 SnapMoveDelta(Rect2 startBounds, Vec2 delta, bool lockX = false, bool lockY = false)
    {
        if (!SnappingActive || startBounds.IsEmpty) return delta;
        var moved = startBounds.Transform(Affine2.Translation(delta));
        var r = GuideSnapping.SnapBounds(Document.Guides, moved, Viewport.ToDocLength(SnapTolerancePx), !lockX, !lockY);
        return delta + r.Delta;
    }

    /// <summary>A point (shape corner, dragged handle) snapped to the nearest vertical/horizontal guide.</summary>
    internal Vec2 SnapPoint(Vec2 doc, bool snapX = true, bool snapY = true)
    {
        if (!SnappingActive) return doc;
        return doc + GuideSnapping.SnapPoint(Document.Guides, doc, Viewport.ToDocLength(SnapTolerancePx), snapX, snapY).Delta;
    }

    // ---- Overlay ----

    private void AddGuideOverlays(List<OverlayItem> items)
    {
        if (!_showGuides) return;
        bool dragging = _guideMode != GuideMode.None;
        foreach (var g in Document.Guides)
        {
            if (ReferenceEquals(g, _selectedGuide) || (dragging && ReferenceEquals(g, _dragGuide))) continue;
            items.Add(new GuideOverlay(g.Origin, g.AngleDegrees, Selected: false, g.Locked));
        }
        // The active guide last, so red draws over the others.
        if (dragging) items.Add(new GuideOverlay(_dragOrigin, _dragAngle, Selected: true, _dragGuide?.Locked ?? false));
        else if (_selectedGuide is { } s) items.Add(new GuideOverlay(s.Origin, s.AngleDegrees, Selected: true, s.Locked));
    }
}
