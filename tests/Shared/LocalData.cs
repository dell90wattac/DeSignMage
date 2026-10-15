using System.Text.Json;

namespace DeSignMage.TestSupport;

/// <summary>
/// Files that are not part of the repository: sample layouts and the like live in the untracked folder <c>local-testdata/</c>
/// next to <c>DeSignMage.slnx</c> (see the README). Tests that need them are skipped, not failed, when they are missing.
/// This file is linked into every test project that uses it.
/// </summary>
internal static class LocalData
{
    /// <summary>The folder holding <c>DeSignMage.slnx</c> (found by walking up from the test binaries), or null.</summary>
    public static string? Root { get; } = FindRoot();

    public static string TestData => Path.Combine(Root ?? AppContext.BaseDirectory, "local-testdata");

    /// <summary>The layout most tests open (<c>local-testdata/layouts/sample.lyt</c>).</summary>
    public const string SampleName = "sample.lyt";

    public static string Sample => Layout(SampleName);

    /// <summary>A file in <c>local-testdata/layouts/</c>.</summary>
    public static string Layout(string name) => Path.Combine(TestData, "layouts", name);

    /// <summary>A path below <c>local-testdata/</c>.</summary>
    public static string Under(params string[] parts) => Path.Combine([TestData, .. parts]);

    /// <summary>Output folder for rendered PNGs, for looking at when a test fails.</summary>
    public static string Artifacts
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "artifacts");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Why the tests that need <paramref name="needs"/> cannot run, or null when everything is there. Each entry is a path below
    /// <c>local-testdata/</c>.
    /// </summary>
    public static string? Missing(params string[] needs)
    {
        if (Root is null) return "the repository folder (DeSignMage.slnx) was not found";
        foreach (var need in needs)
        {
            string full = Path.Combine(TestData, need.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full) && !Directory.Exists(full))
                return $"needs local-testdata/{need} (see the README)";
        }
        return null;
    }

    public static bool Has(params string[] needs) => Missing(needs) is null;

    /// <summary>
    /// The sample layouts the round trip runs over, with the object count each is known to have: the object
    /// <c>"file name": count</c> pairs of <c>local-testdata/layouts/manifest.json</c>. Empty when the file is missing.
    /// </summary>
    public static IReadOnlyList<(string Name, int Objects)> Layouts()
    {
        var manifest = Path.Combine(TestData, "layouts", "manifest.json");
        if (!File.Exists(manifest)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllBytes(manifest));
        return doc.RootElement.EnumerateObject()
            .Select(p => (p.Name, p.Value.GetInt32()))
            .Where(l => File.Exists(Layout(l.Name)))
            .ToList();
    }

    private static string? FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "DeSignMage.slnx")))
                return dir.FullName;
        return null;
    }
}
