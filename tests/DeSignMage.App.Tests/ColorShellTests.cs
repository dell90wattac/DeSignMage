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
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.TestSupport;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>The color UI in the real window (C.3): Palette Strip, Color Mixer, Project Color Bar, eyedropper.</summary>
public class ColorShellTests
{
    private static (MainWindow W, DesignCanvas C) Open()
    {
        var w = new MainWindow(new StartupOptions()) { Width = 1400, Height = 860 };
        w.Show();
        w.ToggleDockPanel("colorMixer");                      // the Dock starts closed; the Color Mixer is what these tests use
        Flush();
        w.CaptureRenderedFrame();
        return (w, w.FindControl<DesignCanvas>("Surface")!);
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    private static Point Center(Control c) => c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), TopLevel.GetTopLevel(c)!).GetValueOrDefault();

    private static Point At(Control c, double x, double y) => c.TranslatePoint(new Point(x, y), TopLevel.GetTopLevel(c)!).GetValueOrDefault();

    private static void Click(MainWindow w, Point p, MouseButton b = MouseButton.Left)
    {
        w.MouseDown(p, b);
        w.MouseUp(p, b);
        Flush();
    }

    private static void Click(MainWindow w, Control c, MouseButton b = MouseButton.Left) => Click(w, Center(c), b);

    private static RectangleObject Draw(MainWindow w, double x, double y, ObjectStyle? style = null)
    {
        var r = new RectangleObject(60, 40) { Transform = Affine2.Translation(x, y), Style = style ?? new ObjectStyle(new SolidPaint(DocColor.Black), StrokeStyle.Solid(DocColor.White, 2)) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, r));
        Flush();
        return r;
    }

    private static DocColor? Fill(DocObject o) => (o.Style.Fill as SolidPaint)?.Color;
    private static DocColor? Stroke(DocObject o) => (o.Style.Stroke?.Paint as SolidPaint)?.Color;

    /// <summary>The realized swatch cells of the far-right strip (virtualized: only those in view).</summary>
    private static List<Control> StripCells(MainWindow w) =>
        w.FindControl<ContentControl>("PaletteStripHost")!.GetVisualDescendants().OfType<Grid>().Where(g => g.Tag is SolidPaint or null && g.Width == PaletteStrip.CellSize).Cast<Control>().ToList();

    private static Control CellFor(MainWindow w, string name) =>
        StripCells(w).First(c => c.Tag is SolidPaint { Color.Name: { } n } && n == name);

    // ---- Palettes and the Palette Strip ----

    [AvaloniaFact]
    public void The_designer_palette_is_active_and_fills_the_strip()
    {
        Assert.SkipUnless(LocalData.Has("assets:palettes/Designer.PAL"), "needs local-assets/palettes/Designer.PAL (see the README)");
        var (w, _) = Open();
        Assert.Equal("Designer", w.ColorState.ActivePalette.Name);
        Assert.Equal(17, w.ColorState.Palettes.Count);
        var cells = StripCells(w);
        Assert.True(cells.Count > 15);
        Assert.Equal(new DocColor(0, 0, 0, 255, "Black", "Major Print Colors"), ((SolidPaint)cells[0].Tag!).Color);   // no "no color" cell: the red X above the strip does that
        Assert.DoesNotContain(cells, c => c.Tag is null);
        w.Close();
    }

    [AvaloniaFact]
    public void Left_click_on_a_swatch_sets_the_fill_and_right_click_the_stroke_in_one_step_each()
    {
        Assert.SkipUnless(LocalData.Has("assets:palettes/Designer.PAL"), "needs local-assets/palettes/Designer.PAL (see the README)");
        var (w, _) = Open();
        var a = Draw(w, 0, 0);
        w.Session.Selection.Set(a);
        int steps = w.Session.History.UndoList.Count;
        var cell = CellFor(w, "Slate Grey BW98");
        var slate = ((SolidPaint)cell.Tag!).Color;

        Click(w, cell, MouseButton.Left);
        Assert.Equal(slate, Fill(a));
        Assert.Equal("Major Print Colors", Fill(a)!.Value.Vendor);        // the vinyl identity travels with the color
        Assert.Equal(steps + 1, w.Session.History.UndoList.Count);

        Click(w, cell, MouseButton.Right);
        Assert.Equal(slate, Stroke(a));
        Assert.Equal(2, a.Style.Stroke!.Width);                           // the stroke keeps its width
        Assert.Equal(steps + 2, w.Session.History.UndoList.Count);

        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Equal(DocColor.White, Stroke(a));
        w.Close();
    }

    [AvaloniaFact]
    public void Right_click_on_the_swatch_strip_never_zooms_the_canvas()
    {
        var (w, _) = Open();
        var view = w.Session.Viewport.State;
        Click(w, StripCells(w)[3], MouseButton.Right);
        Assert.Equal(view, w.Session.Viewport.State);
        Assert.Equal(0, w.Session.Viewport.ZoomStackDepth);
        w.Close();
    }

    [AvaloniaFact]
    public void Switching_palette_changes_the_strip_and_a_cmyk_swatch_keeps_its_model()
    {
        Assert.SkipUnless(LocalData.Has("assets:palettes/Designer.PAL"), "needs local-assets/palettes/Designer.PAL (see the README)");
        var (w, _) = Open();
        var a = Draw(w, 0, 0);
        w.Session.Selection.Set(a);
        var combo = w.ColorMixer.PaletteCombo;
        combo.SelectedIndex = w.ColorState.Palettes.ToList().FindIndex(p => p.Name.StartsWith("Roland"));
        Flush();
        Assert.StartsWith("Roland", w.ColorState.ActivePalette.Name);

        var first = StripCells(w)[0];
        Click(w, first);
        var color = Fill(a)!.Value;
        Assert.Equal(ColorModel.Cmyk, color.Model);
        Assert.Equal("RVW-PR35D", color.Name);
        Assert.Equal(new Cmyk(0, 30, 0, 0), color.Cmyk);
        w.Close();
    }

    [AvaloniaFact]
    public void Swatch_tooltip_shows_the_color_name_and_values()
    {
        Assert.SkipUnless(LocalData.Has("assets:palettes/Designer.PAL"), "needs local-assets/palettes/Designer.PAL (see the README)");
        var (w, _) = Open();
        var tip = ToolTip.GetTip(CellFor(w, "Slate Grey BW98")) as string;
        Assert.Contains("Major Print Colors Slate Grey BW98", tip);
        Assert.Contains("R 127  G 127  B 127", tip);
        w.Close();
    }

    // ---- Color Mixer ----

    [AvaloniaFact]
    public void Dragging_a_slider_updates_live_and_is_one_undo_step()
    {
        var (w, _) = Open();
        var a = Draw(w, 0, 0);
        var b = Draw(w, 100, 0);
        w.Session.Selection.Set([a, b]);
        Flush();
        int steps = w.Session.History.UndoList.Count;
        var red = w.ColorMixer.Rows[0].Slider;
        Assert.Equal(0, red.Value);

        double y = red.Bounds.Height / 2;
        var start = At(red, red.Bounds.Width * 0.2, y);
        w.MouseDown(start, MouseButton.Left);
        double seen = 0;
        for (int i = 1; i <= 10; i++)
        {
            w.MouseMove(At(red, red.Bounds.Width * (0.2 + 0.06 * i), y));
            Assert.Equal(Fill(a), Fill(b));                  // live: every member follows during the drag
            seen = Math.Max(seen, Fill(a)!.Value.R);
        }
        w.MouseUp(At(red, red.Bounds.Width * 0.8, y), MouseButton.Left);
        Flush();

        Assert.True(seen > 100, $"red should have moved a long way, saw {seen}");
        Assert.Equal(Fill(a)!.Value.R, (byte)Math.Round(red.Value));
        Assert.Equal(steps + 1, w.Session.History.UndoList.Count);   // ONE step for the whole drag

        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Equal(DocColor.Black, Fill(a));
        Assert.Equal(DocColor.Black, Fill(b));
        w.Close();
    }

    [AvaloniaFact]
    public void Typing_a_channel_value_is_one_undo_step_and_clears_the_vinyl_name()
    {
        var (w, _) = Open();
        var a = Draw(w, 0, 0, ObjectStyle.FillOnly(new DocColor(10, 20, 30, 255, "Navy", "Oracal 651")));
        w.Session.Selection.Set(a);
        Flush();
        Assert.Equal("Oracal 651 Navy", w.ColorMixer.NameBox.Text);
        int steps = w.Session.History.UndoList.Count;

        var box = w.ColorMixer.Rows[1].Box;                    // G
        box.Focus();
        box.Text = "200";
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Flush();
        Assert.Equal(new DocColor(10, 200, 30), Fill(a));       // edited by hand: no longer "Navy"
        Assert.Equal(steps + 1, w.Session.History.UndoList.Count);
        w.Close();
    }

    [AvaloniaFact]
    public void Stroke_radio_makes_the_panel_edit_the_stroke()
    {
        var (w, _) = Open();
        var a = Draw(w, 0, 0, new ObjectStyle(new SolidPaint(DocColor.Black), StrokeStyle.Solid(new DocColor(255, 0, 0), 3)));
        w.Session.Selection.Set(a);
        Flush();
        Assert.Equal(0, w.ColorMixer.Rows[0].Value);
        Click(w, w.ColorMixer.StrokeRadio);
        Assert.Equal(ColorTarget.Stroke, w.ColorState.Target);
        Assert.Equal(255, w.ColorMixer.Rows[0].Value);          // panel now shows the stroke color

        var green = w.ColorMixer.Rows[1];
        green.Box.Focus();
        green.Box.Text = "128";
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Flush();
        Assert.Equal(new DocColor(255, 128, 0), Stroke(a));
        Assert.Equal(DocColor.Black, Fill(a));
        Assert.Equal(3, a.Style.Stroke!.Width);
        w.Close();
    }

    [AvaloniaFact]
    public void Dragging_in_the_hue_ring_and_square_is_one_undo_step_each()
    {
        var (w, _) = Open();
        var a = Draw(w, 0, 0, ObjectStyle.FillOnly(new DocColor(255, 0, 0)));
        w.Session.Selection.Set(a);
        Flush();
        var wheel = w.ColorMixer.Wheel;
        int steps = w.Session.History.UndoList.Count;

        // Drag along the ring from the top (red) to the right (hue 90, yellow-green).
        var top = wheel.HueMarkerPoint;
        double r = Math.Min(wheel.Bounds.Width, wheel.Bounds.Height) / 2 - 1 - 6.5;
        var cx = wheel.Bounds.Width / 2; var cy = wheel.Bounds.Height / 2;
        w.MouseDown(At(wheel, top.X, top.Y), MouseButton.Left);
        for (int deg = 10; deg <= 90; deg += 10)
        {
            double rad = (deg - 90) * Math.PI / 180;
            w.MouseMove(At(wheel, cx + r * Math.Cos(rad), cy + r * Math.Sin(rad)));
        }
        w.MouseUp(At(wheel, cx + r, cy), MouseButton.Left);
        Flush();
        Assert.InRange(wheel.Hue, 85, 95);
        var hued = Fill(a)!.Value;
        Assert.True(hued.G > 200 && hued.R < 200 && hued.B < 10, hued.ToHex());
        Assert.Equal(steps + 1, w.Session.History.UndoList.Count);

        // Then one drag in the square: all the way to the dark left bottom corner.
        var sq = wheel.SquareRect;
        w.MouseDown(At(wheel, sq.Center.X, sq.Center.Y), MouseButton.Left);
        w.MouseMove(At(wheel, sq.X + 5, sq.Bottom - 5));
        w.MouseUp(At(wheel, sq.X + 1, sq.Bottom - 1), MouseButton.Left);
        Flush();
        Assert.Equal(steps + 2, w.Session.History.UndoList.Count);
        Assert.True(Fill(a)!.Value.G < 40);                     // nearly black

        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Equal(hued, Fill(a));
        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Equal(new DocColor(255, 0, 0), Fill(a));
        w.Close();
    }

    [AvaloniaFact]
    public void Cmyk_mode_shows_and_edits_cmyk_and_the_color_stays_cmyk()
    {
        var (w, _) = Open();
        var a = Draw(w, 0, 0, ObjectStyle.FillOnly(DocColor.FromCmyk(10, 20, 30, 40, "Mix", "Roland")));
        w.Session.Selection.Set(a);
        w.ColorMixer.ModeCombo.SelectedIndex = 1;   // CMYK
        Flush();
        Assert.Equal(ColorEditMode.Cmyk, w.ColorState.Mode);
        Assert.Equal([10, 20, 30, 40], w.ColorMixer.Rows.Select(r => r.Value).ToArray());

        var k = w.ColorMixer.Rows[3];
        k.Box.Focus();
        k.Box.Text = "0";
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Flush();
        var c = Fill(a)!.Value;
        Assert.Equal(ColorModel.Cmyk, c.Model);
        Assert.Equal(new Cmyk(10, 20, 30, 0), c.Cmyk);
        w.Close();
    }

    [AvaloniaFact]
    public void Hsv_mode_edits_rgb_colors()
    {
        var (w, _) = Open();
        var a = Draw(w, 0, 0, ObjectStyle.FillOnly(new DocColor(255, 0, 0)));
        w.Session.Selection.Set(a);
        w.ColorMixer.ModeCombo.SelectedIndex = 2;
        Flush();
        Assert.Equal([0, 100, 100], w.ColorMixer.Rows.Take(3).Select(r => r.Value).ToArray());
        w.ColorMixer.Rows[0].Box.Focus();
        w.ColorMixer.Rows[0].Box.Text = "120";
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Flush();
        Assert.Equal(new DocColor(0, 255, 0), Fill(a));
        Assert.Equal(ColorModel.Rgb, Fill(a)!.Value.Model);
        w.Close();
    }

    [AvaloniaFact]
    public void Picking_a_name_in_the_search_box_applies_that_palette_color()
    {
        Assert.SkipUnless(LocalData.Has("assets:palettes/Designer.PAL"), "needs local-assets/palettes/Designer.PAL (see the README)");
        var (w, _) = Open();
        var a = Draw(w, 0, 0);
        w.Session.Selection.Set(a);
        Flush();
        w.ColorMixer.NameBox.SelectedItem = "Major Print Colors Light Grey BW104";
        Flush();
        Assert.Equal("Light Grey BW104", Fill(a)!.Value.Name);
        Assert.Equal(new DocColor(229, 229, 229, 255, "Light Grey BW104", "Major Print Colors"), Fill(a));
        w.Close();
    }

    [AvaloniaFact]
    public void Adjustment_controls_off_shows_the_palette_as_a_list()
    {
        var (w, _) = Open();
        var a = Draw(w, 0, 0);
        w.Session.Selection.Set(a);
        Flush();
        Assert.False(w.ColorMixer.List.IsVisible);
        w.ColorMixer.Adjustment.IsChecked = false;
        Flush();
        Assert.True(w.ColorMixer.List.IsVisible);
        w.ColorMixer.List.SelectedItem = w.ColorState.ActivePalette.Colors[2];
        Flush();
        Assert.Equal(w.ColorState.ActivePalette.Colors[2], Fill(a));
        Assert.Null(w.ColorMixer.List.SelectedItem);
        w.Close();
    }

    [AvaloniaFact]
    public void With_nothing_selected_the_panel_sets_the_default_for_new_objects_without_undo()
    {
        Assert.SkipUnless(LocalData.Has("assets:palettes/Designer.PAL"), "needs local-assets/palettes/Designer.PAL (see the README)");
        var (w, c) = Open();
        w.Session.Selection.Clear();
        Flush();
        int steps = w.Session.History.UndoList.Count;
        Click(w, CellFor(w, "Slate Grey BW98"));
        Assert.Equal(steps, w.Session.History.UndoList.Count);

        var green = w.ColorMixer.Rows[1];
        green.Box.Focus();
        green.Box.Text = "99";
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Flush();
        Assert.Equal(new DocColor(127, 99, 127), (w.Session.DefaultStyle.Fill as SolidPaint)!.Color);
        Assert.Equal(steps, w.Session.History.UndoList.Count);

        w.Session.SetTool("rectangle");
        var tl = c.TranslatePoint(new Point(200, 200), w).GetValueOrDefault();
        w.MouseDown(tl, MouseButton.Left);
        w.MouseMove(tl + new Point(60, 40));
        w.MouseUp(tl + new Point(120, 80), MouseButton.Left);
        var r = Assert.IsType<RectangleObject>(w.Session.Document.ActiveLayer.Children[^1]);
        Assert.Equal(new DocColor(127, 99, 127), Fill(r));
        w.Close();
    }

    // ---- Eyedropper ----

    [AvaloniaFact]
    public void Eyedropper_takes_the_color_of_the_clicked_object_and_applies_it_to_the_selection()
    {
        var (w, c) = Open();
        var target = Draw(w, 0, 0);
        var donor = Draw(w, 200, 200, ObjectStyle.FillOnly(new DocColor(12, 99, 200, 255, "Sky", "3M")));
        w.Session.Selection.Set(target);
        Flush();
        w.Session.Viewport.Set(1, Vec2.Zero);
        Flush();
        w.CaptureRenderedFrame();

        Click(w, w.ColorMixer.Eyedropper);
        Assert.IsType<DeSignMage.Core.Editing.Tools.EyedropperTool>(w.Session.ActiveTool);

        var at = c.TranslatePoint(new Point(230, 220), w).GetValueOrDefault();
        Click(w, at);
        Assert.Equal(new DocColor(12, 99, 200, 255, "Sky", "3M"), Fill(target));
        Assert.IsType<DeSignMage.Core.Editing.Tools.SelectTool>(w.Session.ActiveTool);
        Assert.Equal(donor.Style, donor.Style);
        w.Close();
    }

    // ---- Project Color Bar ----

    [AvaloniaFact]
    public void The_bottom_bar_shows_the_selection_colors_and_none_as_the_crossed_box()
    {
        var (w, _) = Open();
        var a = Draw(w, 0, 0, new ObjectStyle(new SolidPaint(new DocColor(1, 2, 3)), null));
        w.Session.Selection.Set(a);
        Flush();
        var fill = (Control)w.ProjectColorBar.FillButton.Content!;
        var stroke = (Control)w.ProjectColorBar.StrokeButton.Content!;
        Assert.Equal(new SolidPaint(new DocColor(1, 2, 3)), fill.Tag);
        Assert.Null(stroke.Tag);                                  // none
        Assert.Single(stroke.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>(), p => Equals(p.Stroke, Avalonia.Media.Brushes.Red));   // the red cross
        w.Close();
    }

    /// <summary>Two filled, stroked rectangles grouped together; returns the group and its members.</summary>
    private static (GroupObject Group, RectangleObject A, RectangleObject B) DrawGroup(MainWindow w)
    {
        var a = Draw(w, 0, 0);
        var b = Draw(w, 100, 0);
        w.Session.Selection.Set(a);
        w.Session.GroupObjects([a, b]);
        Flush();
        var group = Assert.IsType<GroupObject>(a.Parent);
        w.Session.Selection.Set(group);
        Flush();
        return (group, a, b);
    }

    [AvaloniaFact]
    public void The_bottom_bar_No_Fill_button_removes_the_fill_in_one_click_without_switching_modes_and_undo_restores_it()
    {
        var (w, _) = Open();
        var (group, a, b) = DrawGroup(w);
        Click(w, w.ProjectColorBar.StrokeButton);                    // the target is Stroke: No Fill must not care
        Assert.Equal(ColorTarget.Stroke, w.ColorState.Target);
        Click(w, w.ProjectColorBar.NoFill);
        Assert.Null(a.Style.Fill);                            // recurses into the group
        Assert.Null(b.Style.Fill);
        Assert.NotNull(a.Style.Stroke);                       // the outline stays
        Assert.Equal(group, w.Session.Selection.Items[0]);
        Assert.Equal("No Fill", w.Session.History.UndoDescription);
        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");   // ONE undo step restores both members
        Assert.Equal(DocColor.Black, Fill(a));
        Assert.Equal(DocColor.Black, Fill(b));
        w.Close();
    }

    [AvaloniaFact]
    public void The_bottom_bar_No_Stroke_button_removes_the_stroke_in_one_click_and_undo_restores_it()
    {
        var (w, _) = Open();
        var (_, a, b) = DrawGroup(w);
        Click(w, w.ProjectColorBar.FillButton);                      // the target is Fill: No Stroke must not care
        Click(w, w.ProjectColorBar.NoStroke);
        Assert.Null(a.Style.Stroke);
        Assert.Null(b.Style.Stroke);
        Assert.NotNull(a.Style.Fill);
        Assert.Equal("No Stroke", w.Session.History.UndoDescription);
        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Equal(DocColor.White, Stroke(a));
        Assert.Equal(DocColor.White, Stroke(b));
        w.Close();
    }

    [AvaloniaFact]
    public void The_Color_panel_has_No_Fill_and_No_Stroke_buttons_that_work_the_same()
    {
        var (w, _) = Open();
        Flush();
        w.CaptureRenderedFrame();
        var (_, a, b) = DrawGroup(w);
        Assert.True(w.ColorMixer.NoFill.IsVisible);
        Click(w, w.ColorMixer.StrokeRadio);
        Click(w, w.ColorMixer.NoFill);                        // independent of the Fill/Stroke radio
        Assert.Null(a.Style.Fill);
        Assert.Null(b.Style.Fill);
        Click(w, w.ColorMixer.NoStroke);
        Assert.Null(a.Style.Stroke);
        Assert.Null(b.Style.Stroke);
        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.NotNull(a.Style.Stroke);
        Assert.Null(a.Style.Fill);                            // only the stroke step was undone
        w.Close();
    }

    [AvaloniaFact]
    public void No_Fill_and_No_Stroke_buttons_have_tooltips_and_sit_beside_their_swatches()
    {
        var (w, _) = Open();
        foreach (var b in new[] { w.ProjectColorBar.NoFill, w.ProjectColorBar.NoStroke, w.ColorMixer.NoFill, w.ColorMixer.NoStroke })
            Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(b) as string));
        Assert.Equal("No Fill", ((TextBlock)w.ProjectColorBar.NoFill.Content!).Text);
        Assert.Equal("No Stroke", ((TextBlock)w.ProjectColorBar.NoStroke.Content!).Text);
        var fillGroup = (Panel)w.ProjectColorBar.FillButton.Parent!;
        var strokeGroup = (Panel)w.ProjectColorBar.StrokeButton.Parent!;
        Assert.Equal(fillGroup.Children.IndexOf(w.ProjectColorBar.FillButton) + 1, fillGroup.Children.IndexOf(w.ProjectColorBar.NoFill));
        Assert.Equal(strokeGroup.Children.IndexOf(w.ProjectColorBar.StrokeButton) + 1, strokeGroup.Children.IndexOf(w.ProjectColorBar.NoStroke));
        w.Close();
    }

    [AvaloniaFact]
    public void No_Fill_with_nothing_selected_changes_the_default_style_for_new_shapes()
    {
        var (w, _) = Open();
        w.Session.Selection.Clear();
        w.Session.DefaultStyle = new ObjectStyle(new SolidPaint(DocColor.Black), StrokeStyle.Solid(DocColor.White, 2));
        Flush();
        var strokeBefore = w.Session.DefaultStyle.Stroke;
        Click(w, w.ProjectColorBar.NoFill);
        Assert.Null(w.Session.DefaultStyle.Fill);
        Assert.Equal(strokeBefore, w.Session.DefaultStyle.Stroke);   // No Fill leaves the outline setting alone
        w.Close();
    }

    [AvaloniaFact]
    public void The_Project_Color_Bar_sets_join_and_cap()
    {
        var (w, _) = Open();
        var a = Draw(w, 0, 0);
        w.Session.Selection.Set(a);
        Flush();

        Click(w, w.ProjectColorBar.JoinButtons[1]);
        Click(w, w.ProjectColorBar.CapButtons[2]);
        Assert.Equal(LineJoin.Round, a.Style.Stroke!.Join);
        Assert.Equal(LineCap.Square, a.Style.Stroke!.Cap);
        Assert.Equal(LineJoin.Round, w.Session.CurrentStyle.Stroke!.Join);
        w.Close();
    }

    [AvaloniaFact]
    public void Panel_join_and_cap_buttons_show_the_current_state()
    {
        var (w, _) = Open();
        var a = Draw(w, 0, 0, new ObjectStyle(null, new StrokeStyle(new SolidPaint(DocColor.Black), 4, LineJoin.Bevel, LineCap.Round)));
        w.Session.Selection.Set(a);
        Flush();
        Assert.Contains("on", w.ColorMixer.JoinButtons[2].Classes);
        Assert.Contains("on", w.ColorMixer.CapButtons[1].Classes);
        Assert.DoesNotContain("on", w.ColorMixer.JoinButtons[0].Classes);

        w.Close();
    }

    // ---- Groups, combined objects, live text ----

    [AvaloniaFact]
    public void A_swatch_click_recolors_a_whole_group_a_combined_object_and_live_text()
    {
        Assert.SkipUnless(LocalData.Has("assets:palettes/Designer.PAL"), "needs local-assets/palettes/Designer.PAL (see the README)");
        var (w, _) = Open();
        var a = Draw(w, 0, 0); var b = Draw(w, 100, 0);
        var group = w.Session.GroupObjects([a, b])!;
        var t = new TextObject("Hi", "Arial", 20);
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, t));
        var p = Draw(w, 0, 300); var q = Draw(w, 30, 300);
        w.Session.Selection.Set([p, q]);
        var combined = w.Session.CombineSelection()!;
        Flush();

        var cell = CellFor(w, "Slate Grey BW98");
        var slate = ((SolidPaint)cell.Tag!).Color;
        w.Session.Selection.Set([group, t, combined]);
        Flush();
        Click(w, cell);

        Assert.Equal(slate, Fill(a)); Assert.Equal(slate, Fill(b));
        Assert.Equal(slate, Fill(t));
        Assert.All(combined.Parts, part => Assert.Equal(slate, Fill(part)));
        Assert.Equal(1, w.Session.History.UndoList.Count(c => c.Description == "Fill Color"));   // all in one step
        w.Close();
    }

    // ---- Job color palette (bottom-left of the bar, requirement R9) ----

    private static List<DocColor> JobColorsShown(MainWindow w) =>
        w.ProjectColorBar.JobCells.Children.OfType<Grid>().Select(g => ((SolidPaint)g.Tag!).Color).ToList();

    [AvaloniaFact]
    public void The_job_palette_lists_the_colors_in_use_and_follows_add_recolor_and_delete()
    {
        Assert.SkipUnless(LocalData.Has("assets:palettes/Designer.PAL"), "needs local-assets/palettes/Designer.PAL (see the README)");
        var (w, _) = Open();
        Flush();
        Assert.Empty(JobColorsShown(w));

        var gold = new DocColor(242, 183, 5, 255, "Golden Yellow", "Oracal 651");
        var a = Draw(w, 0, 0, new ObjectStyle(new SolidPaint(gold), StrokeStyle.Solid(DocColor.White, 2)));
        Assert.Equal([gold, DocColor.White], JobColorsShown(w));

        var b = Draw(w, 100, 0, ObjectStyle.FillOnly(new DocColor(1, 2, 3)));
        Assert.Equal([gold, DocColor.White, new DocColor(1, 2, 3)], JobColorsShown(w));

        w.Session.Selection.Set(b);
        Click(w, CellFor(w, "Slate Grey BW98"));                                      // recolor: (1,2,3) stops being used
        Flush();
        Assert.DoesNotContain(new DocColor(1, 2, 3), JobColorsShown(w));
        Assert.Contains(JobColorsShown(w), c => c.Name == "Slate Grey BW98");

        w.Session.Selection.Set(a);
        w.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Flush();
        Assert.Equal(["Slate Grey BW98"], JobColorsShown(w).Select(c => c.Name!).ToArray());

        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Flush();
        Assert.Contains(gold, JobColorsShown(w));
        w.Close();
    }

    [AvaloniaFact]
    public void Clicking_a_job_color_applies_it_like_a_swatch_click_and_the_bar_has_no_palette_selector()
    {
        var (w, _) = Open();
        var navy = new DocColor(10, 20, 80, 255, "Navy", "Oracal 651");
        Draw(w, 200, 0, ObjectStyle.FillOnly(navy));
        var target = Draw(w, 0, 0);
        w.Session.Selection.Set(target);
        Flush();
        var cell = w.ProjectColorBar.JobCells.Children.OfType<Grid>().First(g => g.Tag is SolidPaint { Color.Name: "Navy" });

        int steps = w.Session.History.UndoList.Count;
        Click(w, cell, MouseButton.Left);
        Assert.Equal(navy, Fill(target));
        cell = w.ProjectColorBar.JobCells.Children.OfType<Grid>().First(g => g.Tag is SolidPaint { Color.Name: "Navy" });   // the row was rebuilt
        Click(w, cell, MouseButton.Right);
        Assert.Equal(navy, Stroke(target));
        Assert.Equal(steps + 2, w.Session.History.UndoList.Count);

        Assert.Null(w.FindControl<ComboBox>("BarPaletteCombo"));                      // palette choice lives in the Color Mixer
        Assert.NotNull(w.ColorMixer.PaletteCombo);
        w.Close();
    }

    [AvaloniaFact]
    public void View_menu_toggles_the_job_palette()
    {
        var (w, _) = Open();
        Draw(w, 0, 0);
        Assert.True(w.ProjectColorBar.JobPalette.IsVisible);
        var view = w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => ((string)m.Header!).Replace("_", "") == "View");
        var item = view.Items.OfType<MenuItem>().Single(m => ((string)m.Header!).Replace("_", "") == "Show Job Color Palette");
        var command = (AppCommand)item.Tag!;
        Assert.Null(command.Gesture);                                                  // no shortcut

        command.Execute();
        Flush();
        Assert.False(w.ProjectColorBar.JobPalette.IsVisible);
        command.Execute();
        Flush();
        Assert.True(w.ProjectColorBar.JobPalette.IsVisible);
        Assert.NotEmpty(JobColorsShown(w));
        w.Close();
    }
}
