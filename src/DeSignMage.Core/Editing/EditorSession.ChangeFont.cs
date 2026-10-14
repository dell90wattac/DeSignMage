using DeSignMage.Core.Commands;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

/// <summary>What Text ▸ Change Font acts on: the live texts in the selection at any depth, and the other objects it leaves alone.</summary>
/// <param name="Texts">Live text objects, including those inside selected groups and parts of combined objects.</param>
/// <param name="Others">Everything else that was selected (shapes, pictures, and text already converted to curves, which can no longer change font).</param>
public readonly record struct TextFontScope(int Texts, int Others);

/// <summary>What one Change Font did.</summary>
/// <param name="Changed">Texts that got the new font.</param>
/// <param name="AlreadyThatFont">Texts that already used it (left as they were).</param>
/// <param name="Skipped">Other selected objects that are not live text.</param>
public readonly record struct ChangeFontResult(int Changed, int AlreadyThatFont, int Skipped);

public sealed partial class EditorSession
{
    /// <summary>
    /// Text ▸ Change Font works while the selection holds live text anywhere inside it (unlike the Context Bar's font controls,
    /// which only show for text selected directly), or while a text is being edited.
    /// </summary>
    public bool CanChangeFont => IsTextEditing || SelectedTexts().Count > 0;

    /// <summary>How many live texts the selection holds and how many other objects Change Font would leave as they are.</summary>
    public TextFontScope GetFontScope()
    {
        int texts = 0, others = 0;
        foreach (var item in Selection.Items) Count(item, ref texts, ref others);
        return new TextFontScope(texts, others);
    }

    // A group or combined object without any live text counts as one object (text converted to curves is a group of letters: one, not one per letter).
    private static void Count(DocObject o, ref int texts, ref int others)
    {
        if (o is not TextObject && TextTargets.Find([o]).Count == 0) { others++; return; }
        switch (o)
        {
            case TextObject: texts++; break;
            case GroupObject g: foreach (var c in g.Children) Count(c, ref texts, ref others); break;
            case CombinedObject c: foreach (var p in c.Parts) Count(p, ref texts, ref others); break;
        }
    }

    /// <summary>
    /// Sets the font family of every live text in the selection (also inside groups, at any depth) as one undo step ("Change Font").
    /// Each text keeps its size, style (weight, italic), spacing, kerning and alignment. Objects that are not live text are skipped.
    /// A text being edited is finished first. Returns what happened; nothing is recorded when no text changes.
    /// </summary>
    public ChangeFontResult ChangeFont(string family)
    {
        FinishTextEditing();
        var scope = GetFontScope();
        var targets = SelectedTexts();
        if (string.IsNullOrWhiteSpace(family) || targets.Count == 0) return new ChangeFontResult(0, 0, scope.Others);

        var changing = targets.Where(t => !string.Equals(t.FontFamily, family, StringComparison.OrdinalIgnoreCase)).ToList();
        if (changing.Count > 0) History.Execute(new ChangeTextFormatCommand(changing, new TextFormatChange(Family: family)));
        return new ChangeFontResult(changing.Count, targets.Count - changing.Count, scope.Others);
    }
}
