using DeSignMage.App.Recovery;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>The recovery folder: one folder per run, a lock that tells a dead run from a live one, and what is left after a kill.</summary>
public class RecoveryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dsm-recovery-test-" + Guid.NewGuid().ToString("N")[..8]);
    private string Recovery => Path.Combine(_root, "recovery");
    private string Crash => Path.Combine(_root, "crash");
    private DateTime _now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
    private readonly List<RecoveryStore> _stores = [];

    public void Dispose()
    {
        foreach (var s in _stores) s.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private RecoveryStore NewRun(string? id = null)
    {
        var store = new RecoveryStore(Recovery, Crash, () => _now, id);
        _stores.Add(store);
        return store;
    }

    private static void Put(RecoveryStore store, string key, string content, RecoveryMeta? meta = null)
    {
        var w = store.BeginWrite(key);
        File.WriteAllText(w.TempPath, content);
        Assert.True(store.Commit(w, meta ?? new RecoveryMeta { Title = key + ".dsm", SavedUtc = DateTime.UtcNow }));
    }

    /// <summary>What a killed process leaves behind: the run's folder, with its lock released.</summary>
    private static void Kill(RecoveryStore store) => store.Dispose();

    [Fact]
    public void A_written_copy_is_found_by_the_next_run_after_a_kill()
    {
        var first = NewRun("run-a");
        Put(first, "design", "PK-bytes", new RecoveryMeta { Title = "Sign.dsm", DocumentPath = @"C:\Jobs\Sign.dsm", SavedUtc = _now, ObjectCount = 7 });
        Kill(first);

        var next = NewRun("run-b");
        var found = Assert.Single(next.FindRecoverable());
        Assert.Equal("Sign.dsm", found.Meta.Title);
        Assert.Equal(@"C:\Jobs\Sign.dsm", found.Meta.DocumentPath);
        Assert.Equal(7, found.Meta.ObjectCount);
        Assert.Equal("run-a", found.RunId);
        Assert.Equal("PK-bytes", File.ReadAllText(found.FilePath));
        Assert.False(found.FromCrashCopy);
    }

    [Fact]
    public void A_run_that_is_still_going_is_never_offered()
    {
        var other = NewRun("run-a");
        Put(other, "design", "x");
        var me = NewRun("run-b");

        Assert.Empty(me.FindRecoverable());   // the other copy of the program is alive: its lock is held
        Assert.True(File.Exists(Path.Combine(other.RunFolder, "design.dsm")));
    }

    [Fact]
    public void A_clean_close_leaves_nothing_and_the_next_start_asks_nothing()
    {
        var first = NewRun("run-a");
        Put(first, "design", "x");
        first.CloseCleanly();

        Assert.False(Directory.Exists(first.RunFolder));
        Assert.Empty(NewRun("run-b").FindRecoverable());
    }

    [Fact]
    public void Clearing_a_design_removes_its_copy_and_a_late_write_cannot_bring_it_back()
    {
        var store = NewRun("run-a");
        Put(store, "design", "old");
        var slow = store.BeginWrite("design");          // a write that started before the save...
        File.WriteAllText(slow.TempPath, "newer but stale");
        store.Clear("design");                           // ...the person saves: the copy goes

        Assert.False(store.HasCopy("design"));
        Assert.False(store.Commit(slow, new RecoveryMeta { Title = "x" }));   // ...and the slow write finishes after it
        Assert.False(store.HasCopy("design"));
        Assert.False(File.Exists(slow.TempPath));
        Kill(store);
        Assert.Empty(NewRun("run-b").FindRecoverable());
    }

    [Fact]
    public void A_newer_write_replaces_the_copy_in_one_piece()
    {
        var store = NewRun("run-a");
        Put(store, "design", "one");
        Put(store, "design", "two");
        Kill(store);

        var found = Assert.Single(NewRun("run-b").FindRecoverable());
        Assert.Equal("two", File.ReadAllText(found.FilePath));
        Assert.Empty(Directory.GetFiles(store.RunFolder, "*.tmp"));
    }

    [Fact]
    public void Several_designs_and_several_dead_runs_are_listed_newest_first()
    {
        var a = NewRun("run-a");
        Put(a, "design", "a", new RecoveryMeta { Title = "Old.dsm", SavedUtc = _now.AddHours(-30) });
        Kill(a);
        var b = NewRun("run-b");
        Put(b, "design", "b", new RecoveryMeta { Title = "New.dsm", SavedUtc = _now.AddMinutes(-3) });
        Put(b, "second", "c", new RecoveryMeta { Title = "Other.dsm", SavedUtc = _now.AddMinutes(-10) });
        Kill(b);

        var titles = NewRun("run-c").FindRecoverable().Select(d => d.Meta.Title).ToList();
        Assert.Equal(["New.dsm", "Other.dsm", "Old.dsm"], titles);
    }

    [Fact]
    public void An_empty_leftover_folder_is_cleaned_away()
    {
        var a = NewRun("run-a");
        a.Open();
        Kill(a);
        Assert.True(Directory.Exists(a.RunFolder));

        Assert.Empty(NewRun("run-b").FindRecoverable());
        Assert.False(Directory.Exists(a.RunFolder));
    }

    [Fact]
    public void Discard_removes_the_copy_and_its_folder()
    {
        var a = NewRun("run-a");
        Put(a, "design", "x");
        Kill(a);
        var me = NewRun("run-b");

        me.Discard(Assert.Single(me.FindRecoverable()));

        Assert.False(Directory.Exists(a.RunFolder));
        Assert.Empty(me.FindRecoverable());
    }

    [Fact]
    public void Two_copies_of_the_program_cannot_both_take_the_same_design()
    {
        var a = NewRun("run-a");
        Put(a, "design", "x");
        Kill(a);
        var first = NewRun("run-b");
        var second = NewRun("run-c");
        var design = Assert.Single(first.FindRecoverable());

        using (var claim = first.TryClaim(design))
        {
            Assert.NotNull(claim);
            Assert.Null(second.TryClaim(design));   // the other copy is already asking about it
        }
        using var later = second.TryClaim(design);
        Assert.NotNull(later);                       // released: it can be claimed again
    }

    [Fact]
    public void Adopt_moves_a_copy_into_this_run_so_it_is_protected_at_once()
    {
        var a = NewRun("run-a");
        Put(a, "design", "bytes", new RecoveryMeta { Title = "Sign.dsm", DocumentPath = @"C:\Jobs\Sign.dsm", SavedUtc = _now });
        Kill(a);
        var me = NewRun("run-b");

        Assert.True(me.Adopt(Assert.Single(me.FindRecoverable()), "design"));

        Assert.True(me.HasCopy("design"));
        Assert.False(Directory.Exists(a.RunFolder));
        Kill(me);
        var again = Assert.Single(NewRun("run-c").FindRecoverable());   // and survives a second kill
        Assert.Equal(@"C:\Jobs\Sign.dsm", again.Meta.DocumentPath);
        Assert.Equal("bytes", File.ReadAllText(again.FilePath));
    }

    [Fact]
    public void An_unreadable_copy_is_set_aside_kept_and_not_offered_again()
    {
        var a = NewRun("run-a");
        Put(a, "design", "garbage");
        Kill(a);
        var me = NewRun("run-b");

        me.SetAsideUnreadable(Assert.Single(me.FindRecoverable()));

        Assert.Empty(me.FindRecoverable());
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(Recovery, "_unreadable"), "*.dsm", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_damaged_note_is_ignored()
    {
        var a = NewRun("run-a");
        Put(a, "design", "x");
        Kill(a);
        File.WriteAllText(Path.Combine(a.RunFolder, "design.json"), "{ not json");

        Assert.Empty(NewRun("run-b").FindRecoverable());
    }

    // ---------------------------------------------------------------- emergency copies from a crash

    private string CrashCopy(RecoveryStore run, string content, DateTime savedUtc, string key = "design")
    {
        var dir = Path.Combine(Crash, "crash-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "recovered-1-Sign.dsm");
        File.WriteAllText(file, content);
        _now = savedUtc;
        run.WriteCrashSidecar(file, key, new RecoveryMeta { Title = "Sign.dsm", DocumentPath = @"C:\Jobs\Sign.dsm" });
        return file;
    }

    [Fact]
    public void An_emergency_copy_newer_than_the_background_copy_is_offered_instead_of_it_and_only_once()
    {
        var a = NewRun("run-a");
        var start = _now;
        Put(a, "design", "background", new RecoveryMeta { Title = "Sign.dsm", DocumentPath = @"C:\Jobs\Sign.dsm", SavedUtc = start.AddMinutes(-4) });
        var crashFile = CrashCopy(a, "emergency", start);
        Kill(a);
        _now = start.AddHours(1);

        var me = NewRun("run-b");
        var found = Assert.Single(me.FindRecoverable());
        Assert.True(found.FromCrashCopy);
        Assert.Equal(crashFile, found.FilePath);
        Assert.Equal("emergency", File.ReadAllText(found.FilePath));
        Assert.Equal(Path.Combine(a.RunFolder, "design.dsm"), found.SlotDsm);   // the background copy stays reachable as a second chance

        me.Discard(found);   // deals with both halves
        Assert.Empty(me.FindRecoverable());
        Assert.True(File.Exists(crashFile));   // the crash report folder keeps its own file
    }

    [Fact]
    public void The_background_copy_wins_when_it_is_newer_than_the_emergency_copy()
    {
        var a = NewRun("run-a");
        var start = _now;
        CrashCopy(a, "emergency-of-a-non-fatal-error", start.AddMinutes(-20));   // a survivable error long ago
        _now = start;
        Put(a, "design", "background", new RecoveryMeta { Title = "Sign.dsm", SavedUtc = start.AddMinutes(-2) });
        Kill(a);

        var found = Assert.Single(NewRun("run-b").FindRecoverable());
        Assert.False(found.FromCrashCopy);
        Assert.Equal("background", File.ReadAllText(found.FilePath));
    }

    [Fact]
    public void An_emergency_copy_alone_is_offered_when_the_crash_came_before_the_first_background_copy()
    {
        var a = NewRun("run-a");
        a.Open();
        CrashCopy(a, "emergency", _now);
        Kill(a);

        var found = Assert.Single(NewRun("run-b").FindRecoverable());
        Assert.True(found.FromCrashCopy);
        Assert.Equal(@"C:\Jobs\Sign.dsm", found.Meta.DocumentPath);
    }

    [Fact]
    public void An_emergency_copy_from_a_survivable_error_is_not_offered_while_that_run_goes_on_or_after_it_closed_cleanly()
    {
        var a = NewRun("run-a");
        a.Open();
        CrashCopy(a, "emergency", _now);

        Assert.Empty(NewRun("run-b").FindRecoverable());   // run-a is alive
        a.CloseCleanly();                                   // and then ended normally: its emergency copies are retired
        Assert.Empty(NewRun("run-c").FindRecoverable());
    }

    [Fact]
    public void Saving_retires_the_emergency_copies_of_that_design()
    {
        var a = NewRun("run-a");
        a.Open();
        CrashCopy(a, "emergency", _now);
        a.Clear("design");   // the person saved
        Kill(a);

        Assert.Empty(NewRun("run-b").FindRecoverable());
    }
}
