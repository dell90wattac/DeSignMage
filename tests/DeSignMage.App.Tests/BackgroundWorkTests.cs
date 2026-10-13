using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DeSignMage.App.Shell;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>The shared progress UI for slow work: quick work shows nothing, slow work shows a modal window that closes by itself, Cancel hands control back.</summary>
public class BackgroundWorkTests
{
    private static void Pump(Func<bool> until, int timeoutMs = 10_000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!until() && watch.ElapsedMilliseconds < timeoutMs)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }

    private static async Task<T> WithShortDelay<T>(Func<Task<T>> body)
    {
        var old = BackgroundWork.ShowAfter;
        BackgroundWork.ShowAfter = TimeSpan.FromMilliseconds(40);
        try { return await body(); }
        finally { BackgroundWork.ShowAfter = old; }
    }

    [AvaloniaFact]
    public async Task Quick_work_runs_on_a_worker_thread_and_shows_no_progress_window()
    {
        var owner = new Window();
        owner.Show();
        int ui = Environment.CurrentManagedThreadId;
        int seen = -1;
        var result = await BackgroundWork.RunAsync(owner, "Test", "Working...", _ => { seen = Environment.CurrentManagedThreadId; return 42; });
        Assert.Equal(42, result);
        Assert.NotEqual(ui, seen);
        Assert.Null(BackgroundWork.Current);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Slow_work_shows_a_modal_progress_window_keeps_the_ui_alive_and_closes_it_when_done()
    {
        await WithShortDelay(async () =>
        {
            var owner = new Window();
            owner.Show();
            bool uiTickRan = false;
            var work = BackgroundWork.RunAsync(owner, "Open", "Opening big.dsm...", _ => { Thread.Sleep(600); return "done"; });
            Pump(() => BackgroundWork.Current is not null);
            var dialog = Assert.IsType<ProgressDialog>(BackgroundWork.Current);
            Assert.Equal("Opening big.dsm...", dialog.Message.Text);
            Assert.Null(dialog.CancelButton);                               // this work cannot be stopped
            Dispatcher.UIThread.Post(() => uiTickRan = true);               // the UI thread is free while the worker sleeps
            Pump(() => uiTickRan, 300);
            Assert.True(uiTickRan);
            Assert.Same(dialog, Assert.Single(owner.OwnedWindows));

            Assert.Equal("done", await work);
            Pump(() => !owner.OwnedWindows.Any(), 2000);
            Assert.Empty(owner.OwnedWindows);
            Assert.Null(BackgroundWork.Current);
            owner.Close();
            return 0;
        });
    }

    [AvaloniaFact]
    public async Task Cancel_returns_control_at_once_and_the_window_closes()
    {
        await WithShortDelay(async () =>
        {
            var owner = new Window();
            owner.Show();
            bool sawCancel = false;
            var work = BackgroundWork.RunAsync(owner, "Vectorize", "Tracing...", ct =>
            {
                while (!ct.IsCancellationRequested) Thread.Sleep(5);
                sawCancel = true;
                return 1;
            }, cancellable: true);
            Pump(() => BackgroundWork.Current is not null);
            var dialog = Assert.IsType<ProgressDialog>(BackgroundWork.Current);
            Assert.NotNull(dialog.CancelButton);

            dialog.CancelButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
            Pump(() => !owner.OwnedWindows.Any(), 2000);
            Assert.Empty(owner.OwnedWindows);
            Pump(() => sawCancel, 2000);
            Assert.True(sawCancel);                                          // the worker was told to stop
            owner.Close();
            return 0;
        });
    }

    [AvaloniaFact]
    public async Task A_failure_in_the_work_is_rethrown_to_the_caller()
    {
        var owner = new Window();
        owner.Show();
        await Assert.ThrowsAsync<InvalidDataException>(() => BackgroundWork.RunAsync<int>(owner, "Open", "x", _ => throw new InvalidDataException("bad file")));
        Assert.Null(BackgroundWork.Current);
        owner.Close();
    }
}
