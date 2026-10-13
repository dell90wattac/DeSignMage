using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Canvas;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>R26: the Context Bar shows only valid choices. A control that would do nothing is hidden, never just greyed.</summary>
public class ContextBarValidChoicesTests
{
    private static (MainWindow W, DesignCanvas C) Open(bool guides = false, double width = 1700)
    {
        var w = new MainWindow(new StartupOptions(Guides: guides)) { Width = width, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        var c = w.FindControl<DesignCanvas>("Surface")!;
        c.Focus();
        return (w, c);
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    private static Control BarContent(MainWindow w) => (Control)w.FindControl<ContentControl>("ContextBarHost")!.Content!;

    private static SelectionContextBar SelectionBar(MainWindow w) => Assert.IsType<SelectionContextBar>(BarContent(w));

    private static Point P(MainWindow w, DesignCanvas c, Vec2 doc)
    {
        var s = w.Session.Viewport.ToScreen(doc);
        return c.TranslatePoint(new Point(s.X, s.Y), w).GetValueOrDefault();
    }

    private static RectangleObject AddRect(MainWindow w, double x, double y, double size = 80)
    {
        var r = new RectangleObject(size, size) { Transform = Affine2.Translation(x, y), Style = ObjectStyle.FillOnly(DocColor.FromHex("#F2B705")) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, r));
        return r;
    }

    private static TextObject AddText(MainWindow w, string text, double x, double y)
    {
        var t = new TextObject(text, "Segoe UI", 24) { Transform = Affine2.Translation(x, y), Style = ObjectStyle.FillOnly(DocColor.Black) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, t));
        return t;
    }

    private static void Select(MainWindow w, params DocObject[] items)
    {
        w.Session.Selection.Set(items);
        Flush();
        w.CaptureRenderedFrame();
    }

    /// <summary>The four group buttons that are on screen right now (by what they say).</summary>
    private static string[] ShownGroupButtons(SelectionContextBar bar) =>
    [
        .. new (string Name, Button Button)[] { ("Combine", bar.Combine), ("Break Apart", bar.BreakApart), ("Group", bar.Group), ("Ungroup", bar.Ungroup) }
            .Where(b => b.Button.IsVisible).Select(b => b.Name),
    ];

    /// <summary>Every control of the bar that is on screen but greyed: there must be none.</summary>
    private static string[] ShownButNotUsable(Control bar) =>
    [
        .. bar.GetVisualDescendants().OfType<Control>()
            .Where(c => c is Button or CheckBox or RadioButton or ToggleButton or TextBox or ComboBox)
            .Where(c => c.IsEffectivelyVisible && !c.IsEffectivelyEnabled)
            .Select(c => $"{c.GetType().Name} {(c as ContentControl)?.Content ?? c.Name}"),
    ];

    // ---------------------------------------------------------------- Combine, Break Apart, Group, Ungroup

    [AvaloniaFact]
    public void Group_buttons_show_only_when_their_command_would_work()
    {
        var (w, _) = Open();
        var s = w.Session;
        var bar = SelectionBar(w);

        Select(w);                                                    // nothing selected
        Assert.Empty(ShownGroupButtons(bar));

        var a = AddRect(w, 100, 100);
        var b = AddRect(w, 240, 100);
        var c = AddRect(w, 380, 100);
        Select(w, a);                                                 // one plain shape: none of the four can do anything
        Assert.Empty(ShownGroupButtons(bar));

        Select(w, a, b);                                              // two shapes: Group and Combine
        Assert.Equal(["Combine", "Group"], ShownGroupButtons(bar));

        s.GroupSelection();                                           // the group is selected: Ungroup, and Break Apart (it breaks groups too); Group needs two things
        Flush();
        w.CaptureRenderedFrame();
        Assert.IsType<GroupObject>(Assert.Single(s.Selection.Items));
        Assert.Contains("Ungroup", ShownGroupButtons(bar));
        Assert.Contains("Break Apart", ShownGroupButtons(bar));
        Assert.DoesNotContain("Group", ShownGroupButtons(bar));

        var group = s.Selection.Items[0];
        Select(w, group, c);                                          // a group together with another object: a group is inside the selection
        Assert.Contains("Ungroup", ShownGroupButtons(bar));
        Assert.Contains("Group", ShownGroupButtons(bar));

        Select(w, c);
        s.SelectAll();                                                // everything
        Flush();
        w.CaptureRenderedFrame();
        s.CombineSelection();                                         // a combined object: Break Apart can undo it, Combine and Group cannot
        Flush();
        w.CaptureRenderedFrame();
        Assert.IsType<CombinedObject>(Assert.Single(s.Selection.Items));
        Assert.Contains("Break Apart", ShownGroupButtons(bar));
        Assert.DoesNotContain("Ungroup", ShownGroupButtons(bar));
        Assert.DoesNotContain("Group", ShownGroupButtons(bar));
        w.Close();
    }

    [AvaloniaFact]
    public void A_group_button_shows_exactly_when_its_command_can_run_and_is_never_greyed()
    {
        var (w, _) = Open();
        var s = w.Session;
        var bar = SelectionBar(w);
        var a = AddRect(w, 100, 100);
        var b = AddRect(w, 240, 100);
        AddText(w, "Hi", 100, 300);
        var selections = new List<Action>
        {
            () => Select(w),
            () => Select(w, a),
            () => Select(w, a, b),
            () => { Select(w, a, b); s.GroupSelection(); Flush(); w.CaptureRenderedFrame(); },
            () => { s.SelectAll(); Flush(); w.CaptureRenderedFrame(); },
        };
        foreach (var make in selections)
        {
            make();
            Assert.Equal(s.CanCombine, bar.Combine.IsVisible);
            Assert.Equal(s.CanBreakApart, bar.BreakApart.IsVisible);
            Assert.Equal(s.CanGroup, bar.Group.IsVisible);
            Assert.Equal(s.CanUngroup, bar.Ungroup.IsVisible);
            foreach (var button in new[] { bar.Combine, bar.BreakApart, bar.Group, bar.Ungroup })
                Assert.Equal(button.IsVisible, button.IsEnabled);                      // shown means usable, hidden means not
            Assert.Empty(ShownButNotUsable(bar));
        }
        w.Close();
    }

    [AvaloniaFact]
    public void The_selection_fields_show_only_while_something_is_selected()
    {
        var (w, _) = Open();
        var bar = SelectionBar(w);
        var boxes = new[] { bar.X, bar.Y, bar.W, bar.H };
        Select(w);
        Assert.All(boxes, t => Assert.False(t.IsEffectivelyVisible));
        Select(w, AddRect(w, 100, 100));
        Assert.All(boxes, t => Assert.True(t.IsEffectivelyVisible && t.IsEnabled));
        w.Close();
    }

    [AvaloniaFact]
    public void The_bar_keeps_its_height_and_the_canvas_stays_put_while_buttons_come_and_go()
    {
        var (w, _) = Open();
        var s = w.Session;
        var barBorder = w.FindControl<Border>("ContextBarBorder")!;
        var canvas = w.FindControl<DesignCanvas>("Surface")!;
        double h = barBorder.Bounds.Height;
        var canvasAt = canvas.TranslatePoint(default, w)!.Value;
        var a = AddRect(w, 100, 100);
        var b = AddRect(w, 240, 100);
        AddText(w, "Hi", 100, 300);
        foreach (var step in new Action[] { () => Select(w), () => Select(w, a), () => Select(w, a, b), () => s.GroupSelection(), () => s.SelectAll(), () => s.CombineSelection(), () => Select(w) })
        {
            step();
            Flush();
            w.CaptureRenderedFrame();
            Assert.Equal(h, barBorder.Bounds.Height, 0.01);
            Assert.Equal(canvasAt, canvas.TranslatePoint(default, w)!.Value);
            // the two rows stay the same height as well
            var rows = SelectionBar(w).GetVisualChildren().OfType<BarRow>().ToArray();
            Assert.All(rows, r => Assert.Equal(30 * UiScale.Scale, r.Bounds.Height, 1.0));
        }
        w.Close();
    }

    // ---------------------------------------------------------------- font controls

    [AvaloniaFact]
    public void Font_controls_show_for_text_itself_and_not_for_a_group_or_a_mixed_selection()
    {
        var (w, _) = Open();
        var s = w.Session;
        var bar = SelectionBar(w);
        var t1 = AddText(w, "One", 100, 300);
        var t2 = AddText(w, "Two", 300, 300);
        var r = AddRect(w, 100, 100);

        Select(w);
        Assert.False(bar.TextPanel.IsVisible);                        // nothing
        Select(w, r);
        Assert.False(bar.TextPanel.IsVisible);                        // a shape
        Select(w, t1);
        Assert.True(bar.TextPanel.IsVisible);                         // one text, selected directly
        Assert.True(bar.TextPanel.IsEffectivelyVisible);
        Select(w, t1, t2);
        Assert.True(bar.TextPanel.IsVisible);                         // two texts, nothing else
        Select(w, t1, r);
        Assert.False(bar.TextPanel.IsVisible);                        // text mixed with a shape
        Select(w, t1, t2, r);
        Assert.False(bar.TextPanel.IsVisible);

        Select(w, t1, r);
        s.GroupSelection();                                           // a group that holds text
        Flush();
        w.CaptureRenderedFrame();
        Assert.IsType<GroupObject>(Assert.Single(s.Selection.Items));
        Assert.False(bar.TextPanel.IsVisible);
        Assert.False(bar.TextPanel.FamilyBox.IsEffectivelyVisible);
        Assert.False(bar.TextPanel.SizeBox.IsEffectivelyVisible);

        Select(w, t2);                                                // back to a lone text: they return
        Assert.True(bar.TextPanel.IsVisible);
        Assert.True(bar.TextPanel.FamilyBox.IsEffectivelyVisible);
        w.Close();
    }

    [AvaloniaFact]
    public void While_text_is_being_edited_its_font_controls_show_and_Convert_to_Curves_only_with_text_to_convert()
    {
        var (w, c) = Open();
        var s = w.Session;
        s.SetTool("text");
        Flush();
        w.MouseDown(P(w, c, new Vec2(300, 300)), MouseButton.Left);
        w.MouseUp(P(w, c, new Vec2(300, 300)), MouseButton.Left);
        Flush();
        w.CaptureRenderedFrame();
        Assert.True(s.IsTextEditing);
        var bar = Assert.IsType<TextToolContextBar>(BarContent(w));
        Assert.True(bar.Panel.IsEffectivelyVisible);
        Assert.True(bar.Panel.SizeBox.IsEffectivelyVisible);
        Assert.True(bar.Panel.FamilyBox.IsEffectivelyVisible);
        Assert.Empty(ShownButNotUsable(bar));                         // no greyed Convert to Curves while there is nothing to convert
        w.Close();
    }

    // ---------------------------------------------------------------- the same rule everywhere else

    [AvaloniaFact]
    public void No_Context_Bar_shows_a_greyed_control_in_any_tool_or_selection()
    {
        var (w, c) = Open(guides: true);
        var s = w.Session;
        var failures = new List<string>();
        void Check(string where)
        {
            Flush();
            w.CaptureRenderedFrame();
            foreach (var item in ShownButNotUsable(BarContent(w))) failures.Add($"{where}: {item}");
        }

        Check("select, nothing");
        var a = AddRect(w, 200, 200, 300);
        var b = AddRect(w, 600, 200, 120);
        var t = AddText(w, "Text", 100, 600);
        Select(w, a); Check("select, one shape");
        Select(w, a, b); Check("select, two shapes");
        Select(w, t); Check("select, text");
        Select(w, a, t); Check("select, text and shape");

        s.SetTool("rectangle"); Check("rectangle");
        s.SetTool("text"); Check("text tool");

        Select(w, a);
        s.SetTool("vector"); Check("vector edit, nothing open");
        w.MouseDown(P(w, c, new Vec2(350, 350)), MouseButton.Left); w.MouseUp(P(w, c, new Vec2(350, 350)), MouseButton.Left);
        w.MouseDown(P(w, c, new Vec2(350, 350)), MouseButton.Left); w.MouseUp(P(w, c, new Vec2(350, 350)), MouseButton.Left);
        Check("vector edit, object open");
        var corner = P(w, c, new Vec2(500, 500));
        w.MouseDown(corner, MouseButton.Left); w.MouseUp(corner, MouseButton.Left);
        Check("vector edit, node selected");

        s.SetTool("select"); Select(w, a);
        s.SetTool("loop"); Check("loop edit, nothing picked");
        w.MouseDown(P(w, c, new Vec2(350, 350)), MouseButton.Left); w.MouseUp(P(w, c, new Vec2(350, 350)), MouseButton.Left);
        Check("loop edit, object open");

        s.SetTool("select"); Select(w, a);
        s.BeginOutline(DeSignMage.Core.Effects.OutlineKind.Outline); Check("outline");
        s.SetTool("select"); Select(w, a, b);
        s.BeginCookieCutter(); Check("cookie cutter");
        s.SetTool("select"); Select(w, a);
        s.BeginKnife(); Check("knife");
        s.SetTool("select");

        s.Selection.Clear();
        s.SelectedGuide = s.Document.Guides.First();
        Check("a ruler guide");
        Assert.Empty(failures);
        w.Close();
    }

    [AvaloniaFact]
    public void The_Vector_Edit_bar_shows_only_the_commands_that_apply_and_its_hint_when_none_does()
    {
        var (w, c) = Open();
        var a = AddRect(w, 200, 200, 300);
        w.Session.SetTool("vector");
        Flush();
        w.CaptureRenderedFrame();
        var bar = Assert.IsType<VectorEditContextBar>(BarContent(w));
        Button[] Buttons() => [.. bar.GetVisualDescendants().OfType<Button>()];
        Assert.NotEmpty(Buttons());
        Assert.DoesNotContain(Buttons(), b => b.IsVisible);                   // nothing open: no command could run
        Assert.Contains(bar.GetVisualDescendants().OfType<TextBlock>(), t => t.IsVisible && t.Text == w.Session.ActiveTool.Hint);   // the hint says what to do

        w.MouseDown(P(w, c, new Vec2(350, 350)), MouseButton.Left); w.MouseUp(P(w, c, new Vec2(350, 350)), MouseButton.Left);
        w.MouseDown(P(w, c, new Vec2(350, 350)), MouseButton.Left); w.MouseUp(P(w, c, new Vec2(350, 350)), MouseButton.Left);
        var corner = P(w, c, new Vec2(500, 500));
        w.MouseDown(corner, MouseButton.Left); w.MouseUp(corner, MouseButton.Left);
        Flush();
        w.CaptureRenderedFrame();
        var shown = Buttons().Where(b => b.IsVisible).ToArray();
        Assert.NotEmpty(shown);                                               // a node is selected: some commands apply
        Assert.All(shown, b => Assert.True(b.IsEnabled));
        Assert.True(Buttons().Any(b => !b.IsVisible));                        // and the ones that do not apply are gone, not greyed
        Assert.DoesNotContain(bar.GetVisualDescendants().OfType<TextBlock>(), t => t.IsVisible && t.Text == w.Session.ActiveTool.Hint);
        Assert.NotNull(a);
        w.Close();
    }
}
