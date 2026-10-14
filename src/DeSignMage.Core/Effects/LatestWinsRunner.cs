namespace DeSignMage.Core.Effects;

/// <summary>
/// Live previews that follow a dragging pointer or a typed number (R31): at most ONE computation runs at a time, on a worker thread; asking for another
/// while one runs only replaces what is waiting, so requests that the pointer has already left are never computed ("the latest request wins") and a slow
/// computation can never pile up behind itself. Every computation that finishes is delivered (to the UI thread, through <see cref="Post"/>) unless
/// <see cref="Cancel"/> was called after it was asked for, so the preview lags the pointer by at most one computation. There is no pause before a
/// computation starts. A failing computation is reported through <c>failed</c> instead of throwing.
/// </summary>
public sealed class LatestWinsRunner
{
    private sealed record Job(Action Run);

    private readonly object _gate = new();
    private Job? _pending;
    private bool _running;
    private int _epoch;
    private CancellationTokenSource _cts = new();
    private TaskCompletionSource _idle = Done();

    public LatestWinsRunner(Action<Action>? post = null) => Post = post;

    /// <summary>Runs an action on the UI thread; null runs it right away on the worker (tests, no UI).</summary>
    public Action<Action>? Post { get; set; }

    /// <summary>How many computations ran (tests, profiling).</summary>
    public int Computed { get; private set; }

    /// <summary>How many requests were dropped because a newer one replaced them before they started (tests, profiling).</summary>
    public int Skipped { get; private set; }

    /// <summary>Completed when nothing is running or waiting (tests await it).</summary>
    public Task Idle
    {
        get
        {
            lock (_gate) return _idle.Task;
        }
    }

    public bool IsBusy
    {
        get
        {
            lock (_gate) return _running || _pending is not null;
        }
    }

    private static TaskCompletionSource Done()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    public void Submit<T>(Func<CancellationToken, T> compute, Action<T> deliver, Action<Exception>? failed = null)
    {
        int epoch;
        Job job;
        lock (_gate)
        {
            epoch = _epoch;
            var token = _cts.Token;
            job = new Job(() =>
            {
                try
                {
                    var result = compute(token);
                    Deliver(epoch, () => deliver(result));
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                    Deliver(epoch, () => failed?.Invoke(e));
                }
            });
            if (_running)
            {
                if (_pending is not null) Skipped++;
                _pending = job;
                return;
            }
            _running = true;
            if (_idle.Task.IsCompleted) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Start(job);
    }

    private void Deliver(int epoch, Action action)
    {
        lock (_gate)
            if (epoch != _epoch) return;
        void Guarded()
        {
            lock (_gate)
                if (epoch != _epoch) return;
            action();
        }
        if (Post is { } post) post(Guarded);
        else action();
    }

    private void Start(Job job) => Task.Run(() =>
    {
        while (true)
        {
            job.Run();
            lock (_gate)
            {
                Computed++;
                if (_pending is { } next)
                {
                    _pending = null;
                    job = next;
                    continue;
                }
                _running = false;
                _idle.TrySetResult();
                return;
            }
        }
    });

    /// <summary>Drops what is waiting and the result of what is running (its token is cancelled so it can stop early; nobody hears of it either way).</summary>
    public void Cancel()
    {
        CancellationTokenSource old;
        lock (_gate)
        {
            _epoch++;
            _pending = null;
            old = _cts;
            _cts = new CancellationTokenSource();
        }
        old.Cancel();
    }
}
