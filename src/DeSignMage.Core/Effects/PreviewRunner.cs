namespace DeSignMage.Core.Effects;

/// <summary>
/// Debounced live preview for the effect tools: every <see cref="Schedule{T}"/> cancels the one before, waits
/// <see cref="Debounce"/> (the user is still typing or dragging), computes on a worker thread and hands the result to the UI
/// thread through <c>post</c>. Results of cancelled runs are never delivered. A failing computation reports through
/// <c>failed</c> instead of throwing, so a preview can never crash the window.
/// </summary>
public sealed class PreviewRunner
{
    private CancellationTokenSource? _cts;

    public PreviewRunner(Action<Action>? post = null, TimeSpan? debounce = null)
    {
        Post = post;
        Debounce = debounce ?? TimeSpan.FromMilliseconds(150);
    }

    /// <summary>Runs an action on the UI thread; null runs it right there (tests, no UI).</summary>
    public Action<Action>? Post { get; set; }

    public TimeSpan Debounce { get; set; }

    /// <summary>The newest scheduled run (completed when its result was delivered or it was cancelled). Tests await it.</summary>
    public Task Settled { get; private set; } = Task.CompletedTask;

    /// <summary>True while a scheduled computation has neither delivered nor been replaced.</summary>
    public bool IsBusy { get; private set; }

    public void Schedule<T>(Func<CancellationToken, T> compute, Action<T> deliver, Action<Exception>? failed = null)
    {
        Cancel();
        var cts = _cts = new CancellationTokenSource();
        var token = cts.Token;
        IsBusy = true;
        Settled = Task.Run(async () =>
        {
            try
            {
                if (Debounce > TimeSpan.Zero) await Task.Delay(Debounce, token).ConfigureAwait(false);
                var result = compute(token);
                token.ThrowIfCancellationRequested();
                Run(() =>
                {
                    if (token.IsCancellationRequested) return;
                    IsBusy = false;
                    deliver(result);
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                Run(() =>
                {
                    if (token.IsCancellationRequested) return;
                    IsBusy = false;
                    failed?.Invoke(e);
                });
            }
        });
    }

    /// <summary>Drops whatever is scheduled or running (its result will not be delivered).</summary>
    public void Cancel()
    {
        _cts?.Cancel();
        _cts = null;
        IsBusy = false;
    }

    private void Run(Action action)
    {
        if (Post is { } post) post(action);
        else action();
    }
}
