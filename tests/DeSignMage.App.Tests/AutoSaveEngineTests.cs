using DeSignMage.App.Recovery;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Native;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>When a recovery copy is made, when it is not, and that it never gets in the way of saving.</summary>
public class AutoSaveEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dsm-autosave-test-" + Guid.NewGuid().ToString("N")[..8]);
    private DateTime _now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
    private readonly RecoveryStore _store;
    private readonly Design _design = new();
    private AutoSaveOptions _options = new() { Interval = TimeSpan.FromMinutes(5) };
    private int _snapshots;

    public AutoSaveEngineTests() => _store = new RecoveryStore(Path.Combine(_root, "recovery"), Path.Combine(_root, "crash"), () => _now);

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>The design as the window would report it.</summary>
    private sealed class Design
    {
        public object Identity = new();
        public bool Unsaved = true;
        public long Revision = 1;
        public Document Document = DemoContent.NewDocument();
        public Func<DsmSnapshot>? Take;
    }

    private AutoSaveEngine Engine(Func<Func<bool>, Task<bool>>? run = null) => new(_store,
        () => new AutoSaveState(_design.Identity, _design.Unsaved, _design.Revision, new RecoveryMeta { Title = "Sign.dsm" },
            () => { _snapshots++; return _design.Take?.Invoke() ?? DsmWriter.Snapshot(_design.Document, new DsmSaveOptions { Compact = true }); }),
        () => _options, () => _now, run ?? (work => Task.FromResult(work())));

    private static void Add(Document doc, double width = 50) =>
        new CommandHistory(doc).Execute(new AddObjectCommand(doc.ActiveLayer, new RectangleObject(width, 20)));

    private AutoSaveResult Tick(AutoSaveEngine engine, double minutesLater = 0)
    {
        _now = _now.AddMinutes(minutesLater);
        return engine.Tick().Result;
    }

    // ---------------------------------------------------------------- timer logic

    [Fact]
    public void Nothing_is_written_until_the_interval_has_passed_since_the_changes_were_first_seen()
    {
        var engine = Engine();
        Assert.Equal(AutoSaveResult.NotDue, Tick(engine));            // first look: the clock starts now
        Assert.Equal(AutoSaveResult.NotDue, Tick(engine, 4.9));
        Assert.False(_store.HasCopy("design"));
        Assert.Equal(AutoSaveResult.Started, Tick(engine, 0.2));      // 5.1 minutes after it was first seen
        Assert.True(_store.HasCopy("design"));
        Assert.Equal(1, engine.Written);
    }

    [Fact]
    public void Nothing_is_written_when_there_are_no_unsaved_changes()
    {
        _design.Unsaved = false;
        var engine = Engine();
        Assert.Equal(AutoSaveResult.NoChanges, Tick(engine));
        Assert.Equal(AutoSaveResult.NoChanges, Tick(engine, 60));
        Assert.Equal(0, _snapshots);
        Assert.False(_store.HasCopy("design"));
    }

    [Fact]
    public void A_design_that_is_not_changing_is_written_once_and_then_left_alone()
    {
        var engine = Engine();
        Tick(engine);
        Assert.Equal(AutoSaveResult.Started, Tick(engine, 5));
        Assert.Equal(AutoSaveResult.Unchanged, Tick(engine, 30));
        Assert.Equal(AutoSaveResult.Unchanged, Tick(engine, 30));
        Assert.Equal(1, _snapshots);
        Assert.Equal(1, engine.Written);
    }

    [Fact]
    public void More_changes_are_written_again_one_interval_later()
    {
        var engine = Engine();
        Tick(engine);
        Tick(engine, 5);
        _design.Revision++;
        Assert.Equal(AutoSaveResult.NotDue, Tick(engine, 1));         // noticed now
        Assert.Equal(AutoSaveResult.NotDue, Tick(engine, 3));
        Assert.Equal(AutoSaveResult.Started, Tick(engine, 2.1));
        Assert.Equal(2, engine.Written);
    }

    [Fact]
    public void The_interval_from_Options_applies_at_once()
    {
        var engine = Engine();
        Tick(engine);
        Assert.Equal(AutoSaveResult.NotDue, Tick(engine, 2));
        _options = _options with { Interval = TimeSpan.FromMinutes(1) };
        Assert.Equal(AutoSaveResult.Started, Tick(engine, 0));        // 2 minutes have passed and 1 is enough now
    }

    [Fact]
    public void Switched_off_means_nothing_is_read_or_written_and_switching_on_starts_the_clock()
    {
        _options = _options with { Enabled = false };
        var engine = Engine();
        Assert.Equal(AutoSaveResult.Disabled, Tick(engine));
        Assert.Equal(AutoSaveResult.Disabled, Tick(engine, 60));
        Assert.Equal(0, _snapshots);

        _options = _options with { Enabled = true };
        Assert.Equal(AutoSaveResult.NotDue, Tick(engine));
        Assert.Equal(AutoSaveResult.Started, Tick(engine, 5));
    }

    [Fact]
    public void A_design_over_the_size_limit_gets_no_copy_and_is_not_read_again_until_it_changes()
    {
        _options = _options with { LargestBytes = 10 };
        var engine = Engine();
        Tick(engine);
        Assert.Equal(AutoSaveResult.TooLarge, Tick(engine, 5));
        Assert.Equal(AutoSaveResult.TooLarge, Tick(engine, 30));
        Assert.Equal(1, _snapshots);
        Assert.False(_store.HasCopy("design"));

        _options = _options with { LargestBytes = 0 };                 // 0 = no limit
        _design.Revision++;
        Tick(engine);
        Assert.Equal(AutoSaveResult.Started, Tick(engine, 5));
    }

    [Fact]
    public async Task Only_one_copy_is_written_at_a_time_and_a_slow_one_stretches_the_interval()
    {
        var release = new TaskCompletionSource<bool>();
        var engine = Engine(work => Task.Run(() => { release.Task.Wait(); return work(); }));
        Tick(engine);
        Assert.Equal(AutoSaveResult.Started, Tick(engine, 5));
        _design.Revision++;
        Assert.Equal(AutoSaveResult.Busy, Tick(engine, 5));            // the first is still being written
        release.SetResult(true);
        await engine.Pending!;

        Assert.Equal(1, engine.Written);
        Assert.True(engine.EffectiveInterval(_options) >= _options.Interval);
    }

    [Fact]
    public void A_copy_that_took_long_to_write_is_made_less_often()
    {
        var engine = Engine();
        Assert.Equal(TimeSpan.FromMinutes(5), engine.EffectiveInterval(_options));          // quick writes: the Options interval
        engine.LastWriteTime = TimeSpan.FromMinutes(2);
        Assert.Equal(TimeSpan.FromMinutes(8), engine.EffectiveInterval(_options));          // four times as long as the last copy took
        engine.LastWriteTime = TimeSpan.FromHours(1);
        Assert.Equal(TimeSpan.FromMinutes(30), engine.EffectiveInterval(_options));         // but never more than half an hour

        engine.LastWriteTime = TimeSpan.FromMinutes(2);
        Tick(engine);
        Assert.Equal(AutoSaveResult.NotDue, Tick(engine, 6));
        Assert.Equal(AutoSaveResult.Started, Tick(engine, 2.1));
    }

    // ---------------------------------------------------------------- cleanup

    [Fact]
    public async Task Saving_removes_the_copy_and_a_copy_still_being_written_cannot_bring_it_back()
    {
        var release = new TaskCompletionSource<bool>();
        var engine = Engine(work => Task.Run(() => { release.Task.Wait(); return work(); }));
        Tick(engine);
        Assert.Equal(AutoSaveResult.Started, Tick(engine, 5));
        engine.Clear();                                                 // File > Save finished while the copy was in the pipe
        _design.Unsaved = false;
        release.SetResult(true);
        await engine.Pending!;

        Assert.False(_store.HasCopy("design"));
        Assert.Equal(0, engine.Written);
        Assert.Empty(Directory.GetFiles(_store.RunFolder, "*.tmp"));
        _store.Dispose();
        Assert.Empty(new RecoveryStore(_store.Root, null).FindRecoverable());
    }

    [Fact]
    public void Undoing_back_to_the_saved_state_removes_the_copy()
    {
        var engine = Engine();
        Tick(engine);
        Tick(engine, 5);
        Assert.True(_store.HasCopy("design"));

        _design.Unsaved = false;                                        // same as the saved file again
        Assert.Equal(AutoSaveResult.NoChanges, Tick(engine, 1));
        Assert.False(_store.HasCopy("design"));
    }

    [Fact]
    public void A_different_design_starts_over_even_when_its_revision_number_is_the_same()
    {
        var engine = Engine();
        Tick(engine);
        Tick(engine, 5);
        Assert.Equal(1, engine.Written);

        _design.Identity = new object();                               // File > New / Open: another document, revision 1 again
        Assert.Equal(AutoSaveResult.NotDue, Tick(engine, 1));
        Assert.Equal(AutoSaveResult.Started, Tick(engine, 5));
        Assert.Equal(2, engine.Written);
    }

    // ---------------------------------------------------------------- failures

    [Fact]
    public void A_design_that_cannot_be_read_is_reported_once_and_tried_again_later()
    {
        _design.Take = () => throw new InvalidOperationException("boom");
        var engine = Engine();
        var messages = new List<string>();
        engine.WriteFailed += messages.Add;
        Tick(engine);

        Assert.Equal(AutoSaveResult.Failed, Tick(engine, 5));
        Assert.Equal(AutoSaveResult.NotDue, Tick(engine, 1));
        Assert.Equal(AutoSaveResult.Failed, Tick(engine, 4.5));
        Assert.Single(messages);                                        // a full disk says so once, not every few minutes
        Assert.NotNull(engine.LastError);

        _design.Take = null;
        Assert.Equal(AutoSaveResult.Started, Tick(engine, 5.5));
        Assert.Null(engine.LastError);
    }

    [Fact]
    public async Task A_copy_that_finishes_after_the_window_closed_is_dropped_and_leaves_nothing_behind()
    {
        var release = new TaskCompletionSource<bool>();
        var engine = Engine(work => Task.Run(() => { release.Task.Wait(); return work(); }));
        Tick(engine);
        Assert.Equal(AutoSaveResult.Started, Tick(engine, 5));
        _store.CloseCleanly();                                          // the window closed (clean) while the worker was still busy
        release.SetResult(true);
        await engine.Pending!;

        Assert.Equal(0, engine.Written);
        Assert.False(Directory.Exists(_store.RunFolder) && Directory.GetFiles(_store.RunFolder).Any(f => !f.EndsWith(RecoveryStore.LockName)));
        Assert.Empty(new RecoveryStore(_store.Root, null).FindRecoverable());
    }

    // ---------------------------------------------------------------- the round trip

    [Fact]
    public async Task A_copy_written_in_the_background_reads_back_as_the_same_design()
    {
        var doc = _design.Document;
        Add(doc, 30);
        Add(doc, 70);
        var engine = Engine(work => Task.Run(work));                    // a real worker thread, like the window
        Assert.Equal(AutoSaveResult.NotDue, Tick(engine));
        _now = _now.AddMinutes(5);
        var tick = engine.Tick();
        Assert.Equal(AutoSaveResult.Started, tick.Result);
        await tick.Write!;
        _store.Dispose();                                               // the run ends without a clean close

        var found = Assert.Single(new RecoveryStore(_store.Root, null, () => _now).FindRecoverable());
        var back = DsmReader.Load(found.FilePath).Document;
        Assert.Equal(doc.ObjectCount, back.ObjectCount);
        Assert.Equal(2, back.ObjectCount);
        Assert.Equal("Sign.dsm", found.Meta.Title);
    }

    [Fact]
    public async Task The_design_may_change_while_the_copy_is_being_packed()
    {
        var doc = _design.Document;
        Add(doc, 30);
        var release = new TaskCompletionSource<bool>();
        var engine = Engine(work => Task.Run(() => { release.Task.Wait(); return work(); }));
        Tick(engine);
        var tick = Tick(engine, 5);
        Assert.Equal(AutoSaveResult.Started, tick);
        Add(doc, 99);                                                   // edited while the worker has not started packing yet
        release.SetResult(true);
        await engine.Pending!;

        _store.Dispose();
        var found = Assert.Single(new RecoveryStore(_store.Root, null).FindRecoverable());
        Assert.Equal(1, DsmReader.Load(found.FilePath).Document.ObjectCount);   // the copy is the design as it was at the snapshot
    }

    [Fact]
    public void Taking_the_snapshot_of_a_big_design_is_quick_and_the_packing_is_where_the_time_goes()
    {
        var doc = _design.Document;
        var history = new CommandHistory(doc);
        for (int i = 0; i < 4000; i++) history.Execute(new AddObjectCommand(doc.ActiveLayer, new RectangleObject(10 + i % 50, 12)));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var snapshot = DsmWriter.Snapshot(doc, new DsmSaveOptions { Compact = true });
        var snapshotMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var path = Path.Combine(_root, "big.dsm");
        Directory.CreateDirectory(_root);
        snapshot.SaveTo(path);

        Assert.Equal(4000, DsmReader.Load(path).Document.ObjectCount);
        Assert.True(snapshotMs < 1000, $"snapshot of 4000 objects took {snapshotMs:0} ms on the UI thread");
        Assert.True(snapshot.UncompressedSize > 100_000);
    }
}
