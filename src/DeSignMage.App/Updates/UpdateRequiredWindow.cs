using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DeSignMage.App.Shell;

namespace DeSignMage.App.Updates;

/// <summary>
/// "DeSign Mage needs to update": shown at a fresh start when a newer version exists. There is no "later" and no close button: the
/// only way on is the button, which installs the update and restarts. (If the download or the installation fails the window says
/// so and the one button then starts the program as it is; it tries again at the next fresh start.)
/// </summary>
internal sealed class UpdateRequiredWindow : Window
{
    /// <summary>A download that makes no progress for this long is given up, so the window can never hang for good.</summary>
    public static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(90);

    private readonly UpdateFlow _flow;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<bool> _done = new();
    private bool _mayClose;

    public Button ActionButton { get; } = new Button { IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(170);
    public TextBlock HeadingText { get; } = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    public TextBlock DetailText { get; } = new() { TextWrapping = TextWrapping.Wrap };
    public TextBlock ProgressText { get; } = new() { TextWrapping = TextWrapping.Wrap };
    public ProgressBar Progress { get; } = new() { Minimum = 0, Maximum = 100 };

    /// <summary>Completes when the person may go on: true = the program starts normally (the update failed), nothing else ever completes (an installed update ends the process).</summary>
    public Task<bool> StartNormally => _done.Task;

    public UpdateRequiredWindow(UpdateFlow flow, string currentVersion)
    {
        _flow = flow;
        Title = "DeSign Mage";
        DialogScale.Attach(this, 480);
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        CanMinimize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ActionButton.Name = "ActionButton";
        HeadingText.Name = "HeadingText";
        DetailText.Name = "DetailText";
        ProgressText.Name = "ProgressText";
        Progress.Name = "Progress";

        ProgressText.Foreground = AppTheme.Brush("Ui.Muted");
        ProgressText.Classes.Add("small");
        DetailText.Foreground = AppTheme.Brush("Ui.Muted");
        DetailText.Classes.Add("small");
        DetailText.Text = $"You have version {currentVersion}. Updating takes a few seconds, and DeSign Mage opens again by itself. Your settings and designs are not touched.";

        Content = DialogScale.Scrollable(new StackPanel
        {
            Margin = UiScale.Space(16), Spacing = UiScale.Sp(8),
            Children =
            {
                HeadingText, DetailText, Progress, ProgressText,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = UiScale.Space(0, 10, 0, 0), Children = { ActionButton } },
            },
        });

        ActionButton.Click += (_, _) =>
        {
            if (_flow.Step == UpdateStep.Failed) { Allow(); return; }
            ActionButton.IsEnabled = false;
            Show("Installing the update…", null);
            // The next line closes this program and starts the installer; it only comes back when that did not work.
            _flow.Apply();
            Refresh();
        };
        Closing += (_, e) =>
        {
            // No way around the button, except where the system itself ends the session, or the update has failed anyway.
            if (_mayClose || e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown) return;
            if (_flow.Step == UpdateStep.Failed) { _mayClose = true; _done.TrySetResult(true); return; }
            e.Cancel = true;
        };
        _flow.Changed += OnFlowChanged;
        Closed += (_, _) => { _flow.Changed -= OnFlowChanged; _cts.Cancel(); };
        Opened += (_, _) => _ = RunAsync();
        Refresh();
    }

    private async Task RunAsync()
    {
        _cts.CancelAfter(StallLimit);
        await _flow.PrepareAsync(_cts.Token);
        // PrepareAsync ends in Ready (the person decides next) or Failed (Refresh already shows it).
    }

    private void OnFlowChanged()
    {
        if (_flow.Step == UpdateStep.Downloading) _cts.CancelAfter(StallLimit);   // progress: the stall clock starts over
        Dispatcher.UIThread.Post(Refresh);
    }

    private void Refresh()
    {
        string version = _flow.Offer.Version;
        DetailText.IsVisible = _flow.Step != UpdateStep.Failed;   // 'updating takes a few seconds' would be wrong after a failure
        switch (_flow.Step)
        {
            case UpdateStep.Idle:
            case UpdateStep.Downloading:
                Show($"A new version of DeSign Mage ({version}) is required.", _flow.Step == UpdateStep.Downloading ? $"Downloading… {_flow.Percent} %" : "Preparing…");
                Progress.IsVisible = true;
                Progress.IsIndeterminate = _flow.Step == UpdateStep.Idle || _flow.Percent <= 0;
                Progress.Value = _flow.Percent;
                ActionButton.Content = "Update and restart";
                ActionButton.IsEnabled = false;
                break;
            case UpdateStep.Ready:
                Show($"A new version of DeSign Mage ({version}) is required.", "Ready to install.");
                Progress.IsVisible = false;
                ActionButton.Content = "Update and restart";
                ActionButton.IsEnabled = true;
                break;
            case UpdateStep.Applying:
                Show($"Installing version {version}…", null);
                Progress.IsVisible = true;
                Progress.IsIndeterminate = true;
                ActionButton.IsEnabled = false;
                break;
            case UpdateStep.Failed:
                Show("DeSign Mage could not update just now.", _flow.FailureText);
                Progress.IsVisible = false;
                ActionButton.Content = "Start DeSign Mage";
                ActionButton.IsEnabled = true;
                break;
        }
    }

    private void Show(string heading, string? progress)
    {
        HeadingText.Text = heading;
        ProgressText.Text = progress ?? "";
        ProgressText.IsVisible = !string.IsNullOrEmpty(progress);
    }

    private void Allow()
    {
        _mayClose = true;
        _done.TrySetResult(true);
        Close();
    }
}
