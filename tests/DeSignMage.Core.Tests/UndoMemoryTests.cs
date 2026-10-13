using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>The undo history is bounded by memory (default 1 GB, unlimited steps): oldest steps go first, pictures only they needed are released.</summary>
public class UndoMemoryTests
{
    private const int Mb = 1 << 20;

    private readonly Document _doc = NewDoc();
    private readonly Random _rnd = new(5);

    /// <summary>A picture of the given size whose bytes are unique (assets are stored by content).</summary>
    private string NewAsset(int bytes)
    {
        var data = new byte[bytes];
        _rnd.NextBytes(data);
        return _doc.Assets.Add(data, "image/png");
    }

    private ImageObject PlaceImage(CommandHistory history, int bytes = Mb)
    {
        var img = new ImageObject(NewAsset(bytes), 100, 100, 10, 10);
        history.Execute(new AddObjectCommand(_doc.ActiveLayer, img));
        return img;
    }

    private void Replace(CommandHistory history, ImageObject img, int bytes) =>
        history.Execute(new ReplaceImageCommand("Edit picture", img, new ImagePixels(NewAsset(bytes), 10, 10), new ImageFrame(100, 100, Affine2.Identity)));

    [Fact]
    public void By_default_the_steps_are_unlimited_and_the_memory_limit_is_one_gigabyte()
    {
        Assert.Null(CommandHistory.DefaultMaxSteps);
        Assert.Equal(1L << 30, CommandHistory.DefaultMemoryLimitBytes);
        var h = new CommandHistory(_doc);
        for (int i = 0; i < 3000; i++) h.Execute(new AddObjectCommand(_doc.ActiveLayer, Rect(0, 0, 1, 1)));
        Assert.Equal(3000, h.UndoList.Count);   // more than the old 1000-step limit
        Assert.Equal(0, h.DroppedSteps);
    }

    [Fact]
    public void Steps_are_dropped_oldest_first_when_over_the_memory_limit()
    {
        var h = new CommandHistory(_doc) { MemoryLimitBytes = 5 * Mb };
        var img = PlaceImage(h);
        for (int i = 0; i < 40; i++) Replace(h, img, Mb);

        Assert.True(h.UndoMemoryBytes <= 5 * Mb, $"{h.UndoMemoryBytes} bytes");
        Assert.InRange(h.UndoList.Count, 2, 8);
        Assert.True(h.DroppedSteps > 30);
        // Pictures nothing needs any more were released: the shown one plus the few kept for undo.
        Assert.True(_doc.Assets.All.Count() <= h.UndoList.Count + 2, $"{_doc.Assets.All.Count()} pictures kept");
    }

    [Fact]
    public void Undo_and_redo_still_work_for_every_kept_step()
    {
        var h = new CommandHistory(_doc) { MemoryLimitBytes = 5 * Mb };
        var img = PlaceImage(h);
        var shown = new List<string> { img.AssetId };
        for (int i = 0; i < 25; i++) { Replace(h, img, Mb); shown.Add(img.AssetId); }

        int kept = h.UndoList.Count;
        Assert.True(kept < 25);
        int undone = 0;
        while (h.CanUndo)
        {
            h.Undo();
            undone++;
            Assert.NotNull(_doc.Assets.Get(img.AssetId));   // the picture undo went back to is still stored
        }
        Assert.Equal(kept, undone);
        // The oldest kept step's "before" picture is the one shown `kept` edits ago.
        Assert.Equal(shown[shown.Count - 1 - kept], img.AssetId);

        while (h.CanRedo) h.Redo();
        Assert.Equal(shown[^1], img.AssetId);
        Assert.NotNull(_doc.Assets.Get(img.AssetId));
    }

    [Fact]
    public void The_newest_step_is_kept_even_when_it_alone_is_over_the_limit()
    {
        var h = new CommandHistory(_doc) { MemoryLimitBytes = Mb / 2 };
        var img = PlaceImage(h, 100);
        var before = img.AssetId;
        Replace(h, img, 2 * Mb);
        Assert.True(h.CanUndo);
        h.Undo();
        Assert.Equal(before, img.AssetId);
        Assert.NotNull(_doc.Assets.Get(before));
    }

    [Fact]
    public void Steps_sharing_a_picture_count_it_once_and_a_shown_picture_does_not_count()
    {
        var h = new CommandHistory(_doc);
        var img = PlaceImage(h, Mb);
        Replace(h, img, Mb);
        Replace(h, img, Mb);
        // Three pictures exist; the shown one is not undo memory, the other two (shared between neighbouring steps) count once each.
        Assert.InRange(h.UndoMemoryBytes, 2 * Mb, 2 * Mb + 4096);
    }

    [Fact]
    public void A_step_limit_can_be_set_next_to_the_memory_limit()
    {
        var h = new CommandHistory(_doc) { MaxSteps = 4 };
        for (int i = 0; i < 10; i++) h.Execute(new AddObjectCommand(_doc.ActiveLayer, Rect(0, 0, 1, 1)));
        Assert.Equal(4, h.UndoList.Count);
        Assert.Equal(6, h.DroppedSteps);

        h.MaxSteps = 2;
        h.EnforceLimits();   // lowering a limit applies at once
        Assert.Equal(2, h.UndoList.Count);
    }

    [Fact]
    public void Lowering_the_memory_limit_applies_at_once()
    {
        var h = new CommandHistory(_doc);
        var img = PlaceImage(h);
        for (int i = 0; i < 20; i++) Replace(h, img, Mb);
        Assert.True(h.UndoMemoryBytes > 15 * Mb);
        h.MemoryLimitBytes = 4 * Mb;
        h.EnforceLimits();
        Assert.True(h.UndoMemoryBytes <= 4 * Mb);
    }

    [Fact]
    public void A_new_edit_after_undo_releases_the_pictures_only_the_discarded_redo_steps_needed()
    {
        var h = new CommandHistory(_doc);
        var img = PlaceImage(h);
        Replace(h, img, Mb);
        Replace(h, img, Mb);
        var undoneTo = img.AssetId;
        var discarded = img.AssetId;   // shown after the second edit
        h.Undo();                      // the redo stack now needs `discarded`
        Assert.NotNull(_doc.Assets.Get(discarded));
        h.Undo();
        Assert.NotEqual(discarded, img.AssetId);

        h.Execute(new SetStyleCommand("Color", [img], s => s));   // a new edit: the redo branch is gone
        Assert.Null(_doc.Assets.Get(discarded));
        Assert.NotNull(_doc.Assets.Get(img.AssetId));
        _ = undoneTo;
    }

    [Fact]
    public void Pictures_on_the_clipboard_are_never_released()
    {
        var h = new CommandHistory(_doc) { MemoryLimitBytes = 3 * Mb };
        var img = PlaceImage(h);
        var copied = img.AssetId;
        h.ExtraLiveAssets = () => [copied];
        for (int i = 0; i < 20; i++) Replace(h, img, Mb);
        Assert.NotNull(_doc.Assets.Get(copied));
    }

    [Fact]
    public void Deleted_pictures_are_released_once_their_steps_are_dropped()
    {
        var h = new CommandHistory(_doc) { MemoryLimitBytes = 3 * Mb };
        var img = PlaceImage(h);
        var asset = img.AssetId;
        h.Execute(new DeleteObjectsCommand([img]));
        Assert.NotNull(_doc.Assets.Get(asset));   // undo of the delete needs it
        for (int i = 0; i < 6; i++)   // more pictures placed and deleted: none is shown, so each one counts
        {
            var other = PlaceImage(h);
            h.Execute(new DeleteObjectsCommand([other]));
        }
        Assert.Null(_doc.Assets.Get(asset));
        Assert.True(h.UndoMemoryBytes <= 3 * Mb);
    }

    [Fact]
    public void Transactions_count_as_one_step_with_all_their_pictures()
    {
        var h = new CommandHistory(_doc);
        var a = PlaceImage(h);
        var b = PlaceImage(h);
        using (h.BeginTransaction("Both"))
        {
            Replace(h, a, Mb);
            Replace(h, b, Mb);
        }
        Assert.Equal(3, h.UndoList.Count);
        Assert.InRange(h.UndoMemoryBytes, 2 * Mb, 2 * Mb + 4096);   // the two replaced pictures
    }
}
