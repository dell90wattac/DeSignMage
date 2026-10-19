using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// Layout tool (the default arrow):
/// <list type="bullet">
/// <item>Click selects (a member of a group selects the whole top-level group), Shift+click adds/removes,
/// double-click opens the context menu.</item>
/// <item>Drag on empty canvas = rubber band: with Auto Grouping (default) two or more caught objects become a
/// persistent group, nesting existing groups; one caught object is just selected. Shift adds to the selection.</item>
/// <item>Drag an object = move; Shift constrains to X or Y; Ctrl+drag = copy.</item>
/// <item>Corner handles = proportional scale anchored at the opposite corner; Ctrl = from center; Shift = height only.</item>
/// <item>Left/right middle handles = extend/condense (width only).</item>
/// <item>Top middle = slant (bottom edge stays put); with Ctrl it extends the height upward instead (bottom edge stays). Bottom
/// middle = rotate about the center; with Ctrl it extends the height downward instead (top edge stays). Ctrl is read at every move,
/// so pressing or releasing it mid-drag switches.</item>
/// <item>Arrow keys nudge (Shift ×5, Ctrl ÷5).</item>
/// <item>Drag the page's border (an edge changes one dimension, a corner two) = resize the page; objects stay where they are. Objects
/// and selection handles win over the border.</item>
/// </list>
/// Not yet: Ctrl "reset" variants (1:1, 0° slant), arc text, kerning handles, double-click popup menu.
/// </summary>
public sealed class SelectTool : Tool
{
    private enum Mode { None, PendingMove, Moving, Handle, Marquee, Dot, Tracking, PageResize }

    private Mode _mode;
    private Vec2 _startDoc, _startScreen;
    private Handle _handle;
    private bool _handleCtrl;                 // Ctrl state at the latest move of a handle drag (it picks the role of the top and bottom middles)
    private PageEdges _pageEdges;
    private Rect2 _pageStart;
    private PointerInput _lastPointer;
    private Rect2 _startBounds, _moveStartBounds;
    private Rect2 _marquee;
    private bool _marqueeAdds, _marqueeGroups;
    private bool _copying;
    private DocObject? _shiftGroupTarget;   // Shift+press on an unselected object: a click groups it with the selection, a drag adds it and moves
    private CursorKind _hoverCursor = CursorKind.Arrow;

    // Letter-spacing dot drag (live text): the object being kerned, the gap, and where the drag started.
    private TextObject? _dotText;
    private int _dotGap;
    private Affine2 _dotPlacementInverse = Affine2.Identity, _trackPlacementInverse = Affine2.Identity;   // text on a path: the start placement of the letter being dragged, kept so the drag has a steady axis
    private double _dotStartKerning, _dotStartLocalX;
    private IReadOnlyList<double> _dotOriginalKerning = [];

    // Tracking handle drag (live text): spreads every gap equally.
    private TextObject? _trackText;
    private double _trackStart, _trackStartLocalX;
    private int _trackGaps;

    public override string Id => "select";
    public override string DisplayName => "Select";

    public override string Hint => _mode switch
    {
        Mode.Dot => "To SPACE LETTERS: Drag the dot left or right (letters to its right move)",
        Mode.Tracking => "To TRACK: Drag the square left or right (all gaps change equally)",
        Mode.Moving => "To MOVE: Drag (Shift: constrain X,Y   Ctrl: copy)",
        Mode.PageResize => "To RESIZE THE PAGE: Drag the border (objects stay where they are)",
        Mode.Handle => SelectionHandles.Role(_handle, _handleCtrl) switch
        {
            HandleRole.Scale => "To SCALE: Drag (Ctrl: from center   Shift: height only)",
            HandleRole.Extend => "To EXTEND/CONDENSE: Drag left or right",
            HandleRole.ExtendUp => "To EXTEND UP: Drag up or down (the bottom edge stays)",
            HandleRole.ExtendDown => "To EXTEND DOWN: Drag up or down (the top edge stays)",
            HandleRole.Slant => "To SLANT: Drag left or right (Ctrl: extend up)",
            _ => "To ROTATE: Drag around the center (Ctrl: extend down)",
        },
        _ => "To GROUP: Press left button and drag a box (Shift: add to selection)   To ZOOM: Press right button",
    };

    public override CursorKind Cursor => _mode switch
    {
        Mode.Moving => _copying ? CursorKind.Copy : CursorKind.Move,
        Mode.Dot or Mode.Tracking => CursorKind.SizeWE,
        Mode.PageResize => PageBorder.Cursor(_pageEdges),
        Mode.Handle => SelectionHandles.Cursor(_handle, _handleCtrl),
        _ => _hoverCursor,
    };

    public override void PointerDown(PointerInput e)
    {
        var sel = Session.Selection;
        _draggedWithEffects = null;
        _startDoc = e.Doc;
        _startScreen = e.Screen;
        _lastPointer = e;

        if (e.ClickCount < 2 && (TryBeginTracking(e) || TryBeginDot(e))) return;

        if (!sel.IsEmpty && !Session.SelectionLocked &&   // a locked selection has no handles
            SelectionHandles.HitTest(Session.Viewport, sel.GetBounds(), e.Screen, Session.HandleRadiusPx) is { } h)
        {
            _mode = Mode.Handle;
            _handle = h;
            _handleCtrl = e.Control;
            _startBounds = sel.GetBounds();
            return;
        }

        var hit = Session.HitTestAt(e.Doc);
        if (hit is not null && e.ClickCount >= 2 && !e.Shift)   // quick Shift+clicks on different objects are grouping clicks, not double-clicks
        {
            // Left double-click (the right button is zoom only), per the user: a single or combined object opens the
            // vector tool on it; a group (or text/images, which have no editable vectors yet) opens its context menu.
            if (!sel.Contains(hit)) sel.Set(hit);
            _mode = Mode.None;
            if (hit is TextObject text)
            {
                Session.EditText(text, e.Doc);   // R6: double-click on text = edit mode (not the context menu)
                return;
            }
            if (hit is ImageObject picture)
            {
                Session.RequestPictureEdit(picture);   // double-click on a picture opens the Picture Editor (not a menu)
                return;
            }
            if (hit is not GroupObject && OutlineView.IsEditable(hit)) Session.EditVectors(hit);
            else Session.RequestContextMenu(hit, e.Screen);
            return;
        }
        if (hit is not null)
        {
            // Shift+click (R2) groups the clicked object with the current selection, but only on release without a drag,
            // because Shift+drag means "move constrained". Shift+click on something already selected does nothing.
            _shiftGroupTarget = null;
            if (!sel.Contains(hit))
            {
                if (e.Shift && !sel.IsEmpty) _shiftGroupTarget = hit;
                else sel.Set(hit);   // plain click, or Shift with nothing selected: just select it
            }
            _mode = Mode.PendingMove;
            return;
        }

        // Not on an object or a handle: the page's border is next (R20). Dragging it resizes the page.
        if (PageBorder.HitTest(Session.Viewport, Session.Document.Page.Bounds, e.Screen, Session.PageBorderRadiusPx) is var edges and not PageEdges.None)
        {
            _mode = Mode.PageResize;
            _pageEdges = edges;
            _pageStart = Session.Document.Page.Bounds;
            return;
        }

        // A double-click on the empty layout (inside the page, on no object, guide or handle) opens Layout Properties.
        if (e.ClickCount >= 2 && !e.Shift && Session.Document.Page.Bounds.Contains(e.Doc))
        {
            sel.Clear();
            _mode = Mode.None;
            Session.RequestLayoutProperties();
            return;
        }

        // Empty canvas: a plain click deselects at once. With Shift, a drag is a rubber band that adds to the selection, and a
        // Shift+click (no drag) deselects too (decided on release).
        if (!e.Shift) sel.Clear();
        _mode = Mode.Marquee;
        _marqueeAdds = e.Shift;
        _marqueeGroups = Session.AutoGrouping || e.Control;
        _marquee = Rect2.FromPoints(e.Doc, e.Doc);
    }

    public override void PointerMove(PointerInput e)
    {
        _lastPointer = e;
        switch (_mode)
        {
            case Mode.None:
                UpdateHover(e);
                break;
            case Mode.PendingMove:
                if (Vec2.Distance(e.Screen, _startScreen) < Session.ClickThresholdPx) break;
                _mode = Mode.Moving;
                if (_shiftGroupTarget is not null)
                {
                    Session.Selection.Add([_shiftGroupTarget]);   // Shift+drag on an unselected object: it joins the move
                    _shiftGroupTarget = null;
                }
                _moveStartBounds = Session.Selection.GetBounds();
                goto case Mode.Moving;
            case Mode.Moving:
                var d = e.Doc - _startDoc;
                bool lockX = false, lockY = false;
                if (e.Shift)
                {
                    bool alongX = Math.Abs(d.X) >= Math.Abs(d.Y);
                    d = alongX ? new Vec2(d.X, 0) : new Vec2(0, d.Y);
                    lockY = alongX;
                    lockX = !alongX;
                }
                d = Session.SnapMoveDelta(_moveStartBounds, d, lockX, lockY);   // edges/centers snap to ruler guides
                _copying = e.Control;
                Session.Preview = new TransformPreview(_copying ? Session.Selection.Items.ToArray() : DraggedWithLiveEffects(), Affine2.Translation(d), _copying);
                break;
            case Mode.Handle:
                _handleCtrl = e.Control;   // read at every move: pressing or releasing Ctrl mid-drag switches the role
                Session.Preview = new TransformPreview(DraggedWithLiveEffects(), HandleTransform(SnapHandlePointer(e)));
                break;
            case Mode.PageResize:
                Session.PagePreview = PageBorder.Drag(_pageStart, _pageEdges, e.Doc - _startDoc);
                break;
            case Mode.Dot:
                DragDot(e);
                break;
            case Mode.Tracking:
                DragTracking(e);
                break;
            case Mode.Marquee:
                _marquee = Rect2.FromPoints(_startDoc, e.Doc);
                Redraw();
                break;
        }
    }

    private DocObject[]? _draggedWithEffects;

    /// <summary>
    /// What a drag preview moves: the selection and the live effects that follow it from outside it (a standalone outline, a shadow of a member picked alone),
    /// so they go along on screen; the release rebuilds them exactly. Worked out once per drag.
    /// </summary>
    private DocObject[] DraggedWithLiveEffects() => _draggedWithEffects ??= Effects.LiveEffects.WithFollowers(Session.Document, Session.Selection.Items);

    public override void PointerUp(PointerInput e)
    {
        // The release position counts even if no move event arrived there.
        if (_mode != Mode.None) PointerMove(e);
        _draggedWithEffects = null;
        switch (_mode)
        {
            case Mode.Moving:
            case Mode.Handle:
                var p = Session.Preview;
                Session.Preview = null;
                if (p is null || p.WorldDelta.IsIdentity) break;
                if (p.Copy) CommitCopy(p);
                else Session.TransformSelection(_mode == Mode.Moving ? "Move" : RoleName(SelectionHandles.Role(_handle, _handleCtrl)), p.WorldDelta);
                break;
            case Mode.PageResize:
                var page = Session.PagePreview;
                Session.PagePreview = null;
                if (page is { } resized) Session.ResizePage(resized);   // one undo step for the whole drag; a click without a drag changes nothing
                if (Vec2.Distance(e.Screen, _startScreen) < Session.ClickThresholdPx && !e.Shift) Session.Selection.Clear();   // a click near the border deselects, as on empty canvas
                break;
            case Mode.PendingMove:
                if (_shiftGroupTarget is not null)
                    Session.GroupObjects([.. Session.Selection.Items, _shiftGroupTarget]);   // R2: same command as a rubber band; the new group is selected
                break;
            case Mode.Dot:
                EndDot();
                break;
            case Mode.Tracking:
                EndTracking();
                break;
            case Mode.Marquee:
                if (Vec2.Distance(e.Screen, _startScreen) >= Session.ClickThresholdPx)
                {
                    var found = HitTesting.EnclosedBy(Session.Document, _marquee).ToList();
                    if (_marqueeAdds) Session.Selection.Add(found);
                    else if (_marqueeGroups && found.Count >= 2) Session.GroupObjects(found);   // R2: box = persistent group
                    else Session.Selection.Set(found);
                }
                else if (_marqueeAdds)
                {
                    Session.Selection.Clear();   // Shift+click on empty canvas deselects, like a plain click
                }
                break;
        }
        _mode = Mode.None;
        _copying = false;
        _shiftGroupTarget = null;
        UpdateHover(e);
        Redraw();
    }

    public override bool KeyDown(EditorKey key, ModifierKeys modifiers)
    {
        if (key == EditorKey.Escape)
        {
            if (_mode != Mode.None) { Cancel(); return true; }
            if (!Session.Selection.IsEmpty) { Session.Selection.Clear(); return true; }
            return false;
        }

        Vec2? dir = key switch
        {
            EditorKey.Left => new Vec2(-1, 0),
            EditorKey.Right => new Vec2(1, 0),
            EditorKey.Up => new Vec2(0, -1),
            EditorKey.Down => new Vec2(0, 1),
            _ => null,
        };
        if (dir is null || Session.Selection.IsEmpty || _mode != Mode.None) return false;

        double step = Session.NudgeDistance;
        if ((modifiers & ModifierKeys.Shift) != 0) step *= 5;
        if ((modifiers & ModifierKeys.Control) != 0) step /= 5;
        Session.NudgeSelection(dir.Value * step);
        return true;
    }

    public override void Cancel()
    {
        if (_mode == Mode.Dot && _dotText is not null)
        {
            _dotText.Kerning = _dotOriginalKerning;
            _dotText = null;
            Session.Document.NotifyChanged();
        }
        if (_mode == Mode.Tracking && _trackText is not null)
        {
            _trackText.Tracking = _trackStart;
            _trackText = null;
            Session.Document.NotifyChanged();
        }
        if (_mode is Mode.Moving or Mode.Handle) Session.Preview = null;
        if (_mode == Mode.PageResize) Session.PagePreview = null;
        _mode = Mode.None;
        _shiftGroupTarget = null;
        _copying = false;
        Redraw();
    }

    public override void BuildOverlay(List<OverlayItem> items)
    {
        if (!Session.Selection.IsEmpty)
        {
            var b = Session.Selection.GetBounds();
            if (Session.Preview is { Copy: false } p) b = b.Transform(p.WorldDelta);
            items.Add(new SelectionOverlay(b, Handles: _mode is Mode.None or Mode.PendingMove && !Session.SelectionLocked));
            if (_mode is Mode.None or Mode.PendingMove or Mode.Dot && SelectedText() is { } text)
                items.Add(new LetterDotsOverlay(text.Layout.PlacedGapPoints().Select(g => text.GetWorldTransform().Transform(g.Point)).ToArray(),
                    _mode == Mode.Dot ? DotIndex(text, _dotGap) : -1));
            if (_mode is Mode.None or Mode.PendingMove or Mode.Tracking && SelectedText() is { } tt && TrackingHandleDoc(tt) is { } handle)
                items.Add(new TrackingHandleOverlay(handle, _mode == Mode.Tracking));
        }
        if (_mode == Mode.Marquee && !_marquee.IsEmpty)
            items.Add(new MarqueeOverlay(_marquee, MarqueeKind.Select));
    }

    /// <summary>The hover cursor and a handle drag follow a Ctrl press or release without waiting for the pointer to move.</summary>
    public override void ModifiersChanged(ModifierKeys modifiers)
    {
        if (_mode is not (Mode.None or Mode.Handle)) return;
        PointerMove(_lastPointer with { Modifiers = modifiers });
        Redraw();
    }

    /// <summary>Scale and extend/condense drags snap the dragged edge to guides; slant and rotate don't.</summary>
    private PointerInput SnapHandlePointer(PointerInput e) => SelectionHandles.Role(_handle, e.Control) switch
    {
        HandleRole.Scale => e with { Doc = Session.SnapPoint(e.Doc) },
        HandleRole.Extend => e with { Doc = Session.SnapPoint(e.Doc, snapY: false) },
        HandleRole.ExtendUp or HandleRole.ExtendDown => e with { Doc = Session.SnapPoint(e.Doc, snapX: false) },
        _ => e,
    };

    /// <summary>World-space transform for the current handle drag.</summary>
    internal Affine2 HandleTransform(PointerInput e)
    {
        var b = _startBounds;
        var grip = SelectionHandles.Position(b, _handle);
        switch (SelectionHandles.Role(_handle, e.Control))
        {
            case HandleRole.ExtendUp:
            case HandleRole.ExtendDown:
            {
                // A plain vertical scale of everything selected, the opposite edge fixed (Ctrl on the top or bottom middle).
                var anchor = SelectionHandles.Position(b, SelectionHandles.Opposite(_handle));
                return Affine2.Scale(1, NonZero(Ratio(e.Doc.Y - anchor.Y, grip.Y - anchor.Y)), anchor);
            }
            case HandleRole.Scale:
            {
                var anchor = e.Control ? b.Center : SelectionHandles.Position(b, SelectionHandles.Opposite(_handle));
                double sx = Ratio(e.Doc.X - anchor.X, grip.X - anchor.X);
                double sy = Ratio(e.Doc.Y - anchor.Y, grip.Y - anchor.Y);
                if (e.Shift) sx = 1;                                      // "Heighten": height only
                else sx = sy = Math.Abs(sx) >= Math.Abs(sy) ? sx : sy;    // proportional
                return Affine2.Scale(NonZero(sx), NonZero(sy), anchor);
            }
            case HandleRole.Extend:
            {
                var anchor = SelectionHandles.Position(b, SelectionHandles.Opposite(_handle));
                return Affine2.Scale(NonZero(Ratio(e.Doc.X - anchor.X, grip.X - anchor.X)), 1, anchor);
            }
            case HandleRole.Slant:
            {
                // Shear in x proportional to height above the bottom edge, so the top edge follows the pointer.
                double k = b.Height > 1e-9 ? (e.Doc.X - grip.X) / b.Height : 0;
                return new Affine2(1, 0, -k, 1, k * b.Bottom, 0);
            }
            default:
            {
                var c = b.Center;
                double a0 = Math.Atan2(_startDoc.Y - c.Y, _startDoc.X - c.X);
                double a1 = Math.Atan2(e.Doc.Y - c.Y, e.Doc.X - c.X);
                double angle = a1 - a0;
                if (e.Control) angle = Math.Round(angle / (Math.PI / 2)) * (Math.PI / 2);
                return Affine2.Rotation(angle, c);
            }
        }
    }

    private void CommitCopy(TransformPreview p)
    {
        using var tx = Session.History.BeginTransaction("Copy");
        // The copies remember where their originals stand in the drawing order, so a copied group keeps whatever is between its members (R22).
        var copies = Stacking.CloneForCopy(Session.Document, p.Objects);
        for (int i = 0; i < copies.Count; i++)
            copies[i].Transform = TransformObjectsCommand.ComposeWorld(p.Objects[i], p.WorldDelta);
        // Each copy goes on top of the layer its original is on.
        foreach (var onLayer in Enumerable.Range(0, copies.Count).GroupBy(i => p.Objects[i].Layer ?? Session.Document.ActiveLayer))
            Session.History.Execute(new AddObjectsCommand(onLayer.Key, onLayer.Select(i => copies[i])));
        Session.Selection.Set(copies);
    }

    // ---- Letter dots (live text, R6) ----

    /// <summary>The selected object when it is exactly one live text: only then are the letter dots shown.</summary>
    private TextObject? SelectedText() => Session.Selection is { Count: 1 } s && s.Items[0] is TextObject { Locked: false } t ? t : null;

    private static Affine2 Inverse(Affine2 m) => m.IsInvertible ? m.Invert() : Affine2.Identity;

    /// <summary>The pointer's position along the straight text: the document point taken into the text's space, then back off the letter's placement on a path (identity for ordinary text).</summary>
    private static double StraightX(TextObject t, Affine2 placementInverse, Vec2 doc) =>
        placementInverse.Transform(t.GetWorldTransform().Invert().Transform(doc)).X;

    private static int DotIndex(TextObject t, int gap) => t.Layout.GapPoints().TakeWhile(g => g.Gap != gap).Count();

    /// <summary>The gap whose dot is within the handle radius of the pointer, or -1. The drawn dot is small; the grab area stays handle-sized.</summary>
    private int DotAt(TextObject t, Vec2 screen)
    {
        var w = t.GetWorldTransform();
        int best = -1;
        double bestD = Session.HandleRadiusPx;
        foreach (var (gap, point) in t.Layout.PlacedGapPoints())   // on a path the dots sit on the letters' placed positions
        {
            double d = Vec2.Distance(Session.Viewport.ToScreen(w.Transform(point)), screen);
            if (d <= bestD) { bestD = d; best = gap; }
        }
        return best;
    }

    // ---- Tracking handle (live text, R6): the larger square at the far right end of the text ----

    /// <summary>First line that has gaps (the one the handle belongs to), or null for text with fewer than two letters.</summary>
    private static Text.TextLine? TrackingLine(TextObject t) => t.Layout.Lines.Cast<Text.TextLine?>().FirstOrDefault(l => l!.LetterCount >= 2);

    /// <summary>Where the handle is, in document coordinates: just past the end of the line (a fixed distance on screen), at mid letter height.</summary>
    internal Vec2? TrackingHandleDoc(TextObject t)
    {
        if (TrackingLine(t) is not { } line) return null;
        var w = t.GetWorldTransform();
        var layout = t.Layout;
        var place = layout.PlacementOf(line.FirstLetter + line.LetterCount - 1);   // on a path: the last letter, so the handle sits past the end of the text along the curve
        var dir = w.TransformVector(place.TransformVector(new Vec2(1, 0)));
        if (dir.Length < 1e-9) return null;
        var end = w.Transform(place.Transform(new Vec2(line.Left + line.Width, line.Baseline - layout.Ascent * 0.4)));
        return end + dir / dir.Length * Session.Viewport.ToDocLength(TrackingHandleOffsetPx);
    }

    public const double TrackingHandleOffsetPx = 14;

    private bool OnTrackingHandle(TextObject t, Vec2 screen) =>
        TrackingHandleDoc(t) is { } h && Vec2.Distance(Session.Viewport.ToScreen(h), screen) <= Session.HandleRadiusPx + 1.5;

    private bool TryBeginTracking(PointerInput e)
    {
        if (SelectedText() is not { } t || !t.GetWorldTransform().IsInvertible || !OnTrackingHandle(t, e.Screen)) return false;
        _mode = Mode.Tracking;
        _trackText = t;
        _trackStart = t.Tracking;
        var line = TrackingLine(t)!;
        _trackGaps = line.LetterCount - 1;
        _trackPlacementInverse = Inverse(t.Layout.PlacementOf(line.FirstLetter + line.LetterCount - 1));
        _trackStartLocalX = StraightX(t, _trackPlacementInverse, e.Doc);
        return true;
    }

    /// <summary>
    /// Live preview: the pointer's movement along the text is shared equally by all gaps of the line, so the end of the text follows
    /// the handle. Tracking adds the same amount to every gap and leaves each gap's own dot offset alone.
    /// </summary>
    private void DragTracking(PointerInput e)
    {
        var t = _trackText!;
        double x = StraightX(t, _trackPlacementInverse, e.Doc);
        t.Tracking = _trackStart + (x - _trackStartLocalX) / Math.Max(t.FontSize, 0.001) / _trackGaps;
        Session.Document.NotifyChanged();
    }

    private void EndTracking()
    {
        var t = _trackText!;
        _trackText = null;
        double final = t.Tracking;
        t.Tracking = _trackStart;   // silently back so the command records the whole drag
        if (Math.Abs(final - _trackStart) < 1e-9) { Session.Document.NotifyChanged(); return; }
        Session.History.Execute(new SetPropertyCommand<TextObject, double>("Tracking", [t], o => o.Tracking, (o, v) => o.Tracking = v, final));
    }

    private bool TryBeginDot(PointerInput e)
    {
        if (SelectedText() is not { } t || !t.GetWorldTransform().IsInvertible) return false;
        int gap = DotAt(t, e.Screen);
        if (gap < 0) return false;
        _mode = Mode.Dot;
        _dotText = t;
        _dotGap = gap;
        _dotOriginalKerning = t.Kerning;
        _dotStartKerning = t.KerningAt(gap);
        _dotPlacementInverse = Inverse(t.Layout.PlacementOf(gap + 1));
        _dotStartLocalX = StraightX(t, _dotPlacementInverse, e.Doc);
        return true;
    }

    /// <summary>
    /// Live preview: the dot's gap grows by however far the pointer moved along the text (in ems, so it survives size changes);
    /// every letter to the right of the dot moves with it (an open question in R6: the spacing might be spread instead).
    /// The change is applied straight to the object and becomes one command when the button is released.
    /// </summary>
    private void DragDot(PointerInput e)
    {
        var t = _dotText!;
        double x = StraightX(t, _dotPlacementInverse, e.Doc);
        t.Kerning = t.KerningWith(_dotGap, _dotStartKerning + (x - _dotStartLocalX) / Math.Max(t.FontSize, 0.001));
        Session.Document.NotifyChanged();
    }

    private void EndDot()
    {
        var t = _dotText!;
        _dotText = null;
        var final = t.Kerning;
        t.Kerning = _dotOriginalKerning;   // silently back to the start so the command records the whole drag
        if (final.SequenceEqual(_dotOriginalKerning)) { Session.Document.NotifyChanged(); return; }
        Session.History.Execute(new SetPropertyCommand<TextObject, IReadOnlyList<double>>(
            "Letter Spacing", [t], o => o.Kerning, (o, v) => o.Kerning = v, final));
    }

    private static string RoleName(HandleRole r) => r switch
    {
        HandleRole.Scale => "Scale",
        HandleRole.Extend => "Extend/Condense",
        HandleRole.Slant => "Slant",
        HandleRole.ExtendUp => "Extend Up",
        HandleRole.ExtendDown => "Extend Down",
        _ => "Rotate",
    };

    private static double Ratio(double num, double den) => Math.Abs(den) > 1e-9 ? num / den : 1;
    private static double NonZero(double s) => Math.Abs(s) < 1e-3 ? (s < 0 ? -1e-3 : 1e-3) : s;

    private void UpdateHover(PointerInput e)
    {
        var sel = Session.Selection;
        if (SelectedText() is { } text && (DotAt(text, e.Screen) >= 0 || OnTrackingHandle(text, e.Screen)))
            _hoverCursor = CursorKind.SizeWE;
        else if (!sel.IsEmpty && !Session.SelectionLocked && SelectionHandles.HitTest(Session.Viewport, sel.GetBounds(), e.Screen, Session.HandleRadiusPx) is { } h)
            _hoverCursor = SelectionHandles.Cursor(h, e.Control);
        else if (Session.HitTestAt(e.Doc) is not null)
            _hoverCursor = CursorKind.SelectHit;   // R10 item 7: arrow with a crosshair at its tip = "a click here selects something"
        else if (PageBorder.HitTest(Session.Viewport, Session.Document.Page.Bounds, e.Screen, Session.PageBorderRadiusPx) is var edges and not PageEdges.None)
            _hoverCursor = PageBorder.Cursor(edges);   // R20: only where no object and no selection handle is
        else
            _hoverCursor = CursorKind.Arrow;
    }
}
