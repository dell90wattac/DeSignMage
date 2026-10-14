using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>Text ▸ Change Font: one font for every live text in the selection, groups at any depth included.</summary>
public class ChangeFontTests
{
    private readonly EditorSession _s = new(NewDoc());

    private TextObject AddText(string text, IObjectContainer? into = null, double size = 40, double tracking = 0, int weight = 400,
        TextAlignment alignment = TextAlignment.Left, string family = "Arial")
    {
        var t = new TextObject(text, family, size) { Tracking = tracking, FontWeight = weight, Alignment = alignment };
        _s.History.Execute(new AddObjectCommand(into ?? _s.Document.ActiveLayer, t));
        return t;
    }

    private GroupObject AddGroup(IObjectContainer? into = null)
    {
        var g = new GroupObject();
        _s.History.Execute(new AddObjectCommand(into ?? _s.Document.ActiveLayer, g));
        return g;
    }

    [Fact]
    public void Every_text_in_the_selection_changes_including_nested_groups_and_each_keeps_its_own_look()
    {
        var outer = AddGroup();
        var middle = AddGroup(outer);
        var inner = AddGroup(middle);
        var deep = AddText("Deep", inner, size: 12, tracking: 0.2, weight: 700, alignment: TextAlignment.Right);
        deep.Kerning = [0.1, -0.05];
        var mid = AddText("Middle", middle, size: 55, alignment: TextAlignment.Center);
        var top = AddText("Top", size: 90, tracking: -0.03);
        var rect = Rect(0, 0, 10, 10);
        _s.History.Execute(new AddObjectCommand(outer, rect));
        var unselected = AddText("Elsewhere");
        _s.Selection.Set([outer, top]);

        Assert.True(_s.CanChangeFont);
        Assert.Equal(new TextFontScope(3, 1), _s.GetFontScope());
        int steps = _s.History.UndoList.Count;

        var result = _s.ChangeFont("Times New Roman");

        Assert.Equal(new ChangeFontResult(3, 0, 1), result);
        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Equal("Change Font", _s.History.UndoDescription);
        Assert.All(new[] { deep, mid, top }, t => Assert.Equal("Times New Roman", t.FontFamily));
        Assert.Equal("Arial", unselected.FontFamily);
        // size, spacing, weight, alignment and kerning stay as they were
        Assert.Equal((12.0, 0.2, 700, TextAlignment.Right), (deep.FontSize, deep.Tracking, deep.FontWeight, deep.Alignment));
        Assert.Equal([0.1, -0.05], deep.Kerning);
        Assert.Equal((55.0, TextAlignment.Center), (mid.FontSize, mid.Alignment));
        Assert.Equal((90.0, -0.03), (top.FontSize, top.Tracking));
    }

    [Fact]
    public void It_is_one_undo_step_and_redo_brings_it_back()
    {
        var group = AddGroup();
        var a = AddText("One", group);
        var b = AddText("Two", group);
        var c = AddText("Three");
        _s.Selection.Set([group, c]);
        int steps = _s.History.UndoList.Count;

        _s.ChangeFont("Verdana");

        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Equal("Change Font", _s.History.UndoDescription);
        _s.Undo();
        Assert.All(new[] { a, b, c }, t => Assert.Equal("Arial", t.FontFamily));
        Assert.Equal(steps, _s.History.UndoList.Count);
        _s.Redo();
        Assert.All(new[] { a, b, c }, t => Assert.Equal("Verdana", t.FontFamily));
    }

    [Fact]
    public void Text_already_converted_to_curves_is_skipped_and_counted()
    {
        var live = AddText("Live");
        var curved = AddText("Gone");
        _s.Selection.Set([curved]);
        _s.ConvertSelectionToCurves();
        var converted = _s.Selection.Items.Single();
        Assert.IsNotType<TextObject>(converted);
        _s.Selection.Set([live, converted]);

        Assert.Equal(new TextFontScope(1, 1), _s.GetFontScope());
        var result = _s.ChangeFont("Georgia");

        Assert.Equal(new ChangeFontResult(1, 0, 1), result);
        Assert.Equal("Georgia", live.FontFamily);
    }

    [Fact]
    public void Texts_that_already_use_the_font_are_left_alone_and_nothing_is_recorded_when_none_change()
    {
        var a = AddText("A");
        var b = AddText("B", family: "Georgia");
        _s.Selection.Set([a, b]);
        int steps = _s.History.UndoList.Count;

        Assert.Equal(new ChangeFontResult(1, 1, 0), _s.ChangeFont("georgia"));   // family names compare without case
        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Equal("georgia", a.FontFamily);
        Assert.Equal("Georgia", b.FontFamily);                                    // untouched: no spelling change, no step for it

        int after = _s.History.UndoList.Count;
        Assert.Equal(new ChangeFontResult(0, 2, 0), _s.ChangeFont("georgia"));
        Assert.Equal(after, _s.History.UndoList.Count);
    }

    [Fact]
    public void It_is_disabled_without_live_text_in_the_selection()
    {
        var rect = Rect(0, 0, 10, 10);
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, rect));
        var shapes = AddGroup();
        _s.History.Execute(new AddObjectCommand(shapes, Rect(5, 5, 10, 10)));
        var text = AddText("Hi");

        _s.Selection.Clear();
        Assert.False(_s.CanChangeFont);
        _s.Selection.Set([rect, shapes]);
        Assert.False(_s.CanChangeFont);
        Assert.Equal(new TextFontScope(0, 2), _s.GetFontScope());

        int steps = _s.History.UndoList.Count;
        Assert.Equal(new ChangeFontResult(0, 0, 2), _s.ChangeFont("Verdana"));
        Assert.Equal(steps, _s.History.UndoList.Count);

        _s.Selection.Set([rect, text]);
        Assert.True(_s.CanChangeFont);                                            // mixed with shapes: enabled (the Context Bar's font controls would hide)
        _s.Selection.Set([text]);
        _s.ConvertSelectionToCurves();
        Assert.False(_s.CanChangeFont);                                           // converted text can't change font
    }

    [Fact]
    public void A_text_being_edited_is_finished_first_and_gets_the_font()
    {
        var t = AddText("Edit me", size: 50);
        _s.Selection.Set([t]);
        _s.EditText(t);
        Assert.True(_s.IsTextEditing);
        Assert.True(_s.CanChangeFont);
        _s.TextInput("!");

        var result = _s.ChangeFont("Courier New");

        Assert.False(_s.IsTextEditing);
        Assert.Equal(1, result.Changed);
        Assert.Equal("Courier New", t.FontFamily);
        Assert.Contains("!", t.Text);
        Assert.Equal(50, t.FontSize);
        Assert.Equal("Change Font", _s.History.UndoDescription);
    }

    [Fact]
    public void Changing_to_a_font_the_computer_lacks_keeps_the_text_working_with_that_name()
    {
        var t = AddText("Hello");
        _s.Selection.Set([t]);

        _s.ChangeFont("Futura Imaginary");

        Assert.Equal("Futura Imaginary", t.FontFamily);
        Assert.True(t.IsFontMissing);
        Assert.False(t.Layout.Outline.IsEmpty);
    }
}
