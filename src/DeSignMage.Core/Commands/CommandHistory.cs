using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Commands;

/// <summary>
/// A reversible document edit. <see cref="Do"/> is called for the first execution and for redo;
/// commands capture whatever they need to <see cref="Undo"/> during <see cref="Do"/>.
/// </summary>
public interface IDocumentCommand
{
    string Description { get; }
    void Do(Document doc);
    void Undo(Document doc);
}

/// <summary>
/// Undo/redo stacks for one document. Every document change goes through <see cref="Execute"/>;
/// <see cref="BeginTransaction"/> groups several commands into one undo step.
/// <para>
/// The history is bounded by memory, not by count (default: unlimited steps, <see cref="DefaultMemoryLimitBytes"/> of undo memory).
/// Each step reports roughly what it keeps alive (<see cref="IUndoMemory"/>: removed objects, old geometry, the old pixels of an
/// edited picture); when the total goes over the limit the oldest steps are dropped, and pictures only they needed are released
/// from the document's asset store. The newest step is always kept, so the last action can be undone even if it alone is over the limit.
/// </para>
/// </summary>
public sealed class CommandHistory
{
    /// <summary>1 GiB. The program replaces this from the user's setting (Tools ▸ Options ▸ Performance &amp; memory).</summary>
    public const long DefaultMemoryLimit = 1L << 30;

    /// <summary>Undo memory limit for histories that don't have their own, in bytes; 0 = no limit.</summary>
    public static long DefaultMemoryLimitBytes { get; set; } = DefaultMemoryLimit;

    /// <summary>Most undo steps kept by histories that don't have their own limit; null = unlimited.</summary>
    public static int? DefaultMaxSteps { get; set; }

    private readonly Document _doc;
    private readonly List<IDocumentCommand> _undo = [];
    private readonly Stack<IDocumentCommand> _redo = new();
    private readonly Stack<Transaction> _open = new();
    private IDocumentCommand? _savedAt;
    private bool _savedAtSet = true;   // a new document counts as saved at "nothing done"

    // Memory bookkeeping for every step on either stack: what it reported when it was added.
    private readonly Dictionary<IDocumentCommand, (long Bytes, string[] Assets)> _tracked = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, int> _assetRefs = [];
    private long _stepBytes;
    private int? _maxSteps;
    private long? _memoryLimit;

    /// <param name="limit">Most steps to keep for this history (the default is <see cref="DefaultMaxSteps"/>, normally unlimited).</param>
    public CommandHistory(Document doc, int? limit = null)
    {
        _doc = doc;
        _maxSteps = limit;
    }

    /// <summary>The step limit in force (<see cref="int.MaxValue"/> = unlimited).</summary>
    public int Limit => MaxSteps ?? int.MaxValue;

    /// <summary>Most undo steps kept; null = unlimited. Set to give this history its own limit instead of <see cref="DefaultMaxSteps"/>.</summary>
    public int? MaxSteps
    {
        get => _maxSteps ?? DefaultMaxSteps;
        set => _maxSteps = value;
    }

    /// <summary>Undo memory limit in bytes (0 = none); by default <see cref="DefaultMemoryLimitBytes"/>. Takes effect at the next step, or call <see cref="EnforceLimits"/>.</summary>
    public long MemoryLimitBytes
    {
        get => _memoryLimit ?? DefaultMemoryLimitBytes;
        set => _memoryLimit = value;
    }

    /// <summary>Pictures that must stay in the asset store although no object shows them (the program's clipboard).</summary>
    public Func<IEnumerable<string>>? ExtraLiveAssets { get; set; }

    /// <summary>How many old steps were dropped to stay within the limits (for the status line and tests).</summary>
    public int DroppedSteps { get; private set; }

    /// <summary>Approximate memory the undo and redo steps keep alive, in bytes: what they hold plus pictures nothing else shows.</summary>
    public long UndoMemoryBytes
    {
        get
        {
            long total = _stepBytes;
            if (_assetRefs.Count > 0) total += NonLiveAssetBytes(LiveAssets());
            return total;
        }
    }

    public bool CanUndo => _undo.Count > 0 && _open.Count == 0;
    public bool CanRedo => _redo.Count > 0 && _open.Count == 0;
    public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;
    public string? RedoDescription => _redo.Count > 0 ? _redo.Peek().Description : null;
    public IReadOnlyList<IDocumentCommand> UndoList => _undo;

    /// <summary>The steps that can be redone, the one <see cref="Redo"/> would do next first.</summary>
    public IEnumerable<IDocumentCommand> RedoList => _redo;

    /// <summary>True when the document differs from the last <see cref="MarkSaved"/> point.</summary>
    public bool IsModified => !_savedAtSet || !ReferenceEquals(_savedAt, _undo.Count > 0 ? _undo[^1] : null);

    public event EventHandler? Changed;

    public void MarkSaved()
    {
        _savedAt = _undo.Count > 0 ? _undo[^1] : null;
        _savedAtSet = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Execute(IDocumentCommand command)
    {
        command.Do(_doc);
        if (_open.Count > 0)
        {
            // Listeners hear about grouped edits once, when the outermost transaction closes.
            _open.Peek().Commands.Add(command);
            return;
        }
        Push(command);
        _doc.NotifyChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Tells listeners that the newest undo step changed what it does while the user is still dragging (a slider, a color wheel),
    /// so the whole drag stays one step. Only for a command already executed and still on top.
    /// </summary>
    public void NotifyTopEdited(IDocumentCommand command)
    {
        if (_undo.Count == 0 || !ReferenceEquals(_undo[^1], command)) return;
        _doc.NotifyChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsTop(IDocumentCommand command) => _open.Count == 0 && _undo.Count > 0 && ReferenceEquals(_undo[^1], command);

    public void Undo()
    {
        if (!CanUndo) return;
        var c = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        c.Undo(_doc);
        _redo.Push(c);
        _doc.NotifyChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var c = _redo.Pop();
        c.Do(_doc);
        _undo.Add(c);
        _doc.NotifyChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Takes the document to the state after <paramref name="applied"/> steps (0 = before the first): undoes or redoes as many
    /// steps as needed, listeners hear about it once. Steps beyond the current one stay redo-able until a new edit. False if the
    /// number is out of range or a group of edits is still open.
    /// </summary>
    public bool JumpTo(int applied)
    {
        if (_open.Count > 0 || applied < 0 || applied > _undo.Count + _redo.Count) return false;
        if (applied == _undo.Count) return true;
        while (_undo.Count > applied)
        {
            var c = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            c.Undo(_doc);
            _redo.Push(c);
        }
        while (_undo.Count < applied)
        {
            var c = _redo.Pop();
            c.Do(_doc);
            _undo.Add(c);
        }
        _doc.NotifyChanged();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        var released = new List<string>();
        foreach (var c in _undo) Untrack(c, released);
        foreach (var c in _redo) Untrack(c, released);
        _undo.Clear();
        _redo.Clear();
        ReleaseAssets(released, null);
        _savedAtSet = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Opens a group: commands executed until the returned transaction is disposed become one undo step.
    /// Call <see cref="Transaction.Rollback"/> to undo them instead. Transactions nest.
    /// </summary>
    public Transaction BeginTransaction(string description)
    {
        var t = new Transaction(this, description);
        _open.Push(t);
        return t;
    }

    private void Push(IDocumentCommand command)
    {
        var released = new List<string>();
        _undo.Add(command);
        Track(command);
        // A new edit ends the redo branch for good: those steps (and pictures only they needed) can go.
        foreach (var c in _redo) Untrack(c, released);
        _redo.Clear();
        ReleaseAssets(released, null);
        EnforceLimits();
    }

    /// <summary>
    /// Drops the oldest undo steps until the step limit and the memory limit hold again (the newest step always stays).
    /// Called after every new step; call it after lowering a limit to apply it at once.
    /// </summary>
    public void EnforceLimits()
    {
        if (_open.Count > 0) return;
        int? maxSteps = MaxSteps;
        long limit = MemoryLimitBytes;
        HashSet<string>? live = null;
        long assetBytes = 0;
        if (limit > 0 && _assetRefs.Count > 0)
        {
            live = LiveAssets();
            assetBytes = NonLiveAssetBytes(live);
        }
        while (_undo.Count > 1)
        {
            bool over = (maxSteps is { } m && _undo.Count > m) || (limit > 0 && _stepBytes + assetBytes > limit);
            if (!over) break;
            DropOldest(ref live, ref assetBytes, limit > 0);
        }
    }

    private void DropOldest(ref HashSet<string>? live, ref long assetBytes, bool trackAssetBytes)
    {
        var oldest = _undo[0];
        // The state before _undo[0] becomes unreachable; the state after it becomes the base (null).
        if (_savedAt is null) _savedAtSet = false;
        else if (ReferenceEquals(_savedAt, oldest)) _savedAt = null;
        _undo.RemoveAt(0);
        DroppedSteps++;

        var released = new List<string>();
        Untrack(oldest, released);
        if (released.Count == 0) return;
        live ??= LiveAssets();
        if (trackAssetBytes)
            foreach (var id in released)
                if (!live.Contains(id)) assetBytes -= _doc.Assets.SizeOf(id);
        ReleaseAssets(released, live);
    }

    private void Track(IDocumentCommand command)
    {
        long bytes = UndoMemory.BytesOf(command);
        var assets = UndoMemory.AssetsOf(command).Distinct().ToArray();
        _tracked[command] = (bytes, assets);
        _stepBytes += bytes;
        foreach (var id in assets) _assetRefs[id] = _assetRefs.GetValueOrDefault(id) + 1;
    }

    /// <summary>Forgets a step's memory; pictures nobody else needs any more are added to <paramref name="released"/>.</summary>
    private void Untrack(IDocumentCommand command, List<string> released)
    {
        if (!_tracked.Remove(command, out var info)) return;
        _stepBytes -= info.Bytes;
        foreach (var id in info.Assets)
        {
            if (!_assetRefs.TryGetValue(id, out int n)) continue;
            if (n <= 1) { _assetRefs.Remove(id); released.Add(id); }
            else _assetRefs[id] = n - 1;
        }
    }

    /// <summary>Removes pictures no step needs from the asset store, unless an object or the clipboard still shows them.</summary>
    private void ReleaseAssets(List<string> candidates, HashSet<string>? live)
    {
        if (candidates.Count == 0) return;
        live ??= LiveAssets();
        foreach (var id in candidates)
            if (!live.Contains(id)) _doc.Assets.Remove(id);
    }

    private HashSet<string> LiveAssets()
    {
        var live = BitmapStats.ReferencedAssetIds(_doc);
        if (ExtraLiveAssets is not null)
            foreach (var id in ExtraLiveAssets()) live.Add(id);
        return live;
    }

    private long NonLiveAssetBytes(HashSet<string> live)
    {
        long total = 0;
        foreach (var id in _assetRefs.Keys)
            if (!live.Contains(id)) total += _doc.Assets.SizeOf(id);
        return total;
    }

    private void Close(Transaction t, bool commit)
    {
        if (_open.Count == 0 || !ReferenceEquals(_open.Peek(), t))
            throw new InvalidOperationException("Transactions must be closed in reverse order of opening.");
        _open.Pop();

        if (!commit)
        {
            for (int i = t.Commands.Count - 1; i >= 0; i--) t.Commands[i].Undo(_doc);
            if (t.Commands.Count > 0 && _open.Count == 0) _doc.NotifyChanged();
            return;
        }
        if (t.Commands.Count == 0) return;

        // The transaction's description is what the user sees in Undo and the History tab.
        var composite = new CompositeCommand(t.Description, t.Commands);
        if (_open.Count > 0) _open.Peek().Commands.Add(composite);
        else
        {
            Push(composite);
            _doc.NotifyChanged();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public sealed class Transaction : IDisposable
    {
        private readonly CommandHistory _owner;
        private bool _closed;

        internal Transaction(CommandHistory owner, string description)
        {
            _owner = owner;
            Description = description;
        }

        public string Description { get; }
        internal List<IDocumentCommand> Commands { get; } = [];

        public void Rollback()
        {
            if (_closed) return;
            _closed = true;
            _owner.Close(this, commit: false);
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            _owner.Close(this, commit: true);
        }
    }
}

/// <summary>Several commands as one undo step. Undo runs in reverse order.</summary>
public sealed class CompositeCommand(string description, IReadOnlyList<IDocumentCommand> commands) : IDocumentCommand, IUndoMemory
{
    public string Description { get; } = description;
    public IReadOnlyList<IDocumentCommand> Commands { get; } = commands.ToArray();

    public long ApproximateBytes => Commands.Sum(UndoMemory.BytesOf);
    public IEnumerable<string> HeldAssetIds => Commands.SelectMany(UndoMemory.AssetsOf);

    public void Do(Document doc)
    {
        foreach (var c in Commands) c.Do(doc);
    }

    public void Undo(Document doc)
    {
        for (int i = Commands.Count - 1; i >= 0; i--) Commands[i].Undo(doc);
    }
}
