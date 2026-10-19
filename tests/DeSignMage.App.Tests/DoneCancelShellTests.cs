using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Canvas;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Feedback;
using SkiaSharp;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>
/// Done and Cancel on the Context Bar of the tools that are modes: the same two buttons, Done then Cancel, at the right end of the tool's own controls; shown only
/// while the mode is on; Done keeps and Cancel takes everything back, both to the Layout tool. The light and dark pictures for the notes are saved by the last test.
/// </summary>
[Collection("app-settings")]   // dark mode is program-wide: one class at a time
public class DoneCancelShellTests
{
    private static async Task WithTheme(Func<Task> body)
    {
        var oldStore = AppSettings.Store;
        AppSettings.Store = new SettingsStore(null);
        UiScale.Store = AppSettings.Store;
        UiScale.Reset();
        AppTheme.Reset();
        try { await body(); }
        finally
        {
            AppTheme.Reset();
            UiScale.Reset();
            UiScale.Store = null;
            AppSettings.Store = oldStore;
        }
    }

    private static MainWindow Open(double width = 1700)
    {
        var w = new MainWindow(new StartupOptions()) { Width = width, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        w.FindControl<DesignCanvas>("Surface")!.Focus();
        return w;
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    private static Control Bar(MainWindow w) => (Control)w.FindControl<ContentControl>("ContextBarHost")!.Content!;

    private static RectangleObject AddRect(MainWindow w, double x, double y, double width = 100, double height = 100)
    {
        var r = new RectangleObject(width, height) { Transform = Affine2.Translation(x, y), Style = ObjectStyle.FillOnly(DocColor.FromHex("#F2B705")) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, r));
        return r;
    }

    private static TextObject AddText(MainWindow w, string text = "Sign")
    {
        var t = new TextObject(text, "Arial", 60) { Transform = Affine2.Translation(200, 400), Style = ObjectStyle.FillOnly(DocColor.Black) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, t));
        return t;
    }

    private static void Click(Control c) => c.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>A real mouse click on the middle of a control.</summary>
    private static void Press(MainWindow w, Control c)
    {
        var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w).GetValueOrDefault();
        w.MouseDown(p, Avalonia.Input.MouseButton.Left);
        w.MouseUp(p, Avalonia.Input.MouseButton.Left);
        Flush();
    }

    /// <summary>A click on the canvas at a document point (the Vector Draw tool places a node).</summary>
    private static void CanvasClick(MainWindow w, double x, double y)
    {
        var doc = new Vec2(x, y);
        var screen = w.Session.Viewport.ToScreen(doc);
        w.Session.PointerDown(new PointerInput(screen, doc, PointerButton.Left, ModifierKeys.None));
        w.Session.PointerUp(new PointerInput(screen, doc, PointerButton.Left, ModifierKeys.None));
        Flush();
    }

    private static Button? Find(Control bar, string text) =>
        bar.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == text && b.IsEffectivelyVisible);

    private static bool Shows(Control bar, string text) => Find(bar, text) is not null;

    /// <summary>The controls of the bar that can be used (buttons, check boxes, fields, lists), in the order they sit in the bar, those showing only.</summary>
    private static List<Control> Controls(Control bar) =>
        bar.GetVisualDescendants().OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && (c is Button or ToggleButton or TextBox or ComboBox or RadioButton or CheckBox))
            .Where(c => !c.GetVisualAncestors().Any(a => a is ComboBox or TextBox or Button && a != c))   // not a part of another control (the arrow button inside a list)
            .ToList();

    private static string TextOf(Control c) => c is ContentControl { Content: string s } ? s : "";

    // ---------------------------------------------------------------- one look everywhere

    /// <summary>Every mode: how to open it on a window with a rectangle (and for the text ones a text) in the layout.</summary>
    private static (string Name, Action<MainWindow, RectangleObject, TextObject> Open)[] Modes() =>
    [
        ("Soft Shadow", (w, r, _) => { w.Session.Selection.Set(r); Assert.True(w.Session.BeginSoftEffect(SoftKind.SoftShadow)); }),
        ("Glow", (w, r, _) => { w.Session.Selection.Set(r); Assert.True(w.Session.BeginSoftEffect(SoftKind.Glow)); }),
        ("Feather", (w, r, _) => { w.Session.Selection.Set(r); Assert.True(w.Session.BeginFeather()); }),
        ("Easy Bevel", (w, r, _) => { w.Session.Selection.Set(r); Assert.True(w.Session.BeginBevel()); }),
        ("Gradient Blend", (w, r, _) => { w.Session.Selection.Set(r); Assert.True(w.Session.BeginGradient()); }),
        ("Outline", (w, r, _) => { w.Session.Selection.Set(r); w.Session.BeginOutline(OutlineKind.Outline); w.Session.GetTool<OutlineTool>().Update(o => o with { First = 10 }); }),
        ("Shadow", (w, r, _) => { w.Session.Selection.Set(r); Assert.True(w.Session.BeginShadow(ShadowForm.Flat)); }),
        ("Text on Path", (w, _, t) => { w.Session.Selection.Set(t); Assert.True(w.Session.BeginTextOnPath()); }),
        ("Convert to Rounded Rectangle", (w, r, _) => { w.Session.Selection.Set(r); w.Session.BeginConvertToRoundedRectangle(); }),
        ("Vector Edit", (w, r, _) => { w.Session.Selection.Set(r); w.Session.SetTool("vector"); }),
        ("Loop Edit", (w, r, _) => { w.Session.Selection.Set(r); w.Session.SetTool("loop"); }),
        ("Vector Draw", (w, _, _) => { w.Session.SetTool("vectordraw"); CanvasClick(w, 600, 500); CanvasClick(w, 700, 520); }),
        ("Text", (w, _, _) => { w.Session.SetTool("text"); w.Session.GetTool<TextTool>().BeginNew(new Vec2(600, 600)); w.Session.TextInput("Hi"); }),
    ];

    [AvaloniaFact]
    public async Task Every_mode_bar_ends_with_Done_then_Cancel_the_same_two_buttons()
    {
        await WithTheme(() =>
        {
            foreach (var (name, open) in Modes())
            {
                var w = Open();
                var r = AddRect(w, 100, 100);
                var t = AddText(w);
                open(w, r, t);
                Flush();
                var bar = Bar(w);
                var last = Controls(bar).TakeLast(2).ToList();
                Assert.True(last.Count == 2, name);
                Assert.Equal(["Done", "Cancel"], last.Select(TextOf));                              // the right end of the tool's own controls, in this order
                Assert.All(last, c => Assert.True(c.Classes.Contains("act"), name));                // the Action helper's look
                Assert.Equal(last[0].Bounds.Height, last[1].Bounds.Height);

                var a = last[0].TranslatePoint(default, w)!.Value;
                var b = last[1].TranslatePoint(default, w)!.Value;
                Assert.Equal(a.Y, b.Y);                                                             // on one line, Done to the left of Cancel
                Assert.True(a.X < b.X, name);
                Assert.Single(Controls(bar), c => TextOf(c) == "Done");
                Assert.Single(Controls(bar), c => TextOf(c) == "Cancel");
                w.Close();
            }
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task The_same_buttons_show_with_Dark_Menus_on()
    {
        await WithTheme(() =>
        {
            AppTheme.SetDarkMenus(true, remember: false);
            var w = Open();
            var r = AddRect(w, 100, 100);
            w.Session.Selection.Set(r);
            w.Session.BeginSoftEffect(SoftKind.SoftShadow);
            Flush();
            var bar = Bar(w);
            Assert.True(Shows(bar, "Done") && Shows(bar, "Cancel"));
            Assert.All(new[] { Find(bar, "Done")!, Find(bar, "Cancel")! }, b => Assert.True(b.Bounds.Width > 20 && b.Bounds.Height > 10));
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public void A_short_bar_stays_on_one_line_with_the_buttons_added()
    {
        var w = Open(1700);
        w.Session.SetTool("vectordraw");
        CanvasClick(w, 600, 500);
        CanvasClick(w, 700, 520);
        var bar = Assert.IsType<VectorDrawContextBar>(Bar(w));
        Assert.True(bar.Done.IsVisible);
        Assert.False(bar.Stacked);                                                                 // the compact one-row Context Bar (C.42) keeps its height
        Assert.Equal(ContextBars.RowHeightBase * UiScale.Scale, bar.Bounds.Height, 1);                // one row high, not two
        w.Close();
    }

    // ---------------------------------------------------------------- shown only while the mode is on

    [AvaloniaFact]
    public void Tools_that_finish_by_themselves_and_the_Layout_tool_have_no_Cancel_and_no_new_Done()
    {
        var w = Open();
        var r = AddRect(w, 100, 100);
        w.Session.Selection.Set(r);
        foreach (var id in new[] { "select", "rectangle", "ellipse", "roundedRectangle", "square", "circle", "polygon", "arrow", "eyedropper", "knife", "cookie" })
        {
            w.Session.SetTool(id);
            Flush();
            Assert.False(Shows(Bar(w), "Cancel"), id);
            Assert.False(Shows(Bar(w), "Done") && id is not ("knife" or "cookie"), id);            // Knife and Cookie Cutter keep their own "leave the tool" button as it was
            w.Session.SetTool("select");
        }
        w.Close();
    }

    [AvaloniaFact]
    public void Vector_draw_shows_the_buttons_only_while_a_path_is_being_drawn()
    {
        var w = Open();
        w.Session.SetTool("vectordraw");
        Flush();
        var bar = Assert.IsType<VectorDrawContextBar>(Bar(w));
        Assert.False(bar.Done.IsVisible || bar.Cancel.IsVisible);                                  // nothing drawn
        CanvasClick(w, 600, 500);
        Assert.True(bar.Done.IsVisible && bar.Cancel.IsVisible);
        CanvasClick(w, 700, 520);
        w.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);   // the existing finish gesture: the path is made, the tool stays
        Flush();
        Assert.False(bar.Done.IsVisible || bar.Cancel.IsVisible);
        Assert.Equal("vectordraw", w.Session.ActiveTool.Id);
        w.Close();
    }

    [AvaloniaFact]
    public void The_text_tool_shows_the_buttons_only_while_a_text_is_being_typed()
    {
        var w = Open();
        w.Session.SetTool("text");
        Flush();
        var bar = Assert.IsType<TextToolContextBar>(Bar(w));
        Assert.False(bar.Panel.Done!.IsVisible || bar.Panel.Cancel!.IsVisible);
        w.Session.GetTool<TextTool>().BeginNew(new Vec2(600, 600));
        w.Session.TextInput("Hi");
        Flush();
        Assert.True(bar.Panel.Done.IsVisible && bar.Panel.Cancel.IsVisible);
        w.Session.FinishTextEditing();                                                              // Enter / Esc / clicking away: the old gestures still work
        Flush();
        Assert.False(Shows(Bar(w), "Done"));
        w.Close();
    }

    [AvaloniaFact]
    public void The_node_tools_show_the_buttons_while_an_object_is_open()
    {
        var w = Open();
        var r = AddRect(w, 100, 100);
        w.Session.Selection.Clear();
        w.Session.SetTool("vector");
        Flush();
        var bar = Assert.IsType<VectorEditContextBar>(Bar(w));
        Assert.False(bar.Done.IsVisible || bar.Cancel.IsVisible);                                  // nothing open
        w.Session.EditVectors(r);
        Flush();
        Assert.True(bar.Done.IsVisible && bar.Cancel.IsVisible);

        w.Session.SetTool("select");
        w.Session.Selection.Clear();
        w.Session.SetTool("loop");
        Flush();
        var loop = Assert.IsType<LoopEditContextBar>(Bar(w));
        Assert.False(loop.Done.IsVisible || loop.Cancel.IsVisible);
        w.Session.OpenPathEdit(r, "loop");
        Flush();
        Assert.True(loop.Done.IsVisible && loop.Cancel.IsVisible);
        w.Close();
    }

    [AvaloniaFact]
    public void Done_waits_for_something_to_keep_where_there_is_nothing_yet_but_Cancel_is_always_there()
    {
        var w = Open();
        var r = AddRect(w, 100, 100);
        w.Session.Selection.Set(r);
        w.Session.BeginOutline(OutlineKind.Outline);
        Flush();
        var bar = Assert.IsType<OutlineContextBar>(Bar(w));
        Assert.False(bar.Done.IsVisible);                                                           // a distance of 0 makes nothing
        Assert.True(bar.Cancel.IsVisible);
        w.Session.GetTool<OutlineTool>().Update(o => o with { First = 10 });
        Flush();
        Assert.True(bar.Done.IsVisible);

        w.Session.SetTool("select");
        w.Session.Selection.Clear();
        w.Session.SetTool("roundCorners");
        Flush();
        var round = Assert.IsType<RoundCornersContextBar>(Bar(w));
        Assert.False(round.Done.IsVisible);
        Assert.True(round.Cancel.IsVisible);
        w.Close();
    }

    // ---------------------------------------------------------------- the buttons work in the window

    [AvaloniaFact]
    public void Soft_shadow_bar_Done_keeps_and_Cancel_takes_back_both_to_the_Layout_tool_with_no_extra_steps()
    {
        var w = Open();
        var r = AddRect(w, 100, 100);
        var s = w.Session;
        s.Selection.Set(r);
        int steps = s.History.UndoList.Count;

        Assert.True(s.BeginSoftEffect(SoftKind.SoftShadow));
        Flush();
        Click(Find(Bar(w), "Cancel")!);
        Flush();
        Assert.IsType<SelectTool>(s.ActiveTool);
        Assert.Equal(steps, s.History.UndoList.Count);
        Assert.Empty(s.History.RedoList);
        Assert.Equal([r], s.Selection.Items);
        Assert.Equal([r], s.Document.ActiveLayer.Children);

        s.BeginSoftEffect(SoftKind.SoftShadow);
        Flush();
        Click(Find(Bar(w), "Done")!);
        Flush();
        Assert.IsType<SelectTool>(s.ActiveTool);
        Assert.Equal(steps + 1, s.History.UndoList.Count);
        Assert.Equal(1, s.Document.GroupCount);
        Assert.False(Shows(Bar(w), "Done"));                                                        // the Layout tool's bar has none
        w.Close();
    }

    [AvaloniaFact]
    public void Vector_draw_bar_Done_finishes_the_open_path_and_Cancel_throws_it_away_with_real_clicks()
    {
        var w = Open();
        var s = w.Session;
        s.SetTool("vectordraw");
        CanvasClick(w, 600, 500);
        CanvasClick(w, 700, 520);
        CanvasClick(w, 700, 620);
        Flush();
        Press(w, Find(Bar(w), "Cancel")!);
        Assert.Empty(s.Document.ActiveLayer.Children);
        Assert.IsType<SelectTool>(s.ActiveTool);
        Assert.Empty(s.History.UndoList);

        s.SetTool("vectordraw");
        CanvasClick(w, 600, 500);
        CanvasClick(w, 700, 520);
        CanvasClick(w, 700, 620);
        Flush();
        Press(w, Find(Bar(w), "Done")!);
        var path = Assert.IsType<PathObject>(Assert.Single(s.Document.ActiveLayer.Children));
        Assert.False(PathEditing.ToLoops(path.Geometry)[0].Closed);
        Assert.IsType<SelectTool>(s.ActiveTool);
        Assert.Equal([path], s.Selection.Items);
        Assert.Single(s.History.UndoList);
        w.Close();
    }

    [AvaloniaFact]
    public void Text_bar_Cancel_discards_new_text_and_Done_finishes_it()
    {
        var w = Open();
        var s = w.Session;
        s.SetTool("text");
        s.GetTool<TextTool>().BeginNew(new Vec2(600, 600));
        s.TextInput("Gone");
        Flush();
        Press(w, Find(Bar(w), "Cancel")!);
        Assert.Empty(s.Document.ActiveLayer.Children);
        Assert.IsType<SelectTool>(s.ActiveTool);
        Assert.Empty(s.History.UndoList);

        s.SetTool("text");
        s.GetTool<TextTool>().BeginNew(new Vec2(600, 600));
        s.TextInput("Kept");
        Flush();
        Press(w, Find(Bar(w), "Done")!);
        var text = Assert.IsType<TextObject>(Assert.Single(s.Document.ActiveLayer.Children));
        Assert.Equal("Kept", text.Text);
        Assert.IsType<SelectTool>(s.ActiveTool);
        Assert.Equal([text], s.Selection.Items);
        w.Close();
    }

    [AvaloniaFact]
    public void Text_on_path_bar_Done_keeps_the_path_and_Cancel_gives_back_the_straight_text()
    {
        var w = Open();
        var s = w.Session;
        var t = AddText(w);
        s.Selection.Set(t);
        int steps = s.History.UndoList.Count;

        s.BeginTextOnPath();
        Flush();
        Assert.True(t.IsOnPath);
        Click(Find(Bar(w), "Cancel")!);
        Assert.False(t.IsOnPath);
        Assert.Equal(steps, s.History.UndoList.Count);
        Assert.IsType<SelectTool>(s.ActiveTool);
        Assert.Equal([t], s.Selection.Items);

        s.BeginTextOnPath();
        Flush();
        Click(Find(Bar(w), "Done")!);
        Assert.True(t.IsOnPath);
        Assert.Equal(steps + 1, s.History.UndoList.Count);
        Assert.IsType<SelectTool>(s.ActiveTool);
        w.Close();
    }

    [AvaloniaFact]
    public void Convert_to_rounded_rectangle_bar_Done_still_applies_and_Cancel_leaves_the_rectangle()
    {
        var w = Open();
        var s = w.Session;
        var r = AddRect(w, 100, 100);
        s.Selection.Set(r);
        int steps = s.History.UndoList.Count;

        s.BeginConvertToRoundedRectangle();
        s.GetTool<RoundCornersTool>().Radius = 15;
        Flush();
        Click(Find(Bar(w), "Cancel")!);
        Assert.Equal(steps, s.History.UndoList.Count);
        Assert.Equal(0, r.CornerRadius);
        Assert.IsType<SelectTool>(s.ActiveTool);

        s.BeginConvertToRoundedRectangle();
        s.GetTool<RoundCornersTool>().Radius = 15;
        Flush();
        Click(Find(Bar(w), "Done")!);
        Assert.Equal(steps + 1, s.History.UndoList.Count);
        Assert.IsType<SelectTool>(s.ActiveTool);
        w.Close();
    }

    [AvaloniaFact]
    public void Vector_edit_bar_Cancel_puts_the_nodes_back_and_Done_keeps_them()
    {
        var w = Open();
        var s = w.Session;
        var r = AddRect(w, 100, 100, 100, 50);
        s.Selection.Set(r);
        s.SetTool("vector");
        var tool = s.GetTool<VectorEditTool>();
        int steps = s.History.UndoList.Count;

        CanvasClick(w, 100, 100);
        tool.SetAnchorPosition(new Vec2(60, 60));
        Flush();
        Assert.IsType<PathObject>(Assert.Single(s.Document.ActiveLayer.Children));
        Click(Find(Bar(w), "Cancel")!);
        Assert.Same(r, Assert.Single(s.Document.ActiveLayer.Children));
        Assert.Equal(steps, s.History.UndoList.Count);
        Assert.Equal([r], s.Selection.Items);

        s.Selection.Set(r);
        s.SetTool("vector");
        CanvasClick(w, 100, 100);
        tool.SetAnchorPosition(new Vec2(60, 60));
        Flush();
        Click(Find(Bar(w), "Done")!);
        Assert.IsType<PathObject>(Assert.Single(s.Document.ActiveLayer.Children));
        Assert.Equal(steps + 1, s.History.UndoList.Count);
        Assert.IsType<SelectTool>(s.ActiveTool);
        w.Close();
    }

    [AvaloniaFact]
    public void Gradient_and_feather_and_bevel_and_shadow_and_outline_bars_cancel_and_finish()
    {
        var w = Open();
        var s = w.Session;
        var r = AddRect(w, 100, 100);
        int steps = s.History.UndoList.Count;
        var before = r.Style;
        foreach (var (name, open) in Modes().Where(m => m.Name is "Gradient Blend" or "Feather" or "Easy Bevel" or "Shadow" or "Outline" or "Glow"))
        {
            open(w, r, null!);
            Flush();
            Click(Find(Bar(w), "Cancel")!);
            Flush();
            Assert.IsType<SelectTool>(s.ActiveTool);
            Assert.Equal(steps, s.History.UndoList.Count);
            Assert.Equal(before, r.Style);
            Assert.Equal([r], s.Document.ActiveLayer.Children);
            Assert.Equal([r], s.Selection.Items);
            Assert.Empty(s.History.RedoList);
            _ = name;
        }
        w.Close();
    }

    // ---------------------------------------------------------------- pictures for the notes

    /// <summary>Saves the Context Bar of three modes, light and dark, when DSM_SCREENSHOTS names a folder (docs only).</summary>
    [AvaloniaFact]
    public async Task Screenshots_of_the_bars_for_notes()
    {
        var dir = Environment.GetEnvironmentVariable("DSM_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        await WithTheme(() =>
        {
            Directory.CreateDirectory(dir);
            foreach (bool dark in new[] { false, true })
            {
                AppTheme.SetDarkMenus(dark, remember: false);
                foreach (var (file, open) in new (string, Action<MainWindow, RectangleObject, TextObject>)[]
                {
                    ("softshadow", Modes().First(m => m.Name == "Soft Shadow").Open),
                    ("vectordraw", Modes().First(m => m.Name == "Vector Draw").Open),
                    ("textpath", Modes().First(m => m.Name == "Text on Path").Open),
                })
                {
                    var w = Open(1700);
                    var r = AddRect(w, 100, 100);
                    var t = AddText(w);
                    open(w, r, t);
                    Flush();
                    SaveBar(w, Path.Combine(dir, $"done-cancel-{file}-{(dark ? "dark" : "light")}.png"));
                    w.Close();
                }
            }
            return Task.CompletedTask;
        });
    }

    /// <summary>The Main Toolbar row and the Context Bar of the window, as a picture.</summary>
    private static void SaveBar(MainWindow w, string path)
    {
        Flush();
        using var ms = new MemoryStream();
#pragma warning disable CS0618 // the replacement overload's options type is not needed for a plain PNG
        w.CaptureRenderedFrame()!.Save(ms);
#pragma warning restore CS0618
        ms.Position = 0;
        using var full = SKBitmap.Decode(ms);
        var host = w.FindControl<ContentControl>("ContextBarHost")!;
        var top = host.TranslatePoint(default, w)!.Value;
        double scale = w.RenderScaling;
        int y0 = Math.Max(0, (int)((top.Y - 36) * scale)), y1 = Math.Min(full.Height, (int)((top.Y + host.Bounds.Height + 8) * scale));
        using var part = new SKBitmap();
        Assert.True(full.ExtractSubset(part, new SKRectI(0, y0, full.Width, y1)));
        using var image = SKImage.FromBitmap(part);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }
}
