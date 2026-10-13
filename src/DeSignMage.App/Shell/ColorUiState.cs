using DeSignMage.Core.Model;
using DeSignMage.Formats.Palettes;

namespace DeSignMage.App.Shell;

/// <summary>Which of an object's two paints the color UI edits (Fill / Stroke radio).</summary>
public enum ColorTarget { Fill, Stroke }

/// <summary>The color model of the Color Mixer's sliders. HSV edits produce plain RGB colors.</summary>
public enum ColorEditMode { Rgb, Cmyk, Hsv }

/// <summary>
/// What the Color Mixer, the Palette Strip and the Project Color Bar share: the active palette, the Fill/Stroke target,
/// and the slider model. One per open document window.
/// </summary>
public sealed class ColorUiState
{
    private static IReadOnlyList<Palette>? _catalog;
    private Palette _palette;
    private ColorTarget _target;
    private ColorEditMode _mode;

    /// <summary>The palettes found in the `palettes` folder next to the exe (loaded once), or a small built-in set.</summary>
    public static IReadOnlyList<Palette> Catalog => _catalog ??= LoadCatalog();

    /// <summary>Replaces the catalog (tests).</summary>
    internal static void UseCatalog(IReadOnlyList<Palette>? palettes) => _catalog = palettes;

    private static IReadOnlyList<Palette> LoadCatalog()
    {
        var folders = new List<string> { PaletteLibrary.BundledFolder, PaletteLibrary.UserFolder };
        folders.AddRange(RememberedFolders());
        var list = PaletteLibrary.LoadFolders(folders).ToList();
        if (list.Count == 0) list.Add(PaletteLibrary.Starter());
        return list;
    }

    /// <summary>The palette folders the user picked earlier (File ▸ Load Color Palettes), remembered between runs.</summary>
    public static IReadOnlyList<string> RememberedFolders() =>
        AppSettings.Store.Get(PaletteLibrary.FoldersSetting, "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Remembers a palette folder for the next start.</summary>
    public static void RememberFolder(string folder)
    {
        var all = RememberedFolders().ToList();
        if (all.Contains(folder, StringComparer.OrdinalIgnoreCase)) return;
        all.Add(folder);
        AppSettings.Store.Set(PaletteLibrary.FoldersSetting, string.Join('|', all));
    }

    /// <summary>
    /// Adds palettes to the catalog (one with the same name as an existing one replaces it) and makes the first of them the active
    /// one. Returns how many were added.
    /// </summary>
    public int AddPalettes(IReadOnlyList<Palette> added)
    {
        if (added.Count == 0) return 0;
        _catalog = PaletteLibrary.Merge(Catalog, added);
        _palette = _catalog.First(p => p.Name.Equals(added[0].Name, StringComparison.OrdinalIgnoreCase));
        PaletteChanged?.Invoke(this, EventArgs.Empty);
        return added.Count;
    }

    /// <summary>Forgets the folders added by the user and goes back to the palettes found at start.</summary>
    public void ForgetAddedPalettes()
    {
        AppSettings.Store.Set(PaletteLibrary.FoldersSetting, "");
        var active = _palette.Name;
        _catalog = LoadCatalog();
        _palette = _catalog.FirstOrDefault(p => p.Name.Equals(active, StringComparison.OrdinalIgnoreCase)) ?? _catalog[0];
        PaletteChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Set by the window: opens the "pick a folder of palettes" dialog.</summary>
    public Action? LoadFolderRequested { get; set; }

    /// <summary>Set by the window: opens the "pick palette files" dialog.</summary>
    public Action? LoadFilesRequested { get; set; }

    public ColorUiState(ColorUiState? previous = null)
    {
        _palette = previous?._palette ??
                   Catalog.FirstOrDefault(p => p.Name.Equals(PaletteLibrary.DefaultPaletteName, StringComparison.OrdinalIgnoreCase)) ??
                   Catalog[0];
        _target = previous?._target ?? ColorTarget.Fill;
        _mode = previous?._mode ?? ColorEditMode.Rgb;
        _showJobPalette = previous?._showJobPalette ?? true;
    }

    public IReadOnlyList<Palette> Palettes => Catalog;

    private bool _showJobPalette = true;

    /// <summary>View ▸ Show Job Color Palette: the used-colors row at the left of the Project Color Bar.</summary>
    public bool ShowJobPalette
    {
        get => _showJobPalette;
        set
        {
            if (_showJobPalette == value) return;
            _showJobPalette = value;
            JobPaletteVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? JobPaletteVisibilityChanged;

    public Palette ActivePalette
    {
        get => _palette;
        set
        {
            if (ReferenceEquals(_palette, value)) return;
            _palette = value;
            PaletteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ColorTarget Target
    {
        get => _target;
        set
        {
            if (_target == value) return;
            _target = value;
            TargetChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ColorEditMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            ModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? PaletteChanged, TargetChanged, ModeChanged;
}
