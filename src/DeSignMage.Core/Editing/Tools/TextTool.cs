using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;

namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// The text tool and text edit mode (requirement R6).
/// <list type="bullet">
/// <item><b>Drag a box</b> on empty canvas (R24): its <b>height</b> sets the letter height (a capital letter is as tall as the box; the width is
/// ignored and nothing wraps), the baseline sits on the box's bottom edge and the line starts at the box's left edge (centered/ending at the box's center/right
/// edge for centered/right-aligned text); then you type and the line grows sideways without limit. A plain click (no drag) keeps the default size.
/// <br/>Click on empty canvas: a new text object starts there (baseline-left at the click) and you type. <b>Enter starts the next line as its own, separate
/// text object</b> one line below (basic text is never kept as several lines); Esc finishes. Click on text: edit it.
/// The Layout tool's double-click on text enters the same mode ("If you double click on text, it allows you to start typing").</item>
/// <item>Caret keys: Left/Right/Home/End (Shift extends the selection), Backspace/Delete; mouse click places the caret, drag selects,
/// double-click selects a word. When editing was started by <b>double-click</b> (Layout tool), <b>Enter finishes editing and the object stays text</b>; Esc and clicking elsewhere finish in both modes.</item>
/// <item>While editing, changes are applied straight to the object so the canvas follows every key; when editing ends the net change
/// becomes <b>one</b> undo step (<see cref="SetTextCommand"/>, or Add Text for a new object; text left empty is discarded).</item>
/// </list>
/// Per-gap kerning follows the letters through insertions and deletions. Not yet: IME composition, copy/paste of text, multi-line entry.
/// </summary>
public sealed class TextTool : Tool
{
    private TextObject? _target;
    private bool _isNew, _dragging, _enterMakesNewLine;
    private IObjectContainer? _newContainer;
    private string _origText = "";
    private IReadOnlyList<double> _origKerning = [];
    private int _caret, _anchor;

    // The size box being dragged (nothing exists yet; the object is created when the button is released).
    private Vec2? _boxStart;
    private Vec2 _boxEnd, _boxStartScreen;

    public override string Id => "text";
    public override string DisplayName => "Text";
    public override CursorKind Cursor => CursorKind.IBeam;

    public override string Hint => _boxStart is not null
        ? "To SET THE LETTER HEIGHT: Drag a box (its height = capital letter height; width is ignored)   Esc: cancel"
        : _target is null
        ? "To ADD TEXT: Click, or drag a box to set the letter height   To EDIT TEXT: Click on it   To LAYOUT: Press Esc"
        : _enterMakesNewLine
            ? "Enter: next line (its own text object)   Esc: finish   Move caret: arrows, Home, End (Shift: select)   Click: place caret"
            : "To FINISH: Enter or Esc (it stays text)   Move caret: arrows, Home, End (Shift: select)   Click: place caret";

    public bool IsEditing => _target is not null;
    /// <summary>True while editing an object that does not exist in the history yet (its Add Text step comes when editing ends).</summary>
    public bool IsNewObject => _target is not null && _isNew;
    public TextObject? Target => _target;
    public int Caret => _caret;
    public int SelectionStart => Math.Min(_caret, _anchor);
    public int SelectionEnd => Math.Max(_caret, _anchor);
    public bool HasSelection => _caret != _anchor;

    public string SelectedText
    {
        get
        {
            if (_target is null || !HasSelection) return "";
            var offs = TextElements.Offsets(_target.Text);
            int a = Math.Min(SelectionStart, offs.Length - 1), b = Math.Min(SelectionEnd, offs.Length - 1);
            return _target.Text[offs[a]..offs[b]];
        }
    }

    // ---- Entering and leaving edit mode ----

    /// <summary>
    /// Starts a new text object with its baseline origin at <paramref name="at"/> (document coordinates), at the default size or
    /// <paramref name="fontSize"/> (em, points).
    /// </summary>
    public void BeginNew(Vec2 at, double? fontSize = null)
    {
        Finish(returnToSelect: false);
        var f = Session.TextDefaults;
        var t = new TextObject("", f.Family, fontSize ?? f.Size)
        {
            FontWeight = f.Weight, Italic = f.Italic, Tracking = f.Tracking, Alignment = f.Alignment,
            Style = f.Style, Transform = Affine2.Translation(at),
        };
        _newContainer = Session.Document.ActiveLayer;
        Session.Document.Insert(_newContainer, -1, t);   // live while typing; becomes one "Add Text" step when finished
        Enter(t, isNew: true, caret: 0, newLines: true);
    }

    /// <summary>
    /// Enter while typing with the text tool: the line is finished as its own text object and a new one starts below it
    /// (same font and look, one normal line down, in the same direction as the text). Basic text is never kept as multiple lines.
    /// </summary>
    private void StartNextLine()
    {
        var prev = _target!;
        double step = prev.Layout.LineHeight;
        var next = new TextObject("", prev.FontFamily, prev.FontSize)
        {
            FontWeight = prev.FontWeight, Italic = prev.Italic, Tracking = prev.Tracking, Alignment = prev.Alignment,
            Style = prev.Style, LineSpacing = prev.LineSpacing,
            Transform = Affine2.Translation(0, step * prev.LineSpacing) * prev.Transform,
        };
        IObjectContainer container = _isNew ? _newContainer! : prev.Parent as Layer ?? Session.Document.ActiveLayer;
        Finish(returnToSelect: false);   // the finished line becomes a normal undo step ("Add Text" / "Edit Text")
        _newContainer = container;
        Session.Document.Insert(container, -1, next);
        Enter(next, isNew: true, caret: 0, newLines: true);
    }

    /// <summary>Starts editing an existing text object with the caret at letter <paramref name="caret"/> (default: the end).</summary>
    /// <param name="enterStartsNewLine">True when typing with the text tool (Enter starts the next line as a new object);
    /// false for double-click editing, where Enter ends editing.</param>
    public void BeginEdit(TextObject t, int? caret = null, bool enterStartsNewLine = false)
    {
        Finish(returnToSelect: false);
        Enter(t, isNew: false, caret ?? t.Layout.LetterCount, enterStartsNewLine);
    }

    private void Enter(TextObject t, bool isNew, int caret, bool newLines)
    {
        _target = t;
        _isNew = isNew;
        _enterMakesNewLine = newLines;
        _origText = t.Text;
        _origKerning = t.Kerning;
        _caret = _anchor = Math.Clamp(caret, 0, t.Layout.LetterCount);
        Session.Selection.Set(t);
        Session.Document.NotifyChanged();
        Redraw();
    }

    /// <summary>
    /// Ends edit mode. The net change is committed as one command (or discarded if nothing changed or the text is empty).
    /// Returns to the Layout tool with the text selected unless <paramref name="returnToSelect"/> is false.
    /// </summary>
    public void Finish(bool returnToSelect = true)
    {
        if (_target is null) return;
        var t = _target;
        _target = null;
        _dragging = false;
        var doc = Session.Document;
        var history = Session.History;

        if (_isNew)
        {
            if (doc.Contains(t)) doc.Remove(t);
            if (t.Text.Length > 0) history.Execute(new AddObjectCommand(_newContainer!, t, -1));
            else doc.NotifyChanged();
        }
        else if (doc.Contains(t))
        {
            string newText = t.Text;
            var newKerning = t.Kerning;
            t.Text = _origText;
            t.Kerning = _origKerning;   // back to the old state, silently, so the command records it
            if (newText.Length == 0) history.Execute(new DeleteObjectsCommand([t]));
            else if (newText != _origText || !newKerning.SequenceEqual(_origKerning)) history.Execute(new SetTextCommand(t, newText, newKerning));
            else doc.NotifyChanged();
        }

        if (doc.Contains(t)) Session.Selection.Set(t); else Session.Selection.Clear();
        if (returnToSelect) Session.SetTool("select");
        Redraw();
    }

    public override void Activate() { }

    /// <summary>Switching tools, undo and redo all end edit mode first, keeping what was typed.</summary>
    public override void Cancel()
    {
        _boxStart = null;
        Finish(returnToSelect: false);
    }

    // ---- Editing operations (letter indices; Count = LetterCount) ----

    private int Count => _target?.Layout.LetterCount ?? 0;

    /// <summary>Types <paramref name="text"/> at the caret, replacing the selection. Control characters are ignored.</summary>
    public void Insert(string text)
    {
        if (_target is null) return;
        text = new string(text.Where(c => !char.IsControl(c)).ToArray());
        if (text.Length == 0) return;
        Replace(SelectionStart, SelectionEnd, text);
    }

    public void Backspace()
    {
        if (_target is null) return;
        if (HasSelection) Replace(SelectionStart, SelectionEnd, "");
        else if (_caret > 0) Replace(_caret - 1, _caret, "");
    }

    public void DeleteForward()
    {
        if (_target is null) return;
        if (HasSelection) Replace(SelectionStart, SelectionEnd, "");
        else if (_caret < Count) Replace(_caret, _caret + 1, "");
    }

    public void SelectAll()
    {
        if (_target is null) return;
        _anchor = 0;
        _caret = Count;
        Redraw();
    }

    public void MoveCaret(int index, bool extend)
    {
        if (_target is null) return;
        _caret = Math.Clamp(index, 0, Count);
        if (!extend) _anchor = _caret;
        Redraw();
    }

    private void Replace(int from, int to, string insert)
    {
        var t = _target!;
        var offs = TextElements.Offsets(t.Text);
        int oldCount = offs.Length - 1;
        from = Math.Clamp(from, 0, oldCount);
        to = Math.Clamp(to, from, oldCount);
        string newText = t.Text[..offs[from]] + insert + t.Text[offs[to]..];
        int inserted = TextElements.Count(newText) - (oldCount - (to - from));   // clusters can merge, so recount
        t.Kerning = TextElements.RemapKerning(t.Kerning, from, to - from, Math.Max(inserted, 0));
        t.Text = newText;
        _caret = _anchor = Math.Min(from + Math.Max(inserted, 0), TextElements.Count(newText));
        Session.Document.NotifyChanged();
        Redraw();
    }

    // ---- Pointer ----

    private Vec2 ToLocal(TextObject t, Vec2 doc) => t.GetWorldTransform().IsInvertible ? t.GetWorldTransform().Invert().Transform(doc) : doc;

    public override void PointerDown(PointerInput e)
    {
        double tol = Session.Viewport.ToDocLength(Session.PickTolerancePx);
        if (_target is not null)
        {
            if (HitTesting.Hits(_target, e.Doc, tol))
            {
                int index = _target.Layout.IndexAt(ToLocal(_target, e.Doc));
                if (e.ClickCount >= 2) SelectWordAt(index);
                else
                {
                    _caret = index;
                    if (!e.Shift) _anchor = index;
                    _dragging = true;
                }
                Redraw();
                return;
            }
            Finish(returnToSelect: false);   // a click elsewhere ends the edit, then acts like a fresh click
        }

        if (HitTesting.HitPainted(Session.Document, e.Doc, tol) is TextObject hit)
        {
            BeginEdit(hit, hit.Layout.IndexAt(ToLocal(hit, e.Doc)), enterStartsNewLine: true);
            _dragging = true;
            return;
        }
        // Empty canvas: a click starts text at the default size, a drag sets the letter height (decided on release).
        _boxStart = _boxEnd = Session.SnapPoint(e.Doc);
        _boxStartScreen = e.Screen;
        Redraw();
    }

    /// <summary>The size box, once the pointer has moved far enough to count as a drag.</summary>
    private Rect2? Box => _boxStart is { } s && Vec2.Distance(Session.Viewport.ToScreen(s), Session.Viewport.ToScreen(_boxEnd)) >= Session.ClickThresholdPx
        ? Rect2.FromPoints(s, _boxEnd) : null;

    private void EndBox(PointerInput e)
    {
        if (_boxStart is not { } start) return;
        _boxEnd = Session.SnapPoint(e.Doc);
        var box = Box;
        _boxStart = null;
        double height = box?.Height ?? 0;
        if (box is null || height * Session.Viewport.Zoom < Session.ClickThresholdPx)
        {
            BeginNew(start);   // a click, or a drag too flat to mean a height
            return;
        }
        var f = Session.TextDefaults;
        double size = height / TextEngine.CapHeightEm(f.Family, f.Weight, f.Italic);   // capital letters as tall as the box
        double x = f.Alignment switch { TextAlignment.Center => box.Value.Center.X, TextAlignment.Right => box.Value.Right, _ => box.Value.Left };
        BeginNew(new Vec2(x, box.Value.Bottom), size);
    }

    public override void PointerMove(PointerInput e)
    {
        if (_boxStart is not null) { _boxEnd = Session.SnapPoint(e.Doc); Redraw(); return; }
        if (_target is null || !_dragging) return;
        _caret = _target.Layout.IndexAt(ToLocal(_target, e.Doc));
        Redraw();
    }

    public override void PointerUp(PointerInput e)
    {
        if (_boxStart is not null) { EndBox(e); return; }
        if (_dragging) PointerMove(e);
        _dragging = false;
    }

    private void SelectWordAt(int index)
    {
        var t = _target!;
        var letters = TextElements.Split(t.Text);
        bool Space(int i) => i >= letters.Count || string.IsNullOrWhiteSpace(letters[i]);
        index = Math.Clamp(index, 0, letters.Count);
        int a = index, b = index;
        if (index < letters.Count && !Space(index) || index > 0 && !Space(index - 1))
        {
            if (index >= letters.Count || Space(index)) { a = b = index - 1; }
            while (a > 0 && !Space(a - 1)) a--;
            while (b < letters.Count && !Space(b)) b++;
        }
        _anchor = a;
        _caret = b;
    }

    // ---- Keys ----

    public override bool KeyDown(EditorKey key, ModifierKeys modifiers)
    {
        if (_boxStart is not null && key == EditorKey.Escape) { _boxStart = null; Redraw(); return true; }   // cancel the size box
        if (_target is null) return false;
        bool shift = (modifiers & ModifierKeys.Shift) != 0;
        switch (key)
        {
            case EditorKey.Enter when _enterMakesNewLine:
                StartNextLine();
                return true;
            case EditorKey.Enter:
            case EditorKey.Escape:
                Finish();
                return true;
            case EditorKey.Back:
                Backspace();
                return true;
            case EditorKey.Delete:
                DeleteForward();
                return true;
            case EditorKey.Left:
                MoveCaret(!shift && HasSelection ? SelectionStart : _caret - 1, shift);
                return true;
            case EditorKey.Right:
                MoveCaret(!shift && HasSelection ? SelectionEnd : _caret + 1, shift);
                return true;
            case EditorKey.Home:
                MoveCaret(LineBounds(_caret).First, shift);
                return true;
            case EditorKey.End:
                MoveCaret(LineBounds(_caret).Last, shift);
                return true;
            case EditorKey.Up or EditorKey.Down:
                return true;   // no arrow nudging of the object while typing
        }
        return false;
    }

    private (int First, int Last) LineBounds(int index)
    {
        foreach (var l in _target!.Layout.Lines)
            if (index <= l.FirstLetter + l.LetterCount) return (l.FirstLetter, l.FirstLetter + l.LetterCount);
        return (0, Count);
    }

    // ---- Overlay ----

    public override void BuildOverlay(List<OverlayItem> items)
    {
        if (_boxStart is not null)
        {
            if (Box is { } box) items.Add(new MarqueeOverlay(box, MarqueeKind.Select));
            return;
        }
        if (_target is null)
        {
            base.BuildOverlay(items);
            return;
        }
        var t = _target;
        var layout = t.Layout;
        var w = t.GetWorldTransform();
        items.Add(new SelectionOverlay(t.GetWorldBounds(), Handles: false));

        var highlights = new List<Vec2[]>();
        if (HasSelection)
        {
            foreach (var line in layout.Lines)
            {
                int a = Math.Max(SelectionStart, line.FirstLetter), b = Math.Min(SelectionEnd, line.FirstLetter + line.LetterCount);
                if (a >= b) continue;
                double xa = layout.CaretPosition(a).X, xb = layout.CaretPosition(b).X;
                double top = line.Baseline - layout.Ascent, bottom = line.Baseline + layout.Descent;
                highlights.Add([w.Transform(new Vec2(xa, top)), w.Transform(new Vec2(xb, top)), w.Transform(new Vec2(xb, bottom)), w.Transform(new Vec2(xa, bottom))]);
            }
        }
        var c = layout.CaretPosition(_caret);
        items.Add(new TextEditOverlay(w.Transform(new Vec2(c.X, c.Y - layout.Ascent)), w.Transform(new Vec2(c.X, c.Y + layout.Descent)), highlights));
    }
}
