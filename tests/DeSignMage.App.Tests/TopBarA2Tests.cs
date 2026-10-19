using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Canvas;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Feedback;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>
/// The top bar redesign (R38, TASKS C.45): the Main Toolbar holds only Open, Save, Undo, Redo and the zoom controls; the quick-tool strips, the
/// dark-mode buttons and the other buttons are gone (their commands stay in the menus, with their keys); the Context Bar is only actions, with one
/// Align drop-down; the measurement fields, the counts and the messages live on the Info Line; and everything fits at a 1280 x 720 window (a
/// 1920 x 1080 laptop at 150% display scaling), where the room for the drawing is measured by <see cref="TopBarMeasureTests"/>.
/// </summary>
[Collection("app-settings")]   // the text size and dark mode are program-wide: one class at a time
public class TopBarA2Tests
{
    private static async Task WithSettings(Func<Task> body)
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

    private static MainWindow Open(double width = 1280, double height = 720)
    {
        var w = new MainWindow(new StartupOptions()) { Width = width, Height = height };
        w.Show();
        Flush();
        w.CaptureRenderedFrame();
        w.FindControl<DesignCanvas>("Surface")!.Focus();
        return w;
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    private static void Settle(MainWindow w)
    {
        Flush();
        w.CaptureRenderedFrame();
        Flush();
        w.CaptureRenderedFrame();
    }

    private static RectangleObject AddRect(MainWindow w, double x = 100, double y = 100, double width = 72, double height = 36)
    {
        var r = new RectangleObject(width, height) { Transform = Affine2.Translation(x, y), Style = ObjectStyle.FillOnly(DocColor.FromHex("#336699")) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, r));
        return r;
    }

    private static TextObject AddText(MainWindow w)
    {
        var t = new TextObject("Hello", "Arial", 40) { Transform = Affine2.Translation(100, 300), Style = ObjectStyle.FillOnly(DocColor.Black) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, t));
        return t;
    }

    private static void Press(MainWindow w, Control c)
    {
        var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w).GetValueOrDefault();
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
        Flush();
    }

    private static void PressKey(MainWindow w, Key key) { w.KeyPress(key, RawInputModifiers.None, key == Key.Enter ? PhysicalKey.Enter : PhysicalKey.Escape, null); Flush(); }

    private static IEnumerable<MenuItem> AllItems(ItemsControl menu) => menu.Items.OfType<MenuItem>().SelectMany(m => AllItems(m).Prepend(m));

    private static HashSet<string> MenuCommandIds(MainWindow w) =>
        [.. AllItems(w.FindControl<Menu>("MainMenu")!).Where(m => m.Tag is AppCommand).Select(m => ((AppCommand)m.Tag!).Id)];

    private static Control Bar(MainWindow w) => (Control)w.FindControl<ContentControl>("ContextBarHost")!.Content!;

    private static bool Stacked(MainWindow w) => Bar(w).GetSelfAndVisualDescendants().OfType<TwoLinePanel>().Any(p => p.Stacked);

    private static double Right(Control c, Visual to) => c.TranslatePoint(new Point(c.Bounds.Width, 0), to)!.Value.X;

    /// <summary>How many lines the visible controls of a row are on: their vertical centers more than 10 px (at 100%) apart are on different lines.</summary>
    private static int LineCount(IEnumerable<Control> items)
    {
        var centers = items.Where(c => c.IsVisible && c.Bounds.Width > 0.5).Select(c => c.Bounds.Y + c.Bounds.Height / 2).OrderBy(y => y).ToList();
        if (centers.Count == 0) return 0;
        int lines = 1;
        double start = centers[0];
        foreach (double y in centers.Skip(1))
            if (y - start > 10 * UiScale.Scale) { lines++; start = y; }
        return lines;
    }

    // ---------------------------------------------------------------- the top row

    [AvaloniaFact]
    public void The_top_row_holds_exactly_Open_Save_Undo_Redo_Zoom_out_the_zoom_box_Zoom_in_and_Fit_page_and_then_Feedback()
    {
        var w = Open();
        var toolbar = w.FindControl<BarRow>("MainToolbar")!;
        string Describe(Control c) => c switch
        {
            BarSeparator => "|",
            ZoomBox => "zoom box",
            Button b => ToolTip.GetTip(b)?.ToString() ?? "?",
            _ => c.GetType().Name,
        };
        var parts = toolbar.Children.Select(Describe).ToList();
        Assert.Equal(10, parts.Count);
        string[] expected = ["Open", "Save", "|", "Undo", "Redo", "|", "Zoom Out", "zoom box", "Zoom In", "Zoom Layout"];
        for (int i = 0; i < expected.Length; i++) Assert.StartsWith(expected[i], parts[i]);

        // nothing else in the top row but the menus and Feedback: no tabs, no switches, no other buttons
        var top = w.FindControl<Border>("TopRowBorder")!;
        Assert.Empty(top.GetVisualDescendants().OfType<ToggleButton>());
        var buttons = top.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.GetVisualAncestors().OfType<Menu>().Count() == 0).ToList();
        Assert.Equal(7 + 1 + 1, buttons.Count);                                    // the seven toolbar buttons, the zoom presets arrow, Feedback
        Assert.Contains(buttons, b => b.Name == "FeedbackButton");

        // Feedback is at the far right, after the toolbar, and quieter: plain, with a small yellow dot
        var feedback = w.FindControl<Button>("FeedbackButton")!;
        Assert.True(feedback.TranslatePoint(new Point(0, 0), w)!.Value.X > Right(toolbar, w) - 1);
        Assert.True(Right(feedback, w) > w.Bounds.Width - 20);
        Assert.True(w.FindControl<ScrollViewer>("MainToolbarScroll")!.Extent.Width <= w.FindControl<ScrollViewer>("MainToolbarScroll")!.Viewport.Width + 0.5, "the toolbar fits without scrolling");
        w.Close();
    }

    [AvaloniaFact]
    public void The_toolbar_buttons_run_their_commands_and_Undo_and_Redo_follow_the_history()
    {
        var w = Open();
        var toolbar = w.FindControl<BarRow>("MainToolbar")!;
        Button B(string tip) => toolbar.Children.OfType<Button>().Single(b => ToolTip.GetTip(b)!.ToString()!.StartsWith(tip));
        Assert.False(B("Undo").IsEnabled);
        var r = AddRect(w);
        Flush();
        Assert.True(B("Undo").IsEnabled);
        Press(w, B("Undo"));
        Assert.DoesNotContain(r, w.Session.Document.ActiveLayer.Children);
        Assert.True(B("Redo").IsEnabled);
        Press(w, B("Redo"));
        Assert.Contains(r, w.Session.Document.ActiveLayer.Children);
        double z = w.Session.Viewport.ZoomPercent;
        Press(w, B("Zoom In"));
        Assert.True(w.Session.Viewport.ZoomPercent > z);
        Press(w, B("Zoom Out"));
        Assert.Equal(z, w.Session.Viewport.ZoomPercent, 6);
        w.Close();
    }

    [AvaloniaFact]
    public void The_quick_tool_strips_are_gone_and_their_commands_stay_in_the_menus()
    {
        var w = Open();
        Assert.Null(w.FindControl<Control>("MenuStrip"));
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<Control>(), c => c.Classes.Contains("mtab") || c.Classes.Contains("strip"));
        Assert.Null(typeof(MainWindow).Assembly.GetType("DeSignMage.App.Shell.MenuStrip"));                  // the code is gone, not hidden
        Assert.Null(typeof(MainWindow).Assembly.GetType("DeSignMage.App.Shell.MenuStripIcons"));
        Assert.DoesNotContain(w.FindControl<Border>("ContextBarBorder")!.GetVisualDescendants().OfType<ToggleButton>(), t => t.IsEffectivelyVisible);

        // what the strips ran is all in the menus (the Image, Effects and Tools menus are as they were)
        var ids = MenuCommandIds(w);
        foreach (var id in new[]
                 {
                     "image.vectorize", "image.editPicture", "image.flatten", "image.crop", "image.resample", "image.rotateCW", "image.rotateCCW", "image.flipH", "image.flipV", "image.invert", "image.zoom11",
                     "effects.outline", "effects.inline", "effects.knife", "effects.weld", "effects.commonWeld", "effects.cookieCutter", "effects.shadow", "effects.shadowExtruded", "effects.shadowPerspective", "effects.shadowCast",
                     "effects.softShadow", "effects.glow", "effects.feather", "effects.gradientBlend", "effects.easyBevel", "effects.textOnPath", "effects.makePlain",
                     "tool.select", "tool.vector", "tool.vectorDraw", "tool.loop", "tools.showTransparency", "tools.options",
                 })
            Assert.Contains(id, ids);

        // and so is what left the top row, with the keys it had
        foreach (var id in new[] { "file.new", "edit.delete", "view.zoomAll", "view.zoomSelection", "view.guides", "view.snapGuides", "view.darkMenus", "view.darkCanvas" })
            Assert.Contains(id, ids);
        var delete = AllItems(w.FindControl<Menu>("MainMenu")!).Select(m => m.Tag).OfType<AppCommand>().First(c => c.Id == "edit.delete");
        Assert.Equal(["Delete"], delete.Gestures.Select(g => g.ToString()));
        foreach (var c in AllItems(w.FindControl<Menu>("MainMenu")!).Select(m => m.Tag).OfType<AppCommand>())
            Assert.Equal(KeyMap.Default.Where(k => k.CommandId == c.Id).Select(k => KeyGesture.Parse(k.Gesture).ToString()), c.Gestures.Select(g => g.ToString()));   // no key added, none lost
        w.Close();
    }

    [AvaloniaFact]
    public void The_Toolbox_still_shows_the_active_tool_pressed_and_the_Tools_menu_is_as_it_was()
    {
        var w = Open();
        var toolbox = w.FindControl<StackPanel>("ToolboxHost")!.Children.OfType<ToggleButton>().Take(Toolbox.Buttons.Length).ToArray();
        string[] Pressed() => [.. Enumerable.Range(0, toolbox.Length).Where(i => toolbox[i].IsChecked == true).Select(i => Toolbox.Buttons[i].ToolId)];
        Assert.Equal(["select"], Pressed());
        for (int i = 0; i < toolbox.Length; i++)
        {
            Press(w, toolbox[i]);
            Assert.Equal([Toolbox.Buttons[i].ToolId], Pressed());
            Assert.Equal(Toolbox.Buttons[i].ToolId, w.Session.ActiveTool.Id);
        }
        Press(w, toolbox[^1]);                                                       // the tool that is already active stays pressed
        Assert.Equal([Toolbox.Buttons[^1].ToolId], Pressed());
        w.Session.SetTool("vectordraw");
        Flush();
        Assert.Equal(["vectordraw"], Pressed());

        var tools = w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => m.Header?.ToString() == "_Tools");
        var ids = tools.Items.OfType<MenuItem>().Where(m => m.Tag is AppCommand).Select(m => ((AppCommand)m.Tag!).Id).ToArray();
        Assert.Equal(["tool.select", "tool.vector", "tool.vectorDraw", "tool.loop", "tools.showTransparency", "tools.options", "debug.stress"], ids);
        w.Close();
    }

    // ---------------------------------------------------------------- the zoom box

    [AvaloniaFact]
    public void The_zoom_box_shows_the_zoom_follows_it_and_typing_a_number_sets_it()
    {
        var w = Open();
        var z = w.ZoomControl;
        var view = w.Session.Viewport;
        Assert.Equal(ZoomBox.Format(view.ZoomPercent), z.Box.Text);

        view.ZoomAt(view.ViewSize / 2, 2);                                          // any other way of zooming (wheel, keys, buttons) updates the box
        Flush();
        Assert.Equal(ZoomBox.Format(view.ZoomPercent), z.Box.Text);

        var center = view.ToDoc(view.ViewSize / 2);
        z.Box.Focus();
        z.Box.Text = "200";
        PressKey(w, Key.Enter);
        Assert.Equal(200, view.ZoomPercent, 6);
        Assert.Equal("200%", z.Box.Text);
        Assert.Equal(center.X, view.ToDoc(view.ViewSize / 2).X, 3);                // it zooms about the middle of the drawing area, like Zoom In
        Assert.True(w.FocusManager!.GetFocusedElement() is DesignCanvas);          // and the keyboard goes back to the canvas

        z.Box.Focus();
        z.Box.Text = " 50 % ";                                                      // a percent sign and spaces are fine
        PressKey(w, Key.Enter);
        Assert.Equal(50, view.ZoomPercent, 6);

        z.Box.Focus();
        z.Box.Text = "not a number";                                                // nothing sensible: the box goes back to the real zoom
        PressKey(w, Key.Enter);
        Assert.Equal(50, view.ZoomPercent, 6);
        Assert.Equal("50%", z.Box.Text);
        z.Box.Focus();
        z.Box.Text = "-5";
        PressKey(w, Key.Enter);
        Assert.Equal(50, view.ZoomPercent, 6);

        z.Box.Focus();
        z.Box.Text = "300";                                                         // Escape drops what was typed
        PressKey(w, Key.Escape);
        Flush();
        Assert.Equal(50, view.ZoomPercent, 6);
        Assert.Equal("50%", z.Box.Text);
        w.Close();
    }

    [AvaloniaFact]
    public void The_zoom_box_arrow_offers_Fit_page_and_the_presets_and_a_new_design_is_followed()
    {
        var w = Open();
        var z = w.ZoomControl;
        var view = w.Session.Viewport;
        w.Session.ZoomToPage(push: false);
        double fit = view.ZoomPercent;

        z.PresetsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Flush();
        Assert.True(z.PresetMenu.IsOpen);
        Assert.Equal(["Fit page", "25%", "50%", "100%", "200%", "400%"], z.PresetMenu.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()));
        z.PresetMenu.Close();

        foreach (var (percent, item) in z.PresetItems)
        {
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(percent, view.ZoomPercent, 6);
            Assert.Equal($"{percent:0}%", z.Box.Text);
        }
        z.FitItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(fit, view.ZoomPercent, 6);                                     // the same as the Fit page button

        // File > New: the box follows the new design's view
        var old = w.Session;
        w.SaveChangesOverride = (_, _) => Task.FromResult(MainWindow.SaveChoice.Discard);
        var file = w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => m.Header?.ToString() == "_File");
        file.Items.OfType<MenuItem>().Single(m => m.Header?.ToString() == "_New").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (ReferenceEquals(w.Session, old) && watch.ElapsedMilliseconds < 5000) { Flush(); Thread.Sleep(10); }
        Assert.NotSame(old, w.Session);
        w.Session.Viewport.ZoomAt(w.Session.Viewport.ViewSize / 2, 3);
        Flush();
        Assert.Equal(ZoomBox.Format(w.Session.Viewport.ZoomPercent), z.Box.Text);
        old.Viewport.ZoomAt(old.Viewport.ViewSize / 2, 2);                         // the old design no longer drives it
        Flush();
        Assert.Equal(ZoomBox.Format(w.Session.Viewport.ZoomPercent), z.Box.Text);
        w.Close();
    }

    // ---------------------------------------------------------------- the Info Line

    [AvaloniaFact]
    public void Typing_a_width_on_the_Info_Line_resizes_the_selection_in_one_undo_step_and_X_Y_H_move_and_size_it_too()
    {
        var w = Open();
        var s = w.Session;
        var r = AddRect(w, 100, 100, 72, 36);                                      // 1 x 0.5 in
        s.Selection.Set(r);
        Settle(w);
        var m = w.MeasureFields;
        var bottomBar = w.FindControl<Border>("BottomBar")!;
        Assert.Contains(bottomBar, m.W.GetVisualAncestors());                      // the fields are on the Info Line ...
        Assert.DoesNotContain(Bar(w).GetVisualDescendants().OfType<TextBox>(), t => t.IsEffectivelyVisible);   // ... and not on the Context Bar
        Assert.Equal(("1", "0.5"), (m.W.Text, m.H.Text));
        Assert.Equal("in", ((TextBlock)((StackPanel)m.SelectionFields).Children.Last()).Text);                    // with their unit

        int steps = s.History.UndoList.Count;
        m.W.Focus();
        m.W.Text = "2";
        PressKey(w, Key.Enter);
        Assert.Equal(144, r.GetWorldBounds().Width, 3);
        Assert.Equal(36, r.GetWorldBounds().Height, 3);                            // no proportion lock: only the width changed
        Assert.Equal(steps + 1, s.History.UndoList.Count);                         // one undo step for the edit ...
        s.Undo();
        Assert.Equal(72, r.GetWorldBounds().Width, 3);                             // ... and one undo takes it back
        Assert.Equal(steps, s.History.UndoList.Count);

        s.Redo();
        Flush();
        m.X.Focus();
        m.X.Text = "0.5";
        PressKey(w, Key.Enter);
        Assert.Equal(Units.FromInches(0.5), r.GetWorldBounds().Left - s.Document.Page.Bounds.Left, 3);
        m.Y.Focus();
        m.Y.Text = "3";
        PressKey(w, Key.Enter);
        Assert.Equal(Units.FromInches(3), s.Document.Page.Bounds.Bottom - r.GetWorldBounds().Bottom, 3);
        m.H.Focus();
        m.H.Text = "1";
        PressKey(w, Key.Enter);
        Assert.Equal(72, r.GetWorldBounds().Height, 3);
        Assert.Equal(steps + 4, s.History.UndoList.Count);                         // W, X, Y and H: one step each

        // leaving a field without typing does nothing (the boxes show rounded numbers)
        m.W.Focus();
        Flush();
        Settle(w);
        w.FindControl<DesignCanvas>("Surface")!.Focus();
        Flush();
        Assert.Equal(steps + 4, s.History.UndoList.Count);
        w.Close();
    }

    [AvaloniaFact]
    public void With_nothing_selected_the_Info_Line_fields_edit_the_layout_and_they_belong_to_the_Layout_tool()
    {
        var w = Open();
        var s = w.Session;
        var m = w.MeasureFields;
        s.Selection.Clear();
        Settle(w);
        Assert.True(m.PageFields.IsEffectivelyVisible);
        Assert.False(m.SelectionFields.IsEffectivelyVisible);
        Assert.Equal(ContextBars.Fmt(Units.ToInches(s.Document.Page.Width)), m.PageW.Text);

        int steps = s.History.UndoList.Count;
        m.PageW.Focus(); m.PageW.Text = "20"; PressKey(w, Key.Enter);
        m.PageH.Focus(); m.PageH.Text = "8.5"; PressKey(w, Key.Enter);
        Assert.Equal(Units.FromInches(20), s.Document.Page.Width, 3);
        Assert.Equal(Units.FromInches(8.5), s.Document.Page.Height, 3);
        Assert.Equal(steps + 2, s.History.UndoList.Count);

        // a shape selected: the object fields; the layout's go away
        var r = AddRect(w);
        s.Selection.Set(r);
        Settle(w);
        Assert.True(m.SelectionFields.IsEffectivelyVisible);
        Assert.False(m.PageFields.IsEffectivelyVisible);

        // any other tool: neither (as on the Context Bar before: only the Layout tool had them)
        s.SetTool("vector");
        Settle(w);
        Assert.False(m.SelectionFields.IsEffectivelyVisible);
        Assert.False(m.PageFields.IsEffectivelyVisible);
        s.SetTool("select");

        // a ruler guide selected: the guide's own bar and fields, not these
        var guide = new Guide(new Vec2(300, 200), 0);
        s.History.Execute(new AddGuideCommand(guide));
        s.SelectedGuide = guide;
        Settle(w);
        Assert.False(m.SelectionFields.IsEffectivelyVisible || m.PageFields.IsEffectivelyVisible);
        s.SelectedGuide = null;

        // a locked selection can't be moved or resized: no fields
        s.Selection.Set(r);
        s.ToggleLockSelection();
        Settle(w);
        Assert.False(m.SelectionFields.IsEffectivelyVisible || m.PageFields.IsEffectivelyVisible);
        s.ToggleLockSelection();
        Settle(w);
        Assert.True(m.SelectionFields.IsEffectivelyVisible);
        w.Close();
    }

    [AvaloniaFact]
    public void The_Context_Bar_has_no_position_or_size_fields_and_no_selected_text_for_any_selection()
    {
        var w = Open(1700);
        var s = w.Session;
        var rect = AddRect(w);
        var text = AddText(w);
        foreach (var selection in new DocObject[][] { [], [rect], [rect, text], [text] })
        {
            s.Selection.Set(selection);
            Settle(w);
            var bar = Bar(w);
            var boxes = bar.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible).ToList();
            Assert.DoesNotContain(boxes, t => t.Classes.Contains("num") && (t.Name ?? "").StartsWith("Measure"));
            if (!selection.Contains(text) || selection.Length != 1) Assert.Empty(boxes);      // the text's own Size and Spacing boxes are the only fields left
            Assert.DoesNotContain(bar.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible), t => (t.Text ?? "").Contains("selected") || (t.Text ?? "") == "Layout");
        }
        w.Close();
    }

    [AvaloniaFact]
    public void The_Info_Line_says_what_is_selected_and_counts_objects_and_groups_and_has_no_hint_and_no_zoom()
    {
        var w = Open();
        var s = w.Session;
        var info = w.FindControl<TextBlock>("InfoLine")!;
        Assert.Equal("0 objects · 0 groups", info.Text);
        var a = AddRect(w, 100, 100); var b = AddRect(w, 300, 100);
        Settle(w);
        Assert.Equal("2 objects · 0 groups", info.Text);
        s.Selection.Set(a);
        Settle(w);
        Assert.Equal($"{a.KindName} selected · 2 objects · 0 groups", info.Text);
        s.Selection.Set([a, b]);
        Settle(w);
        Assert.Equal("2 selected · 2 objects · 0 groups", info.Text);
        s.GroupSelection();
        Settle(w);
        Assert.Equal("Group selected · 2 objects · 1 group", info.Text);
        s.ToggleLockSelection();
        Settle(w);
        Assert.EndsWith("selected (locked) · 2 objects · 1 group", info.Text);
        s.ToggleLockSelection();

        // the hint line and the zoom text are gone from the bottom bar; the pointer position is there
        Assert.Null(w.FindControl<TextBlock>("HintText"));
        Assert.Null(w.FindControl<TextBlock>("ZoomText"));
        var bar = w.FindControl<Border>("BottomBar")!;
        Assert.DoesNotContain(bar.GetVisualDescendants().OfType<TextBlock>(), t => (t.Text ?? "").EndsWith('%'));
        var canvas = w.FindControl<DesignCanvas>("Surface")!;
        w.MouseMove(canvas.TranslatePoint(new Point(200, 200), w)!.Value);
        Flush();
        Assert.StartsWith("X: ", w.FindControl<TextBlock>("CursorText")!.Text);
        // the bitmaps part is only there while the design has pictures (ImageShellTests)
        Assert.False(w.FindControl<TextBlock>("BitmapsText")!.IsVisible);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_message_such_as_Saved_shows_on_the_Info_Line_in_place_of_the_counts_and_the_pointer_for_a_few_seconds()
    {
        var w = Open();
        var s = w.Session;
        AddRect(w);
        Settle(w);
        var panel = w.FindControl<InfoLinePanel>("InfoBar")!;
        var flash = w.FindControl<TextBlock>("FlashText")!;
        var info = w.FindControl<TextBlock>("InfoLine")!;
        var cursor = w.FindControl<TextBlock>("CursorText")!;
        Assert.False(flash.IsVisible);
        Assert.False(panel.IsGivenWay(info));

        var dir = Directory.CreateTempSubdirectory("dsm-flash-").FullName;
        try
        {
            var path = Path.Combine(dir, "flash.dsm");
            w.SavePathOverride = _ => path;
            var save = w.FindControl<BarRow>("MainToolbar")!.Children.OfType<Button>().First(b => ToolTip.GetTip(b)!.ToString()!.StartsWith("Save"));
            Press(w, save);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (w.CurrentFlash is null && watch.ElapsedMilliseconds < 10000) { Flush(); Thread.Sleep(10); }
            Settle(w);
            Assert.Equal("Saved flash.dsm", w.CurrentFlash);
            Assert.True(flash.IsVisible);
            Assert.Equal("Saved flash.dsm", flash.Text);
            Assert.True(flash.Bounds.Width > 40);
            Assert.True(panel.IsGivenWay(info) && panel.IsGivenWay(cursor));       // the counts and the pointer position make room for it
            Assert.True(w.MeasureFields.SelectionFields.IsEffectivelyVisible || w.MeasureFields.PageFields.IsEffectivelyVisible);   // the fields stay
            Assert.True(flash.TranslatePoint(new Point(0, 0), w)!.Value.X > Right(w.FindControl<StackPanel>("MeasureHost")!, w) - 1);   // after them, on the same line
        }
        finally { Directory.Delete(dir, true); }

        // a long message is trimmed to the room, and every message goes after a few seconds
        w.Flash(new string('x', 400));
        Settle(w);
        Assert.True(Right(flash, w) <= w.Bounds.Width + 0.5);
        w.EndFlash();
        Settle(w);
        Assert.False(flash.IsVisible);
        Assert.False(panel.IsGivenWay(info));
        Assert.Equal("1 object · 0 groups", info.Text);
        await Task.CompletedTask;
        w.Close();
    }

    [AvaloniaFact]
    public void The_Info_Line_panel_gives_way_in_order_keeps_the_right_end_at_the_right_and_makes_room_for_a_message()
    {
        Border Box(double width) => new() { Width = width, Height = 20 };
        var fixedLeft = Box(100); var fixedRight = Box(100);
        var bitmaps = Box(80); var pointer = Box(80); var counts = Box(80);
        InfoLinePanel.SetDropOrder(bitmaps, 3);
        InfoLinePanel.SetDropOrder(pointer, 2);
        InfoLinePanel.SetDropOrder(counts, 1);
        InfoLinePanel.SetRight(pointer, true);
        InfoLinePanel.SetRight(fixedRight, true);
        var message = new TextBlock { Text = new string('m', 200), TextTrimming = TextTrimming.CharacterEllipsis, IsVisible = false };
        var panel = new InfoLinePanel { Spacing = 10, Flash = message };
        foreach (var c in new Control[] { fixedLeft, counts, bitmaps, message, pointer, fixedRight }) panel.Children.Add(c);

        void Layout(double width)
        {
            panel.InvalidateMeasure();
            panel.Measure(new Size(width, 30));
            panel.Arrange(new Rect(0, 0, width, 30));
        }
        string Gone() => string.Concat(new[] { (bitmaps, "bitmaps "), (pointer, "pointer "), (counts, "counts ") }.Where(p => panel.IsGivenWay(p.Item1)).Select(p => p.Item2));

        Layout(600);
        Assert.Equal("", Gone());                                                  // everything fits (100 + 100 + 3 x 80 + 4 gaps of 10 = 480)
        Assert.Equal(600, fixedRight.Bounds.Right, 0.5);                           // the right end sits at the right edge ...
        Assert.Equal(490, pointer.Bounds.Right, 0.5);                              // ... the pointer just before it
        Assert.Equal(fixedLeft.Bounds.Right + 10, counts.Bounds.X, 0.5);           // the left end follows the order
        Layout(470);
        Assert.Equal("bitmaps ", Gone());                                          // the bitmaps go first ...
        Layout(380);
        Assert.Equal("bitmaps pointer ", Gone());                                  // ... then the pointer position ...
        Layout(290);
        Assert.Equal("bitmaps pointer counts ", Gone());                           // ... then the counts; the rest is never given up
        Layout(150);
        Assert.Equal("bitmaps pointer counts ", Gone());
        Assert.True(fixedLeft.IsVisible && fixedRight.IsVisible);

        message.IsVisible = true;                                                  // a message takes the room of everything that can give way
        Layout(600);
        Assert.Equal("bitmaps pointer counts ", Gone());
        Assert.InRange(message.Bounds.Width, 100, 600 - 210);
        Assert.True(message.Bounds.Right <= 600 - 100 - 10 + 0.5);                 // and stops before the fixed item at the right end
        Assert.Equal(fixedLeft.Bounds.Right + 10, message.Bounds.X, 3);
    }

    // ---------------------------------------------------------------- the Align drop-down at the window sizes that matter

    [AvaloniaFact]
    public void The_Align_drop_down_stays_with_the_other_Arrange_buttons_on_the_Context_Bar_in_both_themes()
    {
        var w = Open();
        var a = AddRect(w, 100, 100);
        var b = AddRect(w, 300, 100);
        w.Session.Selection.Set([a, b]);
        Settle(w);
        var bar = Assert.IsType<SelectionContextBar>(Bar(w));
        Assert.True(bar.AlignDropDownButton.IsEffectivelyVisible);
        Assert.True(bar.AlignDropDownButton.Bounds.Height <= ContextBars.RowHeightBase * UiScale.Scale + 0.5);
        double x = bar.AlignDropDownButton.TranslatePoint(default, w)!.Value.X;
        Assert.True(bar.MirrorH.TranslatePoint(default, w)!.Value.X > x);          // Align, then Mirror H, Mirror V, then Lock
        Assert.True(bar.Lock.TranslatePoint(default, w)!.Value.X > bar.MirrorV.TranslatePoint(default, w)!.Value.X);
        w.Close();
    }

    // ---------------------------------------------------------------- the same at 150% display scaling (1280 x 720) and at other sizes

    /// <summary>Everything on the top row, the Context Bar and the Info Line is inside the window and nothing needed a second line or a scroll.</summary>
    private static void AssertTopAndBottomFit(MainWindow w, string state, bool top100 = true, bool oneLineInfo = true)
    {
        Settle(w);
        var top = w.FindControl<Border>("TopRowBorder")!;
        var scroll = w.FindControl<ScrollViewer>("MainToolbarScroll")!;
        if (top100) Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 0.5, $"{state}: the Main Toolbar scrolls");   // (at 200% text the toolbar scrolls sideways, as it always did)
        Assert.True(Right(w.FindControl<Button>("FeedbackButton")!, w) <= w.Bounds.Width + 0.5, $"{state}: Feedback is cut off");
        Assert.Equal(1, LineCount(w.FindControl<BarRow>("MainToolbar")!.Children));
        foreach (var c in top.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.Bounds.Height > 0 && c is Button or ZoomBox or Menu))
            Assert.True(c.TranslatePoint(new Point(0, c.Bounds.Height), w)!.Value.Y <= top.Bounds.Height + 0.5, $"{state}: {c.GetType().Name} sticks out of the top row");

        // the Info Line is one line and nothing that stays on it is cut off
        var bottom = w.FindControl<Border>("BottomBar")!;
        var panel = w.FindControl<InfoLinePanel>("InfoBar")!;
        foreach (var c in panel.Children.Where(c => c.IsVisible && !panel.IsGivenWay(c)))
            Assert.True(Right(c, w) <= bottom.Bounds.Width + 0.5, $"{state}: {c.GetType().Name} {c.Name} reaches past the Info Line");
        if (oneLineInfo)
        {
            Assert.True(bottom.Bounds.Height <= 34 * UiScale.Scale + 1, $"{state}: the Info Line is {bottom.Bounds.Height} px tall");
            Assert.Equal(1, LineCount(w.FindControl<BarRow>("ProjectColorBarHost")!.Children));
        }
        else Assert.True(bottom.Bounds.Height <= 2 * 34 * UiScale.Scale + 2, $"{state}: the Info Line is {bottom.Bounds.Height} px tall");   // very large text: the project colors wrap, nothing is cut off
    }

    /// <summary>The Context Bar's rows are one line each and the bar fits the width it has (no sideways scrolling), on one line or two.</summary>
    private static void AssertContextBarFits(MainWindow w, string state, bool expectOneLine)
    {
        Settle(w);
        var scroll = w.FindControl<ScrollViewer>("ContextBarScroll")!;
        var bar = Bar(w);
        foreach (var row in bar.GetSelfAndVisualDescendants().OfType<BarRow>().Where(r => r.IsEffectivelyVisible && r.Parent is TwoLinePanel))
            Assert.True(row.Children.Where(c => c.IsVisible && c.Bounds.Width > 0.5).Select(c => Math.Round((c.Bounds.Y + c.Bounds.Height / 2) / 8)).Distinct().Count() <= 1, $"{state}: a row wraps");
        Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1, $"{state}: the Context Bar is {scroll.Extent.Width} wide in {scroll.Viewport.Width}");
        double height = w.FindControl<Border>("ContextBarBorder")!.Bounds.Height;
        if (expectOneLine)
        {
            Assert.False(Stacked(w), $"{state}: two lines");
            Assert.Equal(ContextBars.OneLineHeight, height, 1);
        }
        else Assert.InRange(height, ContextBars.OneLineHeight - 1, ContextBars.TwoLineHeight + 1);
    }

    [AvaloniaFact]
    public async Task At_1280_x_720_the_top_row_the_Context_Bar_and_the_Info_Line_fit_and_the_drawing_area_is_larger_than_before()
    {
        await WithSettings(async () =>
        {
            var w = Open(1280, 720);
            var rect = AddRect(w);
            var text = AddText(w);
            var s = w.Session;
            var canvas = w.FindControl<DesignCanvas>("Surface")!;

            s.Selection.Clear();
            AssertTopAndBottomFit(w, "nothing selected");
            AssertContextBarFits(w, "nothing selected", expectOneLine: true);
            // before the redesign (measured in the same test window): 1201 x 596 for a plain selection, 1201 x 562 with a text selected
            Assert.True(canvas.Bounds.Height >= 604 - 0.5, $"{canvas.Bounds.Height}");
            Assert.True(canvas.Bounds.Width >= 1201 - 0.5);
            Assert.True(canvas.Bounds.Width * canvas.Bounds.Height / (w.Bounds.Width * w.Bounds.Height) > 0.78);   // was 77.7%

            s.Selection.Set(rect);
            AssertTopAndBottomFit(w, "a shape");
            AssertContextBarFits(w, "a shape", expectOneLine: true);
            var m = w.MeasureFields;
            Assert.True(m.X.IsEffectivelyVisible && m.Y.IsEffectivelyVisible && m.W.IsEffectivelyVisible && m.H.IsEffectivelyVisible);
            Assert.False(w.FindControl<InfoLinePanel>("InfoBar")!.IsGivenWay(w.FindControl<TextBlock>("InfoLine")!));   // and the counts still fit

            s.Selection.Set([rect, text]);
            AssertContextBarFits(w, "a shape and a text", expectOneLine: true);

            s.Selection.Set(text);                                                // the font controls and the Arrange buttons do not fit side by side in 981 px
            AssertTopAndBottomFit(w, "a text");
            AssertContextBarFits(w, "a text", expectOneLine: false);
            Assert.True(Stacked(w));
            Assert.True(canvas.Bounds.Height >= 570 - 0.5);                       // two lines: still more room than the two lines of before (562)

            // a picture
            var dir = Directory.CreateTempSubdirectory("dsm-a2-").FullName;
            try
            {
                var pixels = new DeSignMage.Imaging.PixelBuffer(60, 30);
                for (int y = 0; y < 30; y++)
                    for (int x = 0; x < 60; x++) pixels.Set(x, y, (byte)(x * 4), 80, 200, 255);
                var path = Path.Combine(dir, "p.png");
                File.WriteAllBytes(path, DeSignMage.Imaging.ImageCodec.EncodePng(pixels));
                Assert.Equal(1, await w.ImportImagesAsync([path], null));
                Settle(w);
                AssertContextBarFits(w, "a picture", expectOneLine: true);        // two lines before the redesign
                AssertTopAndBottomFit(w, "a picture");
                Assert.True(w.FindControl<TextBlock>("BitmapsText")!.IsVisible);
            }
            finally { Directory.Delete(dir, true); }

            // the tools that end in Done and Cancel: Done and Cancel are at the end of the bar and on screen, whatever the number of lines
            foreach (var (name, open) in TopBarMeasureTests.DoneCancelModes())
            {
                w.Session.SetTool("select");
                w.Session.Selection.Clear();
                var fresh = Open(1280, 720);
                var r2 = AddRect(fresh);
                var t2 = AddText(fresh);
                open(fresh, r2, t2);
                Settle(fresh);
                var bar = Bar(fresh);
                var buttons = bar.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Content is string).ToList();
                var cancel = buttons.LastOrDefault(b => (string)b.Content! == "Cancel");
                Assert.True(cancel is not null, $"{name}: no Cancel");
                var done = buttons.LastOrDefault(b => (string)b.Content! == "Done");
                var barRight = fresh.FindControl<Border>("ContextBarBorder")!.TranslatePoint(new Point(fresh.FindControl<Border>("ContextBarBorder")!.Bounds.Width, 0), fresh)!.Value.X;
                Assert.True(Right(cancel!, fresh) <= barRight + 0.5, $"{name}: Cancel is scrolled out of the bar");
                if (done is not null) Assert.True(done.TranslatePoint(default, fresh)!.Value.X < cancel!.TranslatePoint(default, fresh)!.Value.X, $"{name}: Done comes before Cancel");
                fresh.Close();
            }
            w.Close();
        });
    }

    [AvaloniaTheory]
    [InlineData(1366, 768, 1.0)]
    [InlineData(1920, 1080, 1.0)]
    [InlineData(1280, 720, 1.5)]
    [InlineData(1280, 720, 2.0)]
    public async Task The_Info_Line_keeps_the_fields_whole_and_gives_way_in_order_at_other_sizes_and_text_sizes(double width, double height, double text)
    {
        await WithSettings(() =>
        {
            UiScale.SetScale(text, remember: false);
            var w = Open(width, height);
            var rect = AddRect(w);
            w.Session.Selection.Set(rect);
            AssertTopAndBottomFit(w, $"{width}x{height} at {text:P0}", top100: text <= 1.5, oneLineInfo: text <= 1.5);
            var m = w.MeasureFields;
            Assert.True(m.X.IsEffectivelyVisible && m.W.IsEffectivelyVisible && m.H.IsEffectivelyVisible);
            foreach (var box in new[] { m.X, m.Y, m.W, m.H }) Assert.True(Right(box, w) <= w.FindControl<Border>("BottomBar")!.Bounds.Width, $"{box.Name} is cut off");
            Assert.NotNull(w.ProjectColorBar.FillButton);
            Assert.True(w.ProjectColorBar.StrokeButton.IsEffectivelyVisible);
            // what gave way is only what may: the counts, the bitmaps, the pointer, the frame times
            var panel = w.FindControl<InfoLinePanel>("InfoBar")!;
            foreach (var c in panel.Children.Where(c => panel.IsGivenWay(c))) Assert.True(InfoLinePanel.GetDropOrder(c) > 0, $"{c.Name} may not give way");
            if (width >= 1366 && text <= 1.0) Assert.False(panel.IsGivenWay(w.FindControl<TextBlock>("InfoLine")!));   // roomy enough for everything
            w.Close();
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- text size and dark menus

    [AvaloniaFact]
    public async Task The_new_controls_follow_the_interface_text_size()
    {
        await WithSettings(() =>
        {
            var w = Open(1600, 900);
            AddRect(w);
            w.Session.Selection.Set(w.Session.Document.ActiveLayer.Children.Last());
            Settle(w);
            var zoom = w.ZoomControl.Box;
            var feedbackText = w.FindControl<Button>("FeedbackButton")!.GetVisualDescendants().OfType<TextBlock>().Single();
            var bar = Assert.IsType<SelectionContextBar>(Bar(w));
            var alignText = bar.AlignDropDownButton.GetVisualDescendants().OfType<TextBlock>().Single();
            double zw = zoom.Bounds.Width, fs = feedbackText.FontSize, aw = alignText.FontSize, top = w.FindControl<Border>("TopRowBorder")!.Bounds.Height, xw = w.MeasureFields.X.Bounds.Width;

            UiScale.SetScale(1.5, remember: false);
            Settle(w);
            Assert.Equal(zw * 1.5, zoom.Bounds.Width, 1);
            Assert.Equal(fs * 1.5, feedbackText.FontSize, 3);
            Assert.Equal(aw * 1.5, alignText.FontSize, 3);
            Assert.Equal(xw * 1.5, w.MeasureFields.X.Bounds.Width, 1);
            Assert.True(w.FindControl<Border>("TopRowBorder")!.Bounds.Height > top * 1.3);
            Assert.True(w.FindControl<Border>("TopRowBorder")!.Bounds.Height < top * 1.7);
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task Dark_Menus_recolors_the_new_controls_and_Feedback_stays_a_plain_button()
    {
        await WithSettings(() =>
        {
            var w = Open();
            w.Session.Selection.Set(AddRect(w));                                                                 // a selection: the fields are in use (a disabled field is grey)
            Settle(w);
            var feedback = w.FindControl<Button>("FeedbackButton")!;
            var dot = feedback.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single();
            Assert.Equal(AppTheme.ColorOf("Ui.ActBack"), ((ISolidColorBrush)feedback.Background!).Color);
            Assert.Equal(Color.Parse("#FBFBFB"), ((ISolidColorBrush)feedback.Background!).Color);               // plain white
            AppTheme.SetDarkMenus(true, remember: false);
            Settle(w);
            Assert.Equal(Color.Parse("#424242"), ((ISolidColorBrush)feedback.Background!).Color);               // plain dark grey
            Assert.Equal(AppTheme.DarkText, ((ISolidColorBrush)feedback.Foreground!).Color);
            Assert.Equal(Color.Parse("#F2B705"), ((ISolidColorBrush)dot.Fill!).Color);                          // the dot keeps its yellow
            Assert.Equal(AppTheme.DarkText, ((ISolidColorBrush)w.ZoomControl.Box.Foreground!).Color);
            Assert.Equal(AppTheme.DarkText, ((ISolidColorBrush)w.MeasureFields.X.Foreground!).Color);
            Assert.Equal(AppTheme.ColorOf("Ui.Chrome"), ((ISolidColorBrush)w.FindControl<Border>("BottomBar")!.Background!).Color);
            w.Close();
            return Task.CompletedTask;
        });
    }
}
