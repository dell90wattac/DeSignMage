using DeSignMage.Core.Model;

namespace DeSignMage.Core.Commands;

/// <summary>
/// Locks or unlocks objects (Arrange ▸ Locked, Unlock All). A locked object can't be selected by clicking or with a rubber band, and can't be
/// moved, transformed or deleted. Each object remembers its own old state, so one undo step restores a mixed selection exactly.
/// </summary>
public sealed class SetLockedCommand : IDocumentCommand
{
    private readonly (DocObject Obj, bool Old)[] _old;
    private readonly bool _locked;

    public SetLockedCommand(IEnumerable<DocObject> objects, bool locked, string? description = null)
    {
        _locked = locked;
        _old = objects.Distinct().Select(o => (o, o.Locked)).ToArray();
        Description = description ?? (locked ? "Lock" : "Unlock");
    }

    public string Description { get; }

    /// <summary>True when every object is already in the requested state (nothing worth an undo step).</summary>
    public bool IsNoOp => _old.All(o => o.Old == _locked);

    public void Do(Document doc)
    {
        foreach (var (o, _) in _old) o.Locked = _locked;
    }

    public void Undo(Document doc)
    {
        foreach (var (o, old) in _old) o.Locked = old;
    }
}
