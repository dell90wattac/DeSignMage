using System.IO.Compression;
using System.Runtime.CompilerServices;
using Avalonia;
using DeSignMage.App.Shell;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DeSignMage.App.Reporting;
using DeSignMage.Core.Model;
using DeSignMage.Feedback;
using SkiaSharp;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>No test ever reaches the real relay: until Program.Main runs, the feedback service is inert (temp folder, no sender).</summary>
internal static class FeedbackTestSetup
{
    [ModuleInitializer]
    internal static void Init() =>
        FeedbackHost.Current = new FeedbackService(new AppPaths(Path.Combine(Path.GetTempPath(), $"dsm-feedback-ui-test-{Environment.ProcessId}")), sender: null);
}

/// <summary>Records what would have been sent (file names and bytes) and answers from a script. Never touches the network.</summary>
internal sealed class RecordingSender(SendStatus status = SendStatus.Sent) : IReportSender
{
    public sealed record Captured(OutgoingReport Report, Dictionary<string, byte[]> Files);

    public List<Captured> Sent { get; } = [];

    public Task<SendResult> SendAsync(OutboxItem item, CancellationToken ct)
    {
        Sent.Add(new Captured(item.Report, item.Report.Files.ToDictionary(f => f.FileName, f => item.ReadFile(f.FileName))));
        return Task.FromResult(new SendResult(status));
    }
}

public class FeedbackShellTests
{
    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "dsm-feedback-ui-" + Guid.NewGuid().ToString("N")[..8]);

    private static (MainWindow Window, FeedbackService Service, RecordingSender? Sender) Open(IReportSender? sender = null)
    {
        var service = new FeedbackService(new AppPaths(TempRoot()), sender, new ReportBuilderOptions { ShrinkScreenshot = ScreenshotTools.Shrink }) { SendTimeout = TimeSpan.FromSeconds(5) };
        FeedbackHost.Current = service;
        var w = new MainWindow(new StartupOptions(Demo: true)) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        return (w, service, sender as RecordingSender);
    }

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static T Find<T>(Window w, string name) where T : Control => w.FindControl<T>(name)!;

    private static MenuItem HelpItem(MainWindow w, string headerContains) =>
        w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => m.Header?.ToString() == "_Help")
            .Items.OfType<MenuItem>().Single(m => m.Header?.ToString()?.Contains(headerContains) == true);

    [AvaloniaFact]
    public void Feedback_button_sits_at_the_far_right_of_the_one_top_row_with_the_menus_and_the_toolbar()
    {
        var (w, _, _) = Open();
        var button = Find<Button>(w, "FeedbackButton");
        var menu = Find<Menu>(w, "MainMenu");
        var toolbar = Find<BarRow>(w, "MainToolbar");

        // a plain button (R38): a small yellow dot and the word Feedback; the same command as before
        var content = Assert.IsType<StackPanel>(button.Content);
        Assert.Equal("Feedback", Assert.Single(content.Children.OfType<TextBlock>()).Text);
        var dot = Assert.Single(content.Children.OfType<Avalonia.Controls.Shapes.Ellipse>());
        Assert.True(dot.Width <= 10 && dot.Height <= 10);
        Assert.Equal(Avalonia.Media.Color.Parse("#F2B705"), ((Avalonia.Media.ISolidColorBrush)dot.Fill!).Color);
        Assert.Contains("act", button.Classes);                                                // the same plain look as the other text buttons, not the old yellow notice color
        Assert.True(button.IsVisible);
        var buttonTop = button.TranslatePoint(new Point(0, 0), w)!.Value;
        var toolbarTop = toolbar.TranslatePoint(new Point(0, 0), w)!.Value;
        var menuTop = menu.TranslatePoint(new Point(0, 0), w)!.Value;
        Assert.True(Math.Abs(buttonTop.Y + button.Bounds.Height / 2 - (menuTop.Y + menu.Bounds.Height / 2)) < 12, "the menus and the button share one row");
        Assert.True(toolbarTop.X >= menuTop.X + menu.Bounds.Width - 1, "the toolbar icons follow the menus");
        Assert.True(Math.Abs(buttonTop.Y + button.Bounds.Height / 2 - (toolbarTop.Y + toolbar.Bounds.Height / 2)) < 12, "same row as the toolbar buttons");
        Assert.True(buttonTop.X > toolbarTop.X + toolbar.Bounds.Width - 1, "right of the toolbar buttons");
        Assert.True(buttonTop.X + button.Bounds.Width > w.Bounds.Width * 0.8, "at the right edge");
        w.Close();
    }

    [AvaloniaFact]
    public void Clicking_the_button_opens_the_dialog()
    {
        var (w, _, _) = Open();
        Assert.Null(w.LastFeedbackDialog);

        Click(Find<Button>(w, "FeedbackButton"));
        Dispatcher.UIThread.RunJobs();

        var d = Assert.IsType<DeSignMage.App.Reporting.FeedbackDialog>(w.LastFeedbackDialog);
        Assert.True(d.IsVisible);
        Assert.Equal("Send Feedback", d.Title);
        d.Close();
        w.Close();
    }

    [AvaloniaFact]
    public void Help_menu_has_Send_Feedback_that_opens_the_same_dialog()
    {
        var (w, _, _) = Open();
        var item = HelpItem(w, "Feedback");
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(w.LastFeedbackDialog);
        w.LastFeedbackDialog!.Close();
        w.Close();
    }

    [AvaloniaFact]
    public void Dialog_has_the_requested_fields_and_defaults()
    {
        var (w, service, _) = Open();
        service.SaveName("Mum");
        Click(Find<Button>(w, "FeedbackButton"));
        Dispatcher.UIThread.RunJobs();
        var d = w.LastFeedbackDialog!;

        Assert.True(d.BugRadio.IsChecked);
        Assert.False(d.FeatureRadio.IsChecked);
        Assert.Equal("Mum", d.NameBox.Text);                     // remembered
        Assert.False(d.DesignCheck.IsChecked);                   // attachments are opt-in: off until ticked once
        Assert.False(d.ScreenshotCheck.IsChecked);
        Assert.True(d.DesignCheck.IsVisible && d.ScreenshotCheck.IsVisible);
        Assert.False(d.SendButton.IsEnabled);                    // nothing typed yet
        Assert.True(d.CancelButton.IsEnabled);
        Assert.True(d.SetupNote.IsVisible);                      // no sender in this test: "Sending isn't set up yet"
        Assert.Contains("Sending isn't set up yet", d.SetupNote.Text);

        d.DescriptionBox.Text = "It crashes when I rotate";
        Assert.True(d.SendButton.IsEnabled);

        d.FeatureRadio.IsChecked = true;                         // feature/feedback: attachments options are bug-only
        Assert.False(d.DesignCheck.IsEffectivelyVisible);
        Assert.False(d.ScreenshotCheck.IsEffectivelyVisible);
        d.BugRadio.IsChecked = true;
        Assert.True(d.DesignCheck.IsVisible);
        d.Close();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Bug_report_sends_log_undo_history_system_info_screenshot_and_the_design()
    {
        var sender = new RecordingSender();
        var (w, service, _) = Open(sender);
        w.Session.SetTool("rectangle");
        w.Session.SetTool("select");
        bool modifiedBefore = w.Session.History.IsModified;
        var recentBefore = w.RecentPaths.ToList();
        var pathBefore = w.DocumentPath;
        int objects = w.Session.Document.ObjectCount;

        Click(Find<Button>(w, "FeedbackButton"));
        Dispatcher.UIThread.RunJobs();
        var d = w.LastFeedbackDialog!;
        Assert.False(d.SetupNote.IsVisible);
        d.DescriptionBox.Text = "Rulers look wrong after zoom";
        d.NameBox.Text = "Mum";
        d.DesignCheck.IsChecked = true;                           // opt in to both attachments
        d.ScreenshotCheck.IsChecked = true;
        await d.SendAsync();

        Assert.Equal("Thanks, sent.", d.StatusText.Text);
        var sent = Assert.Single(sender.Sent);
        Assert.StartsWith("Bug: Rulers look wrong", sent.Report.Title);
        Assert.Contains(sent.Report.Fields, f => f.Name == "From" && f.Value == "Mum");
        Assert.Equal(3, sent.Files.Count);   // one text file, the design and the screenshot
        var textName = Assert.Single(sent.Files.Keys, k => k.EndsWith(".txt"));
        Assert.Matches(@"^DeSignMage-Bug-.+\.txt$", textName);
        Assert.Contains("design.dsm", sent.Files.Keys);
        Assert.Single(sent.Files.Keys, k => k.StartsWith("screenshot"));

        var report = System.Text.Encoding.UTF8.GetString(sent.Files[textName]);
        Assert.Contains("Windows", report);
        Assert.Contains(".NET", report);
        Assert.Contains("Display", report);
        Assert.Contains($"{objects} objects", report);
        Assert.Contains("==== UNDO HISTORY", report);
        var log = report[report.IndexOf("==== LOG ====", StringComparison.Ordinal)..];   // the log is the last section of the same file
        Assert.Contains("[tool]", log);
        Assert.Contains("[feedback] dialog opened", log);

        // the screenshot is a picture of the window
        var shot = sent.Files.Single(f => f.Key.StartsWith("screenshot"));
        using (var image = SKBitmap.Decode(shot.Value))
        {
            Assert.NotNull(image);
            Assert.True(image.Width >= 800 && image.Height >= 400);
        }
        // the design is a readable .dsm, and her document is untouched
        using (var zip = new ZipArchive(new MemoryStream(sent.Files["design.dsm"])))
            Assert.NotNull(zip.GetEntry("document.json"));
        Assert.Equal(modifiedBefore, w.Session.History.IsModified);
        Assert.Equal(pathBefore, w.DocumentPath);
        Assert.Equal(recentBefore, w.RecentPaths);
        Assert.Equal(objects, w.Session.Document.ObjectCount);
        Assert.Empty(service.Outbox.List());
        Assert.Equal("Mum", service.SavedName);
        d.Close();
        w.Close();
    }

    [AvaloniaFact]
    public void Design_and_screenshot_boxes_start_off_for_a_new_person()
    {
        var (w, service, _) = Open();
        Assert.False(service.IncludeDesign);
        Assert.False(service.IncludeScreenshot);

        Click(Find<Button>(w, "FeedbackButton"));
        Dispatcher.UIThread.RunJobs();
        var d = w.LastFeedbackDialog!;
        Assert.False(d.DesignCheck.IsChecked);
        Assert.False(d.ScreenshotCheck.IsChecked);
        d.DesignCheck.IsChecked = true;               // ticking without sending changes nothing for next time
        d.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(service.IncludeDesign);
        w.Close();
    }

    [AvaloniaFact]
    public async Task What_was_ticked_when_sending_is_remembered_for_the_next_bug_report()
    {
        var (w, service, _) = Open(new RecordingSender());
        Click(Find<Button>(w, "FeedbackButton"));
        Dispatcher.UIThread.RunJobs();
        var first = w.LastFeedbackDialog!;
        first.DescriptionBox.Text = "one";
        first.DesignCheck.IsChecked = true;           // design yes, screenshot no
        await first.SendAsync();
        first.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(service.IncludeDesign);
        Assert.False(service.IncludeScreenshot);
        Assert.Contains(FeedbackSettings.IncludeDesign, File.ReadAllText(service.Paths.SettingsFile));

        Click(Find<Button>(w, "FeedbackButton"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.LastFeedbackDialog!.DesignCheck.IsChecked);
        Assert.False(w.LastFeedbackDialog.ScreenshotCheck.IsChecked);
        w.LastFeedbackDialog.Close();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_feature_request_does_not_change_the_remembered_attachment_choices()
    {
        var (w, service, _) = Open(new RecordingSender());
        service.IncludeDesign = true;
        Click(Find<Button>(w, "FeedbackButton"));
        Dispatcher.UIThread.RunJobs();
        var d = w.LastFeedbackDialog!;
        d.DesignCheck.IsChecked = false;
        d.FeatureRadio.IsChecked = true;
        d.DescriptionBox.Text = "an idea";
        await d.SendAsync();
        Assert.True(service.IncludeDesign);
        d.Close();
        w.Close();
    }
    [AvaloniaFact]
    public async Task Unticking_the_boxes_leaves_the_design_and_screenshot_out_and_says_so()
    {
        var sender = new RecordingSender();
        var (w, _, _) = Open(sender);
        Click(Find<Button>(w, "FeedbackButton"));
        Dispatcher.UIThread.RunJobs();
        var d = w.LastFeedbackDialog!;
        d.DescriptionBox.Text = "Just the log please";
        d.DesignCheck.IsChecked = false;
        d.ScreenshotCheck.IsChecked = false;

        await d.SendAsync();

        var sent = Assert.Single(sender.Sent);
        Assert.DoesNotContain(sent.Files.Keys, k => k.EndsWith(".dsm") || k.StartsWith("screenshot"));
        Assert.Contains(sent.Report.Notes, n => n.Contains("Design not included"));
        Assert.Contains(sent.Report.Notes, n => n.Contains("Screenshot not included"));
        d.Close();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Feature_request_sends_just_the_text_and_the_version()
    {
        var sender = new RecordingSender();
        var (w, _, _) = Open(sender);
        Click(Find<Button>(w, "FeedbackButton"));
        Dispatcher.UIThread.RunJobs();
        var d = w.LastFeedbackDialog!;
        d.FeatureRadio.IsChecked = true;
        d.DescriptionBox.Text = "Please add a polygon tool";

        await d.SendAsync();

        var sent = Assert.Single(sender.Sent);
        Assert.StartsWith("Feature: Please add a polygon tool", sent.Report.Title);
        Assert.Empty(sent.Files);
        Assert.Contains(sent.Report.Fields, f => f.Name == "Version" && f.Value.Length > 0);
        Assert.DoesNotContain(sent.Report.Fields, f => f.Name is "Windows" or "Document");
        d.Close();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Offline_says_saved_and_will_send_when_online_and_keeps_the_report()
    {
        var (w, service, _) = Open(new RecordingSender(SendStatus.Offline));
        Click(Find<Button>(w, "FeedbackButton"));
        Dispatcher.UIThread.RunJobs();
        var d = w.LastFeedbackDialog!;
        d.DescriptionBox.Text = "offline test";

        await d.SendAsync();

        Assert.Equal("Saved, will send when online.", d.StatusText.Text);
        Assert.Single(service.Outbox.List());
        d.Close();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Without_a_sender_the_report_is_saved_and_the_dialog_says_sending_is_not_set_up()
    {
        var (w, service, _) = Open(sender: null);
        Click(Find<Button>(w, "FeedbackButton"));
        Dispatcher.UIThread.RunJobs();
        var d = w.LastFeedbackDialog!;
        d.DescriptionBox.Text = "no sender";

        await d.SendAsync();

        Assert.Contains("Sending isn't set up yet", d.StatusText.Text);
        Assert.Single(service.Outbox.List());
        d.Close();
        w.Close();
    }

    private static void LeaveACrash(FeedbackService service)
    {
        Exception ex;
        try { throw new InvalidOperationException("boom"); }
        catch (Exception caught) { ex = caught; }
        new CrashReporter(service.Paths) { Documents = () => [new CrashDocument("Sign.dsm", path => File.WriteAllBytes(path, new byte[300]))] }
            .Report(ex, "AppDomain", fatal: true);
    }

    [AvaloniaFact]
    public async Task A_crash_from_last_time_is_sent_only_after_Send_is_pressed()
    {
        var sender = new RecordingSender();
        var (w, service, _) = Open(sender);
        LeaveACrash(service);

        var asking = w.AskAboutCrashesAsync(service.FindCrashesToAsk()!);
        Dispatcher.UIThread.RunJobs();
        var d = w.LastCrashPrompt!;
        Assert.Contains("closed unexpectedly last time", d.MessageText.Text);
        Assert.Contains("Nothing is sent unless you press Send", d.DetailText.Text);
        Assert.Contains("does not include your design", d.DetailText.Text);
        Assert.Contains("A copy of your design was saved to", d.DetailText.Text);
        Assert.False(d.AlwaysCheck.IsChecked);
        Assert.Empty(sender.Sent);                       // the prompt is showing and nothing has gone out

        Click(d.SendButton);
        await asking;
        Dispatcher.UIThread.RunJobs();

        Assert.Single(sender.Sent);
        Assert.Equal(ReportKind.Crash, sender.Sent[0].Report.Kind);
        Assert.False(service.AlwaysSendCrashes);
        Assert.StartsWith("A crash report from last time was sent.", w.NoticeMessage);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Dont_send_sends_nothing_and_the_notice_says_so()
    {
        var sender = new RecordingSender();
        var (w, service, _) = Open(sender);
        LeaveACrash(service);

        var asking = w.AskAboutCrashesAsync(service.FindCrashesToAsk()!);
        Dispatcher.UIThread.RunJobs();
        w.LastCrashPrompt!.AlwaysCheck.IsChecked = true;   // only counts together with Send
        Click(w.LastCrashPrompt.DontSendButton);
        await asking;
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(sender.Sent);
        Assert.False(service.AlwaysSendCrashes);
        Assert.StartsWith("A crash report from last time was not sent (as you chose).", w.NoticeMessage);
        Assert.Null(service.FindCrashesToAsk());          // not asked again
        w.Close();
    }

    [AvaloniaFact]
    public async Task Always_send_ticked_with_Send_is_remembered()
    {
        var sender = new RecordingSender();
        var (w, service, _) = Open(sender);
        LeaveACrash(service);

        var asking = w.AskAboutCrashesAsync(service.FindCrashesToAsk()!);
        Dispatcher.UIThread.RunJobs();
        w.LastCrashPrompt!.AlwaysCheck.IsChecked = true;
        Click(w.LastCrashPrompt.SendButton);
        await asking;

        Assert.True(service.AlwaysSendCrashes);
        Assert.Single(sender.Sent);
        Assert.Contains("\"feedback.alwaysSendCrashes\": true", File.ReadAllText(service.Paths.SettingsFile));
        w.Close();
    }

    [AvaloniaFact]
    public async Task Closing_the_prompt_sends_nothing_and_keeps_the_question_for_next_time()
    {
        var sender = new RecordingSender();
        var (w, service, _) = Open(sender);
        LeaveACrash(service);

        var asking = w.AskAboutCrashesAsync(service.FindCrashesToAsk()!);
        Dispatcher.UIThread.RunJobs();
        w.LastCrashPrompt!.Close();
        await asking;
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(sender.Sent);
        Assert.NotNull(service.FindCrashesToAsk());
        Assert.Null(w.NoticeMessage);
        w.Close();
    }
    [AvaloniaFact]
    public void Crash_notice_is_one_line_with_the_recovered_folder()
    {
        var (w, _, _) = Open();
        Assert.Null(w.NoticeMessage);

        w.ShowCrashNotice(new CrashNotice(1, Sent: true, Configured: true, @"C:\Users\Mum\AppData\Local\DeSignMage\crash\crash-1", ["recovered-1-Sign.dsm"]));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(@"A crash report from last time was sent. Your design was recovered to: C:\Users\Mum\AppData\Local\DeSignMage\crash\crash-1", w.NoticeMessage);
        Assert.True(Find<Button>(w, "NoticeOpenFolder").IsVisible);
        Click(Find<Button>(w, "NoticeClose"));
        Assert.Null(w.NoticeMessage);
        w.Close();
    }

    [AvaloniaFact]
    public void Commands_and_tool_switches_are_logged()
    {
        var (w, _, _) = Open();
        w.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");   // Select All
        w.Session.SetTool("ellipse");

        var log = string.Join("\n", AppLog.Tail(500));
        Assert.Contains("[cmd] edit.selectAll", log);
        Assert.Contains("[tool] active tool: ellipse", log);
        w.Close();
    }

    [AvaloniaFact]
    public void An_error_on_the_UI_thread_is_reported_and_the_program_keeps_running()
    {
        var (w, service, _) = Open();
        FeedbackHost.ResetUiErrorWindow();
        string flash = "";
        void Caught() => flash = "raised";
        FeedbackHost.UiErrorCaught += Caught;
        try
        {
            bool handled = FeedbackHost.HandleUiException(new InvalidOperationException("ui boom"));

            Assert.True(handled);
            Assert.Equal("raised", flash);
            var crash = Assert.Single(CrashRecovery.List(service.Paths));
            Assert.Equal("Dispatcher", crash.Source);
            Assert.False(crash.Fatal);
            Assert.Single(crash.Recovered);   // emergency save of the open design
            using var zip = ZipFile.OpenRead(Path.Combine(crash.Directory, crash.Recovered[0]));
            Assert.NotNull(zip.GetEntry("document.json"));
        }
        finally { FeedbackHost.UiErrorCaught -= Caught; w.Close(); }
    }

    [AvaloniaFact]
    public void A_flood_of_UI_errors_is_not_swallowed_forever()
    {
        FeedbackHost.ResetUiErrorWindow();
        bool last = true;
        for (int i = 0; i < 40; i++) last = FeedbackHost.HandleUiException(new InvalidOperationException("again " + i));
        Assert.False(last);
        FeedbackHost.ResetUiErrorWindow();
    }

    [AvaloniaFact]
    public void Screenshot_is_the_window_only_and_big_ones_shrink_to_fit()
    {
        var (w, _, _) = Open();
        var png = ScreenshotTools.CapturePng(w)!;
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);

        // a noisy 3000x2000 picture does not compress: it must come out smaller than the target as a JPEG
        using var bitmap = new SKBitmap(3000, 2000);
        var rnd = new Random(1);
        for (int y = 0; y < bitmap.Height; y += 1)
            for (int x = 0; x < bitmap.Width; x += 1)
                bitmap.SetPixel(x, y, new SKColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)));
        using var image = SKImage.FromBitmap(bitmap);
        var big = image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        Assert.True(big.Length > 5_000_000);

        var small = ScreenshotTools.Shrink(big, 1_500_000);

        Assert.NotNull(small);
        Assert.True(small!.Data.Length <= 1_500_000);
        Assert.Equal(".jpg", small.Extension);
        using (var decoded = SKBitmap.Decode(small.Data)) Assert.NotNull(decoded);
        w.Close();
    }
}
