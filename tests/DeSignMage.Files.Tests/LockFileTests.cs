using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Native;
using Xunit;

namespace DeSignMage.Files.Tests;

/// <summary>Arrange ▸ Locked is part of the file: a locked object and a locked group are still locked after saving and opening.</summary>
public class LockFileTests
{
    private static Document RoundTrip(Document doc) => DsmReader.Load(new MemoryStream(DsmWriter.SaveToBytes(doc))).Document;

    private static RectangleObject Box(double x) => new(40, 40) { Transform = Affine2.Translation(x, 100), Style = ObjectStyle.FillOnly(DocColor.Black) };

    [Fact]
    public void Locked_objects_and_groups_stay_locked_through_save_and_open()
    {
        var s = new EditorSession(new Document(PageSetup.FromInches(4, 3)));
        var a = Box(10); var b = Box(100); var c = Box(200); var d = Box(300);
        foreach (var o in new[] { a, b, c, d }) s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, o));
        var group = s.GroupObjects([c, d])!;
        s.Selection.Set([a, group]);
        s.ToggleLockSelection();                                 // lock one object and one group, leave b alone
        Assert.True(a.Locked && group.Locked && !b.Locked);

        var loaded = RoundTrip(s.Document);

        var drawn = loaded.ActiveLayer.Children;
        Assert.Equal(4, drawn.Count);
        Assert.True(drawn[0].Locked);                            // a
        Assert.False(drawn[1].Locked);                           // b
        var loadedGroup = Assert.IsType<GroupObject>(drawn[2].Parent);
        Assert.True(loadedGroup.Locked);                         // the group (its members are covered by it)
        Assert.True(drawn[2].TopLevel.Locked && drawn[3].TopLevel.Locked);
        ModelCompare.AssertEqual(s.Document, loaded);

        // and the opened document behaves: only b can be picked
        var again = new EditorSession(loaded);
        again.Viewport.SetViewSize(new Vec2(800, 600));
        again.Viewport.Set(1, Vec2.Zero);
        Assert.Null(again.HitTestAt(new Vec2(30, 120)));
        Assert.Same(drawn[1], again.HitTestAt(new Vec2(120, 120)));
        Assert.Null(again.HitTestAt(new Vec2(220, 120)));
        Assert.True(again.HasLockedObjects);
        again.UnlockAll();
        Assert.False(again.HasLockedObjects);
    }

    [Fact]
    public void An_undone_lock_is_not_saved()
    {
        var s = new EditorSession(new Document(PageSetup.FromInches(4, 3)));
        var a = Box(10);
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, a));
        s.Selection.Set(a);
        s.ToggleLockSelection();
        s.Undo();
        Assert.False(RoundTrip(s.Document).ActiveLayer.Children[0].Locked);
    }
}
