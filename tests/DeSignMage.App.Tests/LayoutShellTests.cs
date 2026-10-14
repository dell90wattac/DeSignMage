using Avalonia.Controls;
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
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Layout ▸ Guides ▸, Layout Properties... and Size to Fit in the real window.</summary>
public class LayoutShellTests : IDisposable
{
    // The "new layouts" size is a program setting shared by every test in the process: put it back.
    public void Dispose()
    {
        AppSettings.Store.Set(MainWindow.DefaultWidthSetting, 24.0);
        AppSettings.Store.Set(MainWindow.DefaultHeightSetting, 12.0);
    }

    private static MainWindow Open()
    {
        var w = new MainWindow(new StartupOptions()) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        return w;
    }

    private static void Pump(Func<bool> done, int timeoutMs = 15000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && watch.ElapsedMilliseconds < timeoutMs)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static string Strip(object? header) => ((string)header!).Replace("_", "");

    private static MenuItem LayoutMenu(MainWindow w) =>
        w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => Strip(m.Header) == "Layout");

    private static AppCommand Command(MainWindow w, string id)
    {
        IEnumerable<MenuItem> Walk(MenuItem m) => m.Items.OfType<MenuItem>().SelectMany(i => new[] { i }.Concat(Walk(i)));
        return (AppCommand)Walk(LayoutMenu(w)).First(m => m.Tag is AppCommand c && c.Id == id).Tag!;
    }

    private static DocObject AddRect(MainWindow w, double x, double y, double width, double height)
    {
        var r = new RectangleObject(width, height) { Transform = Affine2.Translation(x, y), Style = ObjectStyle.FillOnly(DocColor.Black) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, r));
        return r;
    }

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    // ---- the menu ----

    [AvaloniaFact]
    public void The_Layout_menu_has_a_Guides_submenu_Layout_Properties_and_Size_to_Fit_without_shortcuts()
    {
        var w = Open();
        var layout = LayoutMenu(w);
        var guides = layout.Items.OfType<MenuItem>().Single(m => Strip(m.Header) == "Guides");
        var ids = guides.Items.OfType<MenuItem>().Select(m => ((AppCommand)m.Tag!).Id).ToArray();
        Assert.Equal(MainWindow.GuideMenuCommandIds.Where(i => i != "-"), ids);
        Assert.Equal(["Add Guide At Text Cap Height", "Add Guide At Text Baseline"], guides.Items.OfType<MenuItem>().Take(2).Select(m => Strip(m.Header)).ToArray());
        Assert.Equal(2, guides.Items.OfType<Separator>().Count());

        Assert.Equal("Layout Properties...", Command(w, "layout.properties").Header.Replace("_", ""));
        Assert.Equal("Size to Fit", Command(w, "layout.sizeToFit").Header.Replace("_", ""));
        foreach (var id in MainWindow.GuideMenuCommandIds.Where(i => i != "-").Concat(["layout.properties", "layout.sizeToFit"]))
            Assert.Empty(Command(w, id).Gestures);   // no shortcuts until the user chooses them
        w.Close();
    }

    [AvaloniaFact]
    public void Only_the_guide_commands_that_apply_are_enabled()
    {
        var w = Open();
        Assert.True(Command(w, "layout.guide.edges").CanExecute());       // nothing selected: the page
        Assert.False(Command(w, "layout.guide.baseline").CanExecute());  // no text
        Assert.False(Command(w, "layout.sizeToFit").CanExecute());        // empty design

        var r = AddRect(w, 100, 100, 200, 50);
        w.Session.Selection.Set(r);
        Assert.False(Command(w, "layout.guide.capHeight").CanExecute());  // a rectangle is not text
        Assert.True(Command(w, "layout.sizeToFit").CanExecute());

        var t = new TextObject("Hi", "Arial", 36) { Transform = Affine2.Translation(10, 400) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, t));
        w.Session.Selection.Set(t);
        Assert.True(Command(w, "layout.guide.capHeight").CanExecute());
        Assert.True(Command(w, "layout.guide.baseline").CanExecute());
        w.Close();
    }

    // ---- guides ----

    [AvaloniaFact]
    public void The_menu_adds_edge_guides_around_the_selection_as_one_undo_step_and_ctrl_Z_removes_them()
    {
        var w = Open();
        var r = AddRect(w, 100, 100, 200, 50);
        w.Session.Selection.Set(r);
        int steps = w.Session.History.UndoList.Count;

        Command(w, "layout.guide.edges").Execute();
        Assert.Equal(4, w.Session.Document.Guides.Count);
        Assert.Equal(steps + 1, w.Session.History.UndoList.Count);

        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Empty(w.Session.Document.Guides);
        w.Close();
    }

    [AvaloniaFact]
    public void A_guide_from_the_menu_can_be_picked_locked_and_removed_like_any_other()
    {
        var w = Open();
        w.Session.Selection.Set(AddRect(w, 100, 100, 200, 50));
        Command(w, "layout.guide.centerV").Execute();
        var g = Assert.Single(w.Session.Document.Guides);
        Assert.Equal(200, g.Origin.X, 9);

        w.Session.SelectedGuide = g;
        Assert.Same(g, w.Session.SelectedGuide);
        Assert.Contains(w.GetVisualDescendants().OfType<Button>(), b => b.Name == "GuideLock");   // the guide Context Bar is up
        w.Session.SetGuideLocked(g, true);
        w.Session.RemoveGuide(g);
        Assert.Empty(w.Session.Document.Guides);
        w.Close();
    }

    // ---- Layout Properties ----

    [AvaloniaFact]
    public void The_dialog_opens_on_the_current_page_size_and_switches()
    {
        var w = Open();
        Command(w, "layout.properties").Execute();
        Pump(() => w.OpenLayoutProperties is not null);
        var d = w.OpenLayoutProperties!;
        Assert.Equal("24", d.WidthBox.Text);
        Assert.Equal("12", d.HeightBox.Text);
        Assert.True(d.ShowGuidesBox.IsChecked);
        Assert.True(d.SnapBox.IsChecked);
        Assert.False(d.DefaultBox.IsChecked);
        Assert.True(d.OkButton.IsEnabled);
        Assert.Equal("= 609.6 x 304.8 mm", d.MetricNote.Text);
        Click(d.CancelButton);
        Pump(() => w.OpenLayoutProperties is null);
        w.Close();
    }

    [AvaloniaFact]
    public void OK_applies_the_new_size_as_one_undoable_step_and_nothing_moves()
    {
        var w = Open();
        var r = AddRect(w, 100, 700, 80, 60);
        var bounds = r.GetWorldBounds();
        int steps = w.Session.History.UndoList.Count;

        Command(w, "layout.properties").Execute();
        Pump(() => w.OpenLayoutProperties is not null);
        var d = w.OpenLayoutProperties!;
        d.WidthBox.Text = "10";
        d.HeightBox.Text = "8.5";
        Click(d.OkButton);
        Pump(() => w.OpenLayoutProperties is null);

        var page = w.Session.Document.Page;
        Assert.Equal(720, page.Width, 9);
        Assert.Equal(612, page.Height, 9);
        Assert.Equal(864, page.Y + page.Height, 9);                     // the bottom stays where it was
        Assert.Equal(bounds, r.GetWorldBounds());
        Assert.Equal(steps + 1, w.Session.History.UndoList.Count);
        Assert.Equal("Layout Properties", w.Session.History.UndoDescription);

        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Equal(PageSetup.FromInches(24, 12), w.Session.Document.Page);
        w.Close();
    }

    [AvaloniaFact]
    public void Cancel_and_bad_numbers_change_nothing()
    {
        var w = Open();
        int steps = w.Session.History.UndoList.Count;
        Command(w, "layout.properties").Execute();
        Pump(() => w.OpenLayoutProperties is not null);
        var d = w.OpenLayoutProperties!;

        d.WidthBox.Text = "abc";
        Assert.False(d.OkButton.IsEnabled);
        Assert.True(d.ErrorText.IsVisible);
        d.WidthBox.Text = "0.1";
        Assert.False(d.OkButton.IsEnabled);
        d.WidthBox.Text = "5000";
        Assert.False(d.OkButton.IsEnabled);
        d.WidthBox.Text = "9";
        Assert.True(d.OkButton.IsEnabled);
        Assert.False(d.ErrorText.IsVisible);

        Click(d.CancelButton);
        Pump(() => w.OpenLayoutProperties is null);
        Assert.Equal(PageSetup.FromInches(24, 12), w.Session.Document.Page);
        Assert.Equal(steps, w.Session.History.UndoList.Count);
        w.Close();
    }

    [AvaloniaFact]
    public void The_guide_switches_in_the_dialog_set_Show_Guidelines_and_Snap_to_Guidelines()
    {
        var w = Open();
        Command(w, "layout.properties").Execute();
        Pump(() => w.OpenLayoutProperties is not null);
        var d = w.OpenLayoutProperties!;
        d.ShowGuidesBox.IsChecked = false;
        d.SnapBox.IsChecked = false;
        Click(d.OkButton);
        Pump(() => w.OpenLayoutProperties is null);
        Assert.False(w.Session.ShowGuides);
        Assert.False(w.Session.SnapToGuides);
        Assert.False(w.Session.History.CanUndo);   // view switches are not document edits; the unchanged size made no step
        w.Close();
    }

    [AvaloniaFact]
    public void Use_this_size_for_new_layouts_is_remembered_and_the_next_new_design_starts_with_it()
    {
        var w = Open();
        Command(w, "layout.properties").Execute();
        Pump(() => w.OpenLayoutProperties is not null);
        var d = w.OpenLayoutProperties!;
        d.WidthBox.Text = "36";
        d.HeightBox.Text = "18";
        d.DefaultBox.IsChecked = true;
        Click(d.OkButton);
        Pump(() => w.OpenLayoutProperties is null);

        Assert.Equal(PageSetup.FromInches(36, 18), DemoContent.NewDocument().Page);

        // Open again: the box is ticked for this size; unticking it goes back to the built-in 24 x 12.
        Command(w, "layout.properties").Execute();
        Pump(() => w.OpenLayoutProperties is not null);
        d = w.OpenLayoutProperties!;
        Assert.True(d.DefaultBox.IsChecked);
        d.DefaultBox.IsChecked = false;
        Click(d.OkButton);
        Pump(() => w.OpenLayoutProperties is null);
        Assert.Equal(PageSetup.FromInches(24, 12), DemoContent.NewDocument().Page);
        w.Close();
    }

    [AvaloniaFact]
    public void A_damaged_default_size_setting_never_makes_an_unusable_page()
    {
        AppSettings.Store.Set(MainWindow.DefaultWidthSetting, 0.0);
        AppSettings.Store.Set(MainWindow.DefaultHeightSetting, -4.0);
        Assert.Equal(PageSetup.FromInches(24, 12), DemoContent.NewDocument().Page);
    }

    // ---- Size to Fit ----

    [AvaloniaFact]
    public void Size_to_fit_from_the_menu_fits_the_artwork_and_ctrl_Z_brings_the_old_page_back()
    {
        var w = Open();
        var a = AddRect(w, 100, 100, 200, 50);
        var before = a.GetWorldBounds();

        Command(w, "layout.sizeToFit").Execute();
        Assert.Equal(new Rect2(100, 100, 300, 150), w.Session.Document.Page.Bounds);
        Assert.Equal(before, a.GetWorldBounds());

        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Equal(PageSetup.FromInches(24, 12), w.Session.Document.Page);
        w.Close();
    }
}
