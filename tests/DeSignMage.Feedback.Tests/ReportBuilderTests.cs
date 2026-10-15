using System.Globalization;
using System.Text;

namespace DeSignMage.Feedback.Tests;

public class ReportBuilderTests
{
    private static string Text(PreparedReport p, string name) =>
        Encoding.UTF8.GetString(p.Files.Single(f => f.FileName == name).Data);

    private static int EmbedChars(OutgoingReport r) =>
        r.Title.Length + r.Description.Length + r.Footer.Length + r.Fields.Sum(f => f.Name.Length + f.Value.Length);

    [Fact]
    public void Bug_report_carries_text_log_screenshot_and_design()
    {
        var p = ReportBuilder.Build(Fake.Bug());

        Assert.Equal([Fake.BugFile, "design.dsm", "screenshot.png"], p.Files.Select(f => f.FileName).ToArray());
        Assert.StartsWith("Bug: The ruler is wrong after I zoom.", p.Report.Title);
        Assert.Equal("The ruler is wrong after I zoom.", p.Report.Description);
        Assert.Equal("screenshot.png", p.Report.ImageFile);
        Assert.Contains(p.Report.Fields, f => f.Name == "From" && f.Value == "Mum");
        Assert.Contains(p.Report.Fields, f => f.Name == "Version" && f.Value == "0.1.0");
        Assert.Contains(p.Report.Fields, f => f.Name == "Windows" && f.Value.StartsWith("Windows 11"));
        Assert.Contains(p.Report.Fields, f => f.Name == "Document" && f.Value == "Sign 3.dsm, 14 objects");

        var report = Text(p, Fake.BugFile);
        Assert.Contains("Add Rectangle", report);   // undo history
        Assert.Contains("Group", report);
        Assert.Contains("App version : 0.1.0", report);
        Assert.Contains("Sign 3.dsm, 14 objects", report);
        // the manifest lists the same files with their sizes
        Assert.Equal(p.Files.Select(f => (long)f.Data.Length), p.Report.Files.Select(f => f.Size));
    }

    [Fact]
    public void One_text_file_holds_header_description_document_notes_undo_history_then_the_log()
    {
        var p = ReportBuilder.Build(Fake.Bug() with { Notes = ["Screenshot not included: switched off by the sender."] });

        var txt = Assert.Single(p.Files, f => f.FileName.EndsWith(".txt"));
        Assert.Equal("DeSignMage-Bug-20261012-101500-abc123.txt", txt.FileName);
        var text = Text(p, txt.FileName);
        string[] order = ["Type        : Bug", "Report id   : 20261012-101500-abc123", "Created     :", "From        : Mum", "App version : 0.1.0",
            "==== DESCRIPTION ====", "==== DOCUMENT ====", "==== NOTES", "==== UNDO HISTORY", "==== LOG ===="];
        int at = -1;
        foreach (var marker in order)
        {
            int next = text.IndexOf(marker, at + 1, StringComparison.Ordinal);
            Assert.True(next > at, $"'{marker}' should come after the previous section");
            at = next;
        }
        Assert.Contains("Screenshot not included: switched off by the sender.", text);
        Assert.Contains("line 799", text[text.IndexOf("==== LOG ====", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void Crash_report_puts_the_exception_right_after_the_header()
    {
        var p = ReportBuilder.Build(new ReportRequest
        {
            Kind = ReportKind.Crash, Id = "crash-20261012-101500-abcd", Description = "The program stopped.", CrashSummary = "InvalidOperationException: boom",
            CrashText = "System.InvalidOperationException: boom\n   at X.Y()", SystemInfoText = "App version : 0.1.0\nWindows     : Windows 11", AppVersion = "0.1.0", LogLines = ["a log line"],
        });
        var file = Assert.Single(p.Files).FileName;
        Assert.Equal("DeSignMage-Crash-20261012-101500-abcd.txt", file);
        var text = Text(p, file);
        Assert.True(text.IndexOf("Windows     : Windows 11", StringComparison.Ordinal) < text.IndexOf("==== EXCEPTION ====", StringComparison.Ordinal));
        Assert.True(text.IndexOf("==== EXCEPTION ====", StringComparison.Ordinal) < text.IndexOf("==== DESCRIPTION ====", StringComparison.Ordinal));
        Assert.Contains("at X.Y()", text);
    }

    [Fact]
    public void Log_is_limited_to_the_last_500_lines()
    {
        var p = ReportBuilder.Build(Fake.Bug());
        var report = Text(p, Fake.BugFile);
        var lines = report[(report.IndexOf("==== LOG ====", StringComparison.Ordinal) + "==== LOG ====".Length)..].Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(500, lines.Length);
        Assert.EndsWith("line 799", lines[^1]);
        Assert.EndsWith("line 300", lines[0]);
    }

    [Fact]
    public void Feature_report_is_just_the_text_and_the_version()
    {
        var p = ReportBuilder.Build(new ReportRequest
        {
            Kind = ReportKind.Feature,
            Description = "Please add a polygon tool.",
            Name = "Mum",
            AppVersion = "0.1.0",
            Windows = "Windows 11",
            SystemInfoText = "should not be included",
            LogLines = ["a log line"],
            ScreenshotPng = Fake.Bytes(100),
            Designs = [new ReportFile("design.dsm", "application/octet-stream", Fake.Bytes(100))],
        });

        Assert.Empty(p.Files);
        Assert.Equal("Feature: Please add a polygon tool.", p.Report.Title);
        Assert.Equal(["From", "Version"], p.Report.Fields.Select(f => f.Name).ToArray());
        Assert.Null(p.Report.ImageFile);
    }

    [Fact]
    public void Long_description_is_cut_in_the_embed_and_complete_in_report_txt()
    {
        string long_ = string.Join(" ", Enumerable.Range(0, 2000).Select(i => "word" + i));   // ~14,000 characters
        var p = ReportBuilder.Build(Fake.Bug(long_));

        Assert.True(p.Report.Description.Length <= 4000, "embed description limit");
        Assert.True(p.Report.Title.Length <= ReportBuilder.TitleLimit);
        Assert.True(EmbedChars(p.Report) <= ReportBuilder.EmbedTotalLimit, $"embed total {EmbedChars(p.Report)}");
        Assert.EndsWith("(the full text is in the attached .txt file)", p.Report.Description);
        Assert.Contains("word1999", Text(p, Fake.BugFile));
    }

    [Fact]
    public void Long_feature_text_creates_report_txt_only_when_it_overflows()
    {
        var request = new ReportRequest { Kind = ReportKind.Feature, Description = new string('x', 9000), AppVersion = "0.1.0" };
        var p = ReportBuilder.Build(request);
        var file = Assert.Single(p.Files).FileName;
        Assert.Matches(@"^DeSignMage-Feature-.+\.txt$", file);
        Assert.Contains(new string('x', 9000), Text(p, file));
    }

    [Fact]
    public void Embed_limits_hold_with_hostile_input_everywhere()
    {
        var p = ReportBuilder.Build(Fake.Bug(new string('é', 20000)) with
        {
            Name = new string('N', 500),
            Windows = new string('W', 5000),
            DocumentSummary = new string('D', 5000),
            Notes = Enumerable.Range(0, 100).Select(i => new string('n', 100)).ToList(),
            AppVersion = new string('V', 900),
        });
        Assert.True(p.Report.Title.Length <= ReportBuilder.TitleLimit);
        Assert.All(p.Report.Fields, f => Assert.True(f.Value.Length <= ReportBuilder.FieldValueLimit));
        Assert.True(EmbedChars(p.Report) <= ReportBuilder.EmbedTotalLimit, $"embed total {EmbedChars(p.Report)}");
    }

    [Fact]
    public void Title_is_kind_plus_first_words_cut_at_a_word()
    {
        var p = ReportBuilder.Build(Fake.Bug("   \n  Everything goes blank when I press the undo button after grouping two shapes and one more thing that is long\nsecond line"));
        Assert.StartsWith("Bug: Everything goes blank", p.Report.Title);
        Assert.DoesNotContain("second line", p.Report.Title);
        Assert.EndsWith("…", p.Report.Title);
        Assert.True(p.Report.Title.Length < 110);

        Assert.StartsWith("Bug: (no description)", ReportBuilder.Build(Fake.Bug("  ")).Report.Title);
        Assert.StartsWith("Crash: NullReferenceException", ReportBuilder.Build(new ReportRequest { Kind = ReportKind.Crash, CrashSummary = "NullReferenceException: boom" }).Report.Title);
    }

    [Fact]
    public void Big_screenshot_is_shrunk_first()
    {
        var shrunk = Fake.Bytes(200_000, 9);
        long askedFor = 0;
        var options = new ReportBuilderOptions
        {
            ShrinkScreenshot = (png, target) => { askedFor = target; return new ShrunkImage(shrunk, ".jpg", "image/jpeg"); },
        };
        var p = ReportBuilder.Build(Fake.Bug() with { ScreenshotPng = Fake.Bytes(6_000_000) }, options);

        Assert.True(askedFor > 0 && askedFor <= options.ScreenshotSoftLimit);
        var shot = Assert.Single(p.Files, f => f.FileName.StartsWith("screenshot"));
        Assert.Equal("screenshot.jpg", shot.FileName);
        Assert.Equal(shrunk, shot.Data);
        Assert.Equal("screenshot.jpg", p.Report.ImageFile);
        Assert.True(p.Files.Sum(f => (long)f.Data.Length) <= options.MaxTotalBytes);
    }

    [Fact]
    public void Design_that_does_not_fit_is_left_out_and_the_report_says_so()
    {
        var options = new ReportBuilderOptions { ShrinkScreenshot = (_, _) => new ShrunkImage(Fake.Bytes(100_000), ".jpg", "image/jpeg") };
        var request = Fake.Bug() with { Designs = [new ReportFile("design.dsm", "application/octet-stream", Fake.Bytes(9_000_000))], ScreenshotPng = Fake.Bytes(2_000_000) };
        var p = ReportBuilder.Build(request, options);

        Assert.DoesNotContain(p.Files, f => f.FileName.EndsWith(".dsm"));
        Assert.Contains(p.Files, f => f.FileName.StartsWith("screenshot"));
        Assert.Contains(p.Report.Notes, n => n.Contains("design.dsm") && n.Contains("too big"));
        Assert.Contains(p.Report.Fields, f => f.Name == "Notes" && f.Value.Contains("not attached"));
        Assert.Contains("not attached", Text(p, Fake.BugFile));
        Assert.True(p.Files.Sum(f => (long)f.Data.Length) <= options.MaxTotalBytes);
    }

    [Fact]
    public void Design_just_under_the_budget_is_kept_and_the_screenshot_gives_way()
    {
        var options = new ReportBuilderOptions
        {
            MaxTotalBytes = 7_500_000,
            ShrinkScreenshot = (_, target) => new ShrunkImage(Fake.Bytes((int)Math.Min(target, 300_000)), ".jpg", "image/jpeg"),
        };
        var request = Fake.Bug() with { Designs = [new ReportFile("design.dsm", "application/octet-stream", Fake.Bytes(6_000_000))], ScreenshotPng = Fake.Bytes(3_000_000) };
        var p = ReportBuilder.Build(request, options);

        Assert.Contains(p.Files, f => f.FileName == "design.dsm");
        Assert.True(p.Files.Sum(f => (long)f.Data.Length) <= options.MaxTotalBytes);
    }

    [Fact]
    public void Screenshot_that_cannot_be_shrunk_is_left_out_with_a_note()
    {
        var options = new ReportBuilderOptions { ShrinkScreenshot = (_, _) => null, MaxTotalBytes = 2_000_000 };
        var p = ReportBuilder.Build(Fake.Bug() with { ScreenshotPng = Fake.Bytes(5_000_000), Designs = [] }, options);

        Assert.DoesNotContain(p.Files, f => f.FileName.StartsWith("screenshot"));
        Assert.Null(p.Report.ImageFile);
        Assert.Contains(p.Report.Notes, n => n.Contains("screenshot"));
    }

    [Fact]
    public void A_webhook_address_pasted_into_a_report_is_scrubbed()
    {
        var request = Fake.Bug($"I pasted {Fake.Url} by mistake") with { LogLines = [$"posted to {Fake.Url}"], Name = Fake.Url };
        var p = ReportBuilder.Build(request);
        var all = p.Report.Title + p.Report.Description + string.Concat(p.Report.Fields.Select(f => f.Value)) + string.Concat(p.Files.Where(f => f.ContentType.StartsWith("text")).Select(f => Encoding.UTF8.GetString(f.Data)));
        Assert.DoesNotContain(Fake.Token, all);
        Assert.DoesNotContain("discord.com/api/webhooks", all);
    }

    [Fact]
    public void Several_recovered_designs_keep_their_names_unique()
    {
        var d = new ReportFile("recovered-1-Sign.dsm", "application/octet-stream", Fake.Bytes(10));
        var p = ReportBuilder.Build(Fake.Bug() with { Designs = [d, d, d] });
        Assert.Equal(["recovered-1-Sign.dsm", "recovered-1-Sign-2.dsm", "recovered-1-Sign-3.dsm"], p.Files.Where(f => f.FileName.EndsWith(".dsm")).Select(f => f.FileName).ToArray());
    }
}

/// <summary>
/// The feedback relay (tools/feedback-relay/src/index.js, <c>checkShape</c>) turns away anything that is not shaped like the program's
/// own reports. These tests pin that shape on this side: if a report ever changes, the relay (and its header version) must change too.
/// </summary>
public class RelayShapeTests
{
    private static readonly Dictionary<ReportKind, int> RelayColors = new() { [ReportKind.Bug] = 0xD9534F, [ReportKind.Feature] = 0x2E86DE, [ReportKind.Crash] = 0x8B0000 };
    private static readonly System.Text.RegularExpressions.Regex Footer = new(@"^Report [A-Za-z0-9-]{8,60} · .{8,40} UTC$");
    private static readonly System.Text.RegularExpressions.Regex ReportTxt = new(@"^DeSignMage-(Bug|Feature|Crash)-[A-Za-z0-9._-]{1,70}\.txt$");

    private static IEnumerable<ReportRequest> Requests()
    {
        yield return Fake.Bug();
        yield return new ReportRequest { Kind = ReportKind.Feature, Description = "A polygon tool please", AppVersion = "0.4.0", Name = "Mum" };
        yield return new ReportRequest { Kind = ReportKind.Feature, Description = new string('x', 6000), AppVersion = "0.4.0" };          // long: gets a text file
        yield return Fake.Bug() with { Kind = ReportKind.Crash, Id = "crash-20261012-101500-abc123", CrashText = "System.InvalidOperationException: boom\n   at X()", CrashSummary = "InvalidOperationException: boom" };
    }

    [Fact]
    public void Every_kind_of_report_has_the_shape_the_relay_accepts()
    {
        foreach (var request in Requests())
        {
            var p = ReportBuilder.Build(request);
            var r = p.Report;
            string kind = request.Kind.ToString();
            Assert.StartsWith(kind + ": ", r.Title);
            Assert.True(r.Title.Length > kind.Length + 2 && !char.IsWhiteSpace(r.Title[kind.Length + 2]));
            Assert.Equal(RelayColors[request.Kind], r.Color);
            Assert.False(string.IsNullOrEmpty(r.Description));
            Assert.Matches(Footer, r.Footer);

            var fields = r.Fields.ToDictionary(f => f.Name, f => f.Value);
            Assert.True(fields.GetValueOrDefault("From", "-") != "-" && fields.GetValueOrDefault("Version", "-") != "-");
            if (request.Kind != ReportKind.Feature) Assert.True(fields.GetValueOrDefault("Windows", "-") != "-");

            var names = p.Files.Select(f => f.FileName).ToList();
            var texts = names.Where(n => ReportTxt.IsMatch(n)).ToList();
            Assert.True(texts.Count <= 1);
            if (request.Kind != ReportKind.Feature || request.Description.Length > 1000) Assert.Single(texts);
            foreach (var t in texts) Assert.StartsWith($"DeSignMage-{kind}-", t);
            foreach (var n in names.Except(texts))
                Assert.True(System.Text.RegularExpressions.Regex.IsMatch(n, @"^screenshot\.(png|jpe?g)$") || n.EndsWith(".dsm"), $"unexpected attachment {n}");
            Assert.True(names.Count(n => n.StartsWith("screenshot")) <= 1);
            Assert.True(p.Files.Count <= 8);
        }
    }

    [Theory]
    [InlineData("fi-FI")]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    public void The_footer_does_not_depend_on_the_computers_language_or_calendar(string culture)
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            var p = ReportBuilder.Build(Fake.Bug() with { CreatedUtc = new DateTime(2026, 10, 12, 10, 15, 0, DateTimeKind.Utc) });
            Assert.EndsWith("· 2026-10-12 10:15 UTC", p.Report.Footer);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void The_real_client_introduces_itself_as_the_program()
    {
        using var client = RelaySender.CreateClient();
        Assert.StartsWith("DeSignMage/", client.DefaultRequestHeaders.UserAgent.ToString());
    }
}