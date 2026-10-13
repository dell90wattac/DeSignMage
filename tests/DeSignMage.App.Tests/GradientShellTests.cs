using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using DeSignMage.App.Canvas;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Effects ▸ Gradient Blend in the real window: the command, the Context Bar, the control line on the canvas, the Color Mixer, presets.</summary>
public class GradientShellTests
{
    private static string _presetFile = "";

    private static (MainWindow W, DesignCanvas C, RectangleObject R) Open()
    {
        _presetFile = Path.Combine(Path.GetTempPath(), "dsm-gradient-presets-" + Guid.NewGuid().ToString("N") + ".json");
        GradientPresets.Default = new GradientPresets(_presetFile);

        var w = new MainWindow(new StartupOptions()) { Width = 1400, Height = 860 };
        w.Show();
        w.ToggleDockPanel("colorMixer");
        Flush();
        w.CaptureRenderedFrame();
        var canvas = w.FindControl<DesignCanvas>("Surface")!;

        w.Session.Viewport.Set(1, Vec2.Zero);                                              // one screen pixel per point: page point = screen point
        var r = new RectangleObject(200, 100) { Transform = Affine2.Translation(100, 100), Style = ObjectStyle.FillOnly(new DocColor(240, 190, 20, 255, "Golden Yellow", "Oracal 651")) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, r));
        w.Session.Selection.Set(r);
        Flush();
        w.CaptureRenderedFrame();
        return (w, canvas, r);
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    /// <summary>A page point as the window's mouse sees it.</summary>
    private static Point P(MainWindow w, DesignCanvas c, double x, double y)
    {
        var s = w.Session.Viewport.ToScreen(new Vec2(x, y));
        return c.TranslatePoint(new Point(s.X, s.Y), w).GetValueOrDefault();
    }

    private static void Click(MainWindow w, Point p, RawInputModifiers m = RawInputModifiers.None)
    {
        w.MouseDown(p, MouseButton.Left, m);
        w.MouseUp(p, MouseButton.Left, m);
        Flush();
    }

    private static void Drag(MainWindow w, Point from, Point to)
    {
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2));
        w.MouseMove(to);
        w.MouseUp(to, MouseButton.Left);
        Flush();
    }

    private static GradientContextBar Bar(MainWindow w) => Assert.IsType<GradientContextBar>(w.FindControl<ContentControl>("ContextBarHost")!.Content);

    private static AppCommand EffectsCommand(MainWindow w, string text)
    {
        var effects = w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().First(m => ((string)m.Header!).Replace("_", "") == "Effects");
        var item = effects.Items.OfType<MenuItem>().First(m => m.Header is string h && h.Replace("_", "") == text);
        return (AppCommand)item.Tag!;
    }

    private static GradientPaint G(DocObject o) => Assert.IsType<GradientPaint>(o.Style.Fill);

    // ---- the command ----

    [AvaloniaFact]
    public void The_effects_menu_has_gradient_blend_and_it_needs_one_shape_selected()
    {
        var (w, _, r) = Open();
        var command = EffectsCommand(w, "Gradient Blend...");
        Assert.Null(command.Gesture);                                                       // no shortcut
        Assert.True(command.CanExecute());

        w.Session.Selection.Clear();
        Assert.False(command.CanExecute());
        w.Session.Selection.Set(r);
        command.Execute();
        Flush();

        Assert.IsType<GradientTool>(w.Session.ActiveTool);
        Assert.True(command.IsChecked!());                                                  // the menu item shows the tool is on
        Assert.IsType<GradientContextBar>(w.FindControl<ContentControl>("ContextBarHost")!.Content);
        w.Close();
    }

    // ---- the Context Bar ----

    [AvaloniaFact]
    public void The_bar_shows_only_what_works_and_picking_a_type_makes_the_gradient_in_one_undo_step()
    {
        var (w, _, r) = Open();
        w.Session.BeginGradient();
        Flush();
        var bar = Bar(w);

        Assert.True(bar.TypeCombo.IsEffectivelyVisible);
        Assert.Equal(0, bar.TypeCombo.SelectedIndex);                                       // Flat: a plain fill
        Assert.False(bar.Alternate.IsEffectivelyVisible);                                              // nothing to alternate or reflect yet
        Assert.False(bar.Reflect.IsEffectivelyVisible);
        Assert.False(bar.RemoveButton.IsEffectivelyVisible);
        Assert.False(bar.AddPreset.IsEffectivelyVisible);
        Assert.False(bar.UpdatePreset.IsEffectivelyVisible);
        Assert.True(bar.DoneButton.IsEffectivelyVisible);

        int steps = w.Session.History.UndoList.Count;
        bar.TypeCombo.SelectedIndex = 1;                                                     // Linear
        Flush();
        Assert.Equal(GradientKind.Linear, G(r).Kind);
        Assert.Equal(steps + 1, w.Session.History.UndoList.Count);
        Assert.True(bar.Alternate.IsEffectivelyVisible && bar.Reflect.IsEffectivelyVisible && bar.RemoveButton.IsEffectivelyVisible && bar.AddPreset.IsEffectivelyVisible);
        Assert.Contains("1 of 2", bar.NodeText.Text);

        bar.TypeCombo.SelectedIndex = 5;                                                     // Ellipse
        Flush();
        Assert.Equal(GradientKind.Ellipse, G(r).Kind);

        bar.Alternate.IsChecked = true;
        bar.Reflect.IsChecked = true;
        Flush();
        Assert.True(G(r).AlternateColors && G(r).Reflect);

        bar.RemoveButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Flush();
        Assert.IsType<SolidPaint>(r.Style.Fill);
        Assert.False(bar.Alternate.IsEffectivelyVisible);                                              // and the bar follows
        w.Close();
    }

    [AvaloniaFact]
    public void With_nothing_suitable_selected_the_bar_offers_nothing_but_done()
    {
        var (w, _, _) = Open();
        w.Session.BeginGradient();
        w.Session.Selection.Clear();
        Flush();
        var bar = Bar(w);
        Assert.False(bar.TypeCombo.IsEffectivelyVisible);
        Assert.False(bar.Alternate.IsEffectivelyVisible);
        Assert.False(bar.RemoveButton.IsEffectivelyVisible);
        Assert.True(bar.DoneButton.IsEffectivelyVisible);
        w.Close();
    }

    [AvaloniaFact]
    public void Done_and_escape_leave_the_tool()
    {
        var (w, _, _) = Open();
        w.Session.BeginGradient();
        Flush();
        Bar(w).DoneButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Flush();
        Assert.IsType<SelectTool>(w.Session.ActiveTool);

        w.Session.BeginGradient();
        w.Focus();
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.IsType<SelectTool>(w.Session.ActiveTool);
        w.Close();
    }

    // ---- the canvas ----

    [AvaloniaFact]
    public void Dragging_a_handle_with_the_mouse_moves_the_gradient_in_one_undo_step()
    {
        var (w, c, r) = Open();
        w.Session.BeginGradient();
        w.Session.GetTool<GradientTool>().SetKind(GradientKind.Linear);
        Flush();
        w.CaptureRenderedFrame();
        int steps = w.Session.History.UndoList.Count;

        Drag(w, P(w, c, 300, 200), P(w, c, 250, 150));                                       // the end handle

        Assert.Equal(new Vec2(0.75, 0.5), G(r).Points[1]);
        Assert.Equal(steps + 1, w.Session.History.UndoList.Count);
        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Equal(new Vec2(1, 1), G(r).Points[1]);
        w.Close();
    }

    [AvaloniaFact]
    public void Ctrl_click_adds_a_color_alt_click_a_highlight_shift_click_removes_and_tab_cycles()
    {
        var (w, c, r) = Open();
        w.Session.BeginGradient();
        w.Session.GetTool<GradientTool>().SetKind(GradientKind.Linear);
        Flush();
        w.CaptureRenderedFrame();
        var tool = w.Session.GetTool<GradientTool>();

        Click(w, P(w, c, 200, 150), RawInputModifiers.Control);
        Assert.Equal(3, G(r).Stops.Count);
        Assert.Equal(1, tool.CurrentNode);
        Assert.Contains("2 of 3", Bar(w).NodeText.Text);

        w.Focus();
        w.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Assert.Equal(2, tool.CurrentNode);

        Click(w, P(w, c, 200, 150), RawInputModifiers.Shift);                                // the middle color goes
        Assert.Equal(2, G(r).Stops.Count);

        Click(w, P(w, c, 200, 150), RawInputModifiers.Alt);
        Assert.Equal(5, G(r).Stops.Count);
        Assert.Single(G(r).Stops, s => s.Highlight);
        w.Close();
    }

    // ---- colors ----

    [AvaloniaFact]
    public void The_color_mixer_edits_the_picked_color_of_the_gradient_not_the_whole_fill()
    {
        var (w, c, r) = Open();
        w.Session.BeginGradient();
        w.Session.GetTool<GradientTool>().SetKind(GradientKind.Linear);
        Flush();
        var tool = w.Session.GetTool<GradientTool>();
        tool.SelectNode(1);
        Flush();
        var first = G(r).Stops[0].Color;
        int steps = w.Session.History.UndoList.Count;

        var green = w.ColorMixer.Rows[1];
        green.Box.Focus();
        green.Box.Text = "222";
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Flush();

        Assert.IsType<GradientPaint>(r.Style.Fill);
        Assert.Equal(first, G(r).Stops[0].Color);                                            // the other end is untouched
        Assert.Equal(222, G(r).Stops[1].Color.G);
        Assert.Equal(steps + 1, w.Session.History.UndoList.Count);
        Assert.Equal(222, w.ColorMixer.Rows[1].Value);                                       // the mixer shows the picked color

        tool.SelectNode(0);
        Flush();
        Assert.Equal(first.G, w.ColorMixer.Rows[1].Value);                                   // pick the other one: the mixer follows
        w.Close();
    }

    [AvaloniaFact]
    public void A_slider_drag_on_a_gradient_color_is_one_undo_step()
    {
        var (w, _, r) = Open();
        w.Session.BeginGradient();
        w.Session.GetTool<GradientTool>().SetKind(GradientKind.Linear);
        w.Session.GetTool<GradientTool>().SelectNode(1);
        Flush();
        int steps = w.Session.History.UndoList.Count;

        var slider = w.ColorMixer.Rows[0].Slider;
        w.MouseDown(slider.TranslatePoint(new Point(slider.Bounds.Width * 0.2, slider.Bounds.Height / 2), w).GetValueOrDefault(), MouseButton.Left);
        for (int i = 3; i <= 9; i++)
            w.MouseMove(slider.TranslatePoint(new Point(slider.Bounds.Width * i / 10, slider.Bounds.Height / 2), w).GetValueOrDefault());
        w.MouseUp(slider.TranslatePoint(new Point(slider.Bounds.Width * 0.9, slider.Bounds.Height / 2), w).GetValueOrDefault(), MouseButton.Left);
        Flush();

        Assert.Equal(steps + 1, w.Session.History.UndoList.Count);
        Assert.True(G(r).Stops[1].Color.R > 150);
        w.Close();
    }

    [AvaloniaFact]
    public void The_fill_swatch_of_the_project_color_bar_shows_the_gradient()
    {
        var (w, _, r) = Open();
        w.Session.BeginGradient();
        w.Session.GetTool<GradientTool>().SetKind(GradientKind.Circle);
        Flush();
        var swatch = (Control)w.ProjectColorBar.FillButton.Content!;
        Assert.Equal(G(r), swatch.Tag);
        Assert.Contains(w.Session.JobPalette.Colors, k => k.Name == "Golden Yellow");       // the gradient's colors count as colors in use
        w.Close();
    }

    // ---- saved gradients ----

    [AvaloniaFact]
    public void Saved_gradients_can_be_added_applied_updated_and_deleted_and_survive_a_restart()
    {
        var (w, _, r) = Open();
        w.Session.BeginGradient();
        var tool = w.Session.GetTool<GradientTool>();
        tool.SetKind(GradientKind.Vertical);
        Flush();
        var bar = Bar(w);
        Assert.False(bar.PresetCombo.IsEffectivelyVisible);                                             // nothing saved yet

        bar.AddPreset.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Flush();
        var saved = G(r);
        Assert.Single(GradientPresets.Default.Items);
        Assert.True(File.Exists(_presetFile));
        Assert.True(bar.PresetCombo.IsEffectivelyVisible);
        Assert.Equal(0, bar.PresetCombo.SelectedIndex);
        Assert.True(bar.DeletePreset.IsEffectivelyVisible && bar.UpdatePreset.IsEffectivelyVisible);

        // another shape takes it from the list
        var other = new RectangleObject(50, 300) { Transform = Affine2.Translation(600, 100), Style = ObjectStyle.FillOnly(DocColor.White) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, other));
        w.Session.Selection.Set(other);
        Flush();
        bar.PresetCombo.SelectedIndex = -1;
        Flush();
        bar.PresetCombo.SelectedIndex = 0;
        Flush();
        Assert.Equal(saved, other.Style.Fill);

        // a new program run reads the same file
        Assert.Equal(saved, new GradientPresets(_presetFile).Items.Single());

        bar.DeletePreset.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Flush();
        Assert.Empty(GradientPresets.Default.Items);
        Assert.Empty(new GradientPresets(_presetFile).Items);
        Assert.False(bar.PresetCombo.IsEffectivelyVisible);
        w.Close();
    }
}
