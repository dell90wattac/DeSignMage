using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DeSignMage.App.Canvas;
using DeSignMage.App.Shell;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Native;
using DeSignMage.Imaging;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>The Transparency tab of the Picture Editor (colors, plain background, lasso cut-outs, edge, opacity) and Edit in... (the picture comes back from another program).</summary>
public class PictureTransparencyEditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dsm-transparency-tests-" + Guid.NewGuid().ToString("N"));

    public PictureTransparencyEditorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        PictureSettings.LaunchOverride = null;
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

    /// <summary>White background, a blue-ish "logo" square in the middle, and a white dot inside the square.</summary>
    private static PixelBuffer Logo(int w = 200, int h = 150)
    {
        var p = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool inside = x >= 50 && x < w - 50 && y >= 30 && y < h - 30;
                bool dot = Math.Abs(x - w / 2) < 8 && Math.Abs(y - h / 2) < 8;
                if (inside && !dot) p.Set(x, y, 20, 60, 200);
                else p.Set(x, y, 255, 255, 255);
            }
        return p;
    }

    private string Write(string name, PixelBuffer p)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, ImageCodec.EncodePng(p));
        return path;
    }

    private async Task<ImageObject> ImportOne(MainWindow w, PixelBuffer photo, string name = "logo.png")
    {
        Assert.Equal(1, await w.ImportImagesAsync([Write(name, photo)], null));
        Dispatcher.UIThread.RunJobs();
        return Assert.IsType<ImageObject>(Assert.Single(w.Session.Selection.Items));
    }

    private static async Task<PictureEditorDialog> OpenEditor(MainWindow w)
    {
        w.Session.RequestPictureEdit((ImageObject)w.Session.Selection.Items[0]);
        Pump(() => w.OpenPictureEditor is not null);
        var d = w.OpenPictureEditor!;
        Pump(() => d.Session.PreviewsMade >= 1);
        await Task.CompletedTask;
        return d;
    }

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

    private static PictureEditorDialog ToTransparency(PictureEditorDialog d)
    {
        d.Tabs.SelectedItem = d.TransparencyTab;
        Dispatcher.UIThread.RunJobs();
        return d;
    }

    // ---- the tab ----

    [AvaloniaFact]
    public async Task The_editor_has_a_Transparency_tab_between_Adjust_and_Effects()
    {
        var (w, _) = Open();
        await ImportOne(w, Logo());
        var d = await OpenEditor(w);
        var headers = d.Tabs.Items.OfType<TabItem>().Select(t => (string)t.Header!).ToList();
        Assert.Equal(["Crop", "Adjust", "Transparency", "Effects"], headers);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Controls_that_cannot_do_anything_yet_are_not_shown()
    {
        var (w, _) = Open();
        await ImportOne(w, Logo());
        var d = ToTransparency(await OpenEditor(w));
        Assert.False(d.EdgeOnlyBox.IsVisible);
        Assert.False(d.PutColorsBackButton.IsVisible);
        Assert.False(d.UndoCutButton.IsVisible);
        Assert.False(d.ClearCutsButton.IsVisible);
        Assert.True(d.PickColorButton.IsVisible);
        Assert.True(d.RemoveBackgroundButton.IsVisible);
        d.PickColorAt(new Vec2(2, 2));
        Settle(d);
        Assert.True(d.EdgeOnlyBox.IsVisible);
        Assert.True(d.PutColorsBackButton.IsVisible);
        Assert.Single(d.Swatches.Children);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    // ---- remove a color ----

    [AvaloniaFact]
    public async Task Picking_the_white_background_makes_it_transparent_in_the_result_and_the_canvas_behind()
    {
        var (w, _) = Open();
        var logo = await ImportOne(w, Logo());
        string originalAsset = logo.AssetId;
        var d = ToTransparency(await OpenEditor(w));
        d.SelectTool(PictureEditorDialog.TransparencyTool.Pick);
        d.TransparencyDown(new Vec2(3, 3));                  // a white pixel of the original
        Settle(d);
        Assert.Equal([0xFFFFFF], d.Session.Recipe.Transparency.Colors);
        Assert.Equal(0, d.Session.ResultPreview!.Get(3, 3).A);
        Assert.Equal(255, d.Session.ResultPreview.Get(100, 40).A);
        // the picture in the design behind the window shows the preview
        Assert.NotEqual(originalAsset, logo.AssetId);
        Assert.Equal(0, Pixels(w, logo.AssetId).Get(3, 3).A);
        CloseWith(w, d.CancelButton);
        Assert.Equal(originalAsset, logo.AssetId);           // Cancel puts everything back
        Assert.Null(logo.Recipe);
        w.Close();
    }

    [AvaloniaFact]
    public async Task OK_makes_a_cut_out_with_a_recipe_one_undo_step_and_full_resolution_alpha()
    {
        var (w, _) = Open();
        var logo = await ImportOne(w, Logo());
        int undoBefore = w.Session.History.UndoList.Count;
        var d = ToTransparency(await OpenEditor(w));
        d.SelectTool(PictureEditorDialog.TransparencyTool.Pick);
        d.TransparencyDown(new Vec2(3, 3));
        Settle(d);
        CloseWith(w, d.OkButton);
        Assert.True(d.Committed);
        Assert.Equal(undoBefore + 1, w.Session.History.UndoList.Count);
        Assert.NotNull(logo.SourceAssetId);
        Assert.Equal([0xFFFFFF], logo.Recipe!.Transparency.Colors);
        var px = Pixels(w, logo.AssetId);
        Assert.Equal((200, 150), (px.Width, px.Height));
        Assert.Equal(0, px.Get(3, 3).A);
        Assert.Equal(0, px.Get(100, 75).A);                  // the white dot in the middle goes as well (everywhere mode)
        Assert.Equal(255, px.Get(60, 40).A);
        w.Session.Undo();
        Assert.Null(logo.Recipe);
        Assert.Equal(255, Pixels(w, logo.AssetId).Get(3, 3).A);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Remove_plain_background_takes_the_edge_color_from_the_edge_inward_and_keeps_the_white_dot()
    {
        var (w, _) = Open();
        var logo = await ImportOne(w, Logo());
        var d = ToTransparency(await OpenEditor(w));
        Click(d.RemoveBackgroundButton);
        Settle(d);
        var t = d.Session.Recipe.Transparency;
        Assert.Equal([0xFFFFFF], t.Colors);
        Assert.True(t.EdgeOnly);
        Assert.True(d.EdgeOnlyBox.IsChecked);
        var result = d.Session.ResultPreview!;
        Assert.Equal(0, result.Get(3, 3).A);
        Assert.Equal(255, result.Get(100, 75).A);            // the white dot inside the logo stays
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Remove_plain_background_says_so_when_the_picture_has_no_plain_edge()
    {
        var (w, _) = Open();
        var photo = new PixelBuffer(120, 90);
        for (int y = 0; y < 90; y++) for (int x = 0; x < 120; x++) photo.Set(x, y, (byte)(x * 2), (byte)(y * 2), (byte)(x + y));
        await ImportOne(w, photo);
        var d = ToTransparency(await OpenEditor(w));
        Click(d.RemoveBackgroundButton);
        Assert.True(d.Session.Recipe.Transparency.IsIdentity);
        Assert.Contains("No plain background", d.TransparencyNote.Text);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_tolerance_slider_decides_how_near_a_color_goes_and_a_swatch_puts_a_color_back()
    {
        var (w, _) = Open();
        var shaded = new PixelBuffer(60, 20);
        for (int y = 0; y < 20; y++) for (int x = 0; x < 60; x++) { byte v = (byte)(255 - x * 2); shaded.Set(x, y, v, v, v); }   // white fading to gray
        await ImportOne(w, shaded);
        var d = ToTransparency(await OpenEditor(w));
        d.PickColorAt(new Vec2(0, 5));
        Settle(d);
        int Gone() { var r = d.Session.ResultPreview!; int n = 0; for (int x = 0; x < r.Width; x++) if (r.Get(x, 5).A == 0) n++; return n; }
        int narrow = Gone();
        d.Session.Change(r => r with { Transparency = r.Transparency with { Tolerance = 40 } });
        Settle(d);
        Assert.True(Gone() > narrow + 10, "a larger tolerance removes more of the fade");
        d.SyncFields();
        var swatch = Assert.IsType<Button>(Assert.Single(d.Swatches.Children));
        Click(swatch);
        Settle(d);
        Assert.True(d.Session.Recipe.Transparency.IsIdentity);
        Assert.Equal(0, Gone());
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Picking_outside_the_picture_or_on_a_clear_pixel_does_nothing()
    {
        var (w, _) = Open();
        await ImportOne(w, Logo());
        var d = ToTransparency(await OpenEditor(w));
        d.PickColorAt(new Vec2(-5, 10));
        d.PickColorAt(new Vec2(500, 10));
        Assert.True(d.Session.Recipe.Transparency.IsIdentity);
        d.PickColorAt(new Vec2(3, 3));
        d.PickColorAt(new Vec2(4, 4));                       // the same white again
        Assert.Single(d.Session.Recipe.Transparency.Colors);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    // ---- lasso ----

    [AvaloniaFact]
    public async Task The_lasso_cuts_out_what_is_drawn_around_and_keeps_it_as_an_outline_on_the_original()
    {
        var (w, _) = Open();
        var logo = await ImportOne(w, Logo());
        var d = ToTransparency(await OpenEditor(w));
        d.SelectTool(PictureEditorDialog.TransparencyTool.Lasso);
        d.TransparencyDown(new Vec2(60, 40));
        for (int i = 1; i <= 20; i++) d.TransparencyMove(new Vec2(60 + i * 4, 40));        // along the top
        for (int i = 1; i <= 20; i++) d.TransparencyMove(new Vec2(140, 40 + i * 3));       // down the right
        for (int i = 1; i <= 20; i++) d.TransparencyMove(new Vec2(140 - i * 4, 100));      // back along the bottom
        for (int i = 1; i <= 20; i++) d.TransparencyMove(new Vec2(60, 100 - i * 3));       // up the left
        d.TransparencyUp(new Vec2(60, 40));
        Settle(d);
        var cut = Assert.Single(d.Session.Recipe.Transparency.Cuts);
        Assert.True(cut.Keep);
        Assert.InRange(cut.Points.Count, 4, 12);             // the many mouse points were thinned to the corners
        Assert.Equal(255, d.Session.ResultPreview!.Get(100, 70).A);
        Assert.Equal(0, d.Session.ResultPreview.Get(10, 10).A);
        Assert.Equal(0, d.Session.ResultPreview.Get(180, 140).A);
        Assert.True(d.UndoCutButton.IsVisible);
        CloseWith(w, d.OkButton);
        Assert.Single(logo.Recipe!.Transparency.Cuts);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Remove_inside_cuts_a_hole_and_Undo_last_cut_takes_it_away()
    {
        var (w, _) = Open();
        await ImportOne(w, Logo());
        var d = ToTransparency(await OpenEditor(w));
        d.RemoveInsideRadio.IsChecked = true;
        d.AddCut([new Vec2(80, 60), new Vec2(120, 60), new Vec2(120, 90), new Vec2(80, 90)], d.KeepInside);
        Settle(d);
        Assert.False(d.Session.Recipe.Transparency.Cuts[0].Keep);
        Assert.Equal(0, d.Session.ResultPreview!.Get(90, 70).A);
        Assert.Equal(255, d.Session.ResultPreview.Get(10, 10).A);
        Click(d.UndoCutButton);
        Settle(d);
        Assert.Empty(d.Session.Recipe.Transparency.Cuts);
        Assert.Equal(255, d.Session.ResultPreview!.Get(90, 70).A);
        Assert.False(d.UndoCutButton.IsVisible);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_polygon_lasso_takes_corners_and_closes_on_the_first_corner_or_Enter()
    {
        var (w, _) = Open();
        await ImportOne(w, Logo());
        var d = ToTransparency(await OpenEditor(w));
        d.SelectTool(PictureEditorDialog.TransparencyTool.Polygon);
        foreach (var p in new[] { new Vec2(60, 40), new Vec2(140, 40), new Vec2(140, 110) }) d.TransparencyDown(p);
        Assert.Equal(3, d.OutlineInProgress.Count);
        d.TransparencyDown(new Vec2(60, 40));                // the first corner again closes it
        Settle(d);
        Assert.Single(d.Session.Recipe.Transparency.Cuts);
        Assert.Empty(d.OutlineInProgress);
        // a second one closed with Enter
        foreach (var p in new[] { new Vec2(10, 10), new Vec2(40, 10), new Vec2(40, 30) }) d.TransparencyDown(p);
        d.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Settle(d);
        Assert.Equal(2, d.Session.Recipe.Transparency.Cuts.Count);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Escape_drops_the_outline_being_drawn_first_and_the_tool_next_and_does_not_close_the_editor()
    {
        var (w, _) = Open();
        await ImportOne(w, Logo());
        var d = ToTransparency(await OpenEditor(w));
        d.SelectTool(PictureEditorDialog.TransparencyTool.Polygon);
        d.TransparencyDown(new Vec2(60, 40));
        d.TransparencyDown(new Vec2(100, 40));
        d.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Assert.Empty(d.OutlineInProgress);
        Assert.Equal(PictureEditorDialog.TransparencyTool.Polygon, d.Tool);
        d.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Assert.Equal(PictureEditorDialog.TransparencyTool.None, d.Tool);
        Assert.NotNull(w.OpenPictureEditor);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_cut_drawn_before_turning_the_picture_still_surrounds_the_same_part()
    {
        var (w, _) = Open();
        await ImportOne(w, Logo());
        var d = ToTransparency(await OpenEditor(w));
        d.AddCut([new Vec2(50, 30), new Vec2(150, 30), new Vec2(150, 120), new Vec2(50, 120)], keep: true);   // the blue square
        Settle(d);
        d.Tabs.SelectedItem = d.CropTab;
        d.Orient(r => r.RotatedRight());                     // now 150 wide x 200 high
        Settle(d);
        var result = d.Session.ResultPreview!;
        Assert.Equal((150, 200), (result.Width, result.Height));
        Assert.Equal(0, result.Get(2, 2).A);
        Assert.Equal(255, result.Get(75, 100).A);
        int visible = 0;
        for (int y = 0; y < result.Height; y++) for (int x = 0; x < result.Width; x++) if (result.Get(x, y).A > 128) visible++;
        Assert.InRange(visible, 100 * 90 - 200, 100 * 90 + 200);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    // ---- edge and opacity ----

    [AvaloniaFact]
    public async Task Shrink_soft_edge_and_opacity_are_offered_when_there_is_a_cut_and_change_the_result()
    {
        var (w, _) = Open();
        await ImportOne(w, Logo());
        var d = ToTransparency(await OpenEditor(w));
        d.PickColorAt(new Vec2(3, 3));
        Settle(d);
        var plain = d.Session.ResultPreview!;
        d.Session.Change(r => r with { Transparency = r.Transparency with { Shrink = 3, Softness = 4, Opacity = 50 } });
        Settle(d);
        var edited = d.Session.ResultPreview!;
        Assert.Equal(255, plain.Get(60, 40).A);
        Assert.True(edited.Get(60, 40).A <= 128, "the edge moved in, softly, and the picture is half see-through");
        Assert.InRange((int)edited.Get(100, 75 - 20).A, 100, 140);           // the middle of the picture: about half
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    // ---- Reset, reopen, Flatten ----

    [AvaloniaFact]
    public async Task Reset_all_and_reopening_keep_the_transparency_settings_adjustable()
    {
        var (w, _) = Open();
        var logo = await ImportOne(w, Logo());
        var d = ToTransparency(await OpenEditor(w));
        d.PickColorAt(new Vec2(3, 3));
        d.AddCut([new Vec2(50, 30), new Vec2(150, 30), new Vec2(150, 120)], keep: true);
        Settle(d);
        CloseWith(w, d.OkButton);

        d = ToTransparency(await OpenEditor(w));
        Assert.Single(d.Session.Recipe.Transparency.Colors);
        Assert.Single(d.Session.Recipe.Transparency.Cuts);
        Assert.Equal(logo.SourceAssetId, d.Session.SourceAssetId);
        Click(d.ResetAllButton);
        Settle(d);
        Assert.True(d.Session.Recipe.IsIdentity);
        CloseWith(w, d.OkButton);
        Assert.Null(logo.Recipe);                            // reset + OK: the original again
        w.Close();
    }

    [AvaloniaFact]
    public async Task Flatten_bakes_the_cut_out_into_the_pixels()
    {
        var (w, _) = Open();
        var logo = await ImportOne(w, Logo());
        var d = ToTransparency(await OpenEditor(w));
        d.PickColorAt(new Vec2(3, 3));
        Settle(d);
        CloseWith(w, d.OkButton);
        Assert.True(w.Session.CanFlatten);
        w.Session.FlattenPictures();
        Assert.Null(logo.Recipe);
        Assert.Null(logo.SourceAssetId);
        Assert.Equal(0, Pixels(w, logo.AssetId).Get(3, 3).A);
        w.Close();
    }

    // ---- clicks ----

    [AvaloniaFact]
    public async Task A_click_on_the_transparent_area_of_a_cut_out_does_not_select_the_picture_but_one_on_the_visible_part_does()
    {
        var (w, c) = Open();
        var logo = await ImportOne(w, Logo(200, 150));
        var d = ToTransparency(await OpenEditor(w));
        d.PickColorAt(new Vec2(3, 3));
        Settle(d);
        CloseWith(w, d.OkButton);
        w.Session.ZoomToImagePixels();                       // one picture pixel per screen pixel: the click tolerance is small next to the picture
        Dispatcher.UIThread.RunJobs();
        w.Session.Selection.Clear();
        var b = logo.GetWorldBounds();
        Vec2 At(double fx, double fy) => new(b.Left + b.Width * fx, b.Top + b.Height * fy);

        void Click(Vec2 doc)
        {
            var p = P(c, w.Session.Viewport.ToScreen(doc));
            w.MouseDown(p, MouseButton.Left);
            w.MouseUp(p, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.NotNull(HitTesting.PictureMaskLoader);
        Assert.Null(HitTesting.HitTest(w.Session.Document, At(0.05, 0.05), 1));
        Assert.NotNull(logo.HitMask);
        Click(At(0.05, 0.05));                               // the white corner, now clear
        Assert.True(w.Session.Selection.IsEmpty, "selected: " + string.Join(", ", w.Session.Selection.Items.Select(o => o.GetType().Name + " " + o.GetWorldBounds())));
        Click(At(0.5, 0.45));                              // the blue square
        Assert.Same(logo, Assert.Single(w.Session.Selection.Items));

        w.Session.FlattenPictures();                         // after Flatten the clear corner still does not take clicks
        w.Session.Selection.Clear();
        Click(At(0.05, 0.05));
        Assert.True(w.Session.Selection.IsEmpty);
        w.Close();
    }

    // ---- Edit in... ----

    private static byte[] RedPng(int w, int h)
    {
        var p = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) p.Set(x, y, 220, 30, 30);
        return ImageCodec.EncodePng(p);
    }

    /// <summary>Starts Edit in... without starting a program; returns the temporary file the program would be given.</summary>
    private static string StartEditIn(MainWindow w, PictureEditorDialog d)
    {
        string? opened = null;
        PictureSettings.LaunchOverride = file => { opened = file; return null; };
        Click(d.EditInButton);
        Assert.NotNull(opened);
        return opened!;
    }

    /// <summary>What the other program does: saves the file (with a new time stamp) and, after two looks, the window takes it over.</summary>
    private static void SaveInOtherProgram(MainWindow w, string file, byte[] bytes)
    {
        File.WriteAllBytes(file, bytes);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));
        w.PollExternalEdits();       // the change is noticed
        w.PollExternalEdits();       // it has stopped changing: taken over
        Pump(() => false, 300);
    }

    [AvaloniaFact]
    public async Task Edit_in_writes_the_original_to_a_file_and_starts_the_chosen_program_on_it()
    {
        var (w, _) = Open();
        var logo = await ImportOne(w, Logo());
        var d = await OpenEditor(w);
        Assert.True(d.EditInButton.IsVisible);
        var file = StartEditIn(w, d);
        Assert.StartsWith(Path.GetTempPath(), file);
        Assert.EndsWith(".png", file);
        Assert.True(File.ReadAllBytes(file).AsSpan().SequenceEqual(w.Session.Document.Assets.Get(logo.AssetId)!.Data));
        Assert.Single(w.ExternalEdits);
        Assert.Contains("Waiting for", d.EditInNote.Text);
        CloseWith(w, d.CancelButton);
        w.Session.History.MarkSaved();                      // nothing to ask about when the window closes
        w.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(w.ExternalEdits);
        Assert.False(Directory.Exists(Path.GetDirectoryName(file)));    // the temporary file is cleaned up with the window
    }

    [AvaloniaFact]
    public async Task A_program_that_cannot_be_started_is_reported_in_plain_words_and_nothing_is_watched()
    {
        var (w, _) = Open();
        await ImportOne(w, Logo());
        var d = await OpenEditor(w);
        PictureSettings.LaunchOverride = _ => "Windows has no program set up for editing this kind of picture.";
        Click(d.EditInButton);
        Pump(() => false, 200);
        Assert.Empty(w.ExternalEdits);
        CloseWith(w, d.CancelButton);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_picture_saved_in_the_other_program_replaces_the_original_in_the_open_editor_and_keeps_the_edits()
    {
        var (w, _) = Open();
        var logo = await ImportOne(w, Logo(200, 150));
        var d = ToTransparency(await OpenEditor(w));
        d.PickColorAt(new Vec2(3, 3));
        d.Session.Change(r => r with { Crop = new RectI(20, 15, 100, 75) });
        Settle(d);
        var file = StartEditIn(w, d);

        SaveInOtherProgram(w, file, RedPng(400, 300));       // twice the size
        Pump(() => d.Session.SourceReplaced);
        Settle(d);
        Assert.Equal((400, 300), (d.Session.Source.Width, d.Session.Source.Height));
        Assert.Equal(new RectI(40, 30, 200, 150), d.Session.Recipe.Crop);          // the crop follows the new size
        Assert.Equal([0xFFFFFF], d.Session.Recipe.Transparency.Colors);            // the other edits are kept
        Assert.Contains("Updated from", d.EditInNote.Text);
        Assert.True(d.Session.IsChanged);

        int undoBefore = w.Session.History.UndoList.Count;
        CloseWith(w, d.OkButton);
        Assert.Equal(undoBefore + 1, w.Session.History.UndoList.Count);
        Assert.Equal((200, 150), (logo.PixelWidth, logo.PixelHeight));             // the crop at the new size
        Assert.Equal(400, logo.Recipe!.SourceWidth);
        var px = Pixels(w, logo.AssetId);
        Assert.Equal(220, px.Get(100, 70).R);                                      // the new (red) picture
        w.Session.Undo();
        Assert.Null(logo.Recipe);                                                  // one undo step brings the old picture back
        Assert.Equal((200, 150), (logo.PixelWidth, logo.PixelHeight));
        Assert.Equal(255, Pixels(w, logo.AssetId).Get(3, 3).A);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Cancel_after_a_save_in_the_other_program_puts_the_old_original_back()
    {
        var (w, _) = Open();
        var logo = await ImportOne(w, Logo());
        string before = logo.AssetId;
        var d = await OpenEditor(w);
        var file = StartEditIn(w, d);
        SaveInOtherProgram(w, file, RedPng(200, 150));
        Pump(() => d.Session.SourceReplaced);
        CloseWith(w, d.CancelButton);
        Assert.Equal(before, logo.AssetId);
        Assert.Null(logo.Recipe);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_save_in_the_other_program_after_the_editor_is_closed_updates_the_picture_as_one_undo_step()
    {
        var (w, _) = Open();
        var logo = await ImportOne(w, Logo(200, 150));
        var d = ToTransparency(await OpenEditor(w));
        d.PickColorAt(new Vec2(3, 3));
        Settle(d);
        var file = StartEditIn(w, d);
        CloseWith(w, d.OkButton);                            // the picture has a recipe now; the link goes on
        Assert.Single(w.ExternalEdits);
        int undoBefore = w.Session.History.UndoList.Count;
        string finished = logo.AssetId;

        SaveInOtherProgram(w, file, RedPng(200, 150));
        Pump(() => w.Session.History.UndoList.Count > undoBefore);
        Assert.Equal(undoBefore + 1, w.Session.History.UndoList.Count);
        Assert.NotEqual(finished, logo.AssetId);
        Assert.Equal([0xFFFFFF], logo.Recipe!.Transparency.Colors);                // still a cut-out recipe
        var px = Pixels(w, logo.AssetId);
        Assert.Equal(255, px.Get(3, 3).A);                                         // red is not white: nothing is removed from the new picture
        Assert.Equal(220, px.Get(100, 75).R);
        w.Session.Undo();
        Assert.Equal(finished, logo.AssetId);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_save_for_a_picture_without_edits_replaces_it_at_the_same_printed_size()
    {
        var (w, _) = Open();
        var logo = await ImportOne(w, Logo(200, 150));
        double width = logo.Width, height = logo.Height;
        var d = await OpenEditor(w);
        var file = StartEditIn(w, d);
        CloseWith(w, d.CancelButton);                        // nothing was changed in the editor; the link goes on
        SaveInOtherProgram(w, file, RedPng(400, 300));
        Pump(() => logo.PixelWidth == 400);
        Assert.Equal((400, 300), (logo.PixelWidth, logo.PixelHeight));
        Assert.Equal((width, height), (logo.Width, logo.Height));
        Assert.Null(logo.Recipe);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Saving_in_the_other_program_without_a_change_updates_nothing()
    {
        var (w, _) = Open();
        var logo = await ImportOne(w, Logo());
        var d = await OpenEditor(w);
        var file = StartEditIn(w, d);
        CloseWith(w, d.CancelButton);
        int undoBefore = w.Session.History.UndoList.Count;
        SaveInOtherProgram(w, file, File.ReadAllBytes(file));        // the very same picture, saved again
        Pump(() => false, 300);
        Assert.Equal(undoBefore, w.Session.History.UndoList.Count);
        Assert.Null(logo.Recipe);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_picture_that_leaves_the_design_ends_the_link_and_removes_the_temporary_file()
    {
        var (w, _) = Open();
        var logo = await ImportOne(w, Logo());
        var d = await OpenEditor(w);
        var file = StartEditIn(w, d);
        CloseWith(w, d.CancelButton);
        w.Session.Selection.Set(logo);
        w.Session.DeleteSelection();
        w.PollExternalEdits();
        Assert.Empty(w.ExternalEdits);
        Assert.False(File.Exists(file));
        w.Close();
    }

    // ---- screenshots ----

    private static PixelBuffer Badge(int w, int h)
    {
        var p = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double dx = x - w * 0.5, dy = y - h * 0.52, r = Math.Sqrt(dx * dx + dy * dy), edge = h * 0.40;
                byte rr = 255, gg = 255, bb = 255;
                if (r < edge) { rr = 200; gg = 40; bb = 50; }
                if (r < edge * 0.82) { rr = 255; gg = 255; bb = 255; }                              // a white ring inside: stays with "from the edge"
                if (r < edge * 0.70) { rr = (byte)(20 + 60 * r / edge); gg = (byte)(50 + 70 * (y - h * 0.1) / h); bb = 150; }
                if (Math.Abs(dx) < w * 0.20 && Math.Abs(dy) < h * 0.06) { rr = 250; gg = 205; bb = 40; }   // a yellow bar
                p.Set(x, y, rr, gg, bb);
            }
        return p;
    }

    [AvaloniaFact]
    public async Task Screenshots()
    {
        // Development aid: set DSM_SCREENSHOTS to a folder to save pictures of the Transparency tab there (light and dark).
        var folder = Environment.GetEnvironmentVariable("DSM_SCREENSHOTS");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);

        var (w, _) = Open();
        w.Width = 1500; w.Height = 950;
        await ImportOne(w, Badge(720, 480), "badge.png");
        var d = await OpenEditor(w);
        d.Width = 1280; d.Height = 840;
        Pump(() => d.LeftPane.Bounds.Width > 100);
        PictureSettings.LaunchOverride = _ => null;
        Click(d.EditInButton);
        ToTransparency(d);
        Click(d.RemoveBackgroundButton);
        d.Session.Change(r => r with { Transparency = r.Transparency with { Tolerance = 14, Softness = 2, Shrink = 1 } });
        d.AddCut([new Vec2(250, 150), new Vec2(330, 130), new Vec2(380, 200), new Vec2(300, 240)], keep: false);
        d.SelectTool(PictureEditorDialog.TransparencyTool.Polygon);
        d.TransparencyDown(new Vec2(420, 270));
        d.TransparencyDown(new Vec2(520, 250));
        d.TransparencyDown(new Vec2(540, 340));
        d.TransparencyMove(new Vec2(450, 360));
        Settle(d);
        d.FitCanvas();
        Dispatcher.UIThread.RunJobs();
        Save(d, folder, "picture-editor-transparency-light.png");

        AppTheme.SetDarkMenus(true, remember: false);
        try
        {
            Dispatcher.UIThread.RunJobs();
            Save(d, folder, "picture-editor-transparency-dark.png");
        }
        finally { AppTheme.SetDarkMenus(false, remember: false); }
        d.SelectTool(PictureEditorDialog.TransparencyTool.None);
        CloseWith(w, d.CancelButton);
        w.Session.History.MarkSaved();
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

    // ---- Options ----

    [AvaloniaFact]
    public void The_Pictures_options_page_remembers_the_program_and_can_go_back_to_the_default()
    {
        string old = PictureSettings.EditorProgram;
        try
        {
            var page = new PictureOptionsPage();
            page.ProgramBox.Text = "\"C:\\Tools\\Editor\\editor.exe\"";
            page.Commit();
            Assert.Equal("C:\\Tools\\Editor\\editor.exe", PictureSettings.EditorProgram);
            Assert.Equal("editor", PictureSettings.EditorName);
            Assert.True(page.UseDefaultButton.IsVisible);
            Click(page.UseDefaultButton);
            Assert.Equal("", PictureSettings.EditorProgram);
            Assert.False(page.UseDefaultButton.IsVisible);
            Assert.Contains(OptionsPages.All, p => p.Id == OptionsPages.PicturesId && p.Title == "Pictures");
        }
        finally { PictureSettings.EditorProgram = old; }
    }

    [AvaloniaFact]
    public void A_program_that_does_not_exist_gives_a_plain_message_instead_of_an_error()
    {
        string old = PictureSettings.EditorProgram;
        try
        {
            PictureSettings.EditorProgram = Path.Combine(_dir, "missing.exe");
            var message = PictureSettings.Launch(Path.Combine(_dir, "x.png"));
            Assert.NotNull(message);
            Assert.Contains("not found", message);
        }
        finally { PictureSettings.EditorProgram = old; }
    }
}
