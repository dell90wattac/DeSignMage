namespace DeSignMage.App.Shell;

/// <summary>
/// Which Dock panel is open (R17). One panel at a time: clicking a closed Dock Tab opens that panel across the Dock's whole height
/// (the one that was open closes), and clicking the open tab closes it, which leaves only the tab strip. Nothing is remembered: the program
/// always starts with every panel closed. Pure logic, no controls: the window just shows what this says.
/// </summary>
public sealed class DockLayout
{
    /// <summary>Every Dock panel, in the order of their Dock Tabs.</summary>
    public static readonly IReadOnlyList<string> AllPanels = ["colorMixer", "layers", "history"];

    /// <summary>The tabs this instance knows (the Dock's three, or the list a test gives it).</summary>
    public IReadOnlyList<string> Panels { get; }

    public DockLayout(IReadOnlyList<string>? panels = null) => Panels = panels ?? AllPanels;

    /// <summary>The open panel, or null when every panel is closed.</summary>
    public string? Open { get; private set; }

    public bool IsOpen(string id) => Open == id;

    /// <summary>Raised after a click changed which panel is open.</summary>
    public event EventHandler? Changed;

    /// <summary>A click on the Dock Tab of <paramref name="id"/>: closes its panel if it is open, else opens it and closes the other.</summary>
    public void Toggle(string id)
    {
        if (!Panels.Contains(id)) throw new ArgumentException($"Unknown Dock panel '{id}'.", nameof(id));
        Open = Open == id ? null : id;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
