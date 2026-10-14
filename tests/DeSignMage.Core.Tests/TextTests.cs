using System.Runtime.CompilerServices;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;
using DeSignMage.Text;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>The tests use the real HarfBuzz/Skia font service with Windows' installed fonts (Arial is always there).</summary>
internal static class TestFonts
{
    [ModuleInitializer]
    internal static void Install() => SkiaFontService.Install();
}

/// <summary>Letters, kerning offsets and the pure text helpers.</summary>
public class TextModelTests
{
    private static TextObject Hello(double size = 100) => new("Hello", "Arial", size);

    [Fact]
    public void Letters_are_grapheme_clusters()
    {
        Assert.Equal(["a", "é", "b"], TextElements.Split("aéb"));   // e + combining acute is one letter
        Assert.Equal(["x", "😀"], TextElements.Split("x😀"));
        Assert.Equal(0, TextElements.Count(""));
        Assert.Equal([0, 1, 3, 4], TextElements.Offsets("aéb"));
    }

    [Fact]
    public void Text_lays_out_from_the_real_font()
    {
        var t = Hello();
        var layout = t.Layout;
        Assert.False(t.IsFontMissing);
        Assert.Equal(5, layout.LetterCount);
        Assert.False(layout.Outline.IsEmpty);
        Assert.True(layout.Frame.Width > 150 && layout.Frame.Width < 400, $"width {layout.Frame.Width}");
        Assert.True(layout.Letters.Zip(layout.Letters.Skip(1), (a, b) => b.X > a.X).All(x => x));
        // Glyph ink sits above the baseline (negative y) and the bounds are tight to it.
        var b = t.GetWorldBounds();
        Assert.True(b.Top < -50 && b.Bottom < 5, $"bounds {b}");
        // The layout is cached until something changes.
        Assert.Same(layout, t.Layout);
        t.FontSize = 50;
        Assert.NotSame(layout, t.Layout);
        Assert.True(t.Layout.Frame.Width < layout.Frame.Width);
    }

    [Fact]
    public void Bold_and_italic_come_from_the_font_files()
    {
        var regular = Hello();
        var bold = Hello(); bold.FontWeight = 700;
        var italic = Hello(); italic.Italic = true;
        Assert.True(bold.Layout.Frame.Width > regular.Layout.Frame.Width);
        Assert.False(bold.IsFontMissing);
        Assert.NotEqual(regular.Layout.Outline.GetBounds(), italic.Layout.Outline.GetBounds());
    }

    [Fact]
    public void Installed_fonts_are_listed_with_their_styles()
    {
        var fonts = TextEngine.Fonts;
        Assert.Contains("Arial", fonts.Families);
        Assert.True(fonts.IsInstalled("arial"));   // case-insensitive
        var styles = fonts.StylesOf("Arial");
        Assert.Contains(styles, s => s.Weight == 400 && !s.Italic && s.Name == "Regular");
        Assert.Contains(styles, s => s.Weight == 700 && !s.Italic);
        Assert.Contains(styles, s => s.Italic);
        Assert.Empty(fonts.StylesOf("No Such Font 123"));
    }

    [Fact]
    public void Missing_font_falls_back_but_keeps_its_name()
    {
        var t = new TextObject("Fallback", "No Such Font 123", 40);
        Assert.True(t.IsFontMissing);
        Assert.Equal("No Such Font 123", t.FontFamily);
        Assert.NotEqual("No Such Font 123", t.ResolvedFontFamily);
        Assert.False(t.Layout.Outline.IsEmpty);   // still readable and editable
        Assert.Contains(t.ResolvedFontFamily, TextEngine.Fonts.Families);

        var copy = Assert.IsType<TextObject>(t.Clone());
        Assert.Equal("No Such Font 123", copy.FontFamily);   // the requested name survives copies

        t.FontFamily = "Arial";   // pick a real font later: no longer missing
        Assert.False(t.IsFontMissing);
    }

    [Fact]
    public void Gdi_style_names_like_Segoe_UI_Semibold_resolve_to_the_family_with_that_weight()
    {
        // Legacy files (and GDI) name styles into the family: "Lato Semibold", "Segoe UI Semibold", "Arial Bold".
        var semibold = new TextObject("Hello", "Segoe UI Semibold", 100);
        var regular = new TextObject("Hello", "Segoe UI", 100);
        Assert.False(semibold.IsFontMissing);
        Assert.Equal("Segoe UI", semibold.ResolvedFontFamily);
        Assert.Equal("Segoe UI Semibold", semibold.FontFamily);   // the requested name stays as it was
        Assert.NotEqual(regular.Layout.Frame.Width, semibold.Layout.Frame.Width);
        Assert.True(TextEngine.Fonts.IsInstalled("Arial Bold Italic"));
        Assert.Equal(TextEngine.Fonts.StylesOf("Arial"), TextEngine.Fonts.StylesOf("Arial Bold"));

        var bold = new TextObject("Hello", "Arial Bold", 100);
        var plainBold = new TextObject("Hello", "Arial", 100) { FontWeight = 700 };
        Assert.Equal(plainBold.Layout.Frame.Width, bold.Layout.Frame.Width, 6);
        Assert.True(new TextObject("Hello", "Nonexistent Bold", 100).IsFontMissing);   // a style word alone doesn't make a font up
    }

    [Fact]
    public void Tracking_adds_space_after_every_letter_in_ems()
    {
        var plain = Hello();
        var tracked = Hello(); tracked.Tracking = 0.1;
        Assert.Equal(plain.Layout.Frame.Width + 4 * 0.1 * 100, tracked.Layout.Frame.Width, 3);   // 4 gaps in "Hello"
        tracked.FontSize = 50;   // spacing follows the size
        var half = Hello(50); half.Tracking = 0.1;
        Assert.Equal(half.Layout.Frame.Width, tracked.Layout.Frame.Width, 3);
    }

    [Fact]
    public void Kerning_offsets_shift_every_letter_to_the_right_of_the_gap()
    {
        var plain = Hello();
        var k = Hello();
        k.Kerning = k.KerningWith(1, 0.25);   // the gap after "e" (letter 1)
        var a = plain.Layout.Letters;
        var b = k.Layout.Letters;
        Assert.Equal(a[0].X, b[0].X, 6);
        Assert.Equal(a[1].X, b[1].X, 6);
        for (int i = 2; i < 5; i++) Assert.Equal(a[i].X + 25, b[i].X, 3);
        Assert.Equal(plain.Layout.Frame.Width + 25, k.Layout.Frame.Width, 3);
        Assert.Equal(0.25, k.KerningAt(1));
        Assert.Equal(0, k.KerningAt(0));
        // The dot sits at the next letter's pen position, so it travels with the letters it moves.
        Assert.Equal(b[2].X, k.Layout.GapPoint(1)!.Value.X, 6);
        Assert.Equal(4, k.Layout.GapPoints().Count());   // a dot between each pair of five letters
        Assert.Null(k.Layout.GapPoint(4));                // none after the last letter
    }

    [Fact]
    public void Kerning_can_be_derived_from_known_letter_positions_for_imports()
    {
        var t = new TextObject("Sign", "Arial", 100) { Tracking = 0.02 };
        // Where the layout had put the letters: wider after the 'S' and tighter after the 'i'.
        var natural = t.Layout.Letters.Select(l => l.X).ToArray();
        double[] target = [natural[0] + 5, natural[1] + 30, natural[2] + 22, natural[3] + 30];   // arbitrary absolute origin 5
        t.Kerning = t.KerningForLetterPositions(target);
        var placed = t.Layout.Letters.Select(l => l.X).ToArray();
        for (int i = 1; i < 4; i++) Assert.Equal(target[i] - target[0], placed[i] - placed[0], 3);
        Assert.Equal(0.25, t.KerningAt(0), 6);    // 25 points extra in the first gap = 0.25 em
        Assert.Empty(new TextObject("A", "Arial", 10).KerningForLetterPositions([0]));
    }

    [Fact]
    public void Alignment_is_relative_to_the_objects_origin()
    {
        var left = Hello();
        var center = Hello(); center.Alignment = TextAlignment.Center;
        var right = Hello(); right.Alignment = TextAlignment.Right;
        double w = left.Layout.Frame.Width;
        Assert.Equal(0, left.Layout.Frame.Left, 6);
        Assert.Equal(-w / 2, center.Layout.Frame.Left, 3);
        Assert.Equal(-w, right.Layout.Frame.Left, 3);
    }

    [Fact]
    public void Kerning_follows_the_letters_through_edits()
    {
        IReadOnlyList<double> k = [0, 0.1, 0.2, 0.3];   // five letters; offsets after letters 1, 2, 3
        // Insert two letters at the start: the offsets move right with their letters.
        Assert.Equal([0, 0, 0, 0.1, 0.2, 0.3], TextElements.RemapKerning(k, 0, 0, 2));
        // Delete letter 2: its own offset goes, the rest stay with their letters.
        Assert.Equal([0, 0.1, 0.3], TextElements.RemapKerning(k, 2, 1, 0));
        // Diff-based variant for programmatic changes.
        Assert.Equal([0, 0, 0.1, 0.2, 0.3], TextElements.RemapKerning("Hello", "xHello", k));
        Assert.Equal([0, 0.1, 0.2, 0.3], TextElements.RemapKerning("Hello", "Hallo", k));   // a corrected typo keeps its spacing
        Assert.Empty(TextElements.NormalizeKerning([0, 0, 0]));
    }

    [Fact]
    public void Newline_makes_a_second_line()
    {
        var t = new TextObject("ab\ncd", "Arial", 100);
        var l = t.Layout;
        Assert.Equal(2, l.Lines.Count);
        Assert.Equal(5, l.LetterCount);            // the line break counts as a letter
        Assert.True(l.Lines[1].Baseline > 90);
        Assert.Null(l.GapPoint(1));                // no dot across the break
        Assert.Equal(l.Letters[3].X, l.CaretPosition(3).X, 6);
    }

    [Fact]
    public void Caret_positions_and_picking_by_point()
    {
        var t = Hello();
        var l = t.Layout;
        Assert.Equal(0, l.CaretPosition(0).X, 6);
        Assert.Equal(l.Frame.Right, l.CaretPosition(5).X, 3);
        Assert.Equal(0, l.IndexAt(new Vec2(-30, -10)));
        Assert.Equal(5, l.IndexAt(new Vec2(l.Frame.Right + 30, -10)));
        Assert.Equal(2, l.IndexAt(new Vec2(l.Letters[2].X + 1, -10)));
    }

    [Fact]
    public void Text_is_picked_anywhere_in_its_line_box_not_only_on_glyphs()
    {
        var doc = NewDoc();
        var t = Hello();
        doc.Insert(doc.ActiveLayer, -1, t);
        var f = t.Layout.Frame;
        // The gap between ascender and descender above a lowercase letter has no ink but is still the text.
        Assert.Same(t, HitTesting.HitTest(doc, new Vec2(f.Right - 2, f.Top + 3), 1));
        Assert.Null(HitTesting.HitTest(doc, new Vec2(f.Right + 40, 0), 1));
    }
}

/// <summary>Commands on live text: change font, edit the string, kerning, convert to curves.</summary>
public class TextCommandTests
{
    private readonly Document _doc = NewDoc();
    private readonly CommandHistory _h;

    public TextCommandTests() => _h = new CommandHistory(_doc);

    private TextObject Add(string text = "Hello", IObjectContainer? into = null)
    {
        var t = new TextObject(text, "Arial", 100);
        _h.Execute(new AddObjectCommand(into ?? _doc.ActiveLayer, t));
        return t;
    }

    [Fact]
    public void Change_font_applies_to_text_inside_groups_and_undoes_as_one_step()
    {
        var a = Add();
        var b = Add("World");
        var group = new GroupObject();
        _h.Execute(new AddObjectCommand(_doc.ActiveLayer, group));
        var inner = Add("Inner", group);
        var rect = Rect(0, 0, 10, 10);
        _h.Execute(new AddObjectCommand(_doc.ActiveLayer, rect));

        var targets = TextTargets.Find([a, group, rect]);
        Assert.Equal([a, inner], targets);   // b isn't selected, the rectangle isn't text

        _h.Execute(new ChangeTextFormatCommand(targets, new TextFormatChange(Family: "Times New Roman", Weight: 700, Size: 30)));
        Assert.All(new[] { a, inner }, t => { Assert.Equal("Times New Roman", t.FontFamily); Assert.Equal(700, t.FontWeight); Assert.Equal(30, t.FontSize); });
        Assert.Equal("Arial", b.FontFamily);
        Assert.Equal("Change Font", _h.UndoDescription);

        _h.Undo();
        Assert.All(new[] { a, inner }, t => { Assert.Equal("Arial", t.FontFamily); Assert.Equal(400, t.FontWeight); Assert.Equal(100, t.FontSize); });
        _h.Redo();
        Assert.Equal(30, inner.FontSize);
    }

    [Fact]
    public void Changing_to_a_missing_font_keeps_the_text_working()
    {
        var t = Add();
        _h.Execute(new ChangeTextFormatCommand([t], new TextFormatChange(Family: "Futura Imaginary")));
        Assert.True(t.IsFontMissing);
        Assert.Equal("Futura Imaginary", t.FontFamily);
        Assert.False(t.Layout.Outline.IsEmpty);
        _h.Undo();
        Assert.False(t.IsFontMissing);
    }

    [Fact]
    public void Set_text_remaps_kerning_and_undoes()
    {
        var t = Add();
        t.Kerning = t.KerningWith(2, 0.3);   // after the first "l"
        _h.Execute(new SetTextCommand(t, "OHello"));
        Assert.Equal("OHello", t.Text);
        Assert.Equal(0.3, t.KerningAt(3), 9);   // moved one letter to the right with its letter
        Assert.Equal(0, t.KerningAt(2));
        _h.Undo();
        Assert.Equal("Hello", t.Text);
        Assert.Equal(0.3, t.KerningAt(2), 9);
    }

    [Fact]
    public void Convert_to_curves_makes_a_group_of_letter_paths_and_undoes()
    {
        var t = Add("Hi 5");
        t.Transform = Affine2.Translation(30, 200);
        t.Name = "Title";
        t.Style = ObjectStyle.FillOnly(DocColor.FromHex("#CC0000"));
        t.Kerning = t.KerningWith(0, 0.2);
        var before = t.GetWorldBounds();
        var other = Add("Other");
        int index = _doc.ActiveLayer.IndexOf(t);

        var cmd = new ConvertTextToCurvesCommand(_doc, [t]);
        _h.Execute(cmd);

        Assert.False(_doc.Contains(t));
        var group = Assert.IsType<GroupObject>(_doc.ActiveLayer.Children[index].Parent);   // the letters take the text's place in the drawing order
        Assert.Equal(group.Children, _doc.ActiveLayer.Children.Skip(index).Take(3));
        Assert.Equal("Title", group.Name);
        Assert.Equal(3, group.Children.Count);   // H, i, 5: the space draws nothing
        var paths = group.Children.Cast<PathObject>().ToList();
        Assert.Equal(["H", "i", "5"], paths.Select(p => p.Name));
        Assert.All(paths, p => Assert.Equal(t.Style, p.Style));
        Assert.All(paths, p => Assert.False(p.Geometry.IsEmpty));
        Assert.Equal(before.Left, group.GetWorldBounds().Left, 2);   // looks the same, kerning included
        Assert.Equal(before.Right, group.GetWorldBounds().Right, 2);
        Assert.Equal(before.Bottom, group.GetWorldBounds().Bottom, 2);
        Assert.DoesNotContain(_doc.AllObjects(), o => o is TextObject { Text: "Hi 5" });   // can't change font any more
        Assert.Equal([group], cmd.TopLevelResults);
        Assert.Same(other, _doc.ActiveLayer.Children[^1]);

        _h.Undo();
        Assert.Same(t, _doc.ActiveLayer.Children[index]);
        Assert.Equal("Hi 5", t.Text);
        Assert.Equal(0.2, t.KerningAt(0));
        _h.Redo();
        Assert.Same(group, _doc.ActiveLayer.Children[index].Parent);
    }

    [Fact]
    public void Converting_a_single_letter_gives_just_a_path_and_a_group_converts_all_its_text()
    {
        var one = Add("A");
        var solo = new ConvertTextToCurvesCommand(_doc, [one]);
        _h.Execute(solo);
        Assert.IsType<PathObject>(Assert.Single(solo.Replacements));

        var group = new GroupObject();
        _h.Execute(new AddObjectCommand(_doc.ActiveLayer, group));
        var t1 = Add("Ab", group);
        var t2 = Add("Cd", group);
        var cmd = new ConvertTextToCurvesCommand(_doc, [group]);
        _h.Execute(cmd);
        Assert.Equal(2, cmd.Replacements.Count);
        Assert.All(group.Children, c => Assert.IsType<GroupObject>(c));   // each text became its own group of letters
        Assert.False(_doc.Contains(t1) || _doc.Contains(t2));
        _h.Undo();
        Assert.Equal([t1, t2], group.Children);
    }

    [Fact]
    public void Text_inside_a_combined_object_becomes_one_path_part()
    {
        var t = new TextObject("Hi", "Arial", 100);
        var rect = Rect(0, 0, 200, 120);
        var combined = new CombinedObject([rect, t]);
        _h.Execute(new AddObjectCommand(_doc.ActiveLayer, combined));

        _h.Execute(new ConvertTextToCurvesCommand(_doc, [combined]));
        Assert.IsType<PathObject>(combined.Parts[1]);
        Assert.Equal(2, combined.Parts.Count);
        _h.Undo();
        Assert.Same(t, combined.Parts[1]);
    }
}

/// <summary>The text tool, edit mode, letter dots and conversion, driven like the shell does.</summary>
public class TextEditorTests
{
    private readonly EditorSession _s;

    public TextEditorTests()
    {
        _s = new EditorSession(NewDoc());
        _s.Viewport.SetViewSize(new Vec2(1000, 800));
        _s.Viewport.Set(1, Vec2.Zero);   // screen == document coordinates
        _s.TextDefaults.Size = 100;
        _s.TextDefaults.Family = "Arial";
    }

    private PointerInput At(double x, double y, PointerButton b = PointerButton.None, int clicks = 1, ModifierKeys m = ModifierKeys.None) =>
        new(new Vec2(x, y), _s.Viewport.ToDoc(new Vec2(x, y)), b, m, clicks);

    private void Click(double x, double y, int clicks = 1)
    {
        _s.PointerDown(At(x, y, PointerButton.Left, clicks));
        _s.PointerUp(At(x, y, PointerButton.Left, clicks));
    }

    private void Type(string text) => Assert.True(_s.TextInput(text));
    private void Press(EditorKey key, ModifierKeys m = ModifierKeys.None) => Assert.True(_s.KeyDown(key, m));

    private TextObject OnlyText() => Assert.IsType<TextObject>(Assert.Single(_s.Document.ActiveLayer.Children));

    private TextObject AddText(string text, double x = 100, double y = 300)
    {
        var t = new TextObject(text, "Arial", 100) { Transform = Affine2.Translation(x, y) };
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, t));
        return t;
    }

    // ---- Create and edit ----

    [Fact]
    public void Text_tool_click_then_typing_creates_text_and_escape_finishes_it()
    {
        _s.SetTool("text");
        Click(200, 400);
        Assert.True(_s.IsTextEditing);
        Type("Hi");
        Type(" there");
        Assert.True(_s.IsTextEditing);
        Assert.Equal("Hi there", OnlyText().Text);   // visible live while typing
        Assert.False(_s.History.CanUndo);            // but not an undo step yet

        Press(EditorKey.Escape);

        Assert.False(_s.IsTextEditing);
        var t = OnlyText();   // it stays text
        Assert.Equal("Hi there", t.Text);
        Assert.Equal("Arial", t.FontFamily);
        Assert.Equal(new Vec2(200, 400), t.Transform.Offset);   // baseline-left at the click
        Assert.IsType<SelectTool>(_s.ActiveTool);
        Assert.Equal([t], _s.Selection.Items);
        Assert.Equal("Add Text", _s.History.UndoDescription);
        Assert.Single(_s.History.UndoList);

        _s.Undo();
        Assert.Empty(_s.Document.ActiveLayer.Children);
        _s.Redo();
        Assert.Equal("Hi there", OnlyText().Text);
    }

    [Fact]
    public void Escape_also_finishes_and_keeps_the_text()
    {
        _s.SetTool("text");
        Click(50, 300);
        Type("Keep");
        Press(EditorKey.Escape);
        Assert.Equal("Keep", OnlyText().Text);
        Assert.False(_s.IsTextEditing);
    }

    [Fact]
    public void Empty_text_is_discarded_without_a_history_step()
    {
        _s.SetTool("text");
        Click(50, 300);
        Press(EditorKey.Escape);
        Assert.Empty(_s.Document.ActiveLayer.Children);
        Assert.False(_s.History.CanUndo);
    }

    [Fact]
    public void Caret_keys_backspace_delete_and_shift_selection()
    {
        _s.SetTool("text");
        Click(50, 300);
        Type("Hello");
        Press(EditorKey.Left); Press(EditorKey.Left);   // Hel|lo
        Press(EditorKey.Back);                      // He|lo
        Assert.Equal("Helo", OnlyText().Text);
        Press(EditorKey.Delete);                         // He|o
        Assert.Equal("Heo", OnlyText().Text);
        Type("LL");
        Assert.Equal("HeLLo", OnlyText().Text);
        Press(EditorKey.Home);
        Press(EditorKey.Right, ModifierKeys.Shift); Press(EditorKey.Right, ModifierKeys.Shift);   // "He" selected
        Assert.Equal("He", _s.GetTool<TextTool>().SelectedText);
        Type("J");                                        // typing replaces the selection
        Assert.Equal("JLLo", OnlyText().Text);
        Press(EditorKey.End);
        Type("!");
        Assert.Equal("JLLo!", OnlyText().Text);
        Press(EditorKey.Home);
        Press(EditorKey.End, ModifierKeys.Shift);
        Press(EditorKey.Back);
        Assert.Equal("", OnlyText().Text);
    }

    [Fact]
    public void Control_characters_are_ignored_and_other_keys_are_not_consumed()
    {
        _s.SetTool("text");
        Click(50, 300);
        Type("a\tb\r\n");
        Assert.Equal("ab", OnlyText().Text);
        Assert.False(_s.KeyDown(EditorKey.Tab, ModifierKeys.None));
    }

    [Fact]
    public void Mouse_places_the_caret_and_drag_selects()
    {
        var t = AddText("Hello", 100, 300);
        _s.SetTool("text");
        var l = t.Layout;
        Click(100 + l.Letters[1].X + l.Letters[1].Advance * 0.8, 280);   // right part of the letter 'e' → caret after it
        Assert.True(_s.IsTextEditing);
        Assert.Equal(2, _s.GetTool<TextTool>().Caret);

        _s.PointerDown(At(100 + l.Letters[1].X, 280, PointerButton.Left));
        _s.PointerMove(At(100 + l.Letters[4].X + 2, 280));
        _s.PointerUp(At(100 + l.Letters[4].X + 2, 280, PointerButton.Left));
        Assert.Equal("ell", _s.GetTool<TextTool>().SelectedText);
    }

    [Fact]
    public void Double_click_with_the_layout_tool_enters_edit_mode_and_not_the_context_menu()
    {
        var t = AddText("Hello", 100, 300);
        bool menu = false;
        _s.ContextMenuRequested += (_, _) => menu = true;

        Click(150, 270);                 // select it
        Click(150, 270, clicks: 2);      // double-click

        Assert.True(_s.IsTextEditing);
        Assert.False(menu);
        Assert.Same(t, _s.GetTool<TextTool>().Target);
        Type("X");
        Press(EditorKey.Enter);
        Assert.Contains("X", t.Text);
        Assert.Equal(6, t.Text.Length);
        Assert.IsType<SelectTool>(_s.ActiveTool);
        Assert.Equal([t], _s.Selection.Items);
    }

    [Fact]
    public void Other_objects_still_open_their_context_menu_on_double_click()
    {
        var rect = Rect(100, 100, 100, 100);
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, rect));
        var group = new GroupObject();
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, group));
        _s.History.Execute(new AddObjectCommand(group, Rect(400, 100, 100, 100)));
        ContextMenuRequest? req = null;
        _s.ContextMenuRequested += (_, r) => req = r;
        Click(450, 150, 2);
        Assert.Same(group, req?.Target);
        Assert.False(_s.IsTextEditing);
    }

    [Fact]
    public void Editing_existing_text_is_one_undo_step_and_keeps_kerning_with_its_letters()
    {
        var t = AddText("Hello");
        t.Kerning = t.KerningWith(2, 0.3);
        _s.EditText(t);
        Assert.Equal(5, _s.GetTool<TextTool>().Caret);   // caret at the end
        Press(EditorKey.Home);
        Type("Oh ");
        Type("!");
        Press(EditorKey.Back);
        Assert.Equal("Oh Hello", t.Text);
        Assert.Equal(0.3, t.KerningAt(5), 9);            // the offset moved three letters with "l"
        int steps = _s.History.UndoList.Count;
        Press(EditorKey.Enter);
        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Equal("Edit Text", _s.History.UndoDescription);
        _s.Undo();
        Assert.Equal("Hello", t.Text);
        Assert.Equal(0.3, t.KerningAt(2), 9);
        _s.Redo();
        Assert.Equal("Oh Hello", t.Text);
    }

    [Fact]
    public void Editing_without_changes_adds_no_history_and_emptying_text_deletes_it()
    {
        var t = AddText("Hello");
        int steps = _s.History.UndoList.Count;
        _s.EditText(t);
        Press(EditorKey.Escape);
        Assert.Equal(steps, _s.History.UndoList.Count);

        _s.EditText(t);
        _s.GetTool<TextTool>().SelectAll();
        Press(EditorKey.Back);
        Press(EditorKey.Enter);
        Assert.False(_s.Document.Contains(t));
        _s.Undo();
        Assert.True(_s.Document.Contains(t));
        Assert.Equal("Hello", t.Text);
    }

    [Fact]
    public void Undo_and_tool_changes_end_edit_mode_keeping_what_was_typed()
    {
        var t = AddText("Hello");
        _s.EditText(t);
        Type("X");
        _s.SetTool("rectangle");
        Assert.False(_s.IsTextEditing);
        Assert.Equal("HelloX", t.Text);
        Assert.Equal("Edit Text", _s.History.UndoDescription);
    }

    [Fact]
    public void Click_elsewhere_finishes_and_a_click_on_other_text_edits_that_instead()
    {
        var a = AddText("One", 100, 300);
        var b = AddText("Two", 100, 600);
        _s.EditText(a);
        Type("!");
        Click(120, 570);   // inside "Two"
        Assert.Equal("One!", a.Text);
        Assert.True(_s.IsTextEditing);
        Assert.Same(b, _s.GetTool<TextTool>().Target);
        Press(EditorKey.Escape);
    }

    [Fact]
    public void Format_changes_while_creating_text_are_part_of_the_new_object()
    {
        _s.SetTool("text");
        Click(50, 300);
        Type("Big");
        Assert.True(_s.ApplyTextFormat(new TextFormatChange(Size: 200, Weight: 700)));
        Press(EditorKey.Escape);
        var t = OnlyText();
        Assert.Equal(200, t.FontSize);
        Assert.Equal(700, t.FontWeight);
        Assert.Single(_s.History.UndoList);   // just "Add Text"
        Assert.Equal(200, _s.TextDefaults.Size);   // the next new text starts the same way
    }

    // ---- Letter dots ----

    [Fact]
    public void Selected_text_shows_a_dot_in_every_gap_and_dragging_a_dot_spaces_the_letters()
    {
        var t = AddText("home", 100, 300);
        _s.Selection.Set(t);
        var overlay = _s.BuildOverlay().OfType<LetterDotsOverlay>().Single();
        Assert.Equal(3, overlay.Dots.Count);
        Assert.Equal(new Vec2(100 + t.Layout.Letters[1].X, 300), overlay.Dots[0]);

        double x0 = t.Layout.Letters[2].X;
        var dot = overlay.Dots[1];                // between 'o' and 'm'
        _s.PointerDown(At(dot.X, dot.Y, PointerButton.Left));
        _s.PointerMove(At(dot.X + 10, dot.Y));
        Assert.Equal(x0 + 10, t.Layout.Letters[2].X, 3);   // live preview: letters to the right follow
        Assert.Equal(1, _s.BuildOverlay().OfType<LetterDotsOverlay>().Single().Active);
        _s.PointerMove(At(dot.X + 30, dot.Y));
        _s.PointerUp(At(dot.X + 30, dot.Y, PointerButton.Left));

        Assert.Equal(x0 + 30, t.Layout.Letters[2].X, 3);
        Assert.Equal(30.0 / 100, t.KerningAt(1), 6);   // stored in ems
        Assert.Equal(Affine2.Translation(100, 300), t.Transform);   // the object itself didn't move
        Assert.Equal(2, _s.History.UndoList.Count);     // the Add plus ONE command for the whole drag
        Assert.Equal("Letter Spacing", _s.History.UndoDescription);
        Assert.Equal([t], _s.Selection.Items);

        _s.Undo();
        Assert.Equal(x0, t.Layout.Letters[2].X, 3);
        Assert.Empty(t.Kerning);
        _s.Redo();
        Assert.Equal(x0 + 30, t.Layout.Letters[2].X, 3);

        // Dragging left tightens, and negative offsets are allowed.
        var dot2 = _s.BuildOverlay().OfType<LetterDotsOverlay>().Single().Dots[0];
        _s.PointerDown(At(dot2.X, dot2.Y, PointerButton.Left));
        _s.PointerMove(At(dot2.X - 8, dot2.Y));
        _s.PointerUp(At(dot2.X - 8, dot2.Y, PointerButton.Left));
        Assert.Equal(-0.08, t.KerningAt(0), 6);
    }

    [Fact]
    public void Dot_drag_follows_the_objects_scale_and_rotation()
    {
        var t = AddText("home", 100, 300);
        t.Transform = Affine2.Scale(2, 2) * Affine2.Translation(100, 300);   // everything drawn twice as big
        _s.Selection.Set(t);
        var dot = _s.BuildOverlay().OfType<LetterDotsOverlay>().Single().Dots[0];
        _s.PointerDown(At(dot.X, dot.Y, PointerButton.Left));
        _s.PointerUp(At(dot.X + 20, dot.Y, PointerButton.Left));
        Assert.Equal(10.0 / 100, t.KerningAt(0), 6);   // 20 on screen = 10 in text units = 0.1 em
    }

    [Fact]
    public void Escape_cancels_a_dot_drag()
    {
        var t = AddText("home");
        _s.Selection.Set(t);
        var dot = _s.BuildOverlay().OfType<LetterDotsOverlay>().Single().Dots[0];
        _s.PointerDown(At(dot.X, dot.Y, PointerButton.Left));
        _s.PointerMove(At(dot.X + 25, dot.Y));
        Assert.NotEmpty(t.Kerning);
        Press(EditorKey.Escape);
        Assert.Empty(t.Kerning);
        Assert.Single(_s.History.UndoList);   // just the Add
    }

    [Fact]
    public void The_dots_are_drawn_at_half_their_old_size_but_are_still_grabbed_from_handle_distance()
    {
        Assert.Equal(3.5f / 2, LetterDotsOverlay.RadiusPx);   // the dots used to have a radius of 3.5 px
        Assert.True(_s.HandleRadiusPx >= 6);                  // the grab area was not shrunk with them

        var t = AddText("home", 100, 300);
        _s.Selection.Set(t);
        var dot = _s.BuildOverlay().OfType<LetterDotsOverlay>().Single().Dots[1];
        _s.PointerDown(At(dot.X + 4, dot.Y + 4, PointerButton.Left));   // 5.7 px from the dot's center: well outside the drawn dot
        Assert.Equal(1, _s.BuildOverlay().OfType<LetterDotsOverlay>().Single().Active);
        _s.PointerMove(At(dot.X + 14, dot.Y + 4));
        _s.PointerUp(At(dot.X + 14, dot.Y + 4, PointerButton.Left));
        Assert.Equal(10.0 / 100, t.KerningAt(1), 6);

        var now = _s.BuildOverlay().OfType<LetterDotsOverlay>().Single().Dots[1];
        _s.PointerDown(At(now.X + 4, now.Y + 8, PointerButton.Left));   // 8.9 px away: not a dot any more
        Assert.Equal(-1, _s.BuildOverlay().OfType<LetterDotsOverlay>().SingleOrDefault()?.Active ?? -1);
        _s.PointerUp(At(now.X + 4, now.Y + 8, PointerButton.Left));
        Assert.Equal(10.0 / 100, t.KerningAt(1), 6);                     // and nothing was spaced
    }

    [Fact]
    public void Dots_only_show_for_a_single_selected_text()
    {
        var a = AddText("one", 100, 300);
        var b = AddText("two", 100, 600);
        _s.Selection.Set(a);
        Assert.Single(_s.BuildOverlay().OfType<LetterDotsOverlay>());
        _s.Selection.Set([a, b]);
        Assert.Empty(_s.BuildOverlay().OfType<LetterDotsOverlay>());
        _s.Selection.Clear();
        Assert.Empty(_s.BuildOverlay().OfType<LetterDotsOverlay>());
    }

    // ---- Drag a box with the text tool to set the letter height (R24, TASKS C.30) ----

    private void DragBox(double x0, double y0, double x1, double y1)
    {
        _s.PointerDown(At(x0, y0, PointerButton.Left));
        _s.PointerMove(At((x0 + x1) / 2, (y0 + y1) / 2));
        _s.PointerMove(At(x1, y1));
        _s.PointerUp(At(x1, y1, PointerButton.Left));
    }

    /// <summary>Where the baseline lands for a box starting at <paramref name="top"/>: the font's ascent..descent line fills the box.</summary>
    private static double BaselineFor(double top, double height)
    {
        var f = TextEngine.Fonts.Resolve("Arial", 400, false);
        return top + height * f.Ascent / (f.Ascent + f.Descent);
    }

    [Fact]
    public void Dragging_a_box_sets_the_full_font_size_from_the_box_height_only()
    {
        _s.SetTool("text");
        DragBox(100, 200, 500, 260);              // 60 tall, 400 wide
        Assert.True(_s.IsTextEditing);
        Type("H");
        var t = OnlyText();
        Assert.Equal(60, t.FontSize, 6);           // the box height IS the font size (em): a 2 inch box gives 2 inch type
        Assert.Equal(100, t.Transform.OffsetX, 6); // starting at the box's left edge
        Assert.Equal(BaselineFor(200, 60), t.Transform.OffsetY, 6);
        Assert.True(t.GetWorldBounds().Top >= 200 && t.GetWorldBounds().Bottom <= 260);   // the letters sit inside the box
        Press(EditorKey.Escape);

        // The width is ignored: a box twice as wide and the same height gives the same size, and nothing wraps.
        _s.SetTool("text");
        DragBox(100, 400, 900, 460);
        Type("HHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHH");   // far wider than the box
        var u = _s.GetTool<TextTool>().Target!;
        Assert.Equal(t.FontSize, u.FontSize, 6);
        Assert.Single(u.Layout.Lines);
        Assert.True(u.Layout.Frame.Width > 800);   // the line just keeps growing sideways
        Press(EditorKey.Escape);
    }

    [Fact]
    public void Drag_direction_does_not_matter_and_the_box_shows_while_dragging()
    {
        _s.SetTool("text");
        _s.PointerDown(At(500, 260, PointerButton.Left));
        Assert.Empty(_s.BuildOverlay().OfType<MarqueeOverlay>());   // no drag yet: nothing to show
        _s.PointerMove(At(300, 230));
        _s.PointerMove(At(100, 200));
        var box = Assert.Single(_s.BuildOverlay().OfType<MarqueeOverlay>());
        Assert.Equal(new Rect2(100, 200, 500, 260), box.Bounds);
        Assert.Empty(_s.Document.ActiveLayer.Children);              // the text object only exists once the button is released
        Assert.False(_s.IsTextEditing);
        _s.PointerUp(At(100, 200, PointerButton.Left));
        Assert.True(_s.IsTextEditing);
        Assert.Empty(_s.BuildOverlay().OfType<MarqueeOverlay>());   // the box is gone, the caret shows
        Type("H");
        Assert.Equal(new Vec2(100, BaselineFor(200, 60)), OnlyText().Transform.Offset);   // dragged up and left: still the same box
        Assert.Equal(60, OnlyText().FontSize, 6);
    }

    [Fact]
    public void A_plain_click_or_a_flat_drag_keeps_the_default_size()
    {
        _s.SetTool("text");
        Click(200, 400);
        Type("a");
        Assert.Equal(100, _s.GetTool<TextTool>().Target!.FontSize);
        Press(EditorKey.Escape);

        _s.SetTool("text");
        DragBox(100, 600, 400, 601);               // wide but flat: no height to speak of, so it is just a click
        Type("b");
        var b = _s.GetTool<TextTool>().Target!;
        Assert.Equal(100, b.FontSize);
        Assert.Equal(new Vec2(100, 600), b.Transform.Offset);
    }

    [Fact]
    public void The_box_anchors_centered_and_right_aligned_text_at_its_center_and_right_edge()
    {
        _s.TextDefaults.Alignment = TextAlignment.Center;
        _s.SetTool("text");
        DragBox(100, 200, 500, 260);
        Assert.Equal(300, _s.GetTool<TextTool>().Target!.Transform.OffsetX, 6);
        Press(EditorKey.Escape);

        _s.TextDefaults.Alignment = TextAlignment.Right;
        _s.SetTool("text");
        DragBox(100, 400, 500, 460);
        Assert.Equal(500, _s.GetTool<TextTool>().Target!.Transform.OffsetX, 6);
    }

    [Fact]
    public void A_box_drag_is_one_add_text_step_and_escape_cancels_the_box_before_anything_exists()
    {
        _s.SetTool("text");
        DragBox(100, 200, 500, 260);
        Type("Sign");
        Press(EditorKey.Escape);
        Assert.Single(_s.History.UndoList);
        Assert.Equal("Add Text", _s.History.UndoDescription);
        var t = OnlyText();
        Assert.Equal(60, t.FontSize, 6);
        _s.Undo();
        Assert.Empty(_s.Document.ActiveLayer.Children);
        _s.Redo();
        Assert.Equal("Sign", OnlyText().Text);

        _s.SetTool("text");
        _s.PointerDown(At(100, 500, PointerButton.Left));
        _s.PointerMove(At(300, 560));
        Press(EditorKey.Escape);                     // cancel the box
        Assert.Empty(_s.BuildOverlay().OfType<MarqueeOverlay>());
        _s.PointerUp(At(300, 560, PointerButton.Left));
        Assert.False(_s.IsTextEditing);
        Assert.Single(_s.Document.ActiveLayer.Children);
    }

    [Fact]
    public void Lines_started_with_enter_keep_the_size_of_a_box_drawn_line()
    {
        _s.SetTool("text");
        DragBox(100, 200, 500, 260);
        Type("One");
        double size = _s.GetTool<TextTool>().Target!.FontSize;
        Press(EditorKey.Enter);
        Type("Two");
        Press(EditorKey.Escape);
        var texts = _s.Document.ActiveLayer.Children.Cast<TextObject>().Reverse().ToList();   // new text goes to the bottom, so each new line is under the one before: this is creation order
        Assert.Equal(["One", "Two"], texts.Select(t => t.Text));
        Assert.All(texts, t => Assert.Equal(size, t.FontSize, 6));
        Assert.Equal(100, texts[1].Transform.OffsetX, 6);
        Assert.True(texts[1].Transform.OffsetY > texts[0].Transform.OffsetY + size * 0.9);   // a normal line below
    }

    // ---- Enter while typing: finish this text, start a new separate one under it (R24) ----

    [Fact]
    public void Enter_starts_a_separate_ungrouped_unlinked_text_with_the_same_look_lined_up_by_alignment()
    {
        var gold = ObjectStyle.FillOnly(DocColor.FromHex("#F2B705"));
        _s.TextDefaults.Style = gold;
        _s.TextDefaults.Alignment = TextAlignment.Center;
        _s.TextDefaults.Family = "Times New Roman";
        _s.TextDefaults.Weight = 700;
        _s.SetTool("text");
        Click(400, 300);
        Type("Open");
        Press(EditorKey.Enter);
        Type("Daily");
        Press(EditorKey.Enter);
        Type("9 to 5");
        Press(EditorKey.Escape);

        var texts = _s.Document.ActiveLayer.Children.Cast<TextObject>().Reverse().ToList();
        Assert.Equal(["Open", "Daily", "9 to 5"], texts.Select(t => t.Text));
        Assert.All(texts, t =>
        {
            Assert.Equal(("Times New Roman", 700, 100.0, TextAlignment.Center), (t.FontFamily, t.FontWeight, t.FontSize, t.Alignment));
            Assert.Equal(gold, t.Style);
            Assert.Equal(400, t.Transform.OffsetX, 6);        // centered lines share the same center
            Assert.Same(_s.Document.ActiveLayer, t.Parent);   // plain members of the layer
            Assert.IsNotType<GroupObject>(t.Parent);
        });
        Assert.DoesNotContain(_s.Document.AllObjects(), o => o is GroupObject);   // not grouped
        Assert.Equal(300 + texts[0].Layout.LineHeight, texts[1].Transform.OffsetY, 3);

        // Not linked: each one moves, restyles and undoes on its own.
        _s.Selection.Set(texts[1]);
        _s.NudgeSelection(new Vec2(0, 50));
        double line = texts[0].Layout.LineHeight;
        Assert.Equal(300 + line + 50, texts[1].Transform.OffsetY, 3);   // only the nudged one moved
        Assert.Equal(300, texts[0].Transform.OffsetY, 6);
        Assert.Equal(300 + 2 * line, texts[2].Transform.OffsetY, 3);
        Assert.Equal(4, _s.History.UndoList.Count);       // three "Add Text" steps, then the nudge
        _s.Undo();   // the nudge
        _s.Undo();   // "9 to 5" only
        Assert.Equal(2, _s.Document.ActiveLayer.Children.Count);
        Assert.Equal(["Open", "Daily"], _s.Document.ActiveLayer.Children.Cast<TextObject>().Reverse().Select(t => t.Text));
    }

    [Fact]
    public void Enter_on_a_line_left_empty_keeps_nothing_and_the_other_ways_of_stopping_work()
    {
        _s.SetTool("text");
        Click(100, 300);
        Type("A");
        Press(EditorKey.Enter);                       // second line, left empty...
        Press(EditorKey.Escape);                      // ...and stopped
        Assert.Equal(["A"], _s.Document.ActiveLayer.Children.Cast<TextObject>().Select(t => t.Text));
        Assert.False(_s.IsTextEditing);

        _s.SetTool("text");
        Click(100, 600);
        Type("B");
        Press(EditorKey.Enter);
        Type("C");
        Click(700, 100);                              // click elsewhere: C is kept, a new text starts there
        Assert.Equal(["A", "B", "C"], _s.Document.ActiveLayer.Children.Cast<TextObject>().Reverse().Select(t => t.Text).Where(s => s.Length > 0));
        Assert.True(_s.IsTextEditing);                // the new, still empty text is live (not an undo step yet)
        _s.SetTool("select");                         // another tool: the empty new text is dropped
        Assert.Equal(["A", "B", "C"], _s.Document.ActiveLayer.Children.Cast<TextObject>().Reverse().Select(t => t.Text));
        Assert.False(_s.IsTextEditing);
    }

    // ---- Enter: new line (text tool) vs end editing (double-click) ----

    [Fact]
    public void Enter_with_the_text_tool_starts_a_new_line_as_a_separate_text_object()
    {
        _s.SetTool("text");
        Click(100, 300);
        Type("First");
        Press(EditorKey.Enter);
        Assert.True(_s.IsTextEditing);                       // still typing, one line lower
        Assert.IsType<TextTool>(_s.ActiveTool);
        Type("Second");
        Press(EditorKey.Enter);
        Type("Third");
        Press(EditorKey.Escape);                             // Esc ends it

        var texts = _s.Document.ActiveLayer.Children.Cast<TextObject>().Reverse().ToList();
        Assert.Equal(["First", "Second", "Third"], texts.Select(t => t.Text));   // separate objects, never multi-line
        Assert.All(texts, t => Assert.DoesNotContain('\n', t.Text));
        double step = texts[0].Layout.LineHeight;
        Assert.Equal(100, texts[1].Transform.OffsetX, 6);    // same left edge
        Assert.Equal(300 + step, texts[1].Transform.OffsetY, 3);
        Assert.Equal(300 + 2 * step, texts[2].Transform.OffsetY, 3);
        Assert.All(texts, t => Assert.Equal(texts[0].FontSize, t.FontSize));
        Assert.Equal(3, _s.History.UndoList.Count);          // one "Add Text" per line
        Assert.IsType<SelectTool>(_s.ActiveTool);
        _s.Undo();
        Assert.Equal(2, _s.Document.ActiveLayer.Children.Count);
    }

    [Fact]
    public void Enter_on_an_empty_line_leaves_a_gap_and_creates_nothing_for_it()
    {
        _s.SetTool("text");
        Click(100, 300);
        Type("A");
        Press(EditorKey.Enter);
        Press(EditorKey.Enter);          // blank line
        Type("C");
        Press(EditorKey.Escape);
        var texts = _s.Document.ActiveLayer.Children.Cast<TextObject>().Reverse().ToList();
        Assert.Equal(["A", "C"], texts.Select(t => t.Text));
        Assert.Equal(300 + 2 * texts[0].Layout.LineHeight, texts[1].Transform.OffsetY, 3);
    }

    [Fact]
    public void New_lines_follow_the_font_and_rotation_of_the_line_above()
    {
        _s.ApplyTextFormat(new TextFormatChange(Family: "Times New Roman", Size: 60, Weight: 700));
        _s.SetTool("text");
        Click(100, 300);
        Type("Up");
        _s.GetTool<TextTool>().Target!.Transform = Affine2.Rotation(Math.PI / 2) * Affine2.Translation(500, 100);   // text runs downward
        Press(EditorKey.Enter);
        Type("Next");
        Press(EditorKey.Escape);
        var (a, b) = (_s.Document.ActiveLayer.Children.Cast<TextObject>().Reverse().First(), _s.Document.ActiveLayer.Children.Cast<TextObject>().Reverse().Last());
        Assert.Equal(("Times New Roman", 700, 60.0), (b.FontFamily, b.FontWeight, b.FontSize));
        // "Below" is in the text's own direction: perpendicular to the baseline, here to the left on the page.
        Assert.True(b.Transform.OffsetX < a.Transform.OffsetX - 50);
        Assert.Equal(a.Transform.OffsetY, b.Transform.OffsetY, 3);
    }

    [Fact]
    public void Enter_after_a_double_click_ends_editing_without_a_new_line()
    {
        var t = AddText("Hello");
        _s.Selection.Set(t);
        Click(150, 270);
        Click(150, 270, clicks: 2);
        Assert.True(_s.IsTextEditing);
        Press(EditorKey.Enter);
        Assert.False(_s.IsTextEditing);
        Assert.Single(_s.Document.ActiveLayer.Children);
        Assert.IsType<SelectTool>(_s.ActiveTool);
    }

    // ---- Tracking handle ----

    private TrackingHandleOverlay Handle() => _s.BuildOverlay().OfType<TrackingHandleOverlay>().Single();

    [Fact]
    public void Selected_text_has_a_tracking_square_past_its_right_end()
    {
        var t = AddText("home", 100, 300);
        Assert.Empty(_s.BuildOverlay().OfType<TrackingHandleOverlay>());   // nothing selected
        _s.Selection.Set(t);
        var h = Handle();
        var b = t.GetWorldBounds();
        Assert.True(h.Position.X > b.Right);                               // beyond the last letter, clear of the box handles
        Assert.True(h.Position.Y > b.Top && h.Position.Y < 300);
        _s.Selection.Set(AddText("x", 100, 600));                           // a single letter has no gaps to track
        Assert.Empty(_s.BuildOverlay().OfType<TrackingHandleOverlay>());
        _s.Selection.Set(_s.Document.ActiveLayer.Children);                 // several objects: no handle
        Assert.Empty(_s.BuildOverlay().OfType<TrackingHandleOverlay>());
    }

    [Fact]
    public void Dragging_the_tracking_square_spreads_every_gap_equally_and_keeps_dot_tweaks()
    {
        var t = AddText("home", 100, 300);
        t.Kerning = t.KerningWith(1, 0.2);          // a hand-adjusted gap
        _s.Selection.Set(t);
        var before = t.Layout.Letters.Select(l => l.X).ToArray();
        double width = t.Layout.Frame.Width;
        var h = Handle().Position;

        _s.PointerDown(At(h.X, h.Y, PointerButton.Left));
        _s.PointerMove(At(h.X + 15, h.Y));
        Assert.True(Handle().Active);
        Assert.Equal(width + 15, t.Layout.Frame.Width, 3);                 // live preview
        _s.PointerMove(At(h.X + 30, h.Y));
        _s.PointerUp(At(h.X + 30, h.Y, PointerButton.Left));

        double perGap = 30.0 / 3;                                          // four letters, three gaps
        var after = t.Layout.Letters.Select(l => l.X).ToArray();
        for (int i = 0; i < 4; i++) Assert.Equal(before[i] + i * perGap, after[i], 3);   // every gap grew by the same amount
        Assert.Equal(0.2, t.KerningAt(1), 9);                              // the dot offset is untouched
        Assert.Equal(perGap / 100, t.Tracking, 6);
        Assert.Equal(width + 30, t.Layout.Frame.Width, 3);
        Assert.Equal(Affine2.Translation(100, 300), t.Transform);
        Assert.Equal(2, _s.History.UndoList.Count);                        // the Add plus ONE command for the drag
        Assert.Equal("Tracking", _s.History.UndoDescription);

        _s.Undo();
        Assert.Equal(0, t.Tracking);
        Assert.Equal(before[3], t.Layout.Letters[3].X, 3);
        _s.Redo();
        Assert.Equal(perGap / 100, t.Tracking, 6);

        // Dragging left pulls the letters together (negative tracking is allowed).
        h = Handle().Position;
        _s.PointerDown(At(h.X, h.Y, PointerButton.Left));
        _s.PointerUp(At(h.X - 45, h.Y, PointerButton.Left));
        Assert.Equal((perGap - 15) / 100, t.Tracking, 6);
    }

    [Fact]
    public void Escape_cancels_a_tracking_drag_and_the_handle_wins_over_the_box_handles()
    {
        var t = AddText("home", 100, 300);
        _s.Selection.Set(t);
        var h = Handle().Position;
        _s.PointerDown(At(h.X, h.Y, PointerButton.Left));
        _s.PointerMove(At(h.X + 40, h.Y));
        Assert.NotEqual(0, t.Tracking);
        Press(EditorKey.Escape);
        Assert.Equal(0, t.Tracking);
        Assert.Equal(Affine2.Translation(100, 300), t.Transform);   // it was never a move or a scale
        Assert.Single(_s.History.UndoList);
    }

    // ---- Convert / change font through the session ----

    [Fact]
    public void Convert_selection_to_curves_selects_the_result_and_is_undoable()
    {
        var t = AddText("Hey");
        _s.Selection.Set(t);
        Assert.True(_s.CanConvertToCurves);
        _s.ConvertSelectionToCurves();
        var group = Assert.IsType<GroupObject>(Assert.Single(_s.Selection.Items));
        Assert.Equal(3, group.Children.Count);
        Assert.False(_s.CanConvertToCurves);   // nothing live left
        Assert.Equal("Convert to Curves", _s.History.UndoDescription);
        _s.Undo();
        Assert.Same(t, Assert.Single(_s.Document.ActiveLayer.Children));
    }

    [Fact]
    public void Convert_while_editing_commits_the_edit_first()
    {
        var t = AddText("Hi");
        _s.EditText(t);
        Type("o");
        _s.ConvertSelectionToCurves();
        Assert.Equal(3, _s.Document.ActiveLayer.Children.Count);
        var group = Assert.IsType<GroupObject>(_s.Document.ActiveLayer.Children[0].Parent);
        Assert.Equal(3, group.Children.Count);   // H, i, o
        Assert.False(_s.IsTextEditing);
    }

    [Fact]
    public void Vector_editing_text_converts_it_to_one_editable_path_first()
    {
        var t = AddText("Ho");
        _s.SetTool("vector");
        Click(120, 270);   // click the text with the vector tool
        var tool = _s.GetTool<VectorEditTool>();
        var path = Assert.IsType<PathObject>(Assert.Single(_s.Document.ActiveLayer.Children));
        Assert.Same(path, tool.Target);
        Assert.Equal(2, path.Geometry.Verbs.Count(v => v == PathVerb.MoveTo) - 1);   // H has 1 loop, o has 2: 3 loops
        Assert.False(_s.Document.Contains(t));

        _s.Undo();   // the tool lets go of the object that no longer exists
        Assert.Same(t, Assert.Single(_s.Document.ActiveLayer.Children));
        Assert.Null(tool.Target);
        _s.Redo();
        Assert.IsType<PathObject>(Assert.Single(_s.Document.ActiveLayer.Children));
    }

    [Fact]
    public void Edit_vectors_by_double_click_does_not_apply_to_text()
    {
        var t = AddText("Ho");
        Click(120, 270);
        Click(120, 270, 2);
        Assert.IsType<TextTool>(_s.ActiveTool);   // text edit, never the vector tool
        Assert.Same(t, Assert.Single(_s.Document.ActiveLayer.Children));
    }

    [Fact]
    public void Text_defaults_change_when_nothing_is_selected()
    {
        _s.ApplyTextFormat(new TextFormatChange(Family: "Times New Roman", Size: 20, Tracking: 0.05, Alignment: TextAlignment.Center));
        _s.SetTool("text");
        Click(300, 300);
        Type("Defaults");
        Press(EditorKey.Escape);
        var t = OnlyText();
        Assert.Equal("Times New Roman", t.FontFamily);
        Assert.Equal(20, t.FontSize);
        Assert.Equal(0.05, t.Tracking);
        Assert.Equal(TextAlignment.Center, t.Alignment);
    }

    [Fact]
    public void Selection_format_change_is_a_single_undo_step()
    {
        var a = AddText("A");
        var b = AddText("B", 100, 600);
        _s.Selection.Set([a, b]);
        Assert.True(_s.ApplyTextFormat(new TextFormatChange(Size: 40)));
        Assert.Equal(40, a.FontSize);
        Assert.Equal(40, b.FontSize);
        _s.Undo();
        Assert.Equal(100, a.FontSize);
        Assert.Equal(100, b.FontSize);
    }
}
