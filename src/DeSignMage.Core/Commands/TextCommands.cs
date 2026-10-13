using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;

namespace DeSignMage.Core.Commands;

/// <summary>Changes the string of a text object (and its per-gap kerning, which must follow the letters).</summary>
public sealed class SetTextCommand : IDocumentCommand
{
    private readonly TextObject _obj;
    private readonly string _oldText, _newText;
    private readonly IReadOnlyList<double> _oldKerning, _newKerning;

    /// <param name="kerning">The kerning for the new text; null derives it from the change (see <see cref="TextElements.RemapKerning(string,string,IReadOnlyList{double})"/>).</param>
    public SetTextCommand(TextObject obj, string newText, IReadOnlyList<double>? kerning = null, string description = "Edit Text")
    {
        _obj = obj;
        _oldText = obj.Text;
        _oldKerning = obj.Kerning;
        _newText = newText;
        _newKerning = kerning is null ? TextElements.RemapKerning(_oldText, newText, _oldKerning) : TextElements.NormalizeKerning(kerning);
        Description = description;
    }

    public string Description { get; }

    public void Do(Document doc) { _obj.Text = _newText; _obj.Kerning = _newKerning; }
    public void Undo(Document doc) { _obj.Text = _oldText; _obj.Kerning = _oldKerning; }
}

/// <summary>A change to the look of text. Null members stay as they are.</summary>
public sealed record TextFormatChange(
    string? Family = null, int? Weight = null, bool? Italic = null, double? Size = null,
    double? Tracking = null, TextAlignment? Alignment = null)
{
    public bool IsEmpty => this == new TextFormatChange();

    public void ApplyTo(TextObject t)
    {
        if (Family is not null) t.FontFamily = Family;
        if (Weight is { } w) t.FontWeight = w;
        if (Italic is { } i) t.Italic = i;
        if (Size is { } s) t.FontSize = s;
        if (Tracking is { } k) t.Tracking = k;
        if (Alignment is { } a) t.Alignment = a;
    }
}

/// <summary>
/// Change Font: sets font family, style, size, spacing or alignment on text objects as one undo step. Targets can be
/// anywhere (top level, inside groups, parts of combined objects); use <see cref="TextTargets.Find"/> to collect them.
/// </summary>
public sealed class ChangeTextFormatCommand : IDocumentCommand
{
    private readonly (TextObject Obj, string Family, int Weight, bool Italic, double Size, double Tracking, TextAlignment Alignment)[] _old;
    private readonly TextFormatChange _change;

    public ChangeTextFormatCommand(IEnumerable<TextObject> targets, TextFormatChange change, string description = "Change Font")
    {
        _old = targets.Distinct().Select(t => (t, t.FontFamily, t.FontWeight, t.Italic, t.FontSize, t.Tracking, t.Alignment)).ToArray();
        _change = change;
        Description = description;
    }

    public string Description { get; }
    public int Count => _old.Length;

    public void Do(Document doc)
    {
        foreach (var o in _old) _change.ApplyTo(o.Obj);
    }

    public void Undo(Document doc)
    {
        foreach (var (t, family, weight, italic, size, tracking, alignment) in _old)
        {
            t.FontFamily = family; t.FontWeight = weight; t.Italic = italic;
            t.FontSize = size; t.Tracking = tracking; t.Alignment = alignment;
        }
    }
}

/// <summary>Finds the text objects inside a selection: the objects themselves, and text in groups and combined objects.</summary>
public static class TextTargets
{
    public static List<TextObject> Find(IEnumerable<DocObject> roots)
    {
        var list = new List<TextObject>();
        foreach (var r in roots) Collect(r, list);
        return list;
    }

    private static void Collect(DocObject o, List<TextObject> list)
    {
        switch (o)
        {
            case TextObject t: list.Add(t); break;
            case GroupObject g: foreach (var c in g.Children) Collect(c, list); break;
            case CombinedObject c: foreach (var p in c.Parts) Collect(p, list); break;
        }
    }
}

/// <summary>What <see cref="ConvertTextToCurvesCommand"/> turns each text into.</summary>
public enum CurveConversion
{
    /// <summary>One group holding one path per letter (Convert to Curves). A single letter becomes just its path.</summary>
    Letters,
    /// <summary>One path with every letter as sub-paths: what node editing opens (the user's "it becomes an object like anything else").</summary>
    SinglePath,
}

/// <summary>
/// Convert to Curves: live text becomes ordinary path objects (same place, style, name and stacking position) and can
/// never change font again. Works on text anywhere in the targets; text that is a part of a combined object becomes a
/// single path part. Undo brings the original text objects back, kerning and all.
/// </summary>
public sealed class ConvertTextToCurvesCommand : IDocumentCommand, IUndoMemory
{
    private sealed record Entry(TextObject Text, DocObject Replacement, CombinedObject? Combined, int PartIndex);

    private readonly List<Entry> _entries = [];

    public long ApproximateBytes => _entries.Sum(e => UndoMemory.BytesOf(e.Text) + UndoMemory.BytesOf(e.Replacement));
    public IEnumerable<string> HeldAssetIds => [];

    public ConvertTextToCurvesCommand(Document doc, IEnumerable<DocObject> targets, CurveConversion mode = CurveConversion.Letters)
    {
        foreach (var t in TextTargets.Find(targets))
        {
            if (t.Parent is not null)
            {
                _entries.Add(new Entry(t, Convert(t, mode), null, -1));
                continue;
            }
            // A part of a combined object: find which one.
            foreach (var c in doc.AllObjects().OfType<CombinedObject>())
            {
                int i = IndexOfPart(c, t);
                if (i >= 0) { _entries.Add(new Entry(t, Convert(t, CurveConversion.SinglePath), c, i)); break; }
            }
        }
    }

    private static int IndexOfPart(CombinedObject c, TextObject t)
    {
        for (int i = 0; i < c.Parts.Count; i++) if (ReferenceEquals(c.Parts[i], t)) return i;
        return -1;
    }

    public string Description => "Convert to Curves";
    public bool IsEmpty => _entries.Count == 0;

    /// <summary>The objects that replaced the texts (top level or inside groups).</summary>
    public IReadOnlyList<DocObject> Replacements => _entries.Select(e => e.Replacement).ToArray();

    /// <summary>The top-level objects to select after the conversion (de-duplicated, in document order of discovery).</summary>
    public IReadOnlyList<DocObject> TopLevelResults =>
        _entries.Select(e => e.Combined ?? e.Replacement.TopLevel).Distinct().ToArray();

    public static DocObject Convert(TextObject t, CurveConversion mode)
    {
        var letters = t.Layout.Letters.Where(l => !l.IsBreak && !l.Outline.IsEmpty).ToList();
        string name = t.Name ?? t.Text;

        PathObject Path(PathGeometry g, string? n, Affine2 transform) =>
            new(g) { Name = n, Transform = transform, Style = t.Style, Visible = t.Visible, Locked = t.Locked };

        if (mode == CurveConversion.SinglePath || letters.Count <= 1)
            return Path(t.Layout.Outline, name, t.Transform);

        var group = new GroupObject { Name = name, Transform = t.Transform, Visible = t.Visible, Locked = t.Locked };
        foreach (var l in letters)
            group.AddDetached(Path(l.Outline, t.Text.Substring(l.Start, l.Length), Affine2.Identity));
        return group;
    }

    private readonly Dictionary<Entry, Removal> _removals = [];

    public void Do(Document doc)
    {
        foreach (var e in _entries)
        {
            if (e.Combined is { } c) { c.ReplacePart(e.PartIndex, e.Replacement); continue; }
            var removal = doc.Remove(e.Text);
            _removals[e] = removal;
            doc.Insert(removal.Slot, e.Replacement);
        }
    }

    public void Undo(Document doc)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var e = _entries[i];
            if (e.Combined is { } c) { c.ReplacePart(e.PartIndex, e.Text); continue; }
            doc.Remove(e.Replacement);
            doc.Restore(_removals[e]);
        }
    }
}
