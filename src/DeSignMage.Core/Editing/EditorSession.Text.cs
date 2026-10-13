using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

/// <summary>Live text in the editor (requirement R6): edit mode, typing, Change Font, Convert to Curves.</summary>
public sealed partial class EditorSession
{
    /// <summary>The font, size, spacing and fill that new text starts with.</summary>
    public TextFormat TextDefaults { get; } = new();

    /// <summary>True while a text object is being edited (a caret is showing). Plain letter shortcuts must not fire then.</summary>
    public bool IsTextEditing => _activeTool is TextTool { IsEditing: true };

    /// <summary>The text objects in the selection, including text inside selected groups and combined objects.</summary>
    public List<TextObject> SelectedTexts() => TextTargets.Find(Selection.Items);

    /// <summary>Enters edit mode on <paramref name="text"/> (double-click with the Layout tool, or a click with the text tool).</summary>
    public void EditText(TextObject text, Vec2? at = null)
    {
        SetTool("text");
        var w = text.GetWorldTransform();
        int? caret = at is { } p && w.IsInvertible ? text.Layout.IndexAt(w.Invert().Transform(p)) : null;
        GetTool<TextTool>().BeginEdit(text, caret);
    }

    /// <summary>Characters from the keyboard (the shell's text-input event). Returns true if a text object took them.</summary>
    public bool TextInput(string text)
    {
        if (_activeTool is not TextTool { IsEditing: true } tool) return false;
        tool.Insert(text);
        return true;
    }

    /// <summary>Ends edit mode like Enter does: the object stays text and is selected.</summary>
    public void FinishTextEditing()
    {
        if (_activeTool is TextTool { IsEditing: true } tool) tool.Finish();
    }

    public void TextSelectAll()
    {
        if (_activeTool is TextTool { IsEditing: true } tool) tool.SelectAll();
    }

    /// <summary>
    /// Change Font / size / style / spacing / alignment of the selected text (also text inside selected groups) as one
    /// undo step. With no text selected it changes what new text starts with. Returns true if any object changed.
    /// </summary>
    public bool ApplyTextFormat(TextFormatChange change)
    {
        if (change.IsEmpty) return false;
        bool defaultsToo = SelectedTexts().Count == 0 || _activeTool is TextTool;
        if (defaultsToo) TextDefaults.Apply(change);

        if (_activeTool is TextTool { IsEditing: true, IsNewObject: true } tool)
        {
            change.ApplyTo(tool.Target!);   // part of the object that "Add Text" will create, not a step of its own
            Document.NotifyChanged();
            return true;
        }
        var targets = SelectedTexts();
        if (targets.Count == 0) return false;
        History.Execute(new ChangeTextFormatCommand(targets, change));
        return true;
    }

    public bool CanConvertToCurves => IsTextEditing || SelectedTexts().Count > 0;

    /// <summary>
    /// Convert to Curves: the selected text becomes ordinary path objects (one group of letters per text). The font
    /// can never change afterwards. One undo step.
    /// </summary>
    public void ConvertSelectionToCurves()
    {
        FinishTextEditing();
        var cmd = new ConvertTextToCurvesCommand(Document, Selection.Items);
        if (cmd.IsEmpty) return;
        History.Execute(cmd);
        Selection.Set(cmd.TopLevelResults);
    }

    /// <summary>
    /// Vector editing a text object converts it first, automatically: one path holding every letter, which the vector
    /// tool then opens. Returns the path.
    /// </summary>
    internal DocObject ConvertTextForVectorEditing(TextObject text)
    {
        var cmd = new ConvertTextToCurvesCommand(Document, [text], CurveConversion.SinglePath);
        if (cmd.IsEmpty) return text;
        History.Execute(cmd);
        var path = cmd.Replacements[0];
        foreach (var tool in _tools.OfType<PathEditToolBase>()) tool.NoteConversion(text, path);
        return path;
    }
}
