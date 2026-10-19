using System.Diagnostics;
using DeSignMage.Feedback;
using DeSignMage.Formats.Native;

namespace DeSignMage.App.Recovery;

/// <summary>The open design as the engine sees it at one moment. Everything here is read on the UI thread.</summary>
/// <param name="Identity">Something that is the same object for as long as this is the same design (the document), so the engine knows a new design from an old one.</param>
/// <param name="Revision">Changes with every edit of the design.</param>
/// <param name="TakeSnapshot">Reads the whole design into memory, quickly; the slow packing happens later, on another thread.</param>
public sealed record AutoSaveState(object Identity, bool HasUnsavedChanges, long Revision, RecoveryMeta Meta, Func<DsmSnapshot> TakeSnapshot);

public enum AutoSaveResult
{
    /// <summary>Auto-save is switched off.</summary>
    Disabled,
    /// <summary>No design is open.</summary>
    NothingOpen,
    /// <summary>The design has no unsaved changes (so there is no recovery copy).</summary>
    NoChanges,
    /// <summary>The recovery copy already holds the design as it is now.</summary>
    Unchanged,
    /// <summary>Unsaved changes, but the interval has not run out.</summary>
    NotDue,
    /// <summary>The previous copy is still being written.</summary>
    Busy,
    /// <summary>The design is bigger than the limit in Options.</summary>
    TooLarge,
    /// <summary>Reading the design failed.</summary>
    Failed,
    /// <summary>A copy is being written (see <see cref="AutoSaveTick.Write"/>).</summary>
    Started,
}

public sealed record AutoSaveTick(AutoSaveResult Result, Task? Write = null);

/// <summary>
/// Decides when a recovery copy of the open design is made, and makes it without holding the window up.
/// <para>
/// <see cref="Tick"/> is called on the UI thread every few seconds. When the design has unsaved changes that the last copy does not hold, and
/// the interval since the changes were first noticed has passed, it reads the design into memory (<see cref="AutoSaveState.TakeSnapshot"/>:
/// the only work on the UI thread) and packs and writes the file on a worker. Only one copy is written at a time; a design that takes long to
/// write is copied less often (at least four times the time the last copy took), so writing never takes over the machine.
/// </para>
/// </summary>
public sealed class AutoSaveEngine
{
    public const string DefaultKey = "design";

    private readonly RecoveryStore _store;
    private readonly Func<AutoSaveState?> _state;
    private readonly Func<AutoSaveOptions> _options;
    private readonly Func<DateTime> _now;
    private readonly Func<Func<bool>, Task<bool>> _run;
    private readonly object _lock = new();

    private object? _identity;
    private long _epoch;
    private long _writtenRevision = -1, _tooLargeRevision = -1;
    private DateTime? _dirtySince;
    private TimeSpan _lastWriteTime;
    private Task? _pending;
    private int _failures;

    /// <param name="run">Runs the writing; by default on the thread pool. Tests pass something that runs it at once.</param>
    public AutoSaveEngine(RecoveryStore store, Func<AutoSaveState?> state, Func<AutoSaveOptions> options, Func<DateTime>? utcNow = null,
        Func<Func<bool>, Task<bool>>? run = null, string key = DefaultKey)
    {
        _store = store;
        _state = state;
        _options = options;
        _now = utcNow ?? (() => DateTime.UtcNow);
        _run = run ?? (work => Task.Run(work));
        Key = key;
    }

    public string Key { get; }

    /// <summary>Copies written so far in this run.</summary>
    public int Written { get; private set; }

    /// <summary>How long the last copy took to pack and write, and the part of that spent on the UI thread.</summary>
    public TimeSpan LastWriteTime
    {
        get => _lastWriteTime;
        internal set => _lastWriteTime = value;
    }

    public TimeSpan LastSnapshotTime { get; private set; }

    /// <summary>What went wrong the last time a copy could not be made (null after a success).</summary>
    public string? LastError { get; private set; }

    /// <summary>Raised (on any thread) when a copy could not be written; only for the first of a series, so a full disk says so once.</summary>
    public event Action<string>? WriteFailed;

    /// <summary>The write in progress, if any.</summary>
    public Task? Pending
    {
        get { lock (_lock) return _pending; }
    }

    /// <summary>How long unsaved changes may wait: the Options interval, or more for a design that is slow to write.</summary>
    public TimeSpan EffectiveInterval(AutoSaveOptions options) =>
        TimeSpan.FromTicks(Math.Max(options.Interval.Ticks, Math.Min(_lastWriteTime.Ticks * 4, TimeSpan.FromMinutes(30).Ticks)));

    public AutoSaveTick Tick()
    {
        var options = _options();
        if (!options.Enabled)
        {
            _dirtySince = null;
            return new(AutoSaveResult.Disabled);
        }
        var state = _state();
        if (state is null) return new(AutoSaveResult.NothingOpen);

        lock (_lock)
        {
            if (!ReferenceEquals(_identity, state.Identity))
            {
                // Another design than before: nothing written for the old one says anything about this one.
                _identity = state.Identity;
                ForgetLocked();
            }
            if (_pending is { IsCompleted: false }) return new(AutoSaveResult.Busy);
        }

        if (!state.HasUnsavedChanges)
        {
            _dirtySince = null;
            if (_writtenRevision >= 0 || _store.HasCopy(Key)) Clear();   // saved, or undone back to the saved state: the copy has served its purpose
            return new(AutoSaveResult.NoChanges);
        }
        if (state.Revision == _writtenRevision)
        {
            _dirtySince = null;
            return new(AutoSaveResult.Unchanged);
        }
        if (state.Revision == _tooLargeRevision) return new(AutoSaveResult.TooLarge);

        var now = _now();
        _dirtySince ??= now;
        if (now - _dirtySince.Value < EffectiveInterval(options)) return new(AutoSaveResult.NotDue);

        DsmSnapshot snapshot;
        var watch = Stopwatch.StartNew();
        try { snapshot = state.TakeSnapshot(); }
        catch (Exception ex)
        {
            _dirtySince = now;   // try again after another interval
            Fail($"Could not read the design for a recovery copy ({ex.GetType().Name}: {ex.Message})");
            return new(AutoSaveResult.Failed);
        }
        LastSnapshotTime = watch.Elapsed;

        if (options.LargestBytes > 0 && snapshot.UncompressedSize > options.LargestBytes)
        {
            _tooLargeRevision = state.Revision;
            _dirtySince = null;
            AppLog.Info("autosave", $"No recovery copy: the design is {snapshot.UncompressedSize / 1048576.0:0.#} MB, over the {options.LargestBytes / 1048576} MB limit");
            return new(AutoSaveResult.TooLarge);
        }

        long revision = state.Revision;
        long epoch;
        lock (_lock) epoch = _epoch;
        SlotWrite write;
        try { write = _store.BeginWrite(Key); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dirtySince = now;
            Fail($"Could not prepare the recovery folder ({ex.GetType().Name}: {ex.Message})");
            return new(AutoSaveResult.Failed);
        }
        var meta = state.Meta with { SavedUtc = now };
        var started = Stopwatch.StartNew();
        Task task = _run(() =>
        {
            try
            {
                snapshot.SaveTo(write.TempPath);
                return _store.Commit(write, meta);
            }
            catch (Exception ex)
            {
                try { File.Delete(write.TempPath); } catch (Exception) { }
                Fail($"Could not write the recovery copy ({ex.GetType().Name}: {ex.Message})");
                return false;
            }
        }).ContinueWith(t =>
        {
            lock (_lock)
            {
                _lastWriteTime = started.Elapsed;
                if (t.IsCompletedSuccessfully && t.Result && epoch == _epoch)
                {
                    _writtenRevision = revision;
                    _dirtySince = null;
                    _failures = 0;
                    Written++;
                    LastError = null;
                    AppLog.Info("autosave", $"Recovery copy written ({snapshot.UncompressedSize / 1024} KB, {_lastWriteTime.TotalMilliseconds:0} ms, {LastSnapshotTime.TotalMilliseconds:0} ms on the UI thread)");
                }
                else if (!t.IsCompletedSuccessfully || !t.Result)
                    _dirtySince ??= _now();   // failed or cleared meanwhile: the next check starts the interval again
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        lock (_lock) _pending = task;
        return new(AutoSaveResult.Started, task);
    }

    /// <summary>The design was saved, closed or replaced: deletes its recovery copy and forgets what was written.</summary>
    public void Clear()
    {
        lock (_lock) ForgetLocked();
        _store.Clear(Key);
    }

    private void ForgetLocked()
    {
        _epoch++;
        _writtenRevision = -1;
        _tooLargeRevision = -1;
        _dirtySince = null;
        _failures = 0;
    }

    private void Fail(string message)
    {
        bool first;
        lock (_lock)
        {
            first = _failures++ == 0;
            LastError = message;
        }
        AppLog.Warn("autosave", message);
        if (first) WriteFailed?.Invoke(message);
    }
}
