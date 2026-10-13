using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DeSignMage.App.Canvas;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using Xunit;
using Avalonia.VisualTree;

namespace DeSignMage.App.Tests;

/// <summary>Effects ▸ Outline, Inline, Knife, Weld, Common Weld, Cookie Cutter in the real window.</summary>
public class EffectShellTests
{
    private static MainWindow Open()
    {
        var w = new MainWindow(new StartupOptions()) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        w.FindControl<DesignCanvas>("Surface")!.Focus();
        return w;
    }

    private static RectangleObject AddRect(MainWindow w, double x, double y, double size, string color = "#F2B705")
    {
        var r = new RectangleObject(size, size) { Transform = Affine2.Translation(x, y), Style = ObjectStyle.FillOnly(DocColor.FromHex(color)) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, r));
        return r;
    }

    private static EllipseObject AddCircle(MainWindow w, double cx, double cy, double radius, string color)
    {
        var c = new EllipseObject(radius * 2, radius * 2)
        {
            Transform = Affine2.Translation(cx - radius, cy - radius),
            Style = ObjectStyle.FillOnly(DocColor.FromHex(color)),
        };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, c));
        return c;
    }

    private static MenuItem EffectsMenu(MainWindow w) =>
        w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => ((string)m.Header!).Replace("_", "") == "Effects");

    /// <summary>Opens the Effects menu the way the user does (its items re-read their enabled state) and returns item → enabled.</summary>
    private static Dictionary<string, bool> EffectsState(MainWindow w)
    {
        var menu = EffectsMenu(w);
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
        return menu.Items.OfType<MenuItem>().ToDictionary(i => ((string)i.Header!).Replace("_", ""), i => i.IsEnabled);
    }

    private static MenuItem Item(MainWindow w, string text) =>
        EffectsMenu(w).Items.OfType<MenuItem>().Single(i => ((string)i.Header!).Replace("_", "") == text);

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static async Task Settle(MainWindow w)
    {
        await w.Session.EffectIdle;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void The_effects_menu_lists_the_commands_and_enables_them_by_selection()
    {
        var w = Open();
        var empty = EffectsState(w);
        Assert.Equal(["Outline...", "Inline...", "Knife...", "Weld", "Common Weld", "Cookie Cutter...", "Shadow...", "Extruded Shadow...", "Perspective Shadow...", "Cast Shadow..."], empty.Keys.Take(10));   // other effects (Gradient Blend...) follow
        Assert.All(empty.Values, enabled => Assert.False(enabled));          // an empty drawing: nothing to do

        var a = AddRect(w, 200, 200, 100);
        var b = AddRect(w, 250, 250, 100, "#2255CC");
        w.Session.Selection.Clear();
        var none = EffectsState(w);
        Assert.True(none["Knife..."]);                                         // something to cut
        Assert.False(none["Outline..."]);                                      // nothing selected
        Assert.False(none["Weld"]);
        Assert.False(none["Shadow..."]);                                       // a shadow needs something selected

        w.Session.Selection.Set(a);
        var one = EffectsState(w);
        Assert.True(one["Outline..."] && one["Inline..."] && one["Cookie Cutter..."] && one["Knife..."]);
        Assert.False(one["Weld"]);                                             // Weld needs two closed shapes
        Assert.False(one["Common Weld"]);
        Assert.True(one["Shadow..."] && one["Extruded Shadow..."] && one["Perspective Shadow..."] && one["Cast Shadow..."]);

        w.Session.Selection.Set([a, b]);
        var two = EffectsState(w);
        Assert.True(two["Weld"] && two["Common Weld"] && two["Outline..."]);

        var line = new PathObject(new PathBuilder().MoveTo(0, 0).LineTo(10, 10).Build());
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, line));
        w.Session.Selection.Set(line);
        var open = EffectsState(w);
        Assert.True(open["Outline..."]);                                       // an open path can be outlined (a parallel copy)
        Assert.False(open["Cookie Cutter..."]);                                // but it is no cutter: no area
        Assert.False(open["Weld"]);
        Assert.False(open["Shadow..."]);                                       // a line has no area to cast a shadow from
        w.Close();
    }

    [AvaloniaFact]
    public async Task Weld_from_the_menu_makes_one_object_and_one_undo_step_brings_the_originals_back()
    {
        var w = Open();
        var a = AddRect(w, 200, 200, 100);
        var b = AddRect(w, 250, 250, 100, "#2255CC");
        w.Session.Selection.Set([a, b]);
        int steps = w.Session.History.UndoList.Count;

        Click(Item(w, "Weld"));
        await Settle(w);
        var layer = w.Session.Document.ActiveLayer;
        var weld = Assert.IsType<PathObject>(Assert.Single(layer.Children));
        Assert.Equal("Weld", weld.Name);
        Assert.Equal(steps + 1, w.Session.History.UndoList.Count);
        Assert.True(weld.GetWorldBounds().Width > 149);

        w.Session.Undo();
        Assert.Equal([a, b], layer.Children);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Outline_bar_flow_type_a_distance_see_the_preview_then_Done_makes_a_standalone_object()
    {
        var w = Open();
        var a = AddRect(w, 200, 200, 100);
        w.Session.Selection.Set(a);

        Click(Item(w, "Outline..."));
        Assert.Equal("outline", w.Session.ActiveTool.Id);
        var bar = Assert.IsType<OutlineContextBar>(w.FindControl<ContentControl>("ContextBarHost")!.Content);
        var fields = bar.GetVisualDescendants().OfType<TextBox>().ToList();
        var done = bar.GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Content == "✓ Done");
        Assert.False(done.IsEnabled);                                           // distance 0: nothing to make yet

        var first = fields[0];                                                  // "1st:" in inches
        first.Focus();
        first.Text = "0.1";
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        var tool = w.Session.GetTool<OutlineTool>();
        Assert.Equal(7.2, tool.Settings.First, 6);
        Assert.Equal(7.2, tool.FirstPercent, 3);                                // 7.2 pt of a 100 pt high shape
        await tool.PreviewSettled;
        Dispatcher.UIThread.RunJobs();
        Assert.Single(tool.PreviewPaths);                                       // the live preview arrived
        Assert.True(done.IsEnabled);
        Assert.Single(w.Session.Document.ActiveLayer.Children);                 // but nothing is made yet

        done.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle(w);
        var layer = w.Session.Document.ActiveLayer;
        Assert.Equal(2, layer.Children.Count);
        Assert.Equal("Outline", layer.Children[0].Name);
        Assert.IsType<PathObject>(layer.Children[0]);
        Assert.Same(a, layer.Children[1]);                                      // standalone: nothing grouped
        Assert.Equal("select", w.Session.ActiveTool.Id);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Knife_and_cookie_cutter_bars_work_and_a_live_text_outline_keeps_the_text()
    {
        var w = Open();
        var s = w.Session;
        var dough = AddRect(w, 200, 200, 200, "#2255CC");
        s.Selection.Set(dough);
        Click(Item(w, "Knife..."));
        var knifeBar = Assert.IsType<KnifeContextBar>(w.FindControl<ContentControl>("ContextBarHost")!.Content);
        var selectedOnly = knifeBar.GetVisualDescendants().OfType<CheckBox>().First(c => (string?)c.Content == "Selected Only");
        Assert.True(selectedOnly.IsEnabled && selectedOnly.IsChecked == true);   // on by default, enabled with a selection
        await s.GetTool<KnifeTool>().Cut(new Vec2(150, 300), new Vec2(450, 300));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, s.Document.ActiveLayer.Children.Count);
        Assert.StartsWith("Knife piece", s.Document.ActiveLayer.Children[0].Name);

        // Undo, then cookie cut with a circle on top.
        s.Undo();
        var cutter = AddCircle(w, 300, 300, 40, "#CC2222");
        s.Selection.Set(cutter);
        Click(Item(w, "Cookie Cutter..."));
        var cookieBar = Assert.IsType<CookieCutterContextBar>(w.FindControl<ContentControl>("ContextBarHost")!.Content);
        var outline = cookieBar.GetVisualDescendants().OfType<TextBox>().Single();
        outline.Focus();
        outline.Text = "0.1";
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(7.2, s.GetTool<CookieCutterTool>().Outline, 6);
        var apply = cookieBar.GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Content == "✓ Apply");
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle(w);
        var cut = Assert.IsType<PathObject>(s.Document.ActiveLayer.Children[0]);
        Assert.Equal("Cookie Cut", cut.Name);
        Assert.False(cut.Geometry.Contains(new Vec2(300 + 44, 300)));            // hole = radius 40 + outline 7.2
        Assert.Same(cutter, s.Document.ActiveLayer.Children[1]);
        Assert.Equal("select", s.ActiveTool.Id);

        // Outline of live text leaves the text live.
        var text = new TextObject("Sign", "Arial", 120) { Transform = Affine2.Translation(500, 500) };
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, text));
        s.Selection.Set(text);
        Click(Item(w, "Outline..."));
        s.GetTool<OutlineTool>().Update(o => o with { First = 6 });
        await s.GetTool<OutlineTool>().Apply();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(text, s.Document.ActiveLayer.Children);
        Assert.IsType<TextObject>(text);
        Assert.Equal("Sign", text.Text);
        Assert.Contains(s.Document.ActiveLayer.Children, o => o.Name == "Outline");
        w.Close();
    }

    /// <summary>Saves a screenshot when DSM_SCREENSHOTS names a folder (docs only).</summary>
    [AvaloniaFact]
    public async Task Screenshot_for_notes()
    {
        var dir = Environment.GetEnvironmentVariable("DSM_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        var w = Open();
        var s = w.Session;
        s.Viewport.ZoomToFit(new Rect2(0, 100, 1728, 560));

        // Right: three circles after Common Weld (every pairwise overlap as its own lens).
        var r1 = AddCircle(w, 880, 300, 90, "#CC2222");
        var r2 = AddCircle(w, 1000, 300, 90, "#2255CC");
        var r3 = AddCircle(w, 940, 400, 90, "#E8B400");
        s.Selection.Set([r1, r2, r3]);
        s.CommonWeld();

        // Middle: a rectangle cut by the knife, the pieces pulled apart.
        var box = AddRect(w, 1180, 220, 220, "#2A9D8F");
        s.Selection.Set(box);
        await s.GetTool<KnifeTool>().Cut(new Vec2(1150, 330), new Vec2(1450, 300));
        var pieces = s.Selection.Items.ToList();
        if (pieces.Count == 2) s.History.Execute(TransformObjectsCommand.ApplyWorld("Move", [pieces[1]], Affine2.Translation(30, 25)));

        // Left: three circles with the Outline tool open and its preview showing.
        var c1 = AddCircle(w, 220, 300, 90, "#CC2222");
        var c2 = AddCircle(w, 340, 300, 90, "#2255CC");
        var c3 = AddCircle(w, 280, 400, 90, "#E8B400");
        s.Selection.Set([c1, c2, c3]);
        s.BeginOutline(OutlineKind.Outline);
        var tool = s.GetTool<OutlineTool>();
        tool.Update(o => o with { First = Units.FromInches(0.2) });
        await tool.PreviewSettled;
        Dispatcher.UIThread.RunJobs();
#pragma warning disable CS0618 // the replacement overload's options type is not needed for a plain PNG
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "effects-step2.png"));
#pragma warning restore CS0618
        w.Close();
    }
}
