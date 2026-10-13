using DeSignMage.TestSupport;

namespace DeSignMage.Formats.Tests;

/// <summary>The sample layouts the tests read (untracked, in <c>local-testdata/layouts</c>; see the README). Nothing is copied or written.</summary>
internal static class TestFiles
{
    public static string Sample => LocalData.Sample;

    public static string Layout(string name) => LocalData.Layout(name);

    /// <summary>Output folder for rendered PNGs, for looking at when a test fails.</summary>
    public static string Artifacts => LocalData.Artifacts;

    /// <summary>(file name, object count) for every sample layout listed in the manifest.</summary>
    public static IEnumerable<object[]> LayoutCases() => LocalData.Layouts().Select(l => new object[] { l.Name, l.Objects });
}
