using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Shell;
using DeSignMage.Feedback;
using DeSignMage.Formats.Palettes;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>File ▸ Load Color Palettes / Load Color Palette File and the "…" menu of the Color Mixer: palettes from any folder or file.</summary>
[Collection("app-settings")]   // these change the shared palette catalog and swap the program-wide settings store: one test class at a time
public class PaletteLoadShellTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dsm-palettes-").FullName;

    public PaletteLoadShellTests()
    {
        AppSettings.Store = new SettingsStore(null);
        ColorUiState.UseCatalog(null);
    }

    public void Dispose()
    {
        ColorUiState.UseCatalog(null);
        AppSettings.Store = new SettingsStore(null);
        Directory.Delete(_dir, recursive: true);
    }

    private static MainWindow Show()
    {
        var w = new MainWindow(new StartupOptions()) { Width = 1400, Height = 860 };
        w.Show();
        w.ToggleDockPanel("colorMixer");   // the Dock starts closed; the Color Mixer is what these tests use
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        w.DialogOverride = (_, _) => Task.FromResult(true);
        return w;
    }

    private string WritePal(string name, params (string Color, int R, int G, int B)[] colors)
    {
        var sb = new StringBuilder($"{PaletteFile.PalTag} 6.110000 {colors.Length}\r\n0\r\n");
        foreach (var c in colors) sb.Append($"1|{c.R,3} {c.G,3} {c.B,3}|{c.Color}|Test|\r\n");
        var path = Path.Combine(_dir, name + ".PAL");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(sb.ToString()));
        return path;
    }

    [AvaloniaFact]
    public void Loading_a_folder_adds_its_palettes_selects_the_first_and_remembers_the_folder()
    {
        WritePal("Zz Loaded A", ("Red One", 255, 0, 0), ("Red Two", 200, 0, 0));
        WritePal("Zz Loaded B", ("Blue", 0, 0, 255));
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "not a palette");
        var w = Show();
        int before = w.ColorState.Palettes.Count;
        w.PaletteFolderOverride = () => _dir;

        w.ColorState.LoadFolderRequested!();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before + 2, w.ColorState.Palettes.Count);
        Assert.Equal("Zz Loaded A", w.ColorState.ActivePalette.Name);
        Assert.Equal(2, w.ColorState.ActivePalette.Colors.Count);
        Assert.Contains("Zz Loaded B", w.ColorMixer.PaletteCombo.Items.OfType<string>());
        Assert.Equal("Zz Loaded A", (string?)w.ColorMixer.PaletteCombo.SelectedItem);
        Assert.Equal([_dir], ColorUiState.RememberedFolders());
        Assert.Empty(w.LastPaletteProblems);
    }

    [AvaloniaFact]
    public void Loading_files_reports_the_ones_that_cannot_be_read_and_keeps_the_rest()
    {
        var good = WritePal("Zz Single", ("Green", 0, 160, 0));
        var bad = Path.Combine(_dir, "broken.PAL");
        File.WriteAllText(bad, "this is not a palette");
        var w = Show();
        w.PaletteFilesOverride = () => [good, bad];

        w.ColorState.LoadFilesRequested!();

        Assert.Equal("Zz Single", w.ColorState.ActivePalette.Name);
        Assert.Single(w.LastPaletteProblems);
        Assert.StartsWith("broken.PAL", w.LastPaletteProblems[0]);
        Assert.Empty(ColorUiState.RememberedFolders());            // single files are not remembered
    }

    [AvaloniaFact]
    public void A_palette_with_the_name_of_an_existing_one_replaces_it()
    {
        WritePal("Zz Twice", ("Old", 1, 1, 1));
        var w = Show();
        w.PaletteFolderOverride = () => _dir;
        w.ColorState.LoadFolderRequested!();
        int count = w.ColorState.Palettes.Count;

        WritePal("Zz Twice", ("New A", 2, 2, 2), ("New B", 3, 3, 3));
        w.ColorState.LoadFolderRequested!();

        Assert.Equal(count, w.ColorState.Palettes.Count);
        Assert.Equal(2, w.ColorState.ActivePalette.Colors.Count);
    }

    [AvaloniaFact]
    public void An_empty_folder_changes_nothing_and_says_so()
    {
        var w = Show();
        var active = w.ColorState.ActivePalette;
        int count = w.ColorState.Palettes.Count;
        w.PaletteFolderOverride = () => _dir;

        w.ColorState.LoadFolderRequested!();

        Assert.Equal(count, w.ColorState.Palettes.Count);
        Assert.Same(active, w.ColorState.ActivePalette);
        Assert.Empty(ColorUiState.RememberedFolders());
    }

    [AvaloniaFact]
    public void Forgetting_the_added_folders_goes_back_to_the_palettes_found_at_start()
    {
        WritePal("Zz Forget Me", ("Black", 0, 0, 0));
        var w = Show();
        int before = w.ColorState.Palettes.Count;
        w.PaletteFolderOverride = () => _dir;
        w.ColorState.LoadFolderRequested!();
        Assert.Equal(before + 1, w.ColorState.Palettes.Count);

        w.ColorState.ForgetAddedPalettes();

        Assert.Equal(before, w.ColorState.Palettes.Count);
        Assert.Empty(ColorUiState.RememberedFolders());
        Assert.DoesNotContain(w.ColorState.Palettes, p => p.Name == "Zz Forget Me");
    }

    [AvaloniaFact]
    public void The_file_menu_and_the_color_tab_offer_the_commands()
    {
        var w = Show();
        var file = w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().First(m => (m.Header as string)?.Contains("File") == true);
        string[] headers = [.. file.Items.OfType<MenuItem>().Select(m => ((string?)m.Header ?? "").Replace("_", ""))];
        Assert.Contains("Load Color Palettes...", headers);
        Assert.Contains("Load Color Palette File...", headers);
        Assert.All(file.Items.OfType<MenuItem>().Where(m => ((string?)m.Header ?? "").Contains("Palette")), m => Assert.True(m.IsEnabled));
        Assert.NotNull(w.ColorState.LoadFolderRequested);          // wired by the window
        Assert.NotNull(w.ColorState.LoadFilesRequested);
        var button = w.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "PaletteMenuButton");
        Assert.NotNull(button);
        Assert.IsType<MenuFlyout>(button.Flyout);
    }
}
