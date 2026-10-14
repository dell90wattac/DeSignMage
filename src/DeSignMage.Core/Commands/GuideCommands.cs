using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Commands;

/// <summary>Adds a guide (undo removes the same instance again).</summary>
public sealed class AddGuideCommand(Guide guide, int index = -1) : IDocumentCommand
{
    public Guide Guide { get; } = guide;
    public string Description => "Add Guide";

    public void Do(Document doc) => doc.Guides.Insert(index, Guide);
    public void Undo(Document doc) => doc.Guides.Remove(Guide);
}

/// <summary>Adds several guides as one undo step (Layout ▸ Guides). Undo removes exactly those guides again.</summary>
public sealed class AddGuidesCommand(IReadOnlyList<Guide> guides, string description = "Add Guides") : IDocumentCommand
{
    public IReadOnlyList<Guide> Guides { get; } = guides.ToArray();
    public string Description { get; } = description;

    public void Do(Document doc)
    {
        foreach (var g in Guides) doc.Guides.Insert(-1, g);
    }

    public void Undo(Document doc)
    {
        for (int i = Guides.Count - 1; i >= 0; i--) doc.Guides.Remove(Guides[i]);
    }
}

/// <summary>Removes one guide; undo puts it back at its old position in the list.</summary>
public sealed class RemoveGuideCommand(Guide guide) : IDocumentCommand
{
    private int _index;

    public Guide Guide { get; } = guide;
    public string Description => "Remove Guide";

    public void Do(Document doc) => _index = doc.Guides.Remove(Guide);
    public void Undo(Document doc) => doc.Guides.Insert(_index, Guide);
}

/// <summary>Removes every guide (the red X on the guide Context Bar).</summary>
public sealed class RemoveAllGuidesCommand : IDocumentCommand
{
    private Guide[] _removed = [];

    public string Description => "Remove All Guides";

    public void Do(Document doc)
    {
        _removed = doc.Guides.ToArray();
        doc.Guides.ReplaceAll([]);
    }

    public void Undo(Document doc) => doc.Guides.ReplaceAll(_removed);
}

/// <summary>Moves and/or rotates a guide: the new origin and angle replace the old ones.</summary>
public sealed class SetGuideCommand : IDocumentCommand
{
    private readonly Guide _guide;
    private readonly Vec2 _oldOrigin, _newOrigin;
    private readonly double _oldAngle, _newAngle;

    public SetGuideCommand(string description, Guide guide, Vec2 newOrigin, double? newAngleDegrees = null)
    {
        Description = description;
        _guide = guide;
        _oldOrigin = guide.Origin;
        _oldAngle = guide.AngleDegrees;
        _newOrigin = newOrigin;
        _newAngle = Guide.NormalizeAngle(newAngleDegrees ?? guide.AngleDegrees);
    }

    public string Description { get; }

    /// <summary>True when executing it would change nothing.</summary>
    public bool IsNoOp => Vec2.Distance(_newOrigin, _oldOrigin) < 1e-9 && Math.Abs(_newAngle - _oldAngle) < 1e-9;

    public void Do(Document doc)
    {
        _guide.Origin = _newOrigin;
        _guide.AngleDegrees = _newAngle;
    }

    public void Undo(Document doc)
    {
        _guide.Origin = _oldOrigin;
        _guide.AngleDegrees = _oldAngle;
    }

    public static SetGuideCommand Move(Guide guide, Vec2 newOrigin) => new("Move Guide", guide, newOrigin);

    /// <summary>"+" on the guide Context Bar: center the guide on the page along its axis.</summary>
    public static SetGuideCommand CenterOnPage(Guide guide, PageSetup page) => new("Center Guide", guide, guide.CenteredOn(page));
}

/// <summary>Locks or unlocks a guide.</summary>
public sealed class SetGuideLockedCommand(Guide guide, bool locked) : IDocumentCommand
{
    private bool _old;

    public string Description => locked ? "Lock Guide" : "Unlock Guide";

    public void Do(Document doc)
    {
        _old = guide.Locked;
        guide.Locked = locked;
    }

    public void Undo(Document doc) => guide.Locked = _old;
}
