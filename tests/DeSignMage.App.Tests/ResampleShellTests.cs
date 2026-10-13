using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DeSignMage.App.Shell;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Native;
using DeSignMage.Imaging;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Image ▸ Resample...: the dialog's linked fields, the menu item and the whole flow in the window.</summary>
public class ResampleShellTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dsm-resample-tests-" + Guid.NewGuid().ToString("N"));

    public ResampleShellTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---- helpers ----

    private static (MainWindow Window, Avalonia.Controls.Control Canvas) Open()
    {
        var w = new MainWindow(new StartupOptions()) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        return (w, w.FindControl<Avalonia.Controls.Control>("Surface")!);
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

    /// <summary>A PNG file: top-left quarter red, the rest blue.</summary>
    private string WriteQuadrants(string name, int w, int h)
    {
        var p = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (x < w / 2 && y < h / 2) p.Set(x, y, 255, 0, 0); else p.Set(x, y, 0, 0, 255);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, ImageCodec.EncodePng(p));
        return path;
    }

    private static async Task<ImageObject> ImportOne(MainWindow w, string path)
    {
        Assert.Equal(1, await w.ImportImagesAsync([path], null));
        Dispatcher.UIThread.RunJobs();
        return Assert.IsType<ImageObject>(Assert.Single(w.Session.Selection.Items));
    }

    private static string Strip(object? header) => ((string)header!).Replace("_", "");

    private static AppCommand ResampleCommand(MainWindow w) =>
        (AppCommand)w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => Strip(m.Header) == "Image")
            .Items.OfType<MenuItem>().Single(m => m.Tag is AppCommand { Id: "image.resample" }).Tag!;

    /// <summary>Types into a field the way a user does (the text changes, the dialog reacts).</summary>
    private static void Type(TextBox box, string text) => box.Text = text;

    /// <summary>A 2 x 1 inch picture at 300 dpi: 600 x 300 pixels.</summary>
    private static ImageObject Picture() => new("a", 144, 72, 600, 300);

    // ---- the dialog's linked fields ----

    [AvaloniaFact]
    public void The_dialog_opens_on_the_current_numbers_with_OK_off()
    {
        var d = new ResampleDialog(Picture());
        Assert.Equal("600", d.WidthBox.Text);
        Assert.Equal("300", d.HeightBox.Text);
        Assert.Equal("300", d.DpiBox.Text);
        Assert.True(d.LockBox.IsChecked);
        Assert.False(d.OkButton.IsEnabled);                        // nothing to apply yet
        Assert.Contains("600 x 300 pixels", d.CurrentText.Text);
        Assert.Contains("2 x 1 in", d.CurrentText.Text);
        Assert.Contains("300 dpi", d.CurrentText.Text);
    }

    [AvaloniaFact]
    public void Typing_the_width_sets_the_height_and_the_dpi()
    {
        var d = new ResampleDialog(Picture());
        Type(d.WidthBox, "1200");
        Assert.Equal("1200", d.WidthBox.Text);                     // what was typed stays
        Assert.Equal("600", d.HeightBox.Text);
        Assert.Equal("600", d.DpiBox.Text);
        Assert.True(d.OkButton.IsEnabled);
        Assert.Contains("1200 x 600 pixels", d.ResultText.Text);
        Assert.Contains("Printed size stays 2 x 1 in", d.ResultText.Text);
    }

    [AvaloniaFact]
    public void Typing_the_height_sets_the_width_and_the_dpi()
    {
        var d = new ResampleDialog(Picture());
        Type(d.HeightBox, "150");
        Assert.Equal("300", d.WidthBox.Text);
        Assert.Equal("150", d.DpiBox.Text);
    }

    [AvaloniaFact]
    public void Typing_a_dpi_sets_both_sizes()
    {
        var d = new ResampleDialog(Picture());
        Type(d.DpiBox, "150");
        Assert.Equal("300", d.WidthBox.Text);
        Assert.Equal("150", d.HeightBox.Text);
        Type(d.DpiBox, "72");
        Assert.Equal("144", d.WidthBox.Text);
        Assert.Equal("72", d.HeightBox.Text);
        Assert.Equal("72", d.DpiBox.Text);
    }

    [AvaloniaFact]
    public void With_the_lock_off_the_two_sides_are_free_and_the_dpi_follows_the_width()
    {
        var d = new ResampleDialog(Picture());
        d.LockBox.IsChecked = false;
        Type(d.WidthBox, "1200");
        Assert.Equal("300", d.HeightBox.Text);                     // unchanged
        Assert.Equal("600", d.DpiBox.Text);
        Type(d.HeightBox, "600");
        Assert.Equal("1200", d.WidthBox.Text);                     // unchanged
        Assert.Contains("600 dpi", d.ResultText.Text);

        Type(d.HeightBox, "300");
        Assert.Contains("600 x 300 dpi", d.ResultText.Text);       // different across and down: both are shown
    }

    [AvaloniaFact]
    public void Putting_the_lock_back_applies_to_the_next_edit()
    {
        var d = new ResampleDialog(Picture());
        d.LockBox.IsChecked = false;
        Type(d.WidthBox, "1000");
        d.LockBox.IsChecked = true;
        Type(d.HeightBox, "100");
        Assert.Equal("200", d.WidthBox.Text);
    }

    [AvaloniaFact]
    public void Empty_or_wrong_text_switches_OK_off_and_leaves_the_other_fields_alone()
    {
        var d = new ResampleDialog(Picture());
        Type(d.WidthBox, "800");
        Assert.True(d.OkButton.IsEnabled);
        Type(d.WidthBox, "");
        Assert.False(d.OkButton.IsEnabled);
        Assert.Equal("400", d.HeightBox.Text);                     // still what it was
        Type(d.WidthBox, "abc");
        Assert.False(d.OkButton.IsEnabled);
        Type(d.DpiBox, "-3");
        Assert.False(d.OkButton.IsEnabled);
        Type(d.WidthBox, "0");
        Assert.False(d.OkButton.IsEnabled);
        Type(d.WidthBox, "900");
        Assert.True(d.OkButton.IsEnabled);
    }

    [AvaloniaFact]
    public void A_size_that_is_too_large_says_so_and_cannot_be_applied()
    {
        var d = new ResampleDialog(Picture());
        Type(d.WidthBox, "40000");
        Assert.False(d.OkButton.IsEnabled);
        Assert.Contains("too large", d.ResultText.Text);
        Type(d.WidthBox, "4000");
        Assert.True(d.OkButton.IsEnabled);
    }

    [AvaloniaFact]
    public void Going_back_to_the_current_size_turns_OK_off_again()
    {
        var d = new ResampleDialog(Picture());
        Type(d.WidthBox, "1200");
        Type(d.WidthBox, "600");
        Assert.False(d.OkButton.IsEnabled);
    }

    [AvaloniaFact]
    public void The_dialog_never_judges_the_resolution()
    {
        var d = new ResampleDialog(Picture());
        foreach (var dpi in new[] { "300", "72", "10", "1", "1200", "9600" })
        {
            Type(d.DpiBox, dpi);
            var all = (d.ResultText.Text + " " + d.CurrentText.Text).ToLowerInvariant();
            Assert.DoesNotContain("low", all);
            Assert.DoesNotContain("warning", all);
            Assert.DoesNotContain("poor", all);
            Assert.DoesNotContain("print quality", all);
        }
    }

    [AvaloniaFact]
    public void OK_returns_the_new_pixel_size_and_Cancel_returns_nothing()
    {
        var owner = new Window { Width = 800, Height = 600 };
        owner.Show();
        var d = new ResampleDialog(Picture());
        d.Show(owner);
        Type(d.DpiBox, "150");
        d.OkButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal((300, 150), d.Result);

        var c = new ResampleDialog(Picture());
        c.Show(owner);
        Type(c.WidthBox, "100");
        c.CancelButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Null(c.Result);
        owner.Close();
    }

    [AvaloniaFact]
    public void The_dialog_uses_the_pictures_size_on_the_page_including_its_scale()
    {
        var doc = new Document(PageSetup.FromInches(24, 12));
        var image = Picture();
        image.Transform = Affine2.Scale(2, 2);                     // printed 4 x 2 inches: 150 dpi now
        doc.Insert(doc.ActiveLayer, -1, image);
        var d = new ResampleDialog(image);
        Assert.Contains("4 x 2 in", d.CurrentText.Text);
        Assert.Contains("150 dpi", d.CurrentText.Text);
        Type(d.DpiBox, "300");
        Assert.Equal("1200", d.WidthBox.Text);
        Assert.Equal("600", d.HeightBox.Text);
    }

    // ---- the menu ----

    [AvaloniaFact]
    public async Task The_menu_item_is_on_only_for_exactly_one_selected_picture()
    {
        var (w, _) = Open();
        var cmd = ResampleCommand(w);
        Assert.Equal("Resample...", cmd.Header);
        Assert.Empty(cmd.Gestures);                                // no shortcut

        Assert.False(cmd.CanExecute());                            // nothing selected
        var a = await ImportOne(w, WriteQuadrants("a.png", 60, 40));
        Assert.True(cmd.CanExecute());

        var rect = new RectangleObject(10, 10) { Style = ObjectStyle.FillOnly(DocColor.Black) };
        w.Session.History.Execute(new DeSignMage.Core.Commands.AddObjectCommand(w.Session.Document.ActiveLayer, rect));
        w.Session.Selection.Set(rect);
        Assert.False(cmd.CanExecute());                            // a shape is not a picture

        var b = await ImportOne(w, WriteQuadrants("b.png", 60, 40));
        w.Session.Selection.Set([a, b]);
        Assert.False(cmd.CanExecute());                            // several pictures: one at a time

        w.Session.Selection.Set(b);
        Assert.True(cmd.CanExecute());
        w.Close();
    }

    [AvaloniaFact]
    public void The_Image_menu_lists_Resample_after_Rasterize()
    {
        var (w, _) = Open();
        var items = w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => Strip(m.Header) == "Image").Items.OfType<Control>()
            .Select(c => c is MenuItem m ? Strip(m.Header) : "-").ToList();
        int raster = items.IndexOf("Rasterize...");
        Assert.True(raster >= 0);
        Assert.Equal("Resample...", items[raster + 1]);
        w.Close();
    }

    // ---- the whole flow ----

    [AvaloniaFact]
    public async Task Resampling_a_picture_from_the_menu_changes_its_pixels_not_its_place_or_size_and_undoes()
    {
        var (w, _) = Open();
        var img = await ImportOne(w, WriteQuadrants("q.png", 600, 300));    // no dpi in the file: 300 dpi, 2 x 1 inch
        var bounds = img.GetWorldBounds();
        string original = img.AssetId;

        ResampleCommand(w).Execute();
        Pump(() => w.OpenResampleDialog is not null);
        var dialog = w.OpenResampleDialog!;
        Type(dialog.DpiBox, "150");
        dialog.OkButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump(() => w.OpenResampleDialog is null && img.PixelWidth == 300);

        Assert.Equal((300, 150), (img.PixelWidth, img.PixelHeight));
        Assert.Equal(bounds, img.GetWorldBounds());                         // on the page it is exactly as it was
        Assert.Equal(150, img.EffectiveDpiX, 3);
        Assert.Equal("Resample Bitmap", w.Session.History.UndoDescription);
        var px = ImageCodec.Decode(w.Session.Document.Assets.Get(img.AssetId)!.Data);
        Assert.Equal((300, 150), (px.Width, px.Height));
        Assert.Equal((255, 0, 0), (px.Get(10, 10).R, px.Get(10, 10).G, px.Get(10, 10).B));     // still red at the top-left
        Assert.Equal((0, 0, 255), (px.Get(250, 120).R, px.Get(250, 120).G, px.Get(250, 120).B));

        // The Info Line tells the numbers, as plain information.
        Pump(() => w.FindControl<TextBlock>("InfoLine")!.Text!.Contains("300 x 150 px"));
        Assert.Contains("Bitmap: 300 x 150 px, 150 dpi", w.FindControl<TextBlock>("InfoLine")!.Text);

        w.Session.Undo();
        Assert.Equal(original, img.AssetId);                                // the exact original pixels are back
        Assert.Equal((600, 300), (img.PixelWidth, img.PixelHeight));
        Assert.Equal(bounds, img.GetWorldBounds());
        w.Session.Redo();
        Assert.Equal((300, 150), (img.PixelWidth, img.PixelHeight));
        w.Close();
    }

    [AvaloniaFact]
    public async Task Cancelling_the_dialog_changes_nothing()
    {
        var (w, _) = Open();
        var img = await ImportOne(w, WriteQuadrants("q.png", 600, 300));
        int steps = w.Session.History.UndoList.Count;

        ResampleCommand(w).Execute();
        Pump(() => w.OpenResampleDialog is not null);
        Type(w.OpenResampleDialog!.WidthBox, "100");
        w.OpenResampleDialog!.CancelButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump(() => w.OpenResampleDialog is null);

        Assert.Equal((600, 300), (img.PixelWidth, img.PixelHeight));
        Assert.Equal(steps, w.Session.History.UndoList.Count);
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_Context_Bar_button_opens_the_same_dialog()
    {
        var (w, _) = Open();
        await ImportOne(w, WriteQuadrants("q.png", 600, 300));
        ImageActions.OpenResample!.Invoke();
        Pump(() => w.OpenResampleDialog is not null);
        Assert.NotNull(w.OpenResampleDialog);
        w.OpenResampleDialog!.CancelButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump(() => w.OpenResampleDialog is null);
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_new_pixels_save_and_load_in_the_native_file()
    {
        var (w, _) = Open();
        var img = await ImportOne(w, WriteQuadrants("q.png", 600, 300));
        Assert.True(w.Session.Resample(240, 120));

        var bytes = DsmWriter.SaveToBytes(w.Session.Document);
        var loaded = DsmReader.Load(new MemoryStream(bytes)).Document;
        var back = loaded.AllObjects().OfType<ImageObject>().Single();

        Assert.Equal((240, 120), (back.PixelWidth, back.PixelHeight));
        Assert.Equal(img.Width, back.Width, 6);
        Assert.Equal(img.Height, back.Height, 6);
        Assert.Equal(img.Transform, back.Transform);
        Assert.Equal(img.EffectiveDpiX, back.EffectiveDpiX, 6);
        var saved = ImageCodec.Decode(loaded.Assets.Get(back.AssetId)!.Data);
        var live = ImageCodec.Decode(w.Session.Document.Assets.Get(img.AssetId)!.Data);
        Assert.True(saved.Rgba.AsSpan().SequenceEqual(live.Rgba), "the saved pixels are the resampled pixels");
        Assert.Equal((240, 120), (saved.Width, saved.Height));
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_big_picture_resamples_through_the_progress_window_path()
    {
        var (w, _) = Open();
        var img = await ImportOne(w, WriteQuadrants("big.png", 1400, 900));   // above the background threshold (600,000 pixels)
        Assert.True(w.Session.Resample(350, 225));
        await w.Session.ImageIdle;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((350, 225), (img.PixelWidth, img.PixelHeight));
        w.Session.Undo();
        Assert.Equal((1400, 900), (img.PixelWidth, img.PixelHeight));
        w.Close();
    }

    // ---- screenshot ----

    [AvaloniaFact]
    public void Screenshot()
    {
        // Development aid: set DSM_SCREENSHOTS to a folder to save a picture of the dialog there.
        var folder = Environment.GetEnvironmentVariable("DSM_SCREENSHOTS");
        if (string.IsNullOrEmpty(folder)) return;

        var owner = new Window { Width = 900, Height = 600 };
        owner.Show();
        var doc = new Document(PageSetup.FromInches(24, 12));
        var image = new ImageObject("a", 216, 144, 900, 600, "sunset.png") { Transform = Affine2.Translation(100, 100) };   // 3 x 2 inches: 300 dpi
        doc.Insert(doc.ActiveLayer, -1, image);
        var d = new ResampleDialog(image);
        d.Show(owner);
        Type(d.DpiBox, "150");
        Dispatcher.UIThread.RunJobs();
        var frame = d.CaptureRenderedFrame()!;
        Directory.CreateDirectory(folder);
#pragma warning disable CS0618
        frame.Save(Path.Combine(folder, "resample-dialog.png"));
#pragma warning restore CS0618
        owner.Close();
    }
}
