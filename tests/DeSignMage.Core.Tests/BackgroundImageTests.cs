using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>Image menu operations on big photos run through <see cref="EditorSession.Background"/> (a worker thread in the app); small ones stay inline.</summary>
public class BackgroundImageTests
{
    private readonly EditorSession _s = new(NewDoc());
    private readonly FakeImageService _svc = new();
    private int _jobs;
    private int _workerThread = -1;

    public BackgroundImageTests()
    {
        _s.ImageService = _svc;
        _s.Background = (_, work) =>
        {
            _jobs++;
            return Task.Run(() =>
            {
                _workerThread = Environment.CurrentManagedThreadId;
                return work(CancellationToken.None);
            });
        };
    }

    private ImageObject AddImage(int pw, int ph)
    {
        var img = new ImageObject(_svc.Register(_s.Document, pw, ph), 200, 100, pw, ph);
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, img));
        _s.Selection.Set(img);
        return img;
    }

    [Fact]
    public async Task A_small_picture_is_processed_right_away_without_the_background()
    {
        var img = AddImage(400, 200);
        Assert.Equal(1, _s.ApplyImageOp(new ImageOp.Rotate90(true), "Rotate"));
        Assert.Equal(0, _jobs);
        Assert.Equal((200, 400), (img.PixelWidth, img.PixelHeight));   // done by the time the call returns
        await _s.ImageIdle;
    }

    [Fact]
    public async Task A_big_picture_is_rotated_on_a_worker_and_the_step_appears_when_it_is_done()
    {
        var img = AddImage(4000, 3000);
        string old = img.AssetId;
        int ui = Environment.CurrentManagedThreadId;

        Assert.Equal(1, _s.ApplyImageOp(new ImageOp.Rotate90(true), "Rotate 90 Degrees Clockwise"));
        await _s.ImageIdle;

        Assert.Equal(1, _jobs);
        Assert.NotEqual(ui, _workerThread);
        Assert.NotEqual(old, img.AssetId);
        Assert.Equal((3000, 4000), (img.PixelWidth, img.PixelHeight));
        Assert.Equal("Rotate 90 Degrees Clockwise", _s.History.UndoDescription);

        _s.Undo();
        Assert.Equal((old, 4000, 3000), (img.AssetId, img.PixelWidth, img.PixelHeight));
    }

    [Fact]
    public async Task A_picture_of_unknown_size_goes_to_the_background_and_learns_its_size()
    {
        var img = new ImageObject(_svc.Register(_s.Document, 3000, 2000), 200, 100);   // like a picture from a .lyt: no pixel size yet
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, img));
        _s.Selection.Set(img);

        _s.ApplyImageOp(new ImageOp.Invert(), "Invert");
        await _s.ImageIdle;

        Assert.Equal(1, _jobs);
        Assert.Equal((3000, 2000), (img.PixelWidth, img.PixelHeight));
        Assert.Equal("Invert", _s.History.UndoDescription);
    }

    [Fact]
    public async Task Crop_of_a_big_picture_runs_in_the_background_too()
    {
        var img = AddImage(4000, 3000);
        _s.BeginCrop();
        Assert.True(_s.CropImage(img, 100, 100, 2000, 1000));
        await _s.ImageIdle;

        Assert.Equal(1, _jobs);
        Assert.Equal((2000, 1000), (img.PixelWidth, img.PixelHeight));
        Assert.Equal("Crop Bitmap", _s.History.UndoDescription);
    }

    [Fact]
    public async Task A_picture_that_cannot_be_processed_reports_an_error_and_changes_nothing()
    {
        var img = new ImageObject("unknown-asset", 200, 100, 4000, 3000);
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, img));
        _s.Selection.Set(img);
        int steps = _s.History.UndoList.Count;
        bool finished = false;
        _s.ImageFinished += (_, _) => finished = true;

        _s.ApplyImageOp(new ImageOp.Rotate90(true), "Rotate");
        await _s.ImageIdle;

        Assert.True(finished);
        Assert.NotNull(_s.LastImageError);
        Assert.Equal(steps, _s.History.UndoList.Count);
    }

    [Fact]
    public async Task Without_a_background_runner_everything_stays_inline()
    {
        var s = new EditorSession(NewDoc()) { ImageService = _svc };
        var img = new ImageObject(_svc.Register(s.Document, 4000, 3000), 200, 100, 4000, 3000);
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, img));
        s.Selection.Set(img);
        Assert.Equal(1, s.ApplyImageOp(new ImageOp.Flip(true), "Flip"));
        Assert.Equal("Flip", s.History.UndoDescription);   // already done
        await s.ImageIdle;
    }
}
