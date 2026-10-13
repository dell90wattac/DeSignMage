using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DeSignMage.App.Shell;
using DeSignMage.Feedback;

namespace DeSignMage.App.Reporting;

/// <summary>What was typed and ticked in the feedback dialog.</summary>
internal sealed record FeedbackInput(ReportKind Kind, string Description, string Name, bool IncludeDesign, bool IncludeScreenshot);

/// <summary>
/// "Feedback / Bug": type (bug or feature/feedback), a description, an optional remembered name, and for bugs the two attachment
/// choices (both on by default). Send builds the report through <paramref name="send"/> and shows "Thanks, sent" or
/// "Saved, will send when online".
/// </summary>
internal sealed class FeedbackDialog : Window
{
    private readonly Func<FeedbackInput, Task<SubmitResult>> _send;
    private readonly StackPanel _bugOptions = new() { Spacing = UiScale.Sp(4) };
    private bool _closed;
    private bool _sending;

    public RadioButton BugRadio { get; } = new() { Content = "Bug report (something is wrong)", GroupName = "kind", IsChecked = true };
    public RadioButton FeatureRadio { get; } = new() { Content = "Feature request or feedback", GroupName = "kind" };
    public TextBox DescriptionBox { get; } = new TextBox()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        PlaceholderText = "What happened? What did you expect?",
    }.ScaledMinHeight(140).ScaledMaxHeight(220);
    public TextBox NameBox { get; } = new TextBox { PlaceholderText = "Your name (optional)" }.ScaledWidth(240);
    public CheckBox DesignCheck { get; } = new() { Content = "Include my current design", IsChecked = true };
    public CheckBox ScreenshotCheck { get; } = new() { Content = "Include a screenshot of the DeSign window", IsChecked = true };
    public Button SendButton { get; } = new Button { Content = "Send", HorizontalContentAlignment = HorizontalAlignment.Center, IsEnabled = false }.ScaledMinWidth(90);
    public Button CancelButton { get; } = new Button { Content = "Cancel", IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(90);
    public TextBlock StatusText { get; } = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    public TextBlock SetupNote { get; } = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkOrange };

    /// <summary>What happened to the report once Send finished (null before).</summary>
    public SubmitResult? Result { get; private set; }

    public FeedbackDialog(bool canSend, string? savedName, Func<FeedbackInput, Task<SubmitResult>> send)
    {
        _send = send;
        Title = "Send Feedback";
        DialogScale.Attach(this, 520);
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        BugRadio.Name = "BugRadio";
        FeatureRadio.Name = "FeatureRadio";
        DescriptionBox.Name = "DescriptionBox";
        NameBox.Name = "NameBox";
        DesignCheck.Name = "DesignCheck";
        ScreenshotCheck.Name = "ScreenshotCheck";
        SendButton.Name = "SendButton";
        CancelButton.Name = "CancelButton";
        StatusText.Name = "StatusText";
        SetupNote.Name = "SetupNote";

        NameBox.Text = savedName;
        SetupNote.Text = "Sending isn't set up yet. Your report will be saved on this computer.";
        SetupNote.IsVisible = !canSend;

        var bugNote = new TextBlock
        {
            Foreground = Shell.AppTheme.Brush("Ui.Muted"), TextWrapping = TextWrapping.Wrap,
            Text = "A bug report also sends a log of recent actions (never the text in your design), your undo history, and basic facts about this computer.",
        };
        _bugOptions.Children.Add(DesignCheck);
        _bugOptions.Children.Add(ScreenshotCheck);
        _bugOptions.Children.Add(bugNote);

        var kindRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = UiScale.Sp(18), Children = { BugRadio, FeatureRadio } };
        var nameRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = UiScale.Sp(8),
            Children = { new TextBlock { Text = "Name:", VerticalAlignment = VerticalAlignment.Center }, NameBox },
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = UiScale.Sp(8), Margin = UiScale.Space(0, 10, 0, 0),
            Children = { SendButton, CancelButton },
        };

        bugNote.Classes.Add("small");
        Content = DialogScale.Scrollable(new StackPanel
        {
            Margin = UiScale.Space(16), Spacing = UiScale.Sp(8),
            Children =
            {
                new TextBlock { Text = "Tell the developer what went wrong, or what you would like to see.", TextWrapping = TextWrapping.Wrap },
                kindRow,
                new TextBlock { Text = "Description", FontWeight = FontWeight.SemiBold },
                DescriptionBox,
                nameRow,
                _bugOptions,
                SetupNote,
                StatusText,
                buttons,
            },
        });

        BugRadio.IsCheckedChanged += (_, _) => UpdateState();
        FeatureRadio.IsCheckedChanged += (_, _) => UpdateState();
        DescriptionBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) UpdateState(); };
        SendButton.Click += async (_, _) => await SendAsync();
        CancelButton.Click += (_, _) => Close();
        Closed += (_, _) => _closed = true;
        Opened += (_, _) => DescriptionBox.Focus();
        UpdateState();
    }

    private ReportKind Kind => BugRadio.IsChecked == true ? ReportKind.Bug : ReportKind.Feature;

    private void UpdateState()
    {
        _bugOptions.IsVisible = Kind == ReportKind.Bug;
        SendButton.IsEnabled = !_sending && Result is null && !string.IsNullOrWhiteSpace(DescriptionBox.Text);
    }

    /// <summary>Same as pressing Send. Public for tests.</summary>
    public async Task SendAsync()
    {
        if (_sending || Result is not null || string.IsNullOrWhiteSpace(DescriptionBox.Text)) return;
        _sending = true;
        foreach (Control c in new Control[] { BugRadio, FeatureRadio, DescriptionBox, NameBox, DesignCheck, ScreenshotCheck, SendButton })
            c.IsEnabled = false;
        StatusText.Foreground = Shell.AppTheme.Brush("Ui.Muted");
        StatusText.Text = "Sending...";

        var input = new FeedbackInput(Kind, DescriptionBox.Text!.Trim(), NameBox.Text?.Trim() ?? "", DesignCheck.IsChecked == true, ScreenshotCheck.IsChecked == true);
        SubmitResult result;
        try { result = await _send(input); }
        catch (Exception ex)
        {
            AppLog.Error("feedback", "Sending feedback failed unexpectedly", ex);
            result = new SubmitResult(SubmitStatus.Failed, "");
        }
        _sending = false;
        Result = result;
        if (_closed) return;

        StatusText.Foreground = result.Status == SubmitStatus.Failed ? Shell.AppTheme.Brush("Ui.Error") : result.Status == SubmitStatus.Sent ? Brushes.SeaGreen : Brushes.DarkSlateGray;
        StatusText.Text = result.Message;
        SendButton.IsVisible = false;
        CancelButton.Content = "Close";
        CancelButton.IsDefault = true;
        if (result.Status != SubmitStatus.Failed)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) => { timer.Stop(); if (!_closed) Close(); };
            timer.Start();
        }
    }
}
