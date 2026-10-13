using Avalonia.Headless;
using DeSignMage.App.Shell;
using DeSignMage.Imaging;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>
/// The Win32 clipboard code against the real clipboard. Off by default (it overwrites whatever the user has copied): set the
/// environment variable DSM_REAL_CLIPBOARD to a stage name to run one stage; the stages that read expect another program
/// (PowerShell, in the manual check) to have filled the clipboard first.
/// </summary>
public class RealClipboardTests
{
    private static bool Stage(string name) => Environment.GetEnvironmentVariable("DSM_REAL_CLIPBOARD") == name;

    [Fact]
    public void Write_offers_our_tag_PNG_and_bitmap()
    {
        if (!Stage("write")) return;
        var pixels = new PixelBuffer(30, 20);
        for (int y = 0; y < 20; y++) for (int x = 0; x < 30; x++) pixels.Set(x, y, 200, 30, 30, 255);
        var png = ImageCodec.EncodePng(pixels);
        var clip = new WindowsClipboard();

        clip.SetOwn("token-1", new ClipboardPicture(png, ClipboardImages.ToDibV5(pixels)));

        var available = clip.Available();
        Assert.Equal(ClipboardContent.Own | ClipboardContent.Png | ClipboardContent.Dib, available & (ClipboardContent.Own | ClipboardContent.Png | ClipboardContent.Dib));
        Assert.Equal("token-1", clip.OwnToken());
        Assert.Equal(png, clip.GetPng());
        var back = ClipboardImages.DibToPixels(clip.GetDib()!);
        Assert.Equal((30, 20), (back.Width, back.Height));
        Assert.Equal((200, 30, 30, 255), back.Get(10, 10));
    }

    [Fact]
    public void Read_a_bitmap_put_there_by_another_program()
    {
        if (!Stage("readdib")) return;
        var clip = new WindowsClipboard();
        Assert.True((clip.Available() & ClipboardContent.Dib) != 0);
        Assert.Null(clip.OwnToken());
        var p = ClipboardImages.DibToPixels(clip.GetDib()!);
        Assert.Equal((40, 25), (p.Width, p.Height));
        Assert.Equal((255, 0, 0), (p.Get(2, 2).R, p.Get(2, 2).G, p.Get(2, 2).B));      // red top-left quadrant
        Assert.Equal((0, 0, 255), (p.Get(35, 22).R, p.Get(35, 22).G, p.Get(35, 22).B)); // blue elsewhere
    }

    [Fact]
    public void Read_files_put_there_by_another_program()
    {
        if (!Stage("readfiles")) return;
        var files = new WindowsClipboard().GetFiles();
        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.True(File.Exists(f), f));
        Assert.EndsWith("one.png", files[0]);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task The_window_pastes_a_picture_from_the_real_clipboard()
    {
        if (!Stage("paste")) return;
        var w = new MainWindow(new StartupOptions()) { Width = 1400, Height = 860 };   // uses the real clipboard
        w.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        w.KeyPress(Avalonia.Input.Key.V, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.V, "v");
        await w.ClipboardIdle;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(w.Session.IsPlacing);                                  // the picture hangs on the cursor until a click
        var at = w.Session.Viewport.VisibleDocRect.Center;
        var screen = w.Session.Viewport.ToScreen(at);
        w.Session.PointerDown(new DeSignMage.Core.Editing.PointerInput(screen, at, DeSignMage.Core.Editing.PointerButton.Left, DeSignMage.Core.Editing.ModifierKeys.None));
        w.Session.PointerUp(new DeSignMage.Core.Editing.PointerInput(screen, at, DeSignMage.Core.Editing.PointerButton.Left, DeSignMage.Core.Editing.ModifierKeys.None));

        var img = Assert.IsType<DeSignMage.Core.Model.ImageObject>(Assert.Single(w.Session.Selection.Items));
        var px = ImageCodec.Decode(w.Session.Document.Assets.Get(img.AssetId)!.Data);
        Assert.Equal((40, 25), (px.Width, px.Height));
        Assert.Equal((255, 0, 0), (px.Get(2, 2).R, px.Get(2, 2).G, px.Get(2, 2).B));
        w.Close();
    }
}