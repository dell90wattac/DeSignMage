using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Canvas;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Live text (R6) through the real window: text tool, edit mode, letter dots, font bar, convert to curves.</summary>
public class TextShellTests
{
    private static (MainWindow Window, DesignCanvas Canvas) Open()
    {
        var w = new MainWindow(new StartupOptions()) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        return (w, w.FindControl<DesignCanvas>("Surface")!);
    }

    private static Point Window(DesignCanvas c, double x, double y) =>
        c.TranslatePoint(new Point(x, y), TopLevel.GetTopLevel(c)!).GetValueOrDefault();

    private static Point AtDoc(MainWindow w, DesignCanvas c, Vec2 doc)
    {
        var s = w.Session.Viewport.ToScreen(doc);
        return Window(c, s.X, s.Y);
    }

    private static void Click(MainWindow w, Point p)
    {
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
    }

    private static void Key(MainWindow w, Key key) => w.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);

    private static TextObject AddText(MainWindow w, string text, double inchesX = 4, double inchesY = 6, string family = "Arial")
    {
        var t = new TextObject(text, family, Units.FromInches(1))
        {
            Transform = Affine2.Translation(Units.FromInches(inchesX), Units.FromInches(inchesY)),
        };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, t));
        Dispatcher.UIThread.RunJobs();
        return t;
    }

    [AvaloniaFact]
    public void Dragging_a_box_with_the_text_tool_sets_the_letter_height_then_enter_starts_a_separate_line()
    {
        var (w, c) = Open();
        var s = w.Session;
        s.SetTool("text");
        var from = Window(c, 300, 250);
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(from + new Point(150, 30));
        w.MouseMove(from + new Point(300, 80));        // 80 px tall on screen, 300 wide
        Assert.NotEmpty(s.BuildOverlay().OfType<MarqueeOverlay>());   // the box is shown while dragging
        w.MouseUp(from + new Point(300, 80), MouseButton.Left);
        Assert.True(s.IsTextEditing);

        w.KeyTextInput("HAT");
        Key(w, Avalonia.Input.Key.Enter);
        w.KeyTextInput("Sign");
        Key(w, Avalonia.Input.Key.Escape);

        var texts = s.Document.ActiveLayer.Children.Cast<TextObject>().Reverse().ToList();   // new text goes under the older, so this is creation order
        Assert.Equal(["HAT", "Sign"], texts.Select(x => x.Text));
        double box = 80 / s.Viewport.Zoom;                              // the dragged height in document units
        Assert.Equal(box, texts[0].FontSize, 1);                         // the box height is the font size
        Assert.Equal(texts[0].FontSize, texts[1].FontSize, 6);          // the second line has the same size
        Assert.Empty(s.Document.AllObjects().OfType<GroupObject>());   // and is a separate, ungrouped object
        w.Close();
    }

    [AvaloniaFact]
    public void Vector_view_draws_letter_holes_red_and_outer_loops_blue()
    {
        var (w, _) = Open();                                   // the window installs the real font service
        var t = AddText(w, "o", inchesX: 0, inchesY: 1);
        var b = t.GetWorldBounds();

        using var renderer = new DeSignMage.Rendering.SceneRenderer(w.Session.Document) { Wireframe = true };
        var pic = renderer.GetDocumentPicture(null);
        using var bmp = new SkiaSharp.SKBitmap((int)Math.Ceiling(b.Right) + 4, (int)Math.Ceiling(b.Bottom) + 4);
        using (var canvas = new SkiaSharp.SKCanvas(bmp))
        {
            canvas.Clear(SkiaSharp.SKColors.White);
            canvas.DrawPicture(pic.Picture);
        }
        pic.Release();

        int red = 0, blue = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red > 150 && c.Green < 100 && c.Blue < 100) red++;
                if (c.Blue > 150 && c.Red < 100 && c.Green < 100) blue++;
            }
        Assert.True(blue > 20, $"outer loop should be blue ({blue} px)");
        Assert.True(red > 20, $"the hole of the o should be red ({red} px)");
        w.Close();
    }

    [AvaloniaFact]
    public void Text_tool_click_type_and_escape_creates_live_text_that_stays_text()
    {
        var (w, c) = Open();
        var s = w.Session;
        s.SetTool("text");
        Click(w, Window(c, 300, 300));
        Assert.True(s.IsTextEditing);

        w.KeyTextInput("Sign");
        w.KeyTextInput(" 7");
        Key(w, Avalonia.Input.Key.Escape);

        var t = Assert.IsType<TextObject>(Assert.Single(s.Document.ActiveLayer.Children));
        Assert.Equal("Sign 7", t.Text);
        Assert.False(s.IsTextEditing);
        Assert.IsType<SelectTool>(s.ActiveTool);
        Assert.Equal([t], s.Selection.Items);
        Assert.Equal("Add Text", s.History.UndoDescription);
        Assert.False(t.Layout.Outline.IsEmpty);   // it really rendered from the font
        w.Close();
    }

    [AvaloniaFact]
    public void Typing_never_triggers_letter_shortcuts_and_space_types_a_space()
    {
        var (w, c) = Open();
        var s = w.Session;
        s.SetTool("text");
        Click(w, Window(c, 300, 300));

        // Keys that are shortcuts when the canvas isn't editing text: U, K, 1 (Layout tool), 2 (Vector tool), Space (pan).
        foreach (var (key, text) in new[] { (Avalonia.Input.Key.U, "u"), (Avalonia.Input.Key.K, "k"), (Avalonia.Input.Key.D1, "1"), (Avalonia.Input.Key.D2, "2"), (Avalonia.Input.Key.Space, " ") })
        {
            w.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, text);
            w.KeyTextInput(text);
        }
        Assert.True(s.IsTextEditing);
        Assert.IsType<TextTool>(s.ActiveTool);
        Assert.False(s.SpaceHeld);
        Key(w, Avalonia.Input.Key.Escape);
        Assert.Equal("uk12 ", Assert.IsType<TextObject>(Assert.Single(s.Document.ActiveLayer.Children)).Text);
        w.Close();
    }

    [AvaloniaFact]
    public void Double_click_on_text_edits_it_instead_of_opening_the_context_menu()
    {
        var (w, c) = Open();
        var s = w.Session;
        var t = AddText(w, "home");
        s.ZoomToContent();
        var center = AtDoc(w, c, t.GetWorldBounds().Center);

        Click(w, center);
        w.MouseDown(center, MouseButton.Left);   // second press of a double-click
        w.MouseUp(center, MouseButton.Left);

        Assert.True(s.IsTextEditing);
        Assert.Null(w.LastObjectMenu);
        w.KeyTextInput("X");
        Key(w, Avalonia.Input.Key.Enter);
        Assert.Contains("X", t.Text);
        Assert.Equal(5, t.Text.Length);
        Assert.Equal([t], s.Selection.Items);
        Assert.Equal("Edit Text", s.History.UndoDescription);
        w.Close();
    }

    [AvaloniaFact]
    public void Backspace_and_arrow_keys_edit_the_text()
    {
        var (w, c) = Open();
        var s = w.Session;
        var t = AddText(w, "ab");
        s.EditText(t);
        Key(w, Avalonia.Input.Key.Left);
        w.KeyTextInput("X");                  // a X| b ... caret between a and b
        Key(w, Avalonia.Input.Key.Back);      // removes X again
        Key(w, Avalonia.Input.Key.Delete);    // removes b
        Key(w, Avalonia.Input.Key.Enter);
        Assert.Equal("a", t.Text);
        w.Close();
    }

    [AvaloniaFact]
    public void Dragging_a_letter_dot_spaces_the_letters_as_one_undo_step()
    {
        var (w, c) = Open();
        var s = w.Session;
        var t = AddText(w, "home");
        s.ZoomToContent();
        s.Selection.Set(t);
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();

        var dots = s.BuildOverlay().OfType<LetterDotsOverlay>().Single().Dots;
        Assert.Equal(3, dots.Count);
        double x2 = t.Layout.Letters[2].X;
        var start = AtDoc(w, c, dots[1]);
        w.MouseDown(start, MouseButton.Left);
        w.MouseMove(start + new Point(15, 0));
        w.MouseMove(start + new Point(30, 0));
        w.MouseUp(start + new Point(30, 0), MouseButton.Left);

        Assert.Equal(x2 + 30 / s.Viewport.Zoom, t.Layout.Letters[2].X, 2);
        Assert.Equal("Letter Spacing", s.History.UndoDescription);
        int steps = s.History.UndoList.Count;
        w.KeyPress(Avalonia.Input.Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Equal(steps - 1, s.History.UndoList.Count);
        Assert.Empty(t.Kerning);
        w.Close();
    }

    [AvaloniaFact]
    public void Dragging_the_tracking_square_spreads_the_letters_as_one_undo_step()
    {
        var (w, c) = Open();
        var s = w.Session;
        var t = AddText(w, "home");
        s.ZoomToContent();
        s.Selection.Set(t);
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();

        var handle = s.BuildOverlay().OfType<TrackingHandleOverlay>().Single().Position;
        double width = t.Layout.Frame.Width;
        var start = AtDoc(w, c, handle);
        w.MouseDown(start, MouseButton.Left);
        w.MouseMove(start + new Point(20, 0));
        w.MouseMove(start + new Point(40, 0));
        w.MouseUp(start + new Point(40, 0), MouseButton.Left);

        Assert.Equal(width + 40 / s.Viewport.Zoom, t.Layout.Frame.Width, 2);
        Assert.Equal("Tracking", s.History.UndoDescription);
        s.Undo();
        Assert.Equal(0, t.Tracking);
        w.Close();
    }

    [AvaloniaFact]
    public void Enter_in_the_text_tool_makes_the_next_line_a_separate_object()
    {
        var (w, c) = Open();
        var s = w.Session;
        s.SetTool("text");
        Click(w, Window(c, 300, 200));
        w.KeyTextInput("One");
        Key(w, Avalonia.Input.Key.Enter);
        w.KeyTextInput("Two");
        Key(w, Avalonia.Input.Key.Escape);
        var texts = s.Document.ActiveLayer.Children.Cast<TextObject>().Reverse().ToList();   // new text goes under the older, so this is creation order
        Assert.Equal(["One", "Two"], texts.Select(x => x.Text));
        Assert.True(texts[1].Transform.OffsetY > texts[0].Transform.OffsetY);
        w.Close();
    }

    [AvaloniaFact]
    public void Selecting_text_shows_the_font_bar_and_changing_the_font_is_undoable()
    {
        var (w, c) = Open();
        var s = w.Session;
        var t = AddText(w, "home");
        s.Selection.Set(t);
        Dispatcher.UIThread.RunJobs();

        var panel = w.GetVisualDescendants().OfType<TextFormatPanel>().First();
        Assert.True(panel.IsVisible);
        Assert.Equal("Arial", ((FontItem)panel.FamilyBox.SelectedItem!).Name);
        Assert.True(panel.StyleBox.ItemCount >= 4);   // Regular, Italic, Bold, Bold Italic

        var times = FontCatalog.Installed.First(f => f.Name == "Times New Roman");
        panel.FamilyBox.Choose(times);   // the user clicking the row
        Assert.Equal("Times New Roman", t.FontFamily);
        Assert.Equal("Change Font", s.History.UndoDescription);

        var bold = panel.StyleBox.Items.Cast<DeSignMage.Core.Text.FontStyleInfo>().First(x => x.Weight == 700 && !x.Italic);
        panel.StyleBox.SelectedItem = bold;
        Assert.Equal(700, t.FontWeight);

        panel.SizeBox.Text = "72";
        panel.SizeBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
        Assert.Equal(72, t.FontSize);

        s.Undo(); s.Undo(); s.Undo();
        Assert.Equal("Arial", t.FontFamily);
        Assert.Equal(400, t.FontWeight);
        Assert.Equal(Units.FromInches(1), t.FontSize);
        w.Close();
    }

    [AvaloniaFact]
    public void A_missing_font_is_marked_in_the_font_bar_and_the_text_still_draws()
    {
        var (w, c) = Open();
        var s = w.Session;
        var t = AddText(w, "home", family: "Lato Imaginary Semibold");
        s.Selection.Set(t);
        Dispatcher.UIThread.RunJobs();

        Assert.True(t.IsFontMissing);
        Assert.False(t.Layout.Outline.IsEmpty);
        var panel = w.GetVisualDescendants().OfType<TextFormatPanel>().First();
        var shown = Assert.IsType<FontItem>(panel.FamilyBox.SelectedItem);
        Assert.True(shown.Missing);
        Assert.Equal("Lato Imaginary Semibold", shown.Name);   // the requested name is what the UI keeps showing
        Assert.Equal("Lato Imaginary Semibold", t.FontFamily);
        w.Close();
    }

    [AvaloniaFact]
    public void Convert_to_curves_from_the_text_menu_command()
    {
        var (w, c) = Open();
        var s = w.Session;
        var t = AddText(w, "Hey");
        s.Selection.Set(t);
        Dispatcher.UIThread.RunJobs();

        var button = w.GetVisualDescendants().OfType<Button>().First(b => b.IsVisible && b.Content as string == "Convert to Curves");
        Assert.True(button.IsEnabled);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        var group = Assert.IsType<GroupObject>(Assert.Single(s.Document.ActiveLayer.Children.Select(o => o.TopLevel).Distinct()));
        Assert.Equal(3, group.Children.Count);
        Assert.All(group.Children, ch => Assert.IsType<PathObject>(ch));
        w.Close();
    }

    [AvaloniaFact]
    public void Edit_mode_draws_a_caret_and_text_renders_pixels()
    {
        var (w, c) = Open();
        var s = w.Session;
        var t = AddText(w, "Hi");
        s.ZoomToContent();
        s.EditText(t);
        w.CaptureRenderedFrame();
        Assert.Contains(s.BuildOverlay(), o => o is TextEditOverlay);
        var frame = w.CaptureRenderedFrame()!;
        Assert.True(frame.PixelSize.Width > 100);
        w.Close();
    }
}
