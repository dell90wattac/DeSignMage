namespace DeSignMage.Feedback;

public enum ReportKind { Bug, Feature, Crash }

/// <summary>One attachment: a name, what it is, and its bytes.</summary>
public sealed record ReportFile(string FileName, string ContentType, byte[] Data);

/// <summary>Everything the program knows when someone presses Send (or after a crash). <see cref="ReportBuilder"/> turns it into a report.</summary>
public sealed record ReportRequest
{
    public ReportKind Kind { get; init; } = ReportKind.Bug;
    public string Description { get; init; } = "";
    public string? Name { get; init; }
    public string AppVersion { get; init; } = "";
    public string Windows { get; init; } = "";

    /// <summary>The "System" block of report.txt (<see cref="SystemInfo.ToText"/>).</summary>
    public string? SystemInfoText { get; init; }

    /// <summary>"Sign 3.dsm, 14 objects", or null when no document is open. Never document text.</summary>
    public string? DocumentSummary { get; init; }

    public IReadOnlyList<string> UndoHistory { get; init; } = [];
    public IReadOnlyList<string> LogLines { get; init; } = [];

    /// <summary>PNG of the DeSign window only (bug reports).</summary>
    public byte[]? ScreenshotPng { get; init; }

    /// <summary>The design(s) as .dsm: one for a bug report, one per open document after a crash.</summary>
    public IReadOnlyList<ReportFile> Designs { get; init; } = [];

    /// <summary>Crash reports: the exception and stack, as text.</summary>
    public string? CrashText { get; init; }

    /// <summary>Crash reports: the one-line "what happened" for the title and embed ("NullReferenceException: ...").</summary>
    public string? CrashSummary { get; init; }

    /// <summary>Lines appended to the report's notes (for example "design not included: switched off").</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public string? Id { get; init; }
    public DateTime? CreatedUtc { get; init; }
}

public sealed class EmbedField
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Inline { get; set; }
}

public sealed class OutgoingFile
{
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
}

/// <summary>
/// A finished report as it waits in the outbox (<c>report.json</c>): the Discord embed already trimmed to its limits, the names of the
/// attached files (stored beside it), and the delivery bookkeeping.
/// </summary>
public sealed class OutgoingReport
{
    public string Id { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public ReportKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<EmbedField> Fields { get; set; } = [];
    public int Color { get; set; }
    public string Footer { get; set; } = "";
    /// <summary>Attachment shown inside the embed (the screenshot), or null.</summary>
    public string? ImageFile { get; set; }
    public List<OutgoingFile> Files { get; set; } = [];
    public List<string> Notes { get; set; } = [];

    public int Attempts { get; set; }
    public DateTime? NotBeforeUtc { get; set; }
    public string? LastError { get; set; }
}

/// <summary>A built report: the manifest plus the files to attach.</summary>
public sealed record PreparedReport(OutgoingReport Report, IReadOnlyList<ReportFile> Files);

/// <summary>A smaller picture of a screenshot (JPEG or PNG) produced by the UI layer, which has the image library.</summary>
public sealed record ShrunkImage(byte[] Data, string Extension, string ContentType);

public sealed class ReportBuilderOptions
{
    /// <summary>Discord rejects requests above 8 MB (webhooks); stay a bit below so the multipart framing fits.</summary>
    public long MaxTotalBytes { get; init; } = 7_500_000;

    /// <summary>A screenshot larger than this is shrunk first, even when there is room.</summary>
    public long ScreenshotSoftLimit { get; init; } = 1_500_000;

    public int MaxLogLines { get; init; } = 500;
    public int MaxUndoLines { get; init; } = 100;

    /// <summary>Makes a picture smaller than the given byte count; returns null when it cannot. Supplied by the app (Skia).</summary>
    public Func<byte[], long, ShrunkImage?>? ShrinkScreenshot { get; init; }
}
