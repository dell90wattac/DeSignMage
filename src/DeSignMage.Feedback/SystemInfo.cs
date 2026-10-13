using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace DeSignMage.Feedback;

/// <summary>What a report says about the computer and the build. Collected without asking anything.</summary>
public sealed record SystemInfo(
    string AppVersion, string BuildDate, string Windows, string DotNet, string Architecture,
    int ProcessorCount, long MemoryMb, string Culture, IReadOnlyList<string> Displays)
{
    /// <summary>"Windows 11 (10.0.26300)", for the report's header line.</summary>
    public string WindowsShort => Windows;

    /// <param name="app">The assembly that carries the version and the BuildDate metadata (the program itself).</param>
    /// <param name="displays">One line per monitor (size, scaling), supplied by the UI layer.</param>
    public static SystemInfo Collect(Assembly? app = null, IEnumerable<string>? displays = null)
    {
        app ??= Assembly.GetEntryAssembly() ?? typeof(SystemInfo).Assembly;
        var version = (app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? app.GetName().Version?.ToString() ?? "unknown").Split('+')[0];
        var build = app.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildDate")?.Value ?? "unknown";

        var os = Environment.OSVersion.Version;
        string name = os.Major >= 10 && os.Build >= 22000 ? "Windows 11" : os.Major >= 10 ? "Windows 10" : "Windows";
        long memory = 0;
        try { memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1_048_576; }
        catch (Exception) { }

        return new SystemInfo(
            version, build, $"{name} ({os})", RuntimeInformation.FrameworkDescription,
            $"{RuntimeInformation.OSArchitecture} OS, {RuntimeInformation.ProcessArchitecture} process",
            Environment.ProcessorCount, memory, CultureInfo.CurrentUICulture.Name, displays?.ToArray() ?? []);
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"App version : {AppVersion}");
        sb.AppendLine($"Build date  : {BuildDate}");
        sb.AppendLine($"Windows     : {Windows}, {Architecture}");
        sb.AppendLine($".NET        : {DotNet}");
        sb.AppendLine($"Processors  : {ProcessorCount}, memory {Memory}");
        sb.AppendLine($"Language    : {Culture}");
        if (Displays.Count == 0) sb.AppendLine("Displays    : (unknown)");
        else foreach (var d in Displays) sb.AppendLine($"Display     : {d}");
        return sb.ToString().TrimEnd();
    }

    private string Memory => MemoryMb > 0 ? $"{MemoryMb:N0} MB" : "unknown";
}
