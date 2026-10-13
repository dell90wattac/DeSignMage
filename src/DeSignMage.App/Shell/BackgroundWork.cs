using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace DeSignMage.App.Shell;

/// <summary>
/// A small modal "please wait" window for work that runs on a worker thread: a message, a moving bar and, when the work can be
/// stopped, a Cancel button. The window never closes by itself or by the title bar's X (that would leave the work running unseen);
/// <see cref="BackgroundWork"/> closes it when the work is done.
/// </summary>
public sealed class ProgressDialog : Window
{
    private readonly Button? _cancel;
    private bool _allowClose;

    public ProgressDialog(string title, string text, Action? onCancel)
    {
        Title = title;
        CanResize = false;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        DialogScale.Attach(this, 400);

        Message = new TextBlock { Name = "ProgressMessage", Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var bar = new ProgressBar { Name = "ProgressBar", IsIndeterminate = true }.ScaledMinHeight(10);
        var panel = new StackPanel { Margin = UiScale.Space(18), Spacing = UiScale.Sp(14) };
        panel.Children.Add(Message);
        panel.Children.Add(bar);
        if (onCancel is not null)
        {
            _cancel = new Button { Name = "ProgressCancel", Content = "Cancel", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(90);
            _cancel.Click += (_, _) => { _cancel.IsEnabled = false; Message.Text = "Cancelling...  "; onCancel(); };
            panel.Children.Add(_cancel);
        }
        Content = DialogScale.Scrollable(panel);
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;                       // the X (or Alt+F4) does not abandon work that is still running
            if (_cancel is { IsEnabled: true }) { _cancel.IsEnabled = false; Message.Text = "Cancelling...  "; onCancel!(); }
        };
    }

    internal TextBlock Message { get; }
    internal Button? CancelButton => _cancel;

    /// <summary>Closes the window (the work is over).</summary>
    public void Finish()
    {
        _allowClose = true;
        Close();
    }
}

/// <summary>
/// Runs slow work off the UI thread (so the window never shows "Not Responding") with the shared progress UI: nothing at all if the
/// work is done within <see cref="ShowAfter"/>, otherwise a modal <see cref="ProgressDialog"/> (so nothing in the design can change
/// while the work reads it), with a Cancel button when the work honours its <see cref="CancellationToken"/>.
/// </summary>
public static class BackgroundWork
{
    /// <summary>How long work may take before the progress window appears.</summary>
    public static TimeSpan ShowAfter { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>The progress window that is open now, if any (for tests).</summary>
    public static ProgressDialog? Current { get; private set; }

    /// <summary>
    /// Runs <paramref name="work"/> on a worker thread and returns its result on the calling (UI) thread. With
    /// <paramref name="cancellable"/> the dialog has a Cancel button; cancelling returns control at once by throwing
    /// <see cref="OperationCanceledException"/>, and the worker's result is thrown away when it finishes.
    /// Exceptions from the work are rethrown here.
    /// </summary>
    public static async Task<T> RunAsync<T>(Window? owner, string title, string message, Func<CancellationToken, T> work, bool cancellable = false)
    {
        using var cts = new CancellationTokenSource();
        var task = Task.Run(() => work(cts.Token));
        if (owner is null || await Task.WhenAny(task, Task.Delay(ShowAfter)) == task) return await task;

        var cancelled = new TaskCompletionSource();
        var dialog = new ProgressDialog(title, message, cancellable ? () => { cts.Cancel(); cancelled.TrySetResult(); } : null);
        Current = dialog;
        var shown = dialog.ShowDialog(owner);
        try
        {
            if (task.IsCompleted) dialog.Finish();
            else _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(dialog.Finish), TaskScheduler.Default);
            // Cancel hands the window back without waiting for the worker.
            if (cancellable)
            {
                _ = cancelled.Task.ContinueWith(_ => Dispatcher.UIThread.Post(dialog.Finish), TaskScheduler.Default);
                if (await Task.WhenAny(task, cancelled.Task) != task)
                {
                    _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    await shown;
                    throw new OperationCanceledException(cts.Token);
                }
            }
            await shown;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog)) Current = null;
        }
        return await task;
    }

    public static Task RunAsync(Window? owner, string title, string message, Action<CancellationToken> work, bool cancellable = false) =>
        RunAsync<object?>(owner, title, message, ct => { work(ct); return null; }, cancellable);
}
