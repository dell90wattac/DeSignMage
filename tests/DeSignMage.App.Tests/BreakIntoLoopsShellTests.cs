using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.TestSupport;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Arrange ▸ Break Into Loops, from the menu (it has no keyboard shortcut: the user approves shortcuts).</summary>
public class BreakIntoLoopsShellTests
{
    private static MainWindow Show()
    {
        var w = new MainWindow(new StartupOptions()) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        return w;
    }

    private static MenuItem ArrangeMenu(MainWindow w) =>
        w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().First(m => (m.Header as string)?.Contains("Arrange") == true);

    private static MenuItem LoopsItem(MainWindow w) =>
        ArrangeMenu(w).Items.OfType<MenuItem>().First(m => (m.Header as string)?.Contains("Loops") == true);

    /// <summary>Opens the Arrange menu the way a click would, which refreshes the enabled state of its items.</summary>
    private static void OpenArrange(MainWindow w) => ArrangeMenu(w).RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));

    private static PathObject Paw(MainWindow w, int loops)
    {
        var b = new PathBuilder();
        for (int i = 0; i < loops; i++)
            b.MoveTo(i * 60, 0).CubicTo(i * 60, -20, i * 60 + 40, -20, i * 60 + 40, 0).LineTo(i * 60 + 40, 40).LineTo(i * 60, 40).Close();
        var p = new PathObject(b.Build()) { Style = ObjectStyle.FillOnly(DocColor.FromHex("#FFBC11")), Transform = Affine2.Translation(100, 200) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, p));
        return p;
    }

    [AvaloniaFact]
    public void The_Arrange_menu_has_Break_Into_Loops_without_any_shortcut()
    {
        var w = Show();
        var item = LoopsItem(w);

        Assert.Null(item.InputGesture);
        Assert.DoesNotContain(KeyMap.Default, k => k.CommandId == "arrange.breakIntoLoops");
    }

    [AvaloniaFact]
    public void The_item_is_enabled_only_when_a_multi_loop_graphic_is_selected()
    {
        var w = Show();

        OpenArrange(w);
        Assert.False(LoopsItem(w).IsEnabled);                                   // nothing selected

        var single = Paw(w, 1);
        w.Session.Selection.Set(single);
        OpenArrange(w);
        Assert.False(LoopsItem(w).IsEnabled);                                   // one loop only

        var paw = Paw(w, 5);
        w.Session.Selection.Set(paw);
        OpenArrange(w);
        Assert.True(LoopsItem(w).IsEnabled);
    }

    [AvaloniaFact]
    public void Choosing_it_splits_the_paw_into_five_selectable_loops_and_undo_joins_them_again()
    {
        var w = Show();
        var paw = Paw(w, 5);
        w.Session.Selection.Set(paw);
        int before = w.Session.Document.ObjectCount;
        var existing = w.Session.Document.AllObjects().ToHashSet();
        OpenArrange(w);

        LoopsItem(w).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before - 1 + 5, w.Session.Document.ObjectCount);
        Assert.Empty(w.Session.Selection.Items);                    // splitting deselects everything, like Ungroup
        w.Session.Selection.Set(w.Session.Document.AllObjects().Where(o => !existing.Contains(o)).ToList());
        Assert.Equal(5, w.Session.Selection.Count);
        Assert.False(w.Session.Document.Contains(paw));
        Assert.All(w.Session.Selection.Items, o => Assert.Equal("#FFBC11", ((SolidPaint)o.Style.Fill!).Color.ToHex()));

        // Each loop can now be moved on its own.
        var one = w.Session.Selection.Items[0];
        var others = w.Session.Selection.Items.Skip(1).Select(o => o.GetWorldBounds()).ToList();
        var oneBefore = one.GetWorldBounds();
        w.Session.Selection.Set(one);
        w.Session.History.Execute(TransformObjectsCommand.ApplyWorld("Move", [one], Affine2.Translation(30, 0)));
        Assert.Equal(oneBefore.Left + 30, one.GetWorldBounds().Left, 6);
        Assert.Equal(others, w.Session.Document.Layers[0].Children.Where(o => o != one).Skip(0).TakeLast(4).Select(o => o.GetWorldBounds()));
        w.Session.Undo();

        w.Session.Undo();
        Assert.True(w.Session.Document.Contains(paw));
        Assert.Equal(before, w.Session.Document.ObjectCount);
    }

    [AvaloniaFact]
    public async Task An_imported_multi_loop_shape_breaks_into_its_pad_and_four_toes()
    {
        Assert.SkipUnless(LocalData.Has("layouts/sample.lyt"), "needs local-testdata/layouts/sample.lyt (see the README)");
        var w = Show();
        w.DialogOverride = (_, _) => Task.FromResult(true);
        var sample = LocalData.Sample;
        await w.OpenFileAsync(sample);

        // "Loop 1" with five loops is the large paw on the pasteboard. It sits inside groups, so take it out first.
        var paw = w.Session.Document.AllObjects().OfType<PathObject>()
            .First(o => o.Name == "Loop 1" && PathEditing.ToLoops(o.Geometry).Count == 5);
        Assert.True(BreakIntoLoopsCommand.CanBreak(paw));

        var existing = w.Session.Document.AllObjects().ToHashSet();
        w.Session.Selection.Set(paw);
        w.Session.BreakIntoLoopsSelection();

        Assert.Empty(w.Session.Selection.Items);                    // splitting deselects everything, like Ungroup
        w.Session.Selection.Set(w.Session.Document.AllObjects().Where(o => !existing.Contains(o)).ToList());
        Assert.Equal(5, w.Session.Selection.Count);
        var pieces = w.Session.Selection.Items.Cast<PathObject>().ToList();
        Assert.Equal(1, pieces.Count(p => p.Geometry.HasArcs));                 // the pad is made of arcs
        Assert.Equal(4, pieces.Count(p => p.Geometry.Verbs.Contains(PathVerb.CubicTo) && !p.Geometry.HasArcs));   // the toes are Béziers
        Assert.All(pieces, p => Assert.DoesNotContain(PathVerb.LineTo, p.Geometry.Verbs));
    }

}
