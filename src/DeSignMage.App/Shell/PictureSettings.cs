using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace DeSignMage.App.Shell;

/// <summary>
/// The picture-related choices of Tools ▸ Options ▸ Pictures: which program "Edit in..." opens a picture in. Empty means the program Windows has for editing
/// that kind of picture (many computers have none set up for PNG or JPEG, which is why a program can be chosen).
/// </summary>
public static class PictureSettings
{
    public const string EditorProgramKey = "pictures.editorProgram";

    /// <summary>Full path of the program "Edit in..." starts, or empty for the Windows default for editing.</summary>
    public static string EditorProgram
    {
        get => AppSettings.Store.Get(EditorProgramKey, "").Trim();
        set => AppSettings.Store.Set(EditorProgramKey, (value ?? "").Trim());
    }

    /// <summary>A short name for the chosen program ("Photoshop"), or the words for the default.</summary>
    public static string EditorName
    {
        get
        {
            var program = EditorProgram;
            return program.Length == 0 ? "the default picture editor" : Path.GetFileNameWithoutExtension(program);
        }
    }

    /// <summary>For tests: replaces starting a program. Receives the file to open; returns an error message, or null when the program started.</summary>
    internal static Func<string, string?>? LaunchOverride { get; set; }

    /// <summary>
    /// Opens <paramref name="file"/> in the chosen program (or, with none chosen, the one Windows has for editing that kind of file). Returns a plain-words
    /// error, or null when the program was started.
    /// </summary>
    internal static string? Launch(string file)
    {
        if (LaunchOverride is { } fake) return fake(file);
        var program = EditorProgram;
        try
        {
            if (program.Length > 0)
            {
                if (!File.Exists(program)) return $"The picture program was not found:\n{program}\n\nChoose another one in Tools > Options > Pictures.";
                using var started = Process.Start(new ProcessStartInfo(program) { UseShellExecute = false, ArgumentList = { file } });
            }
            else
            {
                using var started = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, Verb = "edit" });
            }
            return null;
        }
        catch (Win32Exception) when (program.Length == 0)
        {
            return "Windows has no program set up for editing this kind of picture.\n\nChoose one in Tools > Options > Pictures.";
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return "The picture program could not be started: " + e.Message;
        }
    }
}

/// <summary>Options ▸ Pictures: the program "Edit in..." uses.</summary>
public sealed class PictureOptionsPage : StackPanel
{
    private readonly TextBox _program = new() { Name = "EditorProgramBox", PlaceholderText = "Windows default" };
    private readonly Button _browse = new() { Name = "EditorBrowseButton", Content = "Choose..." };
    private readonly Button _useDefault = new() { Name = "EditorDefaultButton", Content = "Use the Windows default" };
    private bool _syncing;

    public PictureOptionsPage()
    {
        Spacing = UiScale.Sp(10);
        Children.Add(new TextBlock { Text = "Edit in another program", FontWeight = FontWeight.Bold }.ScaledFont(15));
        Children.Add(new TextBlock
        {
            Text = "Edit in... (in the Picture Editor) opens the picture in another program, such as a photo editor, and brings it back here when you save it there. " +
                   "Leave this empty to use the program Windows has for editing pictures; if Windows has none, choose one here.",
            TextWrapping = TextWrapping.Wrap,
        });
        Children.Add(new TextBlock { Text = "Program:" });
        _program.ScaledWidth(420);
        _program.HorizontalAlignment = HorizontalAlignment.Left;
        Children.Add(_program);
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = UiScale.Sp(8), Children = { _browse, _useDefault } });

        _program.LostFocus += (_, _) => Commit();
        _program.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Commit(); e.Handled = true; } };
        _useDefault.Click += (_, _) => { PictureSettings.EditorProgram = ""; Sync(); };
        _browse.Click += async (_, _) => await BrowseAsync();
        AttachedToVisualTree += (_, _) => Sync();
        Sync();
    }

    internal TextBox ProgramBox => _program;
    internal Button UseDefaultButton => _useDefault;

    /// <summary>Takes the typed path as the choice.</summary>
    internal void Commit()
    {
        if (_syncing) return;
        PictureSettings.EditorProgram = (_program.Text ?? "").Trim().Trim('"');
        Sync();
    }

    private void Sync()
    {
        _syncing = true;
        _program.Text = PictureSettings.EditorProgram;
        _useDefault.IsVisible = PictureSettings.EditorProgram.Length > 0;
        _syncing = false;
    }

    private async Task BrowseAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the picture program",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Programs") { Patterns = ["*.exe", "*.bat", "*.cmd"] }, FilePickerFileTypes.All],
        });
        if (files.Count == 0) return;
        PictureSettings.EditorProgram = files[0].TryGetLocalPath() ?? "";
        Sync();
    }
}
