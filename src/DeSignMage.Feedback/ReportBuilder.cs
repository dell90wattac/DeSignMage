using System.Globalization;
using System.Text;

namespace DeSignMage.Feedback;

/// <summary>
/// Turns what the program collected into a report: the Discord embed (trimmed to Discord's limits) and the attachments (kept within
/// the size budget: the screenshot is shrunk first, a design that is too big is left out and the report says so).
/// </summary>
public static class ReportBuilder
{
    // Discord embed limits: title 256, description 4096, field value 1024, footer 2048, all text together 6000.
    public const int TitleLimit = 256;
    public const int FieldValueLimit = 1024;
    public const int EmbedTotalLimit = 6000;
    private const int DescriptionHardLimit = 4000;

    private const long ScreenshotReserve = 800_000;
    private const long ScreenshotHardCap = 3_000_000;

    public static int ColorOf(ReportKind kind) => kind switch { ReportKind.Bug => 0xD9534F, ReportKind.Feature => 0x2E86DE, _ => 0x8B0000 };

    public static string LabelOf(ReportKind kind) => kind switch { ReportKind.Bug => "Bug", ReportKind.Feature => "Feature", _ => "Crash" };

    public static string NewId(DateTime utc) => string.Create(CultureInfo.InvariantCulture, $"{utc:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");

    public static PreparedReport Build(ReportRequest r, ReportBuilderOptions? options = null)
    {
        var o = options ?? new ReportBuilderOptions();
        var created = r.CreatedUtc ?? DateTime.UtcNow;
        var id = r.Id ?? NewId(created);
        bool diagnostic = r.Kind != ReportKind.Feature;

        var description = TextUtil.CleanControl(Redactor.Scrub(r.Description)).Trim();
        var name = TextUtil.Truncate(TextUtil.CleanControl(Redactor.Scrub(r.Name)).Replace('\n', ' ').Trim(), 80);
        var notes = r.Notes.Select(n => Redactor.Scrub(n)).ToList();
        var files = new List<ReportFile>();
        long used = 0;

        // ---- log (text, goes at the end of the report file)
        var logLines = diagnostic ? r.LogLines.TakeLast(o.MaxLogLines).Select(l => TextUtil.Truncate(Redactor.Scrub(l), 600)).ToList() : [];
        string logText = logLines.Count > 0 ? string.Join("\r\n", logLines) + "\r\n" : "";

        var undo = r.UndoHistory.TakeLast(o.MaxUndoLines).Select(u => TextUtil.Truncate(u, 200)).ToList();

        // Room kept for the one text file, which is written last (it lists what was left out).
        string reportFileName = ReportFileName(r.Kind, id);
        long reserve = 24_000 + Utf8(description) + Utf8(r.CrashText) + Utf8(r.SystemInfoText) + Utf8(logText) + undo.Sum(u => (long)u.Length + 2);
        bool hasShot = diagnostic && r.ScreenshotPng is { Length: > 0 };

        // ---- designs (cannot be shrunk: attach or leave out)
        if (diagnostic)
        {
            long room = o.MaxTotalBytes - used - reserve - (hasShot ? ScreenshotReserve : 0);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { reportFileName };
            foreach (var d in r.Designs)
            {
                if (d.Data.Length == 0) continue;
                if (d.Data.Length > room)
                {
                    notes.Add($"Design {d.FileName} was not attached: it is {TextUtil.FormatBytes(d.Data.Length)}, too big for one message.");
                    continue;
                }
                var fileName = Unique(TextUtil.SafeFileName(d.FileName, "design.dsm"), taken);
                files.Add(d with { FileName = fileName });
                room -= d.Data.Length;
                used += d.Data.Length;
            }
        }

        // ---- screenshot (shrunk to fit)
        string? image = null;
        if (hasShot)
        {
            var png = r.ScreenshotPng!;
            long room = Math.Min(o.MaxTotalBytes - used - reserve, ScreenshotHardCap);
            long target = Math.Min(room, o.ScreenshotSoftLimit);
            ReportFile? shot = null;
            if (png.Length <= target) shot = new ReportFile("screenshot.png", "image/png", png);
            else
            {
                ShrunkImage? small = null;
                try { small = o.ShrinkScreenshot?.Invoke(png, target); }
                catch (Exception ex) { AppLog.Warn("report", $"Shrinking the screenshot failed: {ex.GetType().Name}"); }
                if (small is { Data.Length: > 0 } && small.Data.Length <= room) shot = new ReportFile("screenshot" + small.Extension, small.ContentType, small.Data);
                else if (small is null && png.Length <= room) shot = new ReportFile("screenshot.png", "image/png", png);
            }
            if (shot is null) notes.Add($"The screenshot was not attached: it is {TextUtil.FormatBytes(png.Length)} and could not be made small enough.");
            else
            {
                files.Add(shot);
                used += shot.Data.Length;
                image = shot.FileName;
            }
        }

        // ---- the embed
        var fields = new List<EmbedField> { new() { Name = "From", Value = name.Length > 0 ? name : "(no name)", Inline = true } };
        fields.Add(new EmbedField { Name = "Version", Value = Fit(r.AppVersion, 100, "unknown"), Inline = true });
        if (diagnostic) fields.Add(new EmbedField { Name = "Windows", Value = Fit(r.Windows, 200, "unknown"), Inline = true });
        if (diagnostic && !string.IsNullOrWhiteSpace(r.DocumentSummary))
            fields.Add(new EmbedField { Name = "Document", Value = TextUtil.Truncate(Redactor.Scrub(r.DocumentSummary), FieldValueLimit) });
        if (notes.Count > 0)
            fields.Add(new EmbedField { Name = "Notes", Value = TextUtil.Truncate(string.Join("\n", notes.Select(n => "• " + n)), FieldValueLimit) });

        string firstWords = TextUtil.FirstWords(r.Kind == ReportKind.Crash ? (r.CrashSummary ?? description) : description, 90);
        string title = TextUtil.Truncate($"{LabelOf(r.Kind)}: {(firstWords.Length > 0 ? firstWords : "(no description)")}", TitleLimit);
        string footer = string.Create(CultureInfo.InvariantCulture, $"Report {id} · {created:yyyy-MM-dd HH:mm} UTC");

        string embedSource = r.Kind == ReportKind.Crash ? CrashEmbedText(r, description) : description;
        if (embedSource.Length == 0) embedSource = "(no description)";
        int others = title.Length + footer.Length + fields.Sum(f => f.Name.Length + f.Value.Length);
        int descMax = Math.Max(200, Math.Min(DescriptionHardLimit, EmbedTotalLimit - 300 - others));
        const string more = "\n\n… (the full text is in the attached .txt file)";
        bool overflow = embedSource.Length > descMax;
        string embedText = overflow ? TextUtil.Truncate(embedSource, descMax - more.Length) + more : embedSource;

        // ---- the one text file: always for bugs and crashes; for feedback only when the text did not fit
        if (diagnostic || overflow)
        {
            var text = ReportText(r, id, created, name, description, undo, notes, logText);
            files.Insert(0, Text(reportFileName, text));
        }

        var report = new OutgoingReport
        {
            Id = id,
            CreatedUtc = created,
            Kind = r.Kind,
            Title = title,
            Description = embedText,
            Fields = fields,
            Color = ColorOf(r.Kind),
            Footer = footer,
            ImageFile = image,
            Notes = notes,
            Files = files.Select(f => new OutgoingFile { FileName = f.FileName, ContentType = f.ContentType, Size = f.Data.Length }).ToList(),
        };
        return new PreparedReport(report, files);
    }

    private static string CrashEmbedText(ReportRequest r, string description)
    {
        var sb = new StringBuilder();
        if (description.Length > 0) sb.AppendLine(description).AppendLine();
        if (!string.IsNullOrWhiteSpace(r.CrashText))
        {
            var lines = TextUtil.CleanControl(Redactor.Scrub(r.CrashText)).Split('\n').Take(14).Select(l => TextUtil.Truncate(l.TrimEnd(), 160));
            sb.AppendLine("```").AppendLine(string.Join("\n", lines).Replace("```", "'''")).Append("```");
        }
        return sb.ToString().Trim();
    }

    /// <summary><c>DeSignMage-Bug-&lt;report id&gt;.txt</c>: the one text attachment of a report.</summary>
    public static string ReportFileName(ReportKind kind, string id)
    {
        var shortId = id.StartsWith("crash-", StringComparison.OrdinalIgnoreCase) ? id[6..] : id;
        return $"DeSignMage-{LabelOf(kind)}-{TextUtil.SafeFileName(shortId, "report")}.txt";
    }

    /// <summary>
    /// Header (type, id, time, sender, version, build, Windows, displays, .NET), the exception for a crash, the description, the open
    /// document, notes about what was included or left out, the undo history, then the log.
    /// </summary>
    private static string ReportText(ReportRequest r, string id, DateTime created, string name, string description, List<string> undo, List<string> notes, string logText)
    {
        var sb = new StringBuilder();
        string heading = r.Kind switch { ReportKind.Bug => "Bug report", ReportKind.Feature => "Feature request / feedback", _ => "Crash report" };
        sb.AppendLine($"DeSign Mage {heading}");
        sb.AppendLine($"Type        : {LabelOf(r.Kind)}");
        sb.AppendLine($"Report id   : {id}");
        sb.AppendLine($"Created     : {created.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC");
        sb.AppendLine($"From        : {(name.Length > 0 ? name : "(no name)")}");
        if (!string.IsNullOrWhiteSpace(r.SystemInfoText)) sb.AppendLine(r.SystemInfoText.TrimEnd());   // version, build date, Windows, .NET, displays
        else
        {
            sb.AppendLine($"App version : {Fit(r.AppVersion, 100, "unknown")}");
            if (r.Kind != ReportKind.Feature) sb.AppendLine($"Windows     : {Fit(r.Windows, 200, "unknown")}");
        }
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(r.CrashText))
            sb.AppendLine("==== EXCEPTION ====").AppendLine(Redactor.Scrub(TextUtil.CleanControl(r.CrashText)).TrimEnd()).AppendLine();
        sb.AppendLine("==== DESCRIPTION ====").AppendLine(description.Length > 0 ? description : "(none)").AppendLine();

        if (r.Kind != ReportKind.Feature)
        {
            sb.AppendLine("==== DOCUMENT ====").AppendLine(string.IsNullOrWhiteSpace(r.DocumentSummary) ? "(no document open)" : Redactor.Scrub(r.DocumentSummary)).AppendLine();
            sb.AppendLine("==== NOTES (what was included or left out) ====");
            if (notes.Count == 0) sb.AppendLine("Nothing was left out.");
            foreach (var n in notes) sb.AppendLine("  " + n);
            sb.AppendLine();
            sb.AppendLine("==== UNDO HISTORY (oldest first) ====");
            if (undo.Count == 0) sb.AppendLine("(none)");
            foreach (var u in undo) sb.AppendLine("  " + u);
            sb.AppendLine();
            sb.AppendLine("==== LOG ====");
            sb.Append(logText.Length > 0 ? logText.Replace("\r\n", "\n") : "(empty)\n");
        }
        else if (notes.Count > 0)
        {
            sb.AppendLine("==== NOTES ====");
            foreach (var n in notes) sb.AppendLine("  " + n);
        }
        return sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    private static ReportFile Text(string fileName, string text) => new(fileName, "text/plain; charset=utf-8", new UTF8Encoding(false).GetBytes(text));

    private static long Utf8(string? s) => string.IsNullOrEmpty(s) ? 0 : Encoding.UTF8.GetByteCount(s);

    private static string Fit(string? s, int max, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : TextUtil.Truncate(TextUtil.CleanControl(s).Replace('\n', ' ').Trim(), max);

    private static string Unique(string fileName, HashSet<string> taken)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var candidate = fileName;
        for (int n = 2; !taken.Add(candidate); n++) candidate = $"{stem}-{n}{ext}";
        return candidate;
    }
}
