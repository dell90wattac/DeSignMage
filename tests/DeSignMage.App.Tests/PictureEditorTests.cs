using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DeSignMage.App.Canvas;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Native;
using DeSignMage.Imaging;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>The Picture Editor in the real window: double-click, the Crop and Adjust tabs, the live canvas behind, OK / Cancel, reopening, undo.</summary>
public class PictureEditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dsm-pictureeditor-tests-" + Guid.NewGuid().ToString("N"));

    public PictureEditorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---- helpers ----

    private static (MainWindow Window, DesignCanvas Canvas) Open()
    {
        var w = new MainWindow(new StartupOptions()) { Width = 1500, Height = 900 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        return (w, w.FindControl<DesignCanvas>("Surface")!);
    }

    private static void Pump(Func<bool> done, int timeoutMs = 20000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && watch.ElapsedMilliseconds < timeoutMs)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static Point P(DesignCanvas c, Vec2 screen) => c.TranslatePoint(new Point(screen.X, screen.Y), TopLevel.GetTopLevel(c)!).GetValueOrDefault();

    /// <summary>A photo-like picture: a gradient with a red square at (x0..x0+size) so crops and turns are visible.</summary>
    private static PixelBuffer Photo(int w, int h, int border = 0)
    {
        var p = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool inside = x >= border && x < w - border && y >= border && y < h - border;
                if (!inside) p.Set(x, y, 255, 255, 255);
                else p.Set(x, y, (byte)(40 + 150 * (x - border) / Math.Max(1, w - 2 * border)), (byte)(60 + 120 * (y - border) / Math.Max(1, h - 2 * border)), 110);
            }
        return p;
    }

    private string Write(string name, PixelBuffer p)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, ImageCodec.EncodePng(p));
        return path;
    }

    private async Task<ImageObject> ImportOne(MainWindow w, PixelBuffer photo, string name = "photo.png")
    {
        Assert.Equal(1, await w.ImportImagesAsync([Write(name, photo)], null));
        Dispatcher.UIThread.RunJobs();
        return Assert.IsType<ImageObject>(Assert.Single(w.Session.Selection.Items));
    }

    /// <summary>Opens the editor on the selected picture and waits until its first preview is ready.</summary>
    private static async Task<PictureEditorDialog> OpenEditor(MainWindow w)
    {
        w.Session.RequestPictureEdit((ImageObject)w.Session.Selection.Items[0]);
        Pump(() => w.OpenPictureEditor is not null);
        var d = w.OpenPictureEditor!;
        Pump(() => d.Session.PreviewsMade >= 1);
        await Task.CompletedTask;
        return d;
    }

    /// <summary>Waits for the preview of the newest change (and delivers it).</summary>
    private static void Settle(PictureEditorDialog d)
    {
        var idle = d.Session.Idle;
        Pump(() => idle.IsCompleted);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void CloseWith(MainWindow w, Button b)
    {
        Click(b);
        Pump(() => w.OpenPictureEditor is null);
    }

    private static PixelBuffer Pixels(MainWindow w, string assetId) => ImageCodec.Decode(w.Session.Document.Assets.Get(assetId)!.Data);

    private static string Strip(object? header) => ((string)header!).Replace("_", "");

    private static AppCommand EditCommand(MainWindow w) =>
        (AppCommand)w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => Strip(m.Header) == "Image")
            .Items.OfType<MenuItem>().Single(m => m.Tag is AppCommand { Id: "image.editPicture" }).Tag!;

    private static ImageContextRow ImageRow(MainWindow w)
    {
        Dispatcher.UIThread.RunJobs();
        var bar = Assert.IsType<SelectionContextBar>(w.FindControl<ContentControl>("ContextBarHost")!.Content);
        return Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(bar).OfType<ImageContextRow>().Single();
    }

    private static AppCommand ImageCommand(MainWindow w, string id) =>
        (AppCommand)w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => Strip(m.Header) == "Image")
            .Items.OfType<MenuItem>().Single(m => m.Tag is AppCommand c && c.Id == id).Tag!;

    // ---- opening ----

    [AvaloniaFact]
    public async Task Double_click_on_a_picture_opens_the_Picture_Editor_instead_of_a_menu()
    {
        var (w, c) = Open();
        var img = await ImportOne(w, Photo(300, 200));
        w.Session.Selection.Clear();
        var center = P(c, w.Session.Viewport.ToScreen(img.GetWorldBounds().Center));
        w.MouseDown(center, MouseButton.Left);
        w.MouseUp(center, MouseButton.Left);
        w.MouseDown(center, MouseButton.Left);
        w.MouseUp(center, MouseButton.Left);

        Pump(() => w.OpenPictureEditor is not null);
        Assert.NotNull(w.OpenPictureEditor);
        Assert.Null(w.LastObjectMenu);
        CloseWith(w, w.OpenPictureEditor!.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Double_click_on_a_shape_or_text_does_not_open_the_Picture_Editor()
    {
        var (w, c) = Open();
        await ImportOne(w, Photo(30, 30));
        var rect = new RectangleObject(100, 60) { Transform = Affine2.Translation(600, 100), Style = ObjectStyle.FillOnly(DocColor.FromHex("#3366AA")) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, rect));
        w.Session.Selection.Clear();
        w.Session.ZoomToPage();
        var at = P(c, w.Session.Viewport.ToScreen(rect.GetWorldBounds().Center));
        w.MouseDown(at, MouseButton.Left); w.MouseUp(at, MouseButton.Left);
        w.MouseDown(at, MouseButton.Left); w.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(w.OpenPictureEditor);
        Assert.Equal("vector", w.Session.ActiveTool.Id);          // a shape: the Vector Edit tool, as before
        w.Close();
    }

    [AvaloniaFact]
    public async Task Edit_Picture_is_in_the_Image_menu_for_one_selected_picture_only_and_has_no_shortcut()
    {
        var (w, _) = Open();
        var cmd = EditCommand(w);
        Assert.Equal("Edit Photo...", cmd.Header);
        Assert.Empty(cmd.Gestures);
        Assert.False(cmd.CanExecute());

        var a = await ImportOne(w, Photo(40, 30), "a.png");
        Assert.True(cmd.CanExecute());
        var b = await ImportOne(w, Photo(40, 30), "b.png");
        w.Session.Selection.Set([a, b]);
        Assert.False(cmd.CanExecute());
        var rect = new RectangleObject(10, 10) { Style = ObjectStyle.FillOnly(DocColor.Black) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, rect));
        w.Session.Selection.Set(rect);
        Assert.False(cmd.CanExecute());

        w.Session.Selection.Set(a);
        cmd.Execute();
        Pump(() => w.OpenPictureEditor is not null);
        Assert.NotNull(w.OpenPictureEditor);
        CloseWith(w, w.OpenPictureEditor!.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_Context_Bar_has_an_Edit_Photo_button_for_a_selected_picture()
    {
        var (w, _) = Open();
        await ImportOne(w, Photo(40, 30));
        Pump(() => true);
        Assert.True(ImageRow(w).EditButton.IsVisible);
        Assert.Equal("Edit Photo", ImageRow(w).EditButton.Content);
        ImageActions.OpenEditor!.Invoke();
        Pump(() => w.OpenPictureEditor is not null);
        Assert.NotNull(w.OpenPictureEditor);
        CloseWith(w, w.OpenPictureEditor!.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Only_the_tabs_that_work_are_shown()
    {
        var (w, _) = Open();
        await ImportOne(w, Photo(80, 60));
        var d = await OpenEditor(w);

        Assert.Equal(["Crop", "Adjust", "Transparency", "Effects"], d.Tabs.Items.OfType<TabItem>().Select(t => (string)t.Header!).ToArray());
        Assert.Same(d.CropTab, d.Tabs.SelectedItem);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task It_opens_on_the_whole_picture_with_its_real_size_and_the_inches_it_prints_at()
    {
        var (w, _) = Open();
        await ImportOne(w, Photo(600, 300));                       // no dpi in the file: 300 dpi, 2 x 1 inches
        var d = await OpenEditor(w);

        Assert.Equal("600", d.CropWidthBox.Text);
        Assert.Equal("300", d.CropHeightBox.Text);
        Assert.Equal("2", d.CropWidthInBox.Text);
        Assert.Equal("1", d.CropHeightInBox.Text);
        Assert.Equal("0", d.AngleBox.Text);
        Assert.True(d.Session.Recipe.IsIdentity);
        Assert.False(d.ResetCropButton.IsVisible);                // nothing to reset: not shown
        Assert.False(d.ResetAngleButton.IsVisible);
        Assert.False(d.RemoveCornersButton.IsVisible);
        Assert.False(d.ResetAllButton.IsVisible);
        Assert.NotNull(d.Session.ResultPreview);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    // ---- crop ----

    [AvaloniaFact]
    public async Task Typing_a_size_crops_and_the_picture_behind_the_window_follows_live()
    {
        var (w, _) = Open();
        var img = await ImportOne(w, Photo(600, 300));
        var before = img.GetWorldBounds();
        var d = await OpenEditor(w);

        d.CropWidthBox.Text = "300";
        d.CropHeightBox.Text = "150";
        Settle(d);

        Assert.Equal(new RectI(0, 0, 300, 150), d.Session.Recipe.Crop);
        var live = img.GetWorldBounds();                           // the canvas behind the window shows it: the top-left quarter, still in place
        Assert.Equal(before.Left, live.Left, 6);
        Assert.Equal(before.Top, live.Top, 6);
        Assert.Equal(before.Width / 2, live.Width, 6);
        Assert.Equal(before.Height / 2, live.Height, 6);
        Assert.True(d.ResetCropButton.IsVisible);
        Assert.True(d.ResetAllButton.IsVisible);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Typing_inches_sets_the_pixels_at_the_pictures_printed_size()
    {
        var (w, _) = Open();
        await ImportOne(w, Photo(600, 300));                       // 300 dpi
        var d = await OpenEditor(w);

        d.CropWidthInBox.Text = "1.5";
        Settle(d);

        Assert.Equal(450, d.Session.Recipe.EffectiveCrop.Width);
        Assert.Equal("450", d.CropWidthBox.Text);
        d.CropHeightInBox.Text = "0.5";
        Settle(d);
        Assert.Equal(150, d.Session.Recipe.EffectiveCrop.Height);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task With_the_lock_on_typing_one_side_keeps_the_proportions()
    {
        var (w, _) = Open();
        await ImportOne(w, Photo(600, 300));
        var d = await OpenEditor(w);
        d.LockAspectBox.IsChecked = true;

        d.CropWidthBox.Text = "300";
        Settle(d);

        Assert.Equal(new RectI(0, 0, 300, 150), d.Session.Recipe.Crop);
        Assert.Equal("150", d.CropHeightBox.Text);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Dragging_the_frame_on_the_left_picture_moves_edges_and_corners_and_draws_new_frames()
    {
        var (w, _) = Open();
        await ImportOne(w, Photo(600, 300));
        var d = await OpenEditor(w);
        d.LeftPane.View.Set(1, 300, 150);                          // 1 DIP per pixel, so canvas pixels are easy to reason about

        d.LeftDown(new Vec2(300, 150)); d.LeftMove(new Vec2(200, 100)); d.LeftMove(new Vec2(100, 50)); d.LeftUp(new Vec2(100, 50));   // starts outside any handle?
        // a press on the middle of an unframed picture is inside the (whole-picture) frame: it moves it, which cannot go anywhere
        Assert.Null(d.Session.Recipe.Crop);

        // the right edge handle of the whole-picture frame, dragged left
        d.LeftDown(new Vec2(600, 150)); d.LeftMove(new Vec2(450, 150)); d.LeftUp(new Vec2(450, 150));
        Assert.Equal(new RectI(0, 0, 450, 300), d.Session.Recipe.Crop);

        // the bottom-left corner dragged up and right
        d.LeftDown(new Vec2(0, 300)); d.LeftMove(new Vec2(60, 200)); d.LeftUp(new Vec2(60, 200));
        Assert.Equal(new RectI(60, 0, 390, 200), d.Session.Recipe.Crop);

        // inside: the whole frame moves
        d.LeftDown(new Vec2(200, 100)); d.LeftMove(new Vec2(210, 130)); d.LeftUp(new Vec2(210, 130));
        Assert.Equal(new RectI(70, 30, 390, 200), d.Session.Recipe.Crop);

        // outside the frame: a new one is drawn
        d.LeftDown(new Vec2(10, 250)); d.LeftMove(new Vec2(50, 280)); d.LeftUp(new Vec2(50, 280));
        Assert.Equal(new RectI(10, 250, 40, 30), d.Session.Recipe.Crop);
        Assert.Equal("40", d.CropWidthBox.Text);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_corner_drag_with_the_lock_keeps_the_proportions_of_the_frame()
    {
        var (w, _) = Open();
        await ImportOne(w, Photo(600, 300));
        var d = await OpenEditor(w);
        d.LeftPane.View.Set(1, 300, 150);
        d.Session.Change(r => r with { Crop = new RectI(100, 50, 200, 100) });
        d.LockAspectBox.IsChecked = true;

        d.LeftDown(new Vec2(300, 150)); d.LeftMove(new Vec2(500, 180)); d.LeftUp(new Vec2(500, 180));

        var crop = d.Session.Recipe.EffectiveCrop;
        Assert.Equal((100, 50), (crop.X, crop.Y));
        Assert.Equal(2.0, (double)crop.Width / crop.Height, 1);
        Assert.Equal(400, crop.Width);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_kept_part_stays_where_it_was_on_the_page_at_the_same_scale()
    {
        var (w, _) = Open();
        var img = await ImportOne(w, Photo(600, 300));            // 144 x 72 points: 0.24 points per pixel
        var before = img.GetWorldBounds();
        double dpi = img.EffectiveDpiX;
        var d = await OpenEditor(w);

        d.Session.Change(r => r with { Crop = new RectI(100, 50, 300, 200) });
        Settle(d);

        var live = img.GetWorldBounds();
        Assert.Equal(before.Left + 100 * 0.24, live.Left, 6);
        Assert.Equal(before.Top + 50 * 0.24, live.Top, 6);
        Assert.Equal(300 * 0.24, live.Width, 6);
        Assert.Equal(200 * 0.24, live.Height, 6);
        // after OK the dpi is unchanged and the picture holds exactly the kept pixels
        Click(d.OkButton);
        Pump(() => w.OpenPictureEditor is null);
        Assert.Equal(dpi, img.EffectiveDpiX, 3);
        Assert.Equal((300, 200), (img.PixelWidth, img.PixelHeight));
        w.Close();
    }

    // ---- cancel / OK / undo / reopen ----

    [AvaloniaFact]
    public async Task Cancel_puts_everything_back_exactly_and_leaves_nothing_behind()
    {
        var (w, _) = Open();
        var img = await ImportOne(w, Photo(600, 300));
        string asset = img.AssetId;
        var transform = img.Transform;
        var bounds = img.GetWorldBounds();
        int assets = w.Session.Document.Assets.All.Count();
        int steps = w.Session.History.UndoList.Count;
        var d = await OpenEditor(w);

        d.Session.Change(r => r with { Crop = new RectI(100, 50, 300, 200), Adjust = r.Adjust with { Brightness = 40 } });
        Settle(d);
        Assert.NotEqual(asset, img.AssetId);                       // the canvas showed the preview
        CloseWith(w, d.CancelButton);

        Assert.Equal(asset, img.AssetId);
        Assert.Equal(transform, img.Transform);
        Assert.Equal(bounds, img.GetWorldBounds());
        Assert.Equal((600, 300), (img.PixelWidth, img.PixelHeight));
        Assert.Null(img.Recipe);
        Assert.Null(img.SourceAssetId);
        Assert.Equal(assets, w.Session.Document.Assets.All.Count());   // the preview pictures are gone
        Assert.Equal(steps, w.Session.History.UndoList.Count);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Closing_the_window_with_the_close_button_cancels_too()
    {
        var (w, _) = Open();
        var img = await ImportOne(w, Photo(600, 300));
        string asset = img.AssetId;
        var d = await OpenEditor(w);
        d.Session.Change(r => r with { Adjust = r.Adjust with { Hue = 90 } });
        Settle(d);

        d.Close();
        Pump(() => w.OpenPictureEditor is null);

        Assert.Equal(asset, img.AssetId);
        w.Close();
    }

    [AvaloniaFact]
    public async Task OK_makes_one_undo_step_with_the_full_resolution_result_and_keeps_the_original()
    {
        var (w, _) = Open();
        var photo = Photo(600, 300);
        var img = await ImportOne(w, photo);
        string original = img.AssetId;
        int steps = w.Session.History.UndoList.Count;
        var d = await OpenEditor(w);

        var recipe = new PictureRecipe(600, 300) { Crop = new RectI(100, 50, 300, 200), Adjust = PictureAdjust.None with { Brightness = 30, Saturation = 20 } };
        d.Session.Change(_ => recipe);
        Settle(d);
        Click(d.OkButton);
        Pump(() => w.OpenPictureEditor is null);

        Assert.True(d.Committed);
        Assert.Equal(steps + 1, w.Session.History.UndoList.Count);
        Assert.Equal("Edit Picture", w.Session.History.UndoDescription);
        Assert.Equal(original, img.SourceAssetId);
        Assert.Equal(recipe, img.Recipe);
        Assert.NotEqual(original, img.AssetId);
        var stored = Pixels(w, img.AssetId);
        Assert.Equal((300, 200), (stored.Width, stored.Height));
        var expected = PictureProcessor.Render(photo, recipe);              // the very same pixels as rendering the recipe from the original
        Assert.True(stored.Rgba.AsSpan().SequenceEqual(expected.Rgba));
        Samples(w, original, photo);

        w.Session.Undo();
        Assert.Equal(original, img.AssetId);
        Assert.Null(img.Recipe);
        Assert.Null(img.SourceAssetId);
        Assert.Equal((600, 300), (img.PixelWidth, img.PixelHeight));
        w.Session.Redo();
        Assert.Equal(recipe, img.Recipe);
        w.Close();
    }

    private static void Samples(MainWindow w, string assetId, PixelBuffer expected)
    {
        var stored = Pixels(w, assetId);
        Assert.True(stored.Rgba.AsSpan().SequenceEqual(expected.Rgba), "the original pixels are untouched");
    }

    [AvaloniaFact]
    public async Task OK_without_any_change_adds_no_undo_step()
    {
        var (w, _) = Open();
        await ImportOne(w, Photo(300, 200));
        int steps = w.Session.History.UndoList.Count;
        var d = await OpenEditor(w);
        Click(d.OkButton);
        Pump(() => w.OpenPictureEditor is null);
        Assert.False(d.Committed);
        Assert.Equal(steps, w.Session.History.UndoList.Count);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Reopening_shows_every_setting_and_the_whole_photo_so_the_crop_can_be_undone()
    {
        var (w, _) = Open();
        var photo = Photo(600, 300);
        var img = await ImportOne(w, photo);
        string original = img.AssetId;
        var firstBounds = img.GetWorldBounds();

        var d = await OpenEditor(w);
        var recipe = new PictureRecipe(600, 300) { Crop = new RectI(100, 50, 300, 200), Adjust = PictureAdjust.None with { Contrast = 25, BalanceR = 15 } };
        d.Session.Change(_ => recipe);
        Settle(d);
        Click(d.OkButton);
        Pump(() => w.OpenPictureEditor is null);

        var again = await OpenEditor(w);
        Assert.Equal(recipe, again.Session.Recipe);                          // the frame is where it was ...
        Assert.Equal("300", again.CropWidthBox.Text);
        Assert.Equal(new RectI(100, 50, 300, 200), again.LeftPane.Frame);
        Assert.Equal((600, 300), (again.Session.Source.Width, again.Session.Source.Height));   // ... on the whole original photo
        Assert.Equal(25, again.Session.Recipe.Adjust.Contrast);
        Assert.Equal(15, again.Session.Recipe.Adjust.BalanceR);
        Assert.True(again.ResetCropButton.IsVisible);

        again.Tabs.SelectedItem = again.AdjustTab;                           // the sliders show the saved values
        var contrast = again.AdjustTab.GetLogicalDescendantsOfType<TextBox>().ToList();
        Assert.Contains(contrast, t => t.Text == "25");

        Click(again.ResetCropButton);                                        // uncrop; the colors stay
        Settle(again);
        Assert.Null(again.Session.Recipe.Crop);
        Click(again.OkButton);
        Pump(() => w.OpenPictureEditor is null);
        Assert.Equal((600, 300), (img.PixelWidth, img.PixelHeight));
        Assert.Equal(firstBounds, img.GetWorldBounds());                     // the whole picture is back in its original place
        Assert.Equal(25, img.Recipe!.Adjust.Contrast);
        Assert.Equal(original, img.SourceAssetId);

        var third = await OpenEditor(w);
        Click(third.ResetAllButton);                                         // reset all returns to the untouched original
        Settle(third);
        Click(third.OkButton);
        Pump(() => w.OpenPictureEditor is null);
        Assert.Equal(original, img.AssetId);
        Assert.Null(img.Recipe);
        Assert.Null(img.SourceAssetId);
        Assert.Equal(firstBounds, img.GetWorldBounds());
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_picture_moved_between_edits_keeps_its_new_place_when_uncropped()
    {
        var (w, _) = Open();
        var img = await ImportOne(w, Photo(600, 300));
        var start = img.GetWorldBounds();

        var d = await OpenEditor(w);
        d.Session.Change(r => r with { Crop = new RectI(100, 50, 300, 200) });
        Settle(d);
        Click(d.OkButton);
        Pump(() => w.OpenPictureEditor is null);

        w.Session.Selection.Set(img);
        w.Session.NudgeSelection(new Vec2(40, 10));                          // the user moves the cropped picture

        var again = await OpenEditor(w);
        Click(again.ResetCropButton);
        Settle(again);
        Click(again.OkButton);
        Pump(() => w.OpenPictureEditor is null);

        Assert.Equal(start.Left + 40, img.GetWorldBounds().Left, 6);         // the whole picture, moved with it
        Assert.Equal(start.Top + 10, img.GetWorldBounds().Top, 6);
        Assert.Equal(start.Width, img.GetWorldBounds().Width, 6);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_scaled_picture_keeps_its_scale_through_an_edit()
    {
        var (w, _) = Open();
        var img = await ImportOne(w, Photo(600, 300));
        w.Session.Selection.Set(img);
        var center = img.GetWorldBounds().Center;
        w.Session.TransformSelection("Size", Affine2.Scale(2, 2, center));  // printed twice as big: 150 dpi
        double dpi = img.EffectiveDpiX;
        Assert.Equal(150, dpi, 3);

        var d = await OpenEditor(w);
        Assert.Equal("4", d.CropWidthInBox.Text);                            // 600 px at 150 dpi = 4 inches
        d.Session.Change(r => r with { Crop = new RectI(0, 0, 300, 150) });
        Settle(d);
        Click(d.OkButton);
        Pump(() => w.OpenPictureEditor is null);

        Assert.Equal(150, img.EffectiveDpiX, 3);
        w.Close();
    }

    // ---- straighten, auto-trim ----

    [AvaloniaFact]
    public async Task Drawing_a_level_line_turns_the_picture_and_the_corners_can_be_cropped_away()
    {
        var (w, _) = Open();
        await ImportOne(w, Photo(600, 400));
        var d = await OpenEditor(w);
        Assert.False(d.RemoveCornersButton.IsVisible);

        d.LevelToolButton.IsChecked = true;
        d.LeftDown(new Vec2(100, 100)); d.LeftMove(new Vec2(300, 117.4977)); d.LeftUp(new Vec2(500, 134.9954));   // a line sinking by 5 degrees
        Settle(d);

        Assert.False(d.LevelToolButton.IsChecked);                           // the tool is used once
        Assert.Equal(-5, d.Session.Recipe.Straighten, 1);
        Assert.Equal("-5", d.AngleBox.Text);
        Assert.True(d.RemoveCornersButton.IsVisible);
        Assert.True(d.ResetAngleButton.IsVisible);
        var (cw, ch) = d.Session.Recipe.CanvasSize;
        Assert.True(cw > 600 && ch > 400);

        Click(d.RemoveCornersButton);
        Settle(d);
        var expected = PictureGeometry.InscribedCrop(600, 400, d.Session.Recipe.Straighten);
        Assert.Equal(expected, d.Session.Recipe.Crop);

        Click(d.OkButton);
        Pump(() => w.OpenPictureEditor is null);
        var img = (ImageObject)w.Session.Selection.Items[0];
        var result = Pixels(w, img.AssetId);
        Assert.Equal((expected.Width, expected.Height), (result.Width, result.Height));
        foreach (var (x, y) in new[] { (0, 0), (result.Width - 1, 0), (0, result.Height - 1), (result.Width - 1, result.Height - 1) })
            Assert.True(result.Get(x, y).A >= 250, $"corner ({x},{y}) is not opaque");
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_typed_angle_straightens_and_keeps_a_crop_about_the_middle()
    {
        var (w, _) = Open();
        await ImportOne(w, Photo(600, 400));
        var d = await OpenEditor(w);
        d.Session.Change(r => r with { Crop = new RectI(100, 100, 300, 200) });

        d.AngleBox.Text = "10";
        Settle(d);

        Assert.Equal(10, d.Session.Recipe.Straighten, 6);
        var crop = d.Session.Recipe.EffectiveCrop;
        Assert.Equal((300, 200), (crop.Width, crop.Height));                 // the same size, shifted with the middle of the bigger canvas
        var (cw, ch) = d.Session.Recipe.CanvasSize;
        Assert.Equal(100 + (cw - 600) / 2, crop.X);
        Assert.Equal(100 + (ch - 400) / 2, crop.Y);

        d.AngleBox.Text = "200";                                             // limited to a half turn either way
        Settle(d);
        Assert.Equal(180, d.Session.Recipe.Straighten, 6);
        Click(d.ResetAngleButton);
        Settle(d);
        Assert.Equal(0, d.Session.Recipe.Straighten);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Auto_trim_cuts_the_plain_border_around_the_photo()
    {
        var (w, _) = Open();
        await ImportOne(w, Photo(600, 400, border: 40));
        var d = await OpenEditor(w);

        Click(d.AutoTrimButton);
        Settle(d);

        Assert.Equal(new RectI(40, 40, 520, 320), d.Session.Recipe.Crop);
        Assert.Equal("520", d.CropWidthBox.Text);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Auto_trim_on_a_photo_without_a_border_changes_nothing_and_says_so()
    {
        var (w, _) = Open();
        await ImportOne(w, Photo(300, 200));
        var d = await OpenEditor(w);
        Click(d.AutoTrimButton);
        Settle(d);
        Assert.Null(d.Session.Recipe.Crop);
        Assert.Contains("No plain border", d.CropNote.Text);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    // ---- adjust ----

    private static IEnumerable<T> Find<T>(Control root) where T : Control => root.GetLogicalDescendantsOfType<T>();

    [AvaloniaFact]
    public async Task The_sliders_change_the_recipe_and_their_reset_buttons_only_show_when_there_is_something_to_reset()
    {
        var (w, _) = Open();
        await ImportOne(w, Photo(300, 200));
        var d = await OpenEditor(w);
        d.Tabs.SelectedItem = d.AdjustTab;

        var sliders = Find<Slider>(d.AdjustTab).ToList();
        Assert.Equal(8, sliders.Count);                                      // brightness, contrast, gamma, saturation, hue, red, green, blue
        var resets = Find<Button>(d.AdjustTab).Where(b => (b.Content as string) == "↺").ToList();
        Assert.Equal(8, resets.Count);
        Assert.All(resets, b => Assert.False(b.IsVisible));

        sliders[0].Value = 35;                                               // brightness
        Settle(d);
        Assert.Equal(35, d.Session.Recipe.Adjust.Brightness);
        Assert.True(resets[0].IsVisible);
        Assert.False(resets[1].IsVisible);

        sliders[2].Value = 1.6;                                              // gamma
        sliders[4].Value = -40;                                              // hue
        Settle(d);
        Assert.Equal(1.6, d.Session.Recipe.Adjust.Gamma, 2);
        Assert.Equal(-40, d.Session.Recipe.Adjust.Hue);

        Click(resets[0]);
        Settle(d);
        Assert.Equal(0, d.Session.Recipe.Adjust.Brightness);
        Assert.False(resets[0].IsVisible);
        Assert.Equal(-40, d.Session.Recipe.Adjust.Hue);                      // the others stay
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_number_typed_beside_a_slider_sets_it_and_the_slider_follows()
    {
        var (w, _) = Open();
        await ImportOne(w, Photo(300, 200));
        var d = await OpenEditor(w);
        d.Tabs.SelectedItem = d.AdjustTab;
        var boxes = Find<TextBox>(d.AdjustTab).ToList();
        var sliders = Find<Slider>(d.AdjustTab).ToList();

        boxes[1].Text = "-60";                                               // contrast
        Settle(d);
        Assert.Equal(-60, d.Session.Recipe.Adjust.Contrast);
        Assert.Equal(-60, sliders[1].Value, 6);

        boxes[1].Text = "900";                                               // beyond the end: held at the end
        Settle(d);
        Assert.Equal(100, d.Session.Recipe.Adjust.Contrast);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Auto_levels_sets_the_black_and_white_points_from_the_picture_in_one_click()
    {
        var (w, _) = Open();
        var dull = new PixelBuffer(200, 100);
        for (int y = 0; y < 100; y++) for (int x = 0; x < 200; x++) { byte v = (byte)(90 + x / 3); dull.Set(x, y, v, v, v); }   // 90 .. 156
        await ImportOne(w, dull);
        var d = await OpenEditor(w);
        d.Tabs.SelectedItem = d.AdjustTab;
        Assert.False(d.ResetLevelsButton.IsVisible);

        Click(d.AutoLevelsButton);
        Settle(d);

        var a = d.Session.Recipe.Adjust;
        Assert.InRange(a.LowR, 88, 95);
        Assert.InRange(a.HighR, 150, 157);
        Assert.True(d.ResetLevelsButton.IsVisible);
        Assert.Contains("Levels", d.LevelsNote.Text);

        Click(d.OkButton);
        Pump(() => w.OpenPictureEditor is null);
        var img = (ImageObject)w.Session.Selection.Items[0];
        var result = Pixels(w, img.AssetId);
        Assert.InRange(result.Get(2, 5).R, 0, 12);                           // the dull picture now fills the range
        Assert.InRange(result.Get(197, 5).R, 243, 255);

        var again = await OpenEditor(w);
        again.Tabs.SelectedItem = again.AdjustTab;
        Click(again.ResetLevelsButton);
        Settle(again);
        Assert.True(again.Session.Recipe.Adjust.LevelsAreDefault);
        CloseWith(w, again.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_adjusted_colors_in_the_result_are_the_ones_the_math_gives()
    {
        var (w, _) = Open();
        var photo = Photo(120, 80);
        var img = await ImportOne(w, photo);
        var d = await OpenEditor(w);
        var adjust = PictureAdjust.None with { Brightness = -15, Contrast = 30, Gamma = 1.3, Saturation = 40, Hue = 25, BalanceG = 20 };
        d.Session.Change(r => r with { Adjust = adjust });
        Settle(d);
        Click(d.OkButton);
        Pump(() => w.OpenPictureEditor is null);

        var result = Pixels(w, img.AssetId);
        foreach (var (x, y) in new[] { (5, 5), (60, 40), (110, 70) })
        {
            var (r, g, b, _) = photo.Get(x, y);
            var (er, eg, eb) = AdjustMath.Apply(adjust, r, g, b);
            Assert.InRange(result.Get(x, y).R, er - 1, er + 1);
            Assert.InRange(result.Get(x, y).G, eg - 1, eg + 1);
            Assert.InRange(result.Get(x, y).B, eb - 1, eb + 1);
        }
        w.Close();
    }

    [AvaloniaFact]
    public async Task Reset_all_goes_back_to_the_original_and_the_buttons_for_it_disappear()
    {
        var (w, _) = Open();
        await ImportOne(w, Photo(300, 200));
        var d = await OpenEditor(w);
        d.Session.Change(r => r with { Straighten = 3, Crop = new RectI(10, 10, 100, 100), Adjust = r.Adjust with { Hue = 30 } });
        d.SyncFields();
        Assert.True(d.ResetAllButton.IsVisible);

        Click(d.ResetAllButton);
        Settle(d);

        Assert.True(d.Session.Recipe.IsIdentity);
        Assert.False(d.ResetAllButton.IsVisible);
        Assert.Equal("300", d.CropWidthBox.Text);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    // ---- the views ----

    [AvaloniaFact]
    public async Task The_two_panes_share_one_zoom_and_position_and_the_views_switch()
    {
        var (w, _) = Open();
        await ImportOne(w, Photo(300, 200));
        var d = await OpenEditor(w);

        Assert.Same(d.LeftPane.View, d.RightPane.View);
        Assert.True(d.LeftPane.IsVisible && d.RightPane.IsVisible);
        Assert.False(d.HoldButton.IsVisible);

        d.LeftPane.View.Set(3, 100, 80);
        Assert.Equal(3, d.RightPane.View.Zoom);
        Assert.Equal(100, d.RightPane.View.CenterX);

        d.SingleRadio.IsChecked = true;
        Assert.False(d.LeftPane.IsVisible);
        Assert.True(d.RightPane.IsVisible);
        Assert.False(d.HoldButton.IsVisible);

        d.CompareRadio.IsChecked = true;
        Assert.True(d.HoldButton.IsVisible);
        Assert.Equal("Result", d.RightPane.Caption);
        d.Hold(true);
        Assert.Equal("Original", d.RightPane.Caption);                       // held: the picture as it was
        d.Hold(false);
        Assert.Equal("Result", d.RightPane.Caption);

        d.SideBySideRadio.IsChecked = true;
        Assert.True(d.LeftPane.IsVisible);
        Assert.False(d.HoldButton.IsVisible);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Fit_and_actual_size_buttons_set_the_shared_view()
    {
        var (w, _) = Open();
        await ImportOne(w, Photo(600, 400));
        var d = await OpenEditor(w);
        d.Session.Change(r => r with { Crop = new RectI(100, 100, 200, 150) });
        Settle(d);

        Click(d.ActualButton);
        Assert.Equal(1, d.LeftPane.View.Zoom);
        Click(d.FitButton);
        double wholeZoom = d.LeftPane.View.Zoom;
        Assert.Equal(300, d.LeftPane.View.CenterX, 3);                       // the middle of the canvas
        Assert.True(d.FitResultButton.IsVisible);
        Click(d.FitResultButton);
        Assert.True(d.LeftPane.View.Zoom > wholeZoom);
        Assert.Equal(200, d.LeftPane.View.CenterX, 3);                       // the middle of the kept part
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    // ---- the picture in other places ----

    [AvaloniaFact]
    public async Task Export_copy_and_a_fresh_Vectorize_read_the_edited_picture_at_full_resolution()
    {
        var (w, _) = Open();
        var img = await ImportOne(w, Photo(600, 300));
        var d = await OpenEditor(w);
        d.Session.Change(r => r with { Crop = new RectI(0, 0, 300, 150), Adjust = r.Adjust with { Hue = 120 } });
        Settle(d);
        Click(d.OkButton);
        Pump(() => w.OpenPictureEditor is null);

        // the stored picture is the finished one (everything that reads a picture reads this)
        var stored = Pixels(w, img.AssetId);
        Assert.Equal((300, 150), (stored.Width, stored.Height));
        // a copy offers the finished picture to other programs
        var clip = new FakeClipboard();
        w.SystemClipboard = clip;
        w.Session.Selection.Set(img);
        w.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        await w.ClipboardIdle;
        Assert.Equal((300, 150), (ImageCodec.Probe(clip.Png!).Width, ImageCodec.Probe(clip.Png!).Height));
        // and the pasted object is still editable from its original
        var copy = Assert.IsType<ImageObject>(w.Session.ClipboardObjects[0]);
        Assert.Equal(img.SourceAssetId, copy.SourceAssetId);
        Assert.Equal(img.Recipe, copy.Recipe);
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_edit_survives_saving_and_opening_and_can_be_edited_again()
    {
        var (w, _) = Open();
        var photo = Photo(600, 300);
        var img = await ImportOne(w, photo);
        var d = await OpenEditor(w);
        d.Session.Change(r => r with { Crop = new RectI(50, 20, 400, 200), Adjust = r.Adjust with { Contrast = 20 } });
        Settle(d);
        Click(d.OkButton);
        Pump(() => w.OpenPictureEditor is null);

        var loaded = DsmReader.Load(new MemoryStream(DsmWriter.SaveToBytes(w.Session.Document))).Document;
        var back = loaded.AllObjects().OfType<ImageObject>().Single();
        Assert.Equal(img.Recipe, back.Recipe);
        Assert.Equal(img.SourceAssetId, back.SourceAssetId);

        w.Session.Document.Assets.Get(back.SourceAssetId!);                  // (the live document still has it, too)
        var second = new EditorSession(loaded) { ImageService = w.Session.ImageService };
        second.Selection.Set(back);
        Assert.NotNull(second.EditPictureTarget);
        Assert.Equal(photo.Width, Pixels2(loaded, back.SourceAssetId!).Width);
        w.Close();
    }

    private static PixelBuffer Pixels2(Document doc, string id) => ImageCodec.Decode(doc.Assets.Get(id)!.Data);

    [AvaloniaFact]
    public async Task A_big_photo_is_edited_through_a_reduced_copy_and_the_result_is_full_resolution()
    {
        var (w, _) = Open();
        var big = new PixelBuffer(3600, 2400);
        for (int y = 0; y < 2400; y++) for (int x = 0; x < 3600; x++) big.Set(x, y, (byte)(x / 15), (byte)(y / 10), 120);
        var img = await ImportOne(w, big, "big.png");
        var d = await OpenEditor(w);

        Assert.True(d.Session.WorkScale < 1);
        Assert.True(Math.Max(d.Session.Work.Width, d.Session.Work.Height) <= PictureProcessor.PreviewSide);
        Assert.True(Math.Max(d.Session.ResultPreview!.Width, d.Session.ResultPreview.Height) <= PictureProcessor.PreviewSide);

        d.Session.Change(r => r with { Crop = new RectI(600, 400, 1800, 1200), Adjust = r.Adjust with { Brightness = 10 } });
        Settle(d);
        Assert.Equal(1800 * d.Session.WorkScale, d.Session.ResultPreview!.Width, 0);   // the preview is the crop at the reduced size
        Click(d.OkButton);
        Pump(() => w.OpenPictureEditor is null, 60000);

        var result = Pixels(w, img.AssetId);
        Assert.Equal((1800, 1200), (result.Width, result.Height));            // the real result is not reduced
        w.Close();
    }

    // ---- turning and flipping, the free angle ----

    [AvaloniaFact]
    public async Task The_rotate_and_flip_buttons_turn_the_picture_and_the_live_canvas_follows()
    {
        var (w, _) = Open();
        var img = await ImportOne(w, Photo(300, 200));
        double w0 = img.Width, h0 = img.Height;
        var d = await OpenEditor(w);

        Assert.False(d.ResetOrientationButton.IsVisible);
        Click(d.RotateRightButton);
        Settle(d);
        Assert.Equal(1, d.Session.Recipe.Quarter);
        Assert.Equal((200, 300), d.Session.Recipe.CanvasSize);
        Assert.Equal((200, 300), (d.Session.ResultPreview!.Width, d.Session.ResultPreview.Height));
        Assert.Equal(h0, img.Width, 6);                           // the picture behind the window is turned too, around its middle
        Assert.Equal(w0, img.Height, 6);
        Assert.Equal("200", d.CropWidthBox.Text);
        Assert.True(d.ResetOrientationButton.IsVisible);
        Assert.True(d.ResetAllButton.IsVisible);

        Click(d.FlipHorizontalButton);
        Settle(d);
        Assert.True(d.Session.Recipe.Mirror);
        Click(d.ResetOrientationButton);
        Settle(d);
        Assert.Equal((false, 0), (d.Session.Recipe.Mirror, d.Session.Recipe.Quarter));
        Assert.True(d.Session.Recipe.IsIdentity);
        Assert.False(d.ResetOrientationButton.IsVisible);
        Assert.Equal(w0, img.Width, 6);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_crop_goes_along_when_the_picture_is_turned_and_the_result_is_the_turned_crop()
    {
        var (w, _) = Open();
        var photo = Photo(300, 200);
        var img = await ImportOne(w, photo);
        var d = await OpenEditor(w);
        d.Session.Change(r => r with { Crop = new RectI(100, 50, 120, 60) });
        Settle(d);
        var before = d.Session.ResultPreview!;

        Click(d.RotateRightButton);
        Settle(d);

        Assert.Equal(new RectI(90, 100, 60, 120), d.Session.Recipe.Crop);              // (200 - 50 - 60, 100, 60, 120)
        var after = d.Session.ResultPreview!;
        Assert.Equal((60, 120), (after.Width, after.Height));
        var turned = PixelOps.Rotate90(before, clockwise: true);
        Assert.True(turned.Rgba.AsSpan().SequenceEqual(after.Rgba), "the result is the old result turned");
        Click(d.OkButton);
        Pump(() => w.OpenPictureEditor is null);
        Assert.Equal(1, img.Recipe!.Quarter);
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_angle_box_and_the_dial_set_any_angle_up_to_180_and_stay_in_step()
    {
        var (w, _) = Open();
        await ImportOne(w, Photo(300, 200));
        var d = await OpenEditor(w);

        d.AngleBox.Text = "135";
        Settle(d);
        Assert.Equal(135, d.Session.Recipe.Straighten, 6);
        Assert.Equal(135, d.Dial.Angle, 6);
        d.AngleBox.Text = "400";
        Assert.Equal(180, d.Session.Recipe.Straighten, 6);                              // limited to a half turn either way
        d.AngleBox.Text = "-90";
        Assert.Equal(-90, d.Dial.Angle, 6);
        Assert.False(d.RemoveCornersButton.IsVisible);                                 // turned by a quarter: no empty corners

        d.Dial.Width = d.Dial.Height = 60;
        d.Dial.Measure(new Size(60, 60));
        d.Dial.Arrange(new Rect(0, 0, 60, 60));
        d.Dial.DragTo(new Point(55, 30));                                              // the right-hand side of the dial
        Assert.Equal(90, d.Session.Recipe.Straighten, 6);
        Assert.Equal("90", d.AngleBox.Text);
        d.Dial.DragTo(new Point(35, 5));                                               // a little right of straight up
        Assert.Equal(11, d.Session.Recipe.Straighten, 6);
        Assert.True(d.RemoveCornersButton.IsVisible);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    [Fact]
    public void The_dial_finds_the_angle_of_a_point_and_snaps_near_the_main_directions()
    {
        Assert.Equal(0, AngleDial.AngleAt(30, 5, 60, 60));
        Assert.Equal(90, AngleDial.AngleAt(55, 30, 60, 60));
        Assert.Equal(180, AngleDial.AngleAt(30, 55, 60, 60));
        Assert.Equal(-90, AngleDial.AngleAt(5, 30, 60, 60));
        Assert.Equal(0, AngleDial.AngleAt(31, 5, 60, 60));                                // 2 degrees off: snaps to straight up
        Assert.Equal(45, AngleDial.AngleAt(30 + 25 * Math.Sin(Math.PI / 4), 30 - 25 * Math.Cos(Math.PI / 4), 60, 60));
        Assert.Null(AngleDial.AngleAt(30, 31, 60, 60));                                   // the middle has no direction
    }

    // ---- effects ----

    private static List<Slider> EffectSliders(PictureEditorDialog d) => Find<Slider>(d.EffectsTab).ToList();   // Noise, Blur, Sharpen, Sepia, Black & white, Levels

    [AvaloniaFact]
    public async Task The_Effects_tab_has_its_sliders_and_every_one_has_its_own_reset_that_shows_only_when_it_is_changed()
    {
        var (w, _) = Open();
        await ImportOne(w, Photo(80, 60));
        var d = await OpenEditor(w);
        d.Tabs.SelectedItem = d.EffectsTab;
        var sliders = EffectSliders(d);
        Assert.Equal(6, sliders.Count);
        var resets = Find<Button>(d.EffectsTab).Where(b => (b.Content as string) == "↺").ToList();
        Assert.Equal(6, resets.Count);
        Assert.All(resets, r => Assert.False(r.IsVisible));

        sliders[0].Value = 40;      // noise
        sliders[1].Value = 3.5;     // blur
        sliders[2].Value = 60;      // sharpen
        sliders[3].Value = 30;      // sepia
        sliders[4].Value = 120;     // black and white level
        sliders[5].Value = 4;       // posterize levels
        var fx = d.Session.Recipe.Effects;
        Assert.Equal((40, 3.5, 60, 30, 120, 4), (fx.NoiseReduction, fx.Blur, fx.Sharpen, fx.Sepia, fx.Threshold, fx.Posterize));
        Assert.All(resets, r => Assert.True(r.IsVisible));

        Click(resets[1]);
        Assert.Equal(0, d.Session.Recipe.Effects.Blur);
        Assert.False(resets[1].IsVisible);
        Assert.True(resets[0].IsVisible);                                              // the others stay
        Assert.Equal(40, d.Session.Recipe.Effects.NoiseReduction);
        Click(resets[5]);
        Assert.Equal(1, d.Session.Recipe.Effects.Posterize);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_switches_and_the_number_boxes_of_the_Effects_tab_change_the_recipe()
    {
        var (w, _) = Open();
        await ImportOne(w, Photo(80, 60));
        var d = await OpenEditor(w);
        d.Tabs.SelectedItem = d.EffectsTab;

        d.GrayscaleBox.IsChecked = true;
        d.InvertBox.IsChecked = true;
        Assert.True(d.Session.Recipe.Effects.Grayscale);
        Assert.True(d.Session.Recipe.Effects.Invert);
        Settle(d);
        var preview = d.Session.ResultPreview!;
        var (r, g, b, _) = preview.Get(10, 10);
        Assert.True(r == g && g == b);                                                  // gray, and inverted

        var boxes = Find<TextBox>(d.EffectsTab).ToList();
        boxes[4].Text = "100";                                                          // the black & white level
        Assert.Equal(100, d.Session.Recipe.Effects.Threshold);
        Click(d.ResetAllButton);
        Assert.True(d.Session.Recipe.IsIdentity);
        Assert.False(d.GrayscaleBox.IsChecked);                                         // the switches follow Reset all
        Assert.False(d.InvertBox.IsChecked);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_picture_with_effects_is_changed_live_and_OK_gives_the_full_resolution_result_of_the_whole_recipe()
    {
        var (w, _) = Open();
        var photo = Photo(240, 160);
        var img = await ImportOne(w, photo);
        string original = img.AssetId;
        var d = await OpenEditor(w);
        var recipe = new PictureRecipe(240, 160)
        {
            Quarter = 1, Mirror = true, Straighten = 6, Crop = new RectI(10, 10, 120, 150),
            Adjust = PictureAdjust.None with { Contrast = 15, Saturation = 20 },
            Effects = PictureEffects.None with { NoiseReduction = 30, Blur = 1.5, Sharpen = 30, Sepia = 20, Posterize = 8 },
        };
        d.Session.Change(_ => recipe);
        Settle(d);
        Assert.NotEqual(original, img.AssetId);                                         // the canvas behind already shows the preview
        Click(d.OkButton);
        Pump(() => w.OpenPictureEditor is null);

        var expected = PictureProcessor.Render(photo, d.Session.Recipe.Normalized());
        var shown = Pixels(w, img.AssetId);
        Assert.Equal((expected.Width, expected.Height), (shown.Width, shown.Height));
        Assert.True(expected.Rgba.AsSpan().SequenceEqual(shown.Rgba), "the stored picture is the recipe rendered at full resolution");
        Assert.Equal(recipe.Normalized(), img.Recipe);
        Assert.Equal(original, img.SourceAssetId);
        Assert.Equal("Edit Picture", w.Session.History.UndoDescription);
        w.Session.Undo();
        Assert.Equal(original, img.AssetId);
        Assert.Null(img.Recipe);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Black_and_white_and_few_colors_leave_only_a_few_values_in_the_picture_that_Vectorize_traces()
    {
        var (w, _) = Open();
        var img = await ImportOne(w, Photo(120, 90));
        var d = await OpenEditor(w);
        d.Session.Change(r => r with { Effects = r.Effects with { Threshold = 128 } });
        Click(d.OkButton);
        Pump(() => w.OpenPictureEditor is null);
        var pixels = Pixels(w, img.AssetId);                                            // the asset every command (Vectorize, export, copy) uses
        var values = new HashSet<byte>();
        for (int i = 0; i < pixels.Rgba.Length; i += 4) { values.Add(pixels.Rgba[i]); values.Add(pixels.Rgba[i + 1]); values.Add(pixels.Rgba[i + 2]); }
        Assert.Subset(new HashSet<byte> { 0, 255 }, values);
        w.Close();
    }

    // ---- Image menu commands on edited pictures ----

    [AvaloniaFact]
    public async Task The_Image_menu_rotate_flip_and_invert_change_the_recipe_of_an_edited_picture_and_keep_the_original()
    {
        var (w, _) = Open();
        var photo = Photo(300, 200);
        var img = await ImportOne(w, photo);
        var d = await OpenEditor(w);
        d.Session.Change(r => r with { Crop = new RectI(50, 40, 150, 100), Adjust = r.Adjust with { Brightness = 10 } });
        Click(d.OkButton);
        Pump(() => w.OpenPictureEditor is null);
        string original = img.SourceAssetId!;

        Assert.True(ImageActions.RotateClockwise(w.Session));
        Assert.Equal(1, img.Recipe!.Quarter);
        Assert.Equal(original, img.SourceAssetId);
        var expected = PictureProcessor.Render(photo, img.Recipe);
        Assert.True(expected.Rgba.AsSpan().SequenceEqual(Pixels(w, img.AssetId).Rgba), "rebuilt from the original");

        Assert.True(ImageActions.Invert(w.Session));
        Assert.True(img.Recipe.Effects.Invert);
        w.Session.Undo();
        Assert.False(img.Recipe.Effects.Invert);
        w.Session.Undo();
        Assert.Equal(0, img.Recipe.Quarter);
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_Image_menu_commands_still_rotate_a_plain_picture_on_its_pixels_without_keeping_a_second_copy()
    {
        var (w, _) = Open();
        var img = await ImportOne(w, Photo(60, 40));
        Assert.True(ImageActions.RotateClockwise(w.Session));
        Assert.Null(img.Recipe);
        Assert.Null(img.SourceAssetId);
        Assert.Equal((40, 60), (img.PixelWidth, img.PixelHeight));
        w.Close();
    }

    // ---- Flatten and the Context Bar ----

    private static void EditWithCrop(MainWindow w, ImageObject img)
    {
        w.Session.Selection.Set(img);
        w.Session.RequestPictureEdit(img);
        Pump(() => w.OpenPictureEditor is { Session.PreviewsMade: >= 1 });
        var d = w.OpenPictureEditor!;
        d.Session.Change(r => r with { Crop = new RectI(5, 5, 30, 20) });
        Click(d.OkButton);
        Pump(() => w.OpenPictureEditor is null);
    }

    [AvaloniaFact]
    public async Task The_Context_Bar_shows_Edit_Photo_for_one_picture_and_Flatten_only_for_pictures_with_edits_Edit_Photo_first()
    {
        var (w, _) = Open();
        var a = await ImportOne(w, Photo(80, 60), "a.png");
        var row = ImageRow(w);
        Assert.True(row.EditButton.IsVisible);
        Assert.False(row.FlattenButton.IsVisible);                                      // nothing to flatten: hidden, not greyed
        Assert.True(row.Children.IndexOf(row.EditButton) < row.Children.IndexOf(row.FlattenButton));
        var rotate = row.Children.OfType<Button>().Single(b => (b.Content as string) == "Rotate ↻");
        Assert.True(row.Children.IndexOf(row.FlattenButton) < row.Children.IndexOf(rotate));       // before the other picture buttons

        EditWithCrop(w, a);
        row = ImageRow(w);
        Assert.True(row.EditButton.IsVisible);
        Assert.True(row.FlattenButton.IsVisible);
        Assert.Equal("Flatten", row.FlattenButton.Content);

        var b = await ImportOne(w, Photo(80, 60), "b.png");
        w.Session.Selection.Set([a, b]);
        row = ImageRow(w);
        Assert.False(row.EditButton.IsVisible);                                         // Edit Photo is for exactly one picture
        Assert.True(row.FlattenButton.IsVisible);                                       // one of the two has edits
        w.Session.Selection.Set(b);
        Assert.False(ImageRow(w).FlattenButton.IsVisible);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Flatten_bakes_the_edits_in_and_is_one_undo_step_from_the_Context_Bar_and_the_Image_menu()
    {
        var (w, _) = Open();
        var a = await ImportOne(w, Photo(80, 60), "a.png");
        EditWithCrop(w, a);
        string finished = a.AssetId, original = a.SourceAssetId!;
        var pixels = Pixels(w, finished);
        var flatten = ImageCommand(w, "image.flatten");
        Assert.Equal("Flatten Picture", flatten.Header);
        Assert.Empty(flatten.Gestures);
        Assert.True(flatten.CanExecute());

        Click(ImageRow(w).FlattenButton);
        Assert.Null(a.Recipe);
        Assert.Null(a.SourceAssetId);
        Assert.Equal(finished, a.AssetId);
        Assert.True(pixels.Rgba.AsSpan().SequenceEqual(Pixels(w, a.AssetId).Rgba));
        Assert.False(flatten.CanExecute());                                              // greyed in the menu when there is nothing to flatten
        Assert.False(ImageRow(w).FlattenButton.IsVisible);                               // hidden on the Context Bar
        Assert.Equal("Flatten Picture", w.Session.History.UndoDescription);

        w.Session.Undo();
        Assert.Equal(original, a.SourceAssetId);
        Assert.NotNull(a.Recipe);
        Assert.True(flatten.CanExecute());
        flatten.Execute();                                                               // the menu entry does the same
        Assert.Null(a.Recipe);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_flattened_picture_opens_in_the_editor_afresh_and_the_file_no_longer_holds_the_original()
    {
        var (w, _) = Open();
        var a = await ImportOne(w, Photo(80, 60), "a.png");
        EditWithCrop(w, a);
        w.Session.FlattenPictures();
        var d = await OpenEditor(w);
        Assert.True(d.Session.Recipe.IsIdentity);
        Assert.Equal(a.AssetId, d.Session.SourceAssetId);                               // the flattened pixels are now the original
        Assert.Equal((30, 20), (d.Session.Source.Width, d.Session.Source.Height));
        CloseWith(w, d.CancelButton);
        var saved = DsmWriter.SaveToBytes(w.Session.Document);
        var back = DsmReader.Load(new MemoryStream(saved)).Document.AllObjects().OfType<ImageObject>().Single();
        Assert.Null(back.SourceAssetId);
        Assert.Null(back.Recipe);
        w.Close();
    }

    // ---- screenshots ----

    [AvaloniaFact]
    public async Task Screenshots()
    {
        // Development aid: set DSM_SCREENSHOTS to a folder to save pictures of the editor there (Crop and Adjust, light and dark).
        var folder = Environment.GetEnvironmentVariable("DSM_SCREENSHOTS");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);

        var (w, _) = Open();
        w.Width = 1500; w.Height = 950;
        var photo = Landscape(1200, 800);
        await ImportOne(w, photo, "mountain.png");
        var d = await OpenEditor(w);
        d.Width = 1280; d.Height = 840;
        Pump(() => d.LeftPane.Bounds.Width > 100);
        d.Session.Change(r => r with { Straighten = 3.5, Crop = new RectI(180, 140, 800, 520), Adjust = r.Adjust with { Contrast = 18, Saturation = 25 } });
        Settle(d);
        d.FitCanvas();
        Dispatcher.UIThread.RunJobs();
        Save(d, folder, "picture-editor-crop-light.png");

        d.Tabs.SelectedItem = d.AdjustTab;
        d.Session.Change(r => r with { Adjust = r.Adjust with { Brightness = 12, Gamma = 1.2, Hue = -15, BalanceR = 8, LowR = 8, HighR = 245, LowG = 6, HighG = 250 } });
        d.SyncFields();
        Settle(d);
        Save(d, folder, "picture-editor-adjust-light.png");

        d.Tabs.SelectedItem = d.EffectsTab;
        d.Session.Change(r => r with { Effects = r.Effects with { NoiseReduction = 25, Blur = 0.8, Sharpen = 35, Sepia = 30, Posterize = 12 } });
        d.SyncFields();
        Settle(d);
        Save(d, folder, "picture-editor-effects-light.png");

        AppTheme.SetDarkMenus(true, remember: false);
        try
        {
            Dispatcher.UIThread.RunJobs();
            d.Tabs.SelectedItem = d.AdjustTab;
            Settle(d);
            Save(d, folder, "picture-editor-adjust-dark.png");
            d.Tabs.SelectedItem = d.EffectsTab;
            Dispatcher.UIThread.RunJobs();
            Save(d, folder, "picture-editor-effects-dark.png");
            d.Tabs.SelectedItem = d.CropTab;
            Dispatcher.UIThread.RunJobs();
            Save(d, folder, "picture-editor-crop-dark.png");
        }
        finally { AppTheme.SetDarkMenus(false, remember: false); }
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    private static void Save(Window window, string folder, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame()!;
#pragma warning disable CS0618
        frame.Save(Path.Combine(folder, name));
#pragma warning restore CS0618
    }

    private static PixelBuffer Landscape(int w, int h)
    {
        var p = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double t = (double)y / h;
                byte r = (byte)(235 - 110 * t), g = (byte)(170 + 20 * t), b = (byte)(110 + 120 * t);                      // sky
                double ridge = h * 0.52 + h * 0.10 * Math.Sin(x / 140.0) + h * 0.05 * Math.Sin(x / 47.0);
                if (y > ridge) { double k = (y - ridge) / (h - ridge); r = (byte)(40 + 25 * k); g = (byte)(90 + 40 * k); b = (byte)(60 + 20 * k); }   // hills
                double dx = x - w * 0.72, dy = y - h * 0.28;
                if (dx * dx + dy * dy < 55 * 55) { r = 255; g = 238; b = 160; }                                           // sun
                p.Set(x, y, r, g, b);
            }
        return p;
    }
}

internal static class LogicalExtensions
{
    public static IEnumerable<T> GetLogicalDescendantsOfType<T>(this Avalonia.LogicalTree.ILogical root) where T : class =>
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<T>();
}
