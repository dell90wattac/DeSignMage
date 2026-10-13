using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace DeSignMage.App.Shell;

/// <summary>
/// Options ▸ Performance &amp; memory: how much memory the undo history may use before the oldest steps are forgotten, and an optional
/// limit on the number of steps. Choices apply as they are made and are remembered.
/// </summary>
public sealed class PerformanceOptionsPage : StackPanel
{
    private readonly ComboBox _memory = new() { Name = "UndoMemoryCombo" };
    private readonly TextBox _steps = new() { Name = "UndoMaxStepsBox", PlaceholderText = "Unlimited" };
    private readonly TextBlock _usage = new() { Name = "UndoUsageText", TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Brush("Ui.Muted") };
    private bool _syncing;

    public PerformanceOptionsPage()
    {
        Spacing = UiScale.Sp(10);

        _memory.ItemsSource = PerformanceSettings.MemoryChoicesMb.Select(MemoryLabel).ToList();
        _memory.ScaledWidth(200);
        _memory.SelectionChanged += (_, _) =>
        {
            if (_syncing || _memory.SelectedIndex < 0) return;
            PerformanceSettings.SetUndoMemoryMb(PerformanceSettings.MemoryChoicesMb[_memory.SelectedIndex]);
            UpdateUsage();
        };

        _steps.ScaledWidth(110);
        _steps.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitSteps(); e.Handled = true; }
        };
        _steps.LostFocus += (_, _) => CommitSteps();

        Children.Add(new TextBlock { Text = "Undo history", FontWeight = FontWeight.Bold }.ScaledFont(15));
        Children.Add(new TextBlock
        {
            Text = "Undo keeps the old version of whatever you change, and pictures take the most room. When the undo memory is full the oldest steps are forgotten " +
                   "so the program never runs out of memory; recent steps always work.",
            TextWrapping = TextWrapping.Wrap,
        });
        Children.Add(new TextBlock { Text = "Undo memory limit:" });
        Children.Add(_memory);
        Children.Add(new TextBlock { Text = "Also keep at most this many steps (leave empty for no limit):" });
        Children.Add(_steps);
        Children.Add(_usage);

        AttachedToVisualTree += (_, _) => Sync();
        Sync();
    }

    internal ComboBox MemoryCombo => _memory;
    internal TextBox StepsBox => _steps;
    internal TextBlock UsageText => _usage;

    private static string MemoryLabel(int mb) => mb == PerformanceSettings.DefaultMemoryMb ? $"{mb / 1024.0:0} GB (standard)" : mb >= 1024 ? $"{mb / 1024.0:0} GB" : $"{mb} MB";

    private void CommitSteps()
    {
        if (_syncing) return;
        var text = _steps.Text?.Trim();
        int? steps = null;
        if (!string.IsNullOrEmpty(text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) steps = n;
        PerformanceSettings.SetUndoMaxSteps(steps);
        Sync();   // shows what was accepted (empty again for an invalid or too small number)
        UpdateUsage();
    }

    private void Sync()
    {
        _syncing = true;
        _memory.SelectedIndex = Array.IndexOf(PerformanceSettings.MemoryChoicesMb, PerformanceSettings.UndoMemoryMb);
        _steps.Text = PerformanceSettings.UndoMaxSteps?.ToString(CultureInfo.InvariantCulture) ?? "";
        _syncing = false;
        UpdateUsage();
    }

    private void UpdateUsage()
    {
        if (PerformanceSettings.UsageProvider is { } provider)
        {
            var (steps, bytes) = provider();
            _usage.Text = $"The open design is using about {bytes / 1048576.0:0.#} MB of undo memory for {steps} undo step{(steps == 1 ? "" : "s")}.";
        }
        else _usage.Text = "";
    }
}
