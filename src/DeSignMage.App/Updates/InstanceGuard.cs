namespace DeSignMage.App.Updates;

/// <summary>
/// Tells copies of the program apart with two named handles in the person's own login session: "Running" is held for the whole life
/// of every copy, "Updating" only while a copy shows the "Update required" window. A named handle exists while any process holds
/// it, and vanishes with the process, even after a crash. What this object reports is a snapshot from when it was created.
/// </summary>
internal sealed class InstanceGuard : IInstanceGuard, IDisposable
{
    public const string DefaultName = "DeSignMage.Instance";

    private readonly string _updatingName;
    private readonly Mutex _running;
    private Mutex? _updating;

    /// <summary>Takes part under this name (tests use their own, so they never meet a real copy of the program).</summary>
    public InstanceGuard(string name = DefaultName)
    {
        _updatingName = $@"Local\{name}.Updating";
        UpdateRunningElsewhere = Mutex.TryOpenExisting(_updatingName, out var other) && Dispose(other);
        _running = new Mutex(false, $@"Local\{name}.Running", out bool created);
        IsOnlyInstance = created;
    }

    public bool IsOnlyInstance { get; }

    public bool UpdateRunningElsewhere { get; }

    public void SetUpdating(bool updating)
    {
        if (updating) _updating ??= new Mutex(false, _updatingName);
        else { _updating?.Dispose(); _updating = null; }
    }

    public void Dispose()
    {
        _updating?.Dispose();
        _running.Dispose();
    }

    private static bool Dispose(Mutex m) { m.Dispose(); return true; }
}
