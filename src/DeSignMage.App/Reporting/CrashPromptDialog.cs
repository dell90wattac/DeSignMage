using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DeSignMage.App.Shell;
using DeSignMage.Feedback;

namespace DeSignMage.App.Reporting;

/// <summary>What the person chose in the crash prompt.</summary>
internal enum CrashChoice
{
    /// <summary>Send the crash report(s) now.</summary>
    Send,
    /// <summary>Do not send; the crash is not asked about again.</summary>
    DontSend,
    /// <summary>The window was closed without an answer: nothing is sent and the same question comes back at the next start.</summary>
    Later,
}

internal sealed record CrashAnswer(CrashChoice Choice, bool Always);

/// <summary>
/// "DeSign Mage closed unexpectedly last time. Send the crash report?" Nothing is sent until the person presses Send. "Always send
/// crash reports" is remembered (it only counts together with Send). The emergency copy of the design is saved either way.
/// </summary>
internal sealed class CrashPromptDialog : Window
{
    public Button SendButton { get; } = new Button { Content = "Send", IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(110);
    public Button DontSendButton { get; } = new Button { Content = "Don't send", HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(110);
    public CheckBox AlwaysCheck { get; } = new() { Content = "Always send crash reports, without asking" };
    public TextBlock MessageText { get; } = new() { TextWrapping = TextWrapping.Wrap };
    public TextBlock DetailText { get; } = new() { TextWrapping = TextWrapping.Wrap };

    /// <summary>What the person chose (null until they did).</summary>
    public CrashAnswer? Answer { get; private set; }

    /// <param name="includesDesign">The remembered "include my design" choice: whether the report would carry the recovered design.</param>
    public CrashPromptDialog(CrashPrompt prompt, bool includesDesign)
    {
        Title = "DeSign Mage";
        DialogScale.Attach(this, 480);
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SendButton.Name = "SendButton";
        DontSendButton.Name = "DontSendButton";
        AlwaysCheck.Name = "AlwaysCheck";
        MessageText.Name = "MessageText";
        DetailText.Name = "DetailText";

        MessageText.FontWeight = FontWeight.SemiBold;
        MessageText.Text = prompt.Count == 1
            ? "DeSign Mage closed unexpectedly last time. Send the crash report?"
            : $"DeSign Mage closed unexpectedly {prompt.Count} times since you last used it. Send the crash reports?";

        string design = includesDesign
            ? "It also includes your design, because you chose to include designs when you sent feedback."
            : "It does not include your design.";
        string saved = prompt.RecoveredFolder is not null && prompt.RecoveredFiles.Count > 0
            ? $" A copy of your design was saved to: {prompt.RecoveredFolder}"
            : "";
        DetailText.Text = "The report says what went wrong, a log of recent actions (never the text in your design), your undo history and basic facts about this computer. " +
                          design + " Nothing is sent unless you press Send." + saved;
        DetailText.Foreground = AppTheme.Brush("Ui.Muted");
        DetailText.Classes.Add("small");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = UiScale.Sp(8), Margin = UiScale.Space(0, 10, 0, 0),
            Children = { SendButton, DontSendButton },
        };
        Content = DialogScale.Scrollable(new StackPanel
        {
            Margin = UiScale.Space(16), Spacing = UiScale.Sp(8),
            Children = { MessageText, DetailText, AlwaysCheck, buttons },
        });

        SendButton.Click += (_, _) => Choose(CrashChoice.Send);
        DontSendButton.Click += (_, _) => Choose(CrashChoice.DontSend);
        Closed += (_, _) => Answer ??= new CrashAnswer(CrashChoice.Later, false);
    }

    private void Choose(CrashChoice choice)
    {
        Answer = new CrashAnswer(choice, choice == CrashChoice.Send && AlwaysCheck.IsChecked == true);
        Close();
    }
}
