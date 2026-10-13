using DeSignMage.Core.Geometry;

namespace DeSignMage.Core.Editing;

[Flags]
public enum ModifierKeys { None = 0, Shift = 1, Control = 2, Alt = 4 }

public enum PointerButton { None, Left, Middle, Right }

/// <summary>A pointer event in both coordinate spaces. <see cref="Button"/> is the button that changed (or None on hover).</summary>
public readonly record struct PointerInput(Vec2 Screen, Vec2 Doc, PointerButton Button, ModifierKeys Modifiers, int ClickCount = 1)
{
    public bool Shift => (Modifiers & ModifierKeys.Shift) != 0;
    public bool Control => (Modifiers & ModifierKeys.Control) != 0;
    public bool Alt => (Modifiers & ModifierKeys.Alt) != 0;
}

/// <summary>The keys editor logic cares about; the UI maps its own key enum onto these.</summary>
public enum EditorKey { Other, Escape, Delete, Back, Enter, Tab, Space, Left, Right, Up, Down, Insert, Home, End }

/// <summary>UI-independent cursor shapes; the shell maps them to platform cursors.</summary>
public enum CursorKind { Arrow, Cross, Move, Copy, SizeNS, SizeWE, SizeNWSE, SizeNESW, Slant, Rotate, Hand, Grabbing, ZoomIn, IBeam, Eyedropper, SelectHit, VectorDraw }

/// <summary>Transient on-canvas feedback drawn above the document (never saved).</summary>
public abstract record OverlayItem;

/// <summary>Selection bounding box, optionally with the 8 scale handles.</summary>
public sealed record SelectionOverlay(Rect2 Bounds, bool Handles) : OverlayItem;

public enum MarqueeKind { Select, Zoom }

/// <summary>Rubber-band rectangle in document coordinates.</summary>
public sealed record MarqueeOverlay(Rect2 Bounds, MarqueeKind Kind) : OverlayItem;

/// <summary>Outline of a shape being drawn, in document coordinates.</summary>
public sealed record OutlineOverlay(PathGeometry Path) : OverlayItem;

/// <summary>
/// Objects drawn displaced by <see cref="WorldDelta"/> while a drag is in progress. With <see cref="Copy"/>
/// the originals stay visible in place (Ctrl+drag duplicates).
/// </summary>
public sealed record TransformPreview(IReadOnlyList<Model.DocObject> Objects, Affine2 WorldDelta, bool Copy = false);

/// <summary>A left double-click on <see cref="Target"/> at <see cref="Screen"/> asks for its context menu.</summary>
public sealed record ContextMenuRequest(Model.DocObject Target, Vec2 Screen, MenuKind Kind = MenuKind.Object);

/// <summary>Which popup: the object's menu (groups, text, ...) or the vector menu inside the vector tool.</summary>
public enum MenuKind { Object, Vector, Loop }

/// <summary>A node mark in vector mode: big square = corner ("structural"), small cross = smooth node.</summary>
public readonly record struct VectorNodeMark(Vec2 Position, bool Corner, bool Selected, bool Start = false);

/// <summary>A loop drawn in vector mode / outline view: blue on the border, red inside, black when broken.</summary>
public readonly record struct OutlineLoop(PathGeometry Path, LoopKind Kind);

/// <summary>Vector mode: the target's loops, its nodes, BÃ©zier/arc handles and the vector under the cursor (document coordinates).</summary>
public sealed record VectorEditOverlay(
    IReadOnlyList<OutlineLoop> Loops,
    IReadOnlyList<VectorNodeMark> Nodes,
    IReadOnlyList<(Vec2 Node, Vec2 Handle)> Handles,
    PathGeometry? Highlight,
    PathGeometry? SelectedVector = null) : OverlayItem;

/// <summary>
/// Loop Edit tool (document coordinates): every loop colored by kind (or by direction), the selected loops dotted red, the
/// start/end point of each loop as a black square, optional loop numbers, and the dotted red preview of a loop being moved or copied.
/// </summary>
public sealed record LoopEditOverlay(
    IReadOnlyList<OutlineLoop> Loops,
    IReadOnlyList<PathGeometry> Selected,
    IReadOnlyList<PathGeometry> Hover,
    IReadOnlyList<PathGeometry> Preview,
    IReadOnlyList<Vec2> StartMarks,
    IReadOnlyList<(Vec2 Position, int Number)> Numbers) : OverlayItem;

/// <summary>
/// Text editing feedback in document coordinates: the caret as a vertical line, and the selected letters as quads.
/// </summary>
public sealed record TextEditOverlay(Vec2 CaretTop, Vec2 CaretBottom, IReadOnlyList<Vec2[]> Highlights) : OverlayItem;

/// <summary>The dots between the letters of the selected text; <see cref="Active"/> is the one being dragged (or -1).</summary>
public sealed record LetterDotsOverlay(IReadOnlyList<Vec2> Dots, int Active) : OverlayItem
{
    /// <summary>Radius of a drawn dot in screen DIPs (half of what it used to be). The area that grabs a dot is bigger: <see cref="EditorSession.HandleRadiusPx"/>.</summary>
    public const float RadiusPx = 1.75f;
}

/// <summary>
/// The crop tool's feedback in document coordinates: the picture's frame, the box to keep (null until one is dragged;
/// four corners, since a rotated picture has a rotated box) and the positions of its eight handles.
/// </summary>
public sealed record CropOverlay(Vec2[] Frame, Vec2[]? Box, IReadOnlyList<Vec2> Handles) : OverlayItem;

/// <summary>The large square at the far right end of selected text: drag to space all letters evenly (uniform tracking). Document coordinates.</summary>
public sealed record TrackingHandleOverlay(Vec2 Position, bool Active) : OverlayItem;
