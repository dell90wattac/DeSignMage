using System.Globalization;
using System.Text.Json;
using DeSignMage.Feedback;

namespace DeSignMage.App.Recovery;

/// <summary>What a recovery copy needs to remember about its design besides the design itself.</summary>
public sealed record RecoveryMeta
{
    /// <summary>The window title: the file name, or "Untitled".</summary>
    public string Title { get; init; } = "Untitled";

    /// <summary>The .dsm the design was saved as; Save goes back to it after a restore. Null for a design never saved.</summary>
    public string? DocumentPath { get; init; }

    /// <summary>The legacy layout or earlier-build document the design came from (never written to; only suggests a name for Save As).</summary>
    public string? SourcePath { get; init; }

    /// <summary>When the copy was made (UTC).</summary>
    public DateTime SavedUtc { get; init; }

    public int ObjectCount { get; init; }

    /// <summary>The program run that made the copy, and which of its designs this is. Filled in by the store.</summary>
    public string RunId { get; init; } = "";
    public string Key { get; init; } = "";
}

/// <summary>A design that was not saved when a run of the program ended without closing (a crash, a kill, a power cut).</summary>
public sealed class RecoverableDesign
{
    internal RecoverableDesign(string runId, string key, RecoveryMeta meta, string filePath, string runFolder, string? crashSidecar, string? slotMeta, string? slotDsm)
    {
        RunId = runId;
        Key = key;
        Meta = meta;
        FilePath = filePath;
        RunFolder = runFolder;
        CrashSidecar = crashSidecar;
        SlotMeta = slotMeta;
        SlotDsm = slotDsm;
    }

    public string Id => RunId + "-" + Key;
    public string RunId { get; }
    public string Key { get; }

    /// <summary>Name, original file, time. The time is that of the copy in <see cref="FilePath"/>.</summary>
    public RecoveryMeta Meta { get; }

    /// <summary>The .dsm to read: the crash copy when the emergency save is newer than the last background copy.</summary>
    public string FilePath { get; }

    /// <summary>True when <see cref="FilePath"/> is the emergency copy written at the moment of a crash (inside the crash report's folder).</summary>
    public bool FromCrashCopy => CrashSidecar is not null;

    internal string RunFolder { get; }
    internal string? CrashSidecar { get; }
    internal string? SlotMeta { get; }
    internal string? SlotDsm { get; }
}

/// <summary>A write of one design's recovery copy that has begun: write the file to <see cref="TempPath"/>, then <see cref="RecoveryStore.Commit"/>.</summary>
public sealed class SlotWrite
{
    internal SlotWrite(string key, long generation, string tempPath)
    {
        Key = key;
        Generation = generation;
        TempPath = tempPath;
    }

    public string Key { get; }
    public string TempPath { get; }
    internal long Generation { get; }
}

/// <summary>
/// The recovery copies of one run of the program, and the way to find the ones an earlier run left behind.
/// <para>
/// Layout under the root (<c>%LOCALAPPDATA%\DeSignMage\recovery</c>, a separate folder from the installed program, so updates and
/// uninstalls leave it alone): one folder per run, <c>&lt;runId&gt;\</c>, holding <c>run.lock</c> (held open, without sharing, for as
/// long as the process lives), and per design <c>&lt;key&gt;.dsm</c> + <c>&lt;key&gt;.json</c> (the name, original path and time).
/// A folder whose lock can be opened belongs to a run that is over. A clean close deletes the whole folder; so does a normal Save (the
/// design's two files). Two copies of the program have two folders and never touch each other's.
/// </para>
/// </summary>
public sealed class RecoveryStore : IDisposable
{
    public const string LockName = "run.lock";
    private const string UnreadableFolder = "_unreadable";
    private const string ClaimFolder = "_claims";
    public const string SidecarSuffix = ".recovery.json";
    private const string SidecarDoneSuffix = ".recovery.done";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly string _root;
    private readonly string? _crashRoot;
    private readonly Func<DateTime> _utcNow;
    private readonly object _lock = new();
    private readonly Dictionary<string, long> _generation = [];
    private FileStream? _runLock;
    private bool _closed;
    private long _writes;

    /// <param name="root">The recovery folder.</param>
    /// <param name="crashRoot">The crash reports folder, where an emergency save leaves its own copy and a sidecar; null to ignore those.</param>
    public RecoveryStore(string root, string? crashRoot = null, Func<DateTime>? utcNow = null, string? runId = null)
    {
        _root = root;
        _crashRoot = crashRoot;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        RunId = runId ?? string.Create(CultureInfo.InvariantCulture, $"{_utcNow():yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..4]}");
    }

    public string RunId { get; }
    public string Root => _root;
    public string RunFolder => Path.Combine(_root, RunId);
    public bool IsClosed => _closed;

    private string DsmPath(string key) => Path.Combine(RunFolder, key + ".dsm");
    private string MetaPath(string key) => Path.Combine(RunFolder, key + ".json");

    // ---------------------------------------------------------------- this run's copies

    /// <summary>Creates this run's folder and takes its lock. Called by the first write; safe to call again.</summary>
    public void Open()
    {
        lock (_lock)
        {
            if (_closed || _runLock is not null) return;
            Directory.CreateDirectory(RunFolder);
            _runLock = new FileStream(Path.Combine(RunFolder, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
    }

    public bool HasCopy(string key) => !_closed && File.Exists(MetaPath(key)) && File.Exists(DsmPath(key));

    /// <summary>Starts writing the copy of the design <paramref name="key"/>: the caller writes the .dsm to <see cref="SlotWrite.TempPath"/> (any thread), then calls <see cref="Commit"/>.</summary>
    public SlotWrite BeginWrite(string key)
    {
        Open();
        long generation;
        lock (_lock) generation = _generation.GetValueOrDefault(key);
        return new SlotWrite(key, generation, Path.Combine(RunFolder, $"{key}.{Interlocked.Increment(ref _writes)}.tmp"));
    }

    /// <summary>
    /// Puts the finished file in place as the design's current copy. False (and the file is deleted) when the copy was cleared or the
    /// run was closed since <see cref="BeginWrite"/>: a save must not be undone by a write that was still running.
    /// </summary>
    public bool Commit(SlotWrite write, RecoveryMeta meta)
    {
        lock (_lock)
        {
            if (_closed || _generation.GetValueOrDefault(write.Key) != write.Generation || !File.Exists(write.TempPath))
            {
                TryDelete(write.TempPath);
                return false;
            }
            File.Move(write.TempPath, DsmPath(write.Key), overwrite: true);
            WriteMeta(MetaPath(write.Key), meta with { RunId = RunId, Key = write.Key });
            return true;
        }
    }

    /// <summary>The design was saved, closed or replaced: its copy is no longer needed. Also retires emergency copies of it from earlier crashes in this run.</summary>
    public void Clear(string key)
    {
        lock (_lock)
        {
            _generation[key] = _generation.GetValueOrDefault(key) + 1;
            TryDelete(MetaPath(key));
            TryDelete(DsmPath(key));
            if (Directory.Exists(RunFolder))
                foreach (var tmp in Directory.EnumerateFiles(RunFolder, key + ".*.tmp")) TryDelete(tmp);
        }
        RetireCrashCopies(RunId, key);
    }

    /// <summary>
    /// A clean end of the run (the window closed, saved or discarded): deletes this run's folder and retires its emergency copies.
    /// After this the store takes no more writes.
    /// </summary>
    public void CloseCleanly()
    {
        lock (_lock)
        {
            if (_closed) return;
            _closed = true;
            _runLock?.Dispose();
            _runLock = null;
            if (Directory.Exists(RunFolder))
            {
                // The copies first, one by one: if the folder itself cannot be removed (a scanner holds a file), nothing is left that looks like a design to restore.
                foreach (var file in SafeFiles(RunFolder, "*.json").Concat(SafeFiles(RunFolder, "*.dsm"))) TryDelete(file);
                try { Directory.Delete(RunFolder, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        RetireCrashCopies(RunId, null);
    }

    /// <summary>Lets go of the lock without deleting anything: what a process that was killed does. For tests.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _closed = true;
            _runLock?.Dispose();
            _runLock = null;
        }
    }

    // ---------------------------------------------------------------- emergency copies (crash folders)

    /// <summary>
    /// Writes the note that goes next to an emergency copy (<paramref name="dsmPath"/>, inside a crash report folder), so the next start can
    /// offer it for restoring. Never throws.
    /// </summary>
    public void WriteCrashSidecar(string dsmPath, string key, RecoveryMeta meta)
    {
        try { WriteMeta(dsmPath + SidecarSuffix, meta with { RunId = RunId, Key = key, SavedUtc = _utcNow() }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("recovery", $"Could not write the note next to the emergency copy: {ex.GetType().Name}"); }
    }

    private void RetireCrashCopies(string runId, string? key)
    {
        if (_crashRoot is null || !Directory.Exists(_crashRoot)) return;
        try
        {
            foreach (var sidecar in Directory.EnumerateFiles(_crashRoot, "*" + SidecarSuffix, SearchOption.AllDirectories))
            {
                var meta = ReadMeta(sidecar);
                if (meta is null || meta.RunId != runId || (key is not null && meta.Key != key)) continue;
                Retire(sidecar);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Retire(string sidecar)
    {
        try { File.Move(sidecar, sidecar[..^SidecarSuffix.Length] + SidecarDoneSuffix, overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- what earlier runs left

    /// <summary>
    /// The designs that earlier runs of the program left unsaved, newest first: copies in run folders whose process is gone, and emergency
    /// copies from crash folders. Runs that are still going (another copy of the program, or this one) are left alone. A design that has both a
    /// background copy and an emergency copy is listed once, with the newer file. Folders that hold nothing are removed.
    /// </summary>
    public IReadOnlyList<RecoverableDesign> FindRecoverable()
    {
        var found = new Dictionary<string, RecoverableDesign>();
        void Offer(RecoverableDesign d)
        {
            if (!found.TryGetValue(d.Id, out var existing) || d.Meta.SavedUtc > existing.Meta.SavedUtc) found[d.Id] = d;
        }

        if (Directory.Exists(_root))
        {
            foreach (var folder in Directory.EnumerateDirectories(_root))
            {
                string runId = Path.GetFileName(folder);
                if (runId.StartsWith('_') || runId == RunId || IsRunAlive(folder)) continue;
                bool any = false;
                foreach (var metaFile in SafeFiles(folder, "*.json"))
                {
                    var meta = ReadMeta(metaFile);
                    string dsm = Path.ChangeExtension(metaFile, ".dsm");
                    if (meta is null || !File.Exists(dsm)) continue;
                    string key = Path.GetFileNameWithoutExtension(metaFile);
                    Offer(new RecoverableDesign(runId, key, meta with { RunId = runId, Key = key }, dsm, folder, null, metaFile, dsm));
                    any = true;
                }
                if (!any) DeleteRun(folder);
            }
        }

        if (_crashRoot is not null && Directory.Exists(_crashRoot))
        {
            foreach (var sidecar in SafeFiles(_crashRoot, "*" + SidecarSuffix, SearchOption.AllDirectories))
            {
                var meta = ReadMeta(sidecar);
                string dsm = sidecar[..^SidecarSuffix.Length];
                if (meta is null || meta.RunId == RunId || !File.Exists(dsm)) continue;
                if (IsRunAlive(Path.Combine(_root, meta.RunId))) continue;   // the program that wrote it is still running: it was a survivable error
                // A background copy of the same design in the run's folder may still exist: that entry is replaced when this one is newer.
                string runFolder = Path.Combine(_root, meta.RunId);
                string? slotMeta = File.Exists(Path.Combine(runFolder, meta.Key + ".json")) ? Path.Combine(runFolder, meta.Key + ".json") : null;
                Offer(new RecoverableDesign(meta.RunId, meta.Key, meta, dsm, runFolder, sidecar, slotMeta, slotMeta is null ? null : Path.ChangeExtension(slotMeta, ".dsm")));
            }
        }
        // A background copy that lost against the emergency copy still has to be removed with it: keep its files in the chosen entry.
        return found.Values.OrderByDescending(d => d.Meta.SavedUtc).ToList();
    }

    /// <summary>Takes the right to deal with <paramref name="design"/> (null if another copy of the program is already doing so). Dispose it when done.</summary>
    public IDisposable? TryClaim(RecoverableDesign design)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(_root, ClaimFolder));
            var path = Path.Combine(_root, ClaimFolder, design.Id + ".claim");
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return stream;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The person said no, or the design is now open: removes the copy (and retires its emergency copy; the crash report folder keeps its own file).</summary>
    public void Discard(RecoverableDesign design)
    {
        if (design.SlotMeta is not null) TryDelete(design.SlotMeta);
        if (design.SlotDsm is not null) TryDelete(design.SlotDsm);
        if (design.CrashSidecar is not null) Retire(design.CrashSidecar);
        // The same design may also have its other half listed under another entry (emergency copy + background copy share an id: both go).
        if (_crashRoot is not null && Directory.Exists(_crashRoot))
            foreach (var sidecar in SafeFiles(_crashRoot, "*" + SidecarSuffix, SearchOption.AllDirectories))
                if (ReadMeta(sidecar) is { } m && m.RunId == design.RunId && m.Key == design.Key) Retire(sidecar);
        if (Directory.Exists(design.RunFolder) && !IsRunAlive(design.RunFolder) && !SafeFiles(design.RunFolder, "*.json").Any()) DeleteRun(design.RunFolder);
    }

    /// <summary>The copy cannot be read: it is set aside in <c>_unreadable</c> (kept, never offered again) so the same message does not come back at every start.</summary>
    public void SetAsideUnreadable(RecoverableDesign design)
    {
        try
        {
            var dir = Path.Combine(_root, UnreadableFolder, design.Id);
            Directory.CreateDirectory(dir);
            File.Copy(design.FilePath, Path.Combine(dir, Path.GetFileName(design.FilePath)), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Discard(design);
    }

    /// <summary>
    /// Brings the design's file into this run as the copy of <paramref name="key"/> and removes the old entry, so the restored design is protected
    /// from the first moment, before the next background copy is due. False if copying failed (the old entry then stays).
    /// </summary>
    public bool Adopt(RecoverableDesign design, string key)
    {
        try
        {
            var write = BeginWrite(key);
            File.Copy(design.FilePath, write.TempPath, overwrite: true);
            if (!Commit(write, design.Meta)) return false;
            Discard(design);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("recovery", $"Could not copy the recovered design into this run: {ex.GetType().Name}");
            return false;
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>True while the process that owns the run folder is alive (its lock is held).</summary>
    public static bool IsRunAlive(string runFolder)
    {
        var lockFile = Path.Combine(runFolder, LockName);
        if (!File.Exists(lockFile)) return false;
        try
        {
            using var _ = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static void DeleteRun(string folder)
    {
        try { Directory.Delete(folder, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static IEnumerable<string> SafeFiles(string folder, string pattern, SearchOption option = SearchOption.TopDirectoryOnly)
    {
        try { return Directory.EnumerateFiles(folder, pattern, option).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static void WriteMeta(string path, RecoveryMeta meta)
    {
        var temp = path + ".part";
        File.WriteAllText(temp, JsonSerializer.Serialize(meta, Json));
        File.Move(temp, path, overwrite: true);
    }

    private static RecoveryMeta? ReadMeta(string path)
    {
        try { return JsonSerializer.Deserialize<RecoveryMeta>(File.ReadAllText(path), Json); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
