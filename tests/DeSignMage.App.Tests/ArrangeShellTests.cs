using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Canvas;
using DeSignMage.App.Reporting;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Feedback;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Arrange ▸ Align, Mirror, Locked in the real window: the L / R / N keys, the menu, the Context Bar and the Layers Panel.</summary>
public class ArrangeShellTests
{
    private static (MainWindow W, DesignCanvas C) Open()
    {
        var w = new MainWindow(new StartupOptions()) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        var c = w.FindControl<DesignCanvas>("Surface")!;
        c.Focus();
        return (w, c);
    }

    private static RectangleObject AddRect(MainWindow w, double x, double y, double width, double height = 60)
    {
        var r = new RectangleObject(width, height) { Transform = Affine2.Translation(x, y), Style = ObjectStyle.FillOnly(DocColor.FromHex("#E53935")) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, r));
        return r;
    }

    private static (MainWindow W, DesignCanvas C, RectangleObject A, RectangleObject B) OpenWithTwo()
    {
        var (w, c) = Open();
        var a = AddRect(w, 200, 200, 100); var b = AddRect(w, 500, 320, 60);
        w.Session.Selection.Set([a, b]);
        Dispatcher.UIThread.RunJobs();
        return (w, c, a, b);
    }

    private static void Press(MainWindow w, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        PhysicalKey physical = key switch { Key.L => PhysicalKey.L, Key.R => PhysicalKey.R, Key.N => PhysicalKey.N, Key.A => PhysicalKey.A, Key.Z => PhysicalKey.Z, _ => PhysicalKey.None };
        w.KeyPress(key, mods, physical, key.ToString().ToLowerInvariant());
        Dispatcher.UIThread.RunJobs();
    }

    private static Point P(MainWindow w, DesignCanvas c, Vec2 doc)
    {
        var s = w.Session.Viewport.ToScreen(doc);
        return c.TranslatePoint(new Point(s.X, s.Y), w).GetValueOrDefault();
    }

    private static MenuItem Arrange(MainWindow w) =>
        w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => m.Header?.ToString() == "_Arrange");

    private static SelectionContextBar Bar(MainWindow w)
    {
        var bar = Assert.IsType<SelectionContextBar>(w.FindControl<ContentControl>("ContextBarHost")!.Content);
        bar.Refresh();
        return bar;
    }

    // ---- the keys ----

    [AvaloniaFact]
    public void Plain_L_R_and_N_align_left_right_and_center_and_undo_takes_each_back()
    {
        var (w, _, a, b) = OpenWithTwo();                           // a: x 200..300, b: x 500..560
        Press(w, Key.L);
        Assert.Equal(200, b.GetWorldBounds().Left, 6);
        Assert.Equal("Align Left", w.Session.History.UndoDescription);
        Press(w, Key.Z);                                            // plain Z undoes it
        Assert.Equal(500, b.GetWorldBounds().Left, 6);

        Press(w, Key.R);
        Assert.Equal(560, a.GetWorldBounds().Right, 6);
        Press(w, Key.Z);

        Press(w, Key.N);                                            // the selection spans 200..560: center 380
        Assert.Equal(380, a.GetWorldBounds().Center.X, 6);
        Assert.Equal(380, b.GetWorldBounds().Center.X, 6);
        w.Close();
    }

    [AvaloniaFact]
    public void The_keys_do_nothing_with_a_Ctrl_Shift_Alt_or_Win_modifier()
    {
        var (w, _, a, b) = OpenWithTwo();
        foreach (var key in new[] { Key.L, Key.R, Key.N })
            foreach (var mods in new[] { RawInputModifiers.Control, RawInputModifiers.Shift, RawInputModifiers.Alt, RawInputModifiers.Meta, RawInputModifiers.Control | RawInputModifiers.Shift })
                Press(w, key, mods);
        Assert.Equal(200, a.GetWorldBounds().Left, 6);
        Assert.Equal(500, b.GetWorldBounds().Left, 6);
        w.Close();
    }

    [AvaloniaFact]
    public void The_keys_need_something_to_align_and_the_Layout_tool()
    {
        var (w, _) = Open();
        var a = AddRect(w, 200, 200, 100);
        int steps = w.Session.History.UndoList.Count;
        w.Session.Selection.Set(a);
        Press(w, Key.L); Press(w, Key.R); Press(w, Key.N);          // one object: nothing to line up with
        Assert.Equal(steps, w.Session.History.UndoList.Count);

        var b = AddRect(w, 500, 320, 60);
        w.Session.Selection.Set([a, b]);
        w.Session.SetTool("rectangle");                              // another tool: the letters are not Align
        Press(w, Key.L);
        Assert.Equal(500, b.GetWorldBounds().Left, 6);
        w.Close();
    }

    [AvaloniaFact]
    public void L_in_the_Vector_Edit_tool_is_still_To_Line_and_does_not_align()
    {
        var (w, c) = Open();
        var other = AddRect(w, 900, 200, 80);
        var r = AddRect(w, 200, 200, 300, 300);
        w.Session.EditVectors(r);
        Assert.Equal("vector", w.Session.ActiveTool.Id);
        w.Session.Selection.Set([r, other]);                         // a selection that Align could work on, were the Layout tool active
        w.MouseMove(P(w, c, new Vec2(320, 200)));                    // on the top edge
        Press(w, Key.A);                                             // To Arc
        var path = Assert.IsType<PathObject>(w.Session.Document.ActiveLayer.Children.Single(o => o is PathObject));
        Assert.True(path.Geometry.HasArcs);
        Press(w, Key.L);                                             // To Line
        Assert.False(path.Geometry.HasArcs);
        Assert.Equal(900, other.GetWorldBounds().Left, 6);           // and no object moved
        Assert.Equal(200, path.GetWorldBounds().Left, 6);
        w.Close();
    }

    [AvaloniaFact]
    public void The_keys_are_ignored_while_text_is_edited_and_in_a_text_field_or_dialog()
    {
        var (w, c, a, b) = OpenWithTwo();
        var s = w.Session;

        // in a focused number field
        var bar = Bar(w);
        bar.X.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.IsType<TextBox>(w.FocusManager!.GetFocusedElement());
        foreach (var key in new[] { Key.L, Key.R, Key.N }) Press(w, key);
        Assert.Equal(500, b.GetWorldBounds().Left, 6);

        // in a dialog
        var dialog = new FeedbackDialog(canSend: false, savedName: null, _ => Task.FromResult(new SubmitResult(SubmitStatus.Queued, "x")));
        dialog.Show(w);
        Dispatcher.UIThread.RunJobs();
        dialog.GetVisualDescendants().OfType<TextBox>().First().Focus();
        foreach (var key in new[] { Key.L, Key.R, Key.N }) dialog.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, key.ToString().ToLowerInvariant());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(500, b.GetWorldBounds().Left, 6);
        dialog.Close();

        // while typing text on the canvas
        c.Focus();
        s.Selection.Clear();
        s.SetTool("text");
        var click = c.TranslatePoint(new Point(300, 500), TopLevel.GetTopLevel(c)!).GetValueOrDefault();
        w.MouseDown(click, MouseButton.Left);
        w.MouseUp(click, MouseButton.Left);
        Assert.True(s.IsTextEditing);
        foreach (var key in new[] { Key.L, Key.R, Key.N })
        {
            Press(w, key);
            w.KeyTextInput(key.ToString().ToLowerInvariant());
        }
        Assert.True(s.IsTextEditing);
        Assert.Equal("lrn", Assert.Single(s.Document.ActiveLayer.Children.OfType<TextObject>()).Text);
        Assert.Equal(200, a.GetWorldBounds().Left, 6);
        Assert.Equal(500, b.GetWorldBounds().Left, 6);
        w.Close();
    }

    // ---- the menu ----

    [AvaloniaFact]
    public void The_Arrange_menu_has_the_Align_submenu_Mirror_Locked_and_Unlock_All_with_the_keys_shown()
    {
        var (w, _, _, _) = OpenWithTwo();
        var arrange = Arrange(w);
        var align = arrange.Items.OfType<MenuItem>().Single(m => m.Header?.ToString() == "Alig_n");
        var items = align.Items.OfType<MenuItem>().ToList();
        Assert.Equal(["Align _Left", "Align C_enter", "Align _Right", "Align _Top Edge", "Align _Vertical Center", "Align _Bottom Edge",
                      "_Horizontal Center of Layout", "V_ertical Center of Layout", "Cent_er of Layout"], items.Select(i => i.Header?.ToString()!).ToArray());
        string? Gesture(string header) => items.Single(i => i.Header?.ToString() == header).InputGesture?.ToString();
        Assert.Equal("L", Gesture("Align _Left"));
        Assert.Equal("N", Gesture("Align C_enter"));
        Assert.Equal("R", Gesture("Align _Right"));
        Assert.Null(Gesture("Align _Top Edge"));                   // no other key was wired
        var headers = arrange.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()).ToList();
        foreach (var h in new[] { "Loc_ked", "Unlock A_ll", "Horizontal _Mirror", "Vertical Mi_rror" }) Assert.Contains(h, headers);
        w.Close();
    }

    [AvaloniaFact]
    public void The_menu_items_run_the_commands_and_are_enabled_exactly_when_they_would_work()
    {
        var (w, _, a, b) = OpenWithTwo();
        var arrange = Arrange(w);
        MenuItem Find(string header) =>
            arrange.Items.OfType<MenuItem>().SelectMany(m => m.Header?.ToString() == "Alig_n" ? m.Items.OfType<MenuItem>().Append(m) : [m]).First(m => m.Header?.ToString() == header);
        void Open(MenuItem m) => m.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
        void Click(string header) { Find(header).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs(); }

        Open(arrange); Open(Find("Alig_n"));
        Assert.True(Find("Align _Top Edge").IsEnabled);
        Assert.False(Find("Unlock A_ll").IsEnabled);                // nothing is locked yet

        Click("Align _Top Edge");
        Assert.Equal(200, b.GetWorldBounds().Top, 6);
        Click("Horizontal _Mirror");
        Assert.Equal("Mirror Horizontal", w.Session.History.UndoDescription);
        Click("Loc_ked");
        Assert.True(a.Locked && b.Locked);
        Open(arrange); Open(Find("Alig_n"));
        Assert.True(Find("Loc_ked").IsChecked);                     // checked while the selection is locked
        Assert.True(Find("Unlock A_ll").IsEnabled);
        Assert.False(Find("Align _Top Edge").IsEnabled);            // locked objects can't be aligned
        Assert.False(Find("Horizontal _Mirror").IsEnabled);
        Click("Unlock A_ll");
        Assert.False(a.Locked || b.Locked);

        w.Session.Selection.Clear();
        Dispatcher.UIThread.RunJobs();
        Open(arrange); Open(Find("Alig_n"));
        Assert.False(Find("Align _Left").IsEnabled);                // no selection: nothing to align
        Assert.False(Find("Horizontal _Mirror").IsEnabled);
        Assert.False(Find("Loc_ked").IsEnabled);
        w.Close();
    }

    // ---- the Context Bar (R26: only valid choices) ----

    [AvaloniaFact]
    public void The_Context_Bar_shows_only_the_Arrange_buttons_that_would_work()
    {
        var (w, _) = Open();
        var a = AddRect(w, 200, 200, 100); var b = AddRect(w, 500, 320, 60);
        var s = w.Session;
        var bar = Bar(w);
        foreach (var mode in Enum.GetValues<AlignMode>()) Assert.False(bar.AlignButton(mode).IsVisible);   // nothing selected
        Assert.False(bar.MirrorH.IsVisible || bar.MirrorV.IsVisible || bar.CenterLayout.IsVisible || bar.Lock.IsVisible);

        s.Selection.Set(a);                                          // one object: Mirror, Lock and "In Layout", but nothing to align with
        bar.Refresh();
        foreach (var mode in Enum.GetValues<AlignMode>()) Assert.False(bar.AlignButton(mode).IsVisible);
        Assert.True(bar.MirrorH.IsVisible && bar.MirrorV.IsVisible && bar.CenterLayout.IsVisible && bar.Lock.IsVisible);

        s.Selection.Set([a, b]);                                     // two objects: Align too
        bar.Refresh();
        foreach (var mode in Enum.GetValues<AlignMode>()) Assert.True(bar.AlignButton(mode).IsVisible);

        bar.AlignButton(AlignMode.Left).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(200, b.GetWorldBounds().Left, 6);
        bar.MirrorV.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Mirror Vertical", s.History.UndoDescription);
        bar.CenterLayout.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(s.Document.Page.Bounds.Center.X, s.Selection.GetBounds().Center.X, 6);

        Assert.DoesNotContain("on", bar.Lock.Classes);          // not pressed: nothing locked
        bar.Lock.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(a.Locked && b.Locked);
        bar.Refresh();
        Assert.Contains("on", bar.Lock.Classes);            // pressed: a click unlocks
        Assert.True(bar.Lock.IsVisible);
        Assert.False(bar.MirrorH.IsVisible || bar.CenterLayout.IsVisible);   // a locked selection can't be changed: those buttons are gone
        foreach (var mode in Enum.GetValues<AlignMode>()) Assert.False(bar.AlignButton(mode).IsVisible);
        Assert.False(bar.X.IsEffectivelyVisible || bar.W.IsEffectivelyVisible);                 // and so are the position and size fields
        bar.Lock.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(a.Locked || b.Locked);
        w.Close();
    }

    [AvaloniaFact]
    public void A_single_text_offers_only_the_horizontal_alignments_it_does_not_have_yet()
    {
        var (w, _) = Open();
        var t = new TextObject("Hello", "Arial", 100) { Transform = Affine2.Translation(300, 400) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, t));
        w.Session.Selection.Set(t);
        var bar = Bar(w);
        Assert.False(bar.AlignButton(AlignMode.Left).IsVisible);     // already left aligned
        Assert.True(bar.AlignButton(AlignMode.Center).IsVisible);
        Assert.True(bar.AlignButton(AlignMode.Right).IsVisible);
        Assert.False(bar.AlignButton(AlignMode.Top).IsVisible);
        Press(w, Key.R);
        Assert.Equal(TextAlignment.Right, t.Alignment);
        w.Close();
    }

    // ---- the Layers Panel ----

    [AvaloniaFact]
    public void A_locked_object_is_marked_in_the_Layers_Panel_and_can_be_picked_there_to_unlock_it()
    {
        var (w, c) = Open();
        var a = AddRect(w, 200, 200, 100); var b = AddRect(w, 500, 320, 60);
        var s = w.Session;
        s.Selection.Set(a);
        s.ToggleLockSelection();
        s.Selection.Clear();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();

        var list = w.FindControl<ListBox>("LayersList")!;
        var rows = list.Items.Cast<string>().ToList();
        Assert.Equal(3, rows.Count);                                  // the layer, b, a
        Assert.DoesNotContain("Locked", rows[1]);
        Assert.Contains("Locked", rows[2]);

        // the canvas ignores it...
        w.MouseMove(P(w, c, new Vec2(250, 230)));
        w.MouseDown(P(w, c, new Vec2(250, 230)), MouseButton.Left);
        w.MouseUp(P(w, c, new Vec2(250, 230)), MouseButton.Left);
        Assert.True(s.Selection.IsEmpty);

        // ...the panel does not: pick it there and unlock it with the Context Bar's button
        list.SelectedIndex = 2;
        Assert.Equal([a], s.Selection.Items);
        Dispatcher.UIThread.RunJobs();
        var bar = Bar(w);
        Assert.Contains("on", bar.Lock.Classes);            // pressed: a click unlocks
        bar.Lock.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.False(a.Locked);
        Assert.DoesNotContain(list.Items.Cast<string>(), r => r.Contains("Locked"));
        w.Close();
    }

    [AvaloniaFact]
    public void Locked_state_is_part_of_the_undo_history_and_the_title_star()
    {
        var (w, _) = Open();
        var a = AddRect(w, 200, 200, 100);
        w.Session.History.MarkSaved();
        w.Session.Selection.Set(a);
        w.Session.ToggleLockSelection();
        Dispatcher.UIThread.RunJobs();
        Assert.EndsWith("* - DeSign Mage", w.Title);
        Press(w, Key.Z);
        Assert.False(a.Locked);
        w.Close();
    }
}
