using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Vectorize;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Image â–¸ Vectorize in the real window: menu, options dialog with live preview, undoable insert.</summary>
public class VectorizeShellTests
{
    private static RasterImage SquareAndDisc()
    {
        var img = RasterImage.Solid(120, 90, 255, 255, 255);
        for (int y = 15; y < 50; y++) for (int x = 15; x < 50; x++) img.SetPixel(x, y, 220, 30, 30);
        for (int y = 0; y < 90; y++)
            for (int x = 0; x < 120; x++)
                if ((x - 85) * (x - 85) + (y - 50) * (y - 50) <= 28 * 28) img.SetPixel(x, y, 30, 60, 200);
        return img;
    }

    private static void Pump(Func<bool> done, int timeoutMs = 15000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && watch.ElapsedMilliseconds < timeoutMs)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Image_menu_has_Vectorize()
    {
        var w = new MainWindow(new StartupOptions()) { Width = 1200, Height = 800 };
        w.Show();
        Dispatcher.UIThread.RunJobs();

        var menu = w.FindControl<Menu>("MainMenu")!;
        var image = menu.Items.OfType<MenuItem>().Single(m => ((string)m.Header!).Replace("_", "") == "Image");
        var item = image.Items.OfType<MenuItem>().First();   // Image menu starts with Vectorize
        Assert.Equal("_Vectorize...", item.Header);
        Assert.True(((AppCommand)item.Tag!).CanExecute());
        w.Close();
    }

    [AvaloniaFact]
    public void Apply_inserts_one_group_and_undo_removes_it()
    {
        var w = new MainWindow(new StartupOptions()) { Width = 1200, Height = 800 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var session = w.Session;
        var result = Vectorizer.Trace(SquareAndDisc(), new VectorizeOptions { MinWorkingSize = 0 });

        var group = VectorizeFlow.Apply(session, result, VectorizeFlow.CenteredOnPage(SquareAndDisc(), session.Document.Page));

        Assert.Equal(3, group.Children.Count);
        Assert.Equal(group.Children, session.Document.ActiveLayer.Children);   // the three parts are the drawn objects; the group is membership only
        Assert.Same(group, Assert.Single(session.Selection.Items));
        var page = session.Document.Page.Bounds;
        var b = group.GetWorldBounds();
        Assert.True(page.Contains(b.TopLeft) && page.Contains(b.BottomRight));
        Assert.Equal("Vectorize", session.History.UndoList[^1].Description);

        session.Undo();
        Assert.Empty(session.Document.ActiveLayer.Children);
        session.Redo();
        Assert.Equal(3, Assert.IsType<GroupObject>(Assert.Single(session.Document.ActiveLayer.Children.Select(o => o.TopLevel).Distinct())).Children.Count);
        w.Close();
    }

    [AvaloniaFact]
    public void Dialog_previews_live_and_accept_returns_the_full_trace()
    {
        var owner = new Window { Width = 1000, Height = 700 };
        owner.Show();
        var dialog = new VectorizeDialog(SquareAndDisc(), "test.png");
        dialog.Show(owner);

        Pump(() => dialog.PreviewsCompleted >= 1);
        Assert.NotNull(dialog.LastPreview);
        Assert.Equal(3, dialog.LastPreview!.Shapes.Count);
        Assert.Equal(1, dialog.PreviewsCompleted);

        // The first preview can be superseded; a change of option restarts it.
        var options = dialog.CurrentOptions();
        Assert.Equal(VectorizeMode.Color, options.Mode);

        _ = dialog.AcceptAsync();
        Pump(() => dialog.Result is not null);
        Assert.NotNull(dialog.Result);
        Assert.Equal(120, dialog.Result!.Width);
        Assert.Equal(3, dialog.Result.Shapes.Count);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Cancel_stops_the_full_trace_and_keeps_the_dialog_open()
    {
        var big = RasterImage.Solid(900, 900, 255, 255, 255);
        for (int y = 0; y < 900; y++)
            for (int x = 0; x < 900; x++)
                if (((x / 7) + (y / 5)) % 3 == 0) big.SetPixel(x, y, (byte)(x % 256), (byte)(y % 256), 90);
        var owner = new Window { Width = 1000, Height = 700 };
        owner.Show();
        var dialog = new VectorizeDialog(big, "big.png");
        dialog.Show(owner);
        Pump(() => dialog.PreviewsCompleted >= 1);

        var accept = dialog.AcceptAsync();
        Assert.True(dialog.IsTracing);
        Assert.False(dialog.VectorizeButton.IsEnabled);                         // no second trace while one runs
        dialog.CancelButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await accept.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.False(dialog.IsTracing);
        Assert.Null(dialog.Result);                                             // nothing was accepted
        Assert.True(dialog.IsVisible);                                          // the dialog is still there for another try
        Assert.True(dialog.VectorizeButton.IsEnabled);
        owner.Close();
    }

    [AvaloniaFact]
    public void Dialog_screenshot()
    {
        // Development aid: set DSM_SCREENSHOTS to a folder to save a picture of the dialog there.
        var folder = Environment.GetEnvironmentVariable("DSM_SCREENSHOTS");
        if (string.IsNullOrEmpty(folder)) return;
        var sample = Environment.GetEnvironmentVariable("DSM_SCREENSHOT_IMAGE");
        var source = sample is not null ? DeSignMage.Vectorize.Imaging.SkiaImaging.Decode(sample) : SquareAndDisc();

        var owner = new Window { Width = 1000, Height = 700 };
        owner.Show();
        var dialog = new VectorizeDialog(source, sample is null ? "test.png" : Path.GetFileName(sample)) { Width = 920, Height = 640 };
        dialog.Show(owner);
        Pump(() => dialog.PreviewsCompleted >= 1);
        Dispatcher.UIThread.RunJobs();
        var frame = dialog.CaptureRenderedFrame()!;
        Directory.CreateDirectory(folder);
#pragma warning disable CS0618 // replacement overload's options type not needed for a plain PNG
        frame.Save(Path.Combine(folder, "vectorize-dialog.png"));
#pragma warning restore CS0618
        owner.Close();
    }

    [Fact]
    public void Placement_fits_a_big_picture_on_the_page_and_centers_it()
    {
        var page = PageSetup.FromInches(24, 12);   // 1728 x 864 pt
        var big = RasterImage.Solid(4000, 1000, 0, 0, 0);

        var m = VectorizeFlow.CenteredOnPage(big, page);

        var tl = m.Transform(new Vec2(0, 0));
        var br = m.Transform(new Vec2(4000, 1000));
        Assert.InRange(br.X - tl.X, 1555, 1556);                       // 90 % of the page width
        Assert.InRange((tl.X + br.X) / 2, 863.9, 864.1);               // centered
        Assert.InRange((tl.Y + br.Y) / 2, 431.9, 432.1);
    }

    [Fact]
    public void Placement_over_a_bitmap_object_matches_its_frame()
    {
        var img = new ImageObject("x", 200, 100) { Transform = Affine2.Translation(30, 40) };
        var px = RasterImage.Solid(50, 25, 0, 0, 0);

        var m = VectorizeFlow.PlacementOver(img, px);

        Assert.Equal(new Vec2(30, 40), m.Transform(new Vec2(0, 0)));
        Assert.Equal(new Vec2(230, 140), m.Transform(new Vec2(50, 25)));
    }
}

