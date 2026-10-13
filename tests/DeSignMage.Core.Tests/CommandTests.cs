using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

public class CommandTests
{
    private readonly Document _doc = NewDoc();
    private readonly CommandHistory _history;

    public CommandTests() => _history = new CommandHistory(_doc);

    private RectangleObject AddRect(double x = 0, double y = 0)
    {
        var r = Rect(x, y, 10, 10);
        _history.Execute(new AddObjectCommand(_doc.ActiveLayer, r));
        return r;
    }

    [Fact]
    public void Add_undo_redo()
    {
        var r = AddRect();
        Assert.True(_doc.Contains(r));
        Assert.Equal("Add Rectangle", _history.UndoDescription);

        _history.Undo();
        Assert.False(_doc.Contains(r));
        Assert.True(_history.CanRedo);

        _history.Redo();
        Assert.True(_doc.Contains(r));
        Assert.Same(r, _doc.ActiveLayer.Children[0]);
    }

    [Fact]
    public void JumpTo_goes_back_and_forward_in_one_action_and_keeps_later_steps_redoable()
    {
        var a = AddRect(0); var b = AddRect(20); var c = AddRect(40);
        int changes = 0;
        _history.Changed += (_, _) => changes++;

        Assert.True(_history.JumpTo(1));                         // back two steps at once
        Assert.Equal([a], _doc.ActiveLayer.Children);
        Assert.Equal(1, changes);                                // one notification, not two
        Assert.Single(_history.UndoList);
        Assert.Equal(["Add Rectangle", "Add Rectangle"], _history.RedoList.Select(x => x.Description));
        Assert.True(_history.CanRedo);

        Assert.True(_history.JumpTo(0));
        Assert.Empty(_doc.ActiveLayer.Children);

        Assert.True(_history.JumpTo(3));                         // all the way forward again
        Assert.Equal([a, b, c], _doc.ActiveLayer.Children);
        Assert.False(_history.CanRedo);

        Assert.True(_history.JumpTo(3));                         // already there: nothing happens
        Assert.Equal(3, changes);
    }

    [Fact]
    public void JumpTo_refuses_numbers_out_of_range_and_a_new_edit_after_a_jump_drops_the_later_steps()
    {
        AddRect(0); AddRect(20); AddRect(40);
        Assert.False(_history.JumpTo(-1));
        Assert.False(_history.JumpTo(4));
        Assert.Equal(3, _doc.ActiveLayer.Children.Count);

        _history.JumpTo(1);
        AddRect(60);
        Assert.False(_history.CanRedo);                          // the two undone steps are gone for good
        Assert.Equal(2, _doc.ActiveLayer.Children.Count);
        Assert.False(_history.JumpTo(3));

        using (_history.BeginTransaction("Group of edits"))
            Assert.False(_history.JumpTo(0));                    // not while edits are open
    }
    [Fact]
    public void Delete_undo_restores_stacking_order()
    {
        var objs = Enumerable.Range(0, 5).Select(i => AddRect(i * 20)).ToArray();
        _history.Execute(new DeleteObjectsCommand([objs[3], objs[1], objs[4]]));
        Assert.Equal([objs[0], objs[2]], _doc.ActiveLayer.Children);

        _history.Undo();
        Assert.Equal(objs, _doc.ActiveLayer.Children);
    }

    [Fact]
    public void Delete_of_group_and_its_child_is_safe()
    {
        var g = new GroupObject();
        _history.Execute(new AddObjectCommand(_doc.ActiveLayer, g));
        var child = Rect(0, 0, 1, 1);
        _history.Execute(new AddObjectCommand(g, child));

        _history.Execute(new DeleteObjectsCommand([g, child]));
        Assert.Equal(0, _doc.ObjectCount);
        _history.Undo();
        Assert.Same(g, child.Parent);
        Assert.Equal(2, _doc.ObjectCount);
    }

    [Fact]
    public void Transform_undo_restores_old_transform()
    {
        var r = AddRect(5, 5);
        var before = r.Transform;
        _history.Execute(TransformObjectsCommand.ApplyWorld("Move", [r], Affine2.Translation(10, 0)));
        Assert.Equal(new Rect2(15, 5, 25, 15), r.GetWorldBounds());
        _history.Undo();
        Assert.Equal(before, r.Transform);
    }

    [Fact]
    public void World_delta_is_applied_correctly_inside_a_scaled_group()
    {
        var g = new GroupObject { Transform = Affine2.Scale(2, 2) * Affine2.Translation(100, 100) };
        _history.Execute(new AddObjectCommand(_doc.ActiveLayer, g));
        var child = Rect(0, 0, 10, 10);
        _history.Execute(new AddObjectCommand(g, child));
        Assert.Equal(new Rect2(100, 100, 120, 120), child.GetWorldBounds());

        _history.Execute(TransformObjectsCommand.ApplyWorld("Move", [child], Affine2.Translation(5, 0)));
        var b = child.GetWorldBounds();
        Assert.Equal(105, b.Left, 9);
        Assert.Equal(125, b.Right, 9);
    }

    [Fact]
    public void Set_style_and_undo()
    {
        var r = AddRect();
        var old = r.Style;
        var red = new SolidPaint(DocColor.FromHex("#FF0000"));
        _history.Execute(new SetStyleCommand("Fill", [r], s => s.WithFill(red)));
        Assert.Equal(red, r.Style.Fill);
        _history.Undo();
        Assert.Equal(old, r.Style);
    }

    [Fact]
    public void Set_property_command()
    {
        var r = AddRect();
        _history.Execute(new SetPropertyCommand<RectangleObject, double>("Radius", [r], o => o.CornerRadius, (o, v) => o.CornerRadius = v, 4));
        Assert.Equal(4, r.CornerRadius);
        _history.Undo();
        Assert.Equal(0, r.CornerRadius);
    }

    [Fact]
    public void Reorder_and_undo()
    {
        var a = AddRect();
        var b = AddRect();
        var c = AddRect();
        _history.Execute(new ReorderObjectCommand(a, 2));
        Assert.Equal([b, c, a], _doc.ActiveLayer.Children);
        _history.Undo();
        Assert.Equal([a, b, c], _doc.ActiveLayer.Children);
    }

    [Fact]
    public void New_command_clears_redo()
    {
        AddRect();
        _history.Undo();
        Assert.True(_history.CanRedo);
        AddRect();
        Assert.False(_history.CanRedo);
    }

    [Fact]
    public void Transaction_is_one_undo_step_and_notifies_once()
    {
        int notifications = 0;
        _doc.Changed += (_, _) => notifications++;
        using (_history.BeginTransaction("Three"))
        {
            AddRect();
            AddRect();
            AddRect();
            Assert.Equal(0, notifications);
        }
        Assert.Equal(1, notifications);
        Assert.Single(_history.UndoList);
        Assert.Equal("Three", _history.UndoDescription);

        _history.Undo();
        Assert.Equal(0, _doc.ObjectCount);
        _history.Redo();
        Assert.Equal(3, _doc.ObjectCount);
    }

    [Fact]
    public void Transaction_description_names_the_undo_step_even_for_one_command()
    {
        using (_history.BeginTransaction("Copy")) AddRect();
        Assert.Equal("Copy", _history.UndoDescription);
    }

    [Fact]
    public void Empty_transaction_adds_nothing()
    {
        using (_history.BeginTransaction("Nothing")) { }
        Assert.False(_history.CanUndo);
    }

    [Fact]
    public void Rollback_undoes_the_transaction()
    {
        var tx = _history.BeginTransaction("Oops");
        AddRect();
        AddRect();
        tx.Rollback();
        Assert.Equal(0, _doc.ObjectCount);
        Assert.False(_history.CanUndo);
        tx.Dispose();   // no-op after rollback
        Assert.False(_history.CanUndo);
    }

    [Fact]
    public void Nested_transactions_fold_into_the_outer_one()
    {
        using (_history.BeginTransaction("Outer"))
        {
            AddRect();
            using (_history.BeginTransaction("Inner"))
            {
                AddRect();
                AddRect();
            }
            Assert.False(_history.CanUndo);   // nothing committed while a transaction is open
        }
        Assert.Single(_history.UndoList);
        _history.Undo();
        Assert.Equal(0, _doc.ObjectCount);
    }

    [Fact]
    public void Modified_flag_tracks_save_point()
    {
        Assert.False(_history.IsModified);
        AddRect();
        Assert.True(_history.IsModified);
        _history.MarkSaved();
        Assert.False(_history.IsModified);
        _history.Undo();
        Assert.True(_history.IsModified);
        _history.Redo();
        Assert.False(_history.IsModified);
        _history.Undo();
        AddRect();   // diverges; the save point is no longer reachable
        Assert.True(_history.IsModified);
    }

    [Fact]
    public void History_limit_drops_oldest()
    {
        var h = new CommandHistory(_doc, limit: 3);
        for (int i = 0; i < 5; i++) h.Execute(new AddObjectCommand(_doc.ActiveLayer, Rect(0, 0, 1, 1)));
        Assert.Equal(3, h.UndoList.Count);
        while (h.CanUndo) h.Undo();
        Assert.Equal(2, _doc.ObjectCount);
        Assert.True(h.IsModified);   // the original empty state is gone
    }

    [Fact]
    public void Every_executed_command_notifies_document_listeners()
    {
        long rev = _doc.Revision;
        AddRect();
        Assert.True(_doc.Revision > rev);
        rev = _doc.Revision;
        _history.Undo();
        Assert.True(_doc.Revision > rev);
    }
}
