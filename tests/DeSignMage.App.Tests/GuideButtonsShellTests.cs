using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Feedback;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>The three one-click guide buttons (Toolbox) and their Layout ▸ Guides ▸ twins: a guide on the top, bottom or left edge of the selection.</summary>
public class GuideButtonsShellTests
{
    private static MainWindow Open()
    {
        var w = new MainWindow(new StartupOptions()) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        return w;
    }

    private static Button Named(MainWindow w, string name) =>
        w.GetVisualDescendants().OfType<Button>().First(b => b.Name == name);

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static DocObject AddRect(MainWindow w, double x, double y, double width, double height)
    {
        var r = new RectangleObject(width, height) { Transform = Affine2.Translation(x, y), Style = ObjectStyle.FillOnly(DocColor.FromHex("#3366CC")) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, r));
        return r;
    }

    private static AppCommand Command(MainWindow w, string id)
    {
        IEnumerable<MenuItem> Walk(MenuItem m) => m.Items.OfType<MenuItem>().SelectMany(i => new[] { i }.Concat(Walk(i)));
        var layout = w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => ((string)m.Header!).Replace("_", "") == "Layout");
        return (AppCommand)Walk(layout).First(m => m.Tag is AppCommand c && c.Id == id).Tag!;
    }

    [AvaloniaFact]
    public void The_four_buttons_sit_in_a_group_after_the_color_mode_button_and_are_not_tools()
    {
        var w = Open();
        var host = w.FindControl<StackPanel>("ToolboxHost")!;
        var order = host.Children.Select(c => c.Name).OfType<string>().Where(n => n.Length > 0).ToArray();
        Assert.Equal(["ToolboxColorMode", "ToolboxGuideTop", "ToolboxGuideCenter", "ToolboxGuideBottom", "ToolboxGuideSide"], order);
        Assert.All(new[] { "ToolboxGuideTop", "ToolboxGuideCenter", "ToolboxGuideBottom", "ToolboxGuideSide" }, n => Assert.IsNotType<ToggleButton>(Named(w, n)));
        Assert.Equal(Toolbox.Buttons.Length, host.Children.OfType<ToggleButton>().Count() - 1);   // tools + color mode only
        w.Close();
    }

    [AvaloniaFact]
    public void The_buttons_are_disabled_with_nothing_selected_and_work_with_a_selection()
    {
        var w = Open();
        var top = Named(w, "ToolboxGuideTop"); var center = Named(w, "ToolboxGuideCenter");
        var bottom = Named(w, "ToolboxGuideBottom"); var side = Named(w, "ToolboxGuideSide");
        Assert.All(new[] { top, center, bottom, side }, b => Assert.False(b.IsEnabled));

        var r = AddRect(w, 100, 100, 200, 50);
        w.Session.Selection.Set(r);
        Dispatcher.UIThread.RunJobs();
        Assert.All(new[] { top, center, bottom, side }, b => Assert.True(b.IsEnabled));
        int steps = w.Session.History.UndoList.Count;

        Click(top);
        Click(center);
        Click(bottom);
        Click(side);
        var guides = w.Session.Document.Guides.ToArray();
        Assert.Equal(4, guides.Length);
        Assert.Equal(GuideOrientation.Horizontal, guides[0].Orientation);
        Assert.Equal(100, guides[0].Origin.Y, 9);
        Assert.Equal(GuideOrientation.Horizontal, guides[1].Orientation);
        Assert.Equal(125, guides[1].Origin.Y, 9);                       // through the middle of the box
        Assert.Equal(GuideOrientation.Horizontal, guides[2].Orientation);
        Assert.Equal(150, guides[2].Origin.Y, 9);
        Assert.Equal(GuideOrientation.Vertical, guides[3].Orientation);
        Assert.Equal(100, guides[3].Origin.X, 9);                       // the left edge
        Assert.Equal(steps + 4, w.Session.History.UndoList.Count);       // one undo step per click
        Assert.Same(r, Assert.Single(w.Session.Selection.Items));        // the object stays selected, so the next button can be clicked at once

        Click(center);                                                   // the same guide again adds nothing
        Assert.Equal(4, w.Session.Document.Guides.Count);
        Assert.Equal(steps + 4, w.Session.History.UndoList.Count);

        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Equal(3, w.Session.Document.Guides.Count);

        w.Session.Selection.Clear();
        Dispatcher.UIThread.RunJobs();
        Assert.All(new[] { top, center, bottom, side }, b => Assert.False(b.IsEnabled));
        w.Close();
    }

    [AvaloniaFact]
    public void A_group_and_several_objects_count_as_one_whole()
    {
        var w = Open();
        var a = AddRect(w, 100, 100, 50, 50); var b = AddRect(w, 300, 200, 80, 40);
        w.Session.Selection.Set([a, b]);
        Click(Named(w, "ToolboxGuideTop"));
        Click(Named(w, "ToolboxGuideCenter"));
        Click(Named(w, "ToolboxGuideBottom"));
        Click(Named(w, "ToolboxGuideSide"));
        Assert.Equal([100.0, 170.0, 240.0], w.Session.Document.Guides.Where(g => g.Orientation == GuideOrientation.Horizontal).Select(g => g.Origin.Y).OrderBy(y => y).ToArray());
        Assert.Equal(100, w.Session.Document.Guides.Single(g => g.Orientation == GuideOrientation.Vertical).Origin.X, 9);

        w.Session.Document.Guides.ToList().ForEach(g => w.Session.RemoveGuide(g));
        var group = w.Session.GroupObjects([a, b])!;
        w.Session.Selection.Set(group);
        Click(Named(w, "ToolboxGuideSide"));
        Assert.Equal(100, Assert.Single(w.Session.Document.Guides).Origin.X, 9);
        Click(Named(w, "ToolboxGuideCenter"));
        Assert.Equal(170, w.Session.Document.Guides.Single(g => g.Orientation == GuideOrientation.Horizontal).Origin.Y, 9);   // the group's middle
        w.Close();
    }

    [AvaloniaFact]
    public void The_Layout_Guides_menu_has_the_same_four_in_the_same_order_and_they_need_a_selection()
    {
        var w = Open();
        Assert.Equal(["layout.guide.selTop", "layout.guide.selCenter", "layout.guide.selBottom", "layout.guide.selSide"], MainWindow.GuideMenuCommandIds.Take(4));
        Assert.Equal("Guide at Center of Selection", Command(w, "layout.guide.selCenter").Header.Replace("_", ""));
        foreach (var id in new[] { "layout.guide.selTop", "layout.guide.selCenter", "layout.guide.selBottom", "layout.guide.selSide" })
        {
            Assert.False(Command(w, id).CanExecute());
            Assert.Empty(Command(w, id).Gestures);                        // no shortcuts
        }
        Assert.True(Command(w, "layout.guide.top").CanExecute());         // the older edge item still falls back to the page
        w.Session.Selection.Set(AddRect(w, 100, 100, 200, 50));
        Command(w, "layout.guide.selBottom").Execute();
        Assert.Equal(150, Assert.Single(w.Session.Document.Guides).Origin.Y, 9);
        Command(w, "layout.guide.selTop").Execute();
        Command(w, "layout.guide.selSide").Execute();
        Command(w, "layout.guide.selCenter").Execute();
        Assert.Equal(4, w.Session.Document.Guides.Count);
        Assert.Contains(w.Session.Document.Guides, g => g.Orientation == GuideOrientation.Horizontal && Math.Abs(g.Origin.Y - 125) < 1e-9);
        w.Close();
    }

    [AvaloniaFact]
    public void The_Tools_strip_is_unchanged_it_lists_tools_not_these_buttons()
    {
        var w = Open();
        var strip = w.FindControl<MenuStrip>("MenuStrip")!;
        Assert.DoesNotContain(strip.CommandsOf("Tools"), c => c.Id is not null && c.Id.StartsWith("layout.guide"));
        w.Close();
    }

    /// <summary>Pictures of the Toolbox with a selected object and the three guides, light and dark, when DSM_SCREENSHOTS names a folder (docs only).</summary>
    [AvaloniaFact]
    public void Screenshots_of_the_guide_buttons_for_notes()
    {
        var dir = Environment.GetEnvironmentVariable("DSM_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        var oldStore = AppSettings.Store;
        AppSettings.Store = new SettingsStore(null);
        try
        {
            Directory.CreateDirectory(dir);
            var w = Open();
            AddRect(w, 400, 300, 300, 140);
            w.Session.Selection.Set(w.Session.Document.ActiveLayer.Children.Last());
            Click(Named(w, "ToolboxGuideTop"));
            Click(Named(w, "ToolboxGuideCenter"));
            Click(Named(w, "ToolboxGuideSide"));
            Dispatcher.UIThread.RunJobs();
            foreach (bool dark in new[] { false, true })
            {
                AppTheme.SetDarkMenus(dark, remember: false);
                Dispatcher.UIThread.RunJobs();
#pragma warning disable CS0618
                w.CaptureRenderedFrame()!.Save(System.IO.Path.Combine(dir, $"guide-buttons-toolbox-{(dark ? "dark" : "light")}.png"));
#pragma warning restore CS0618
            }
            AppTheme.SetDarkMenus(false, remember: false);
            w.Close();
        }
        finally { AppSettings.Store = oldStore; }
    }
}
