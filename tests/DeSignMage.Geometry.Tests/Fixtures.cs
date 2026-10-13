using DeSignMage.TestSupport;

namespace DeSignMage.Geometry.Tests;

/// <summary>The sample layouts the tests read (untracked, in <c>local-testdata/layouts</c>; see the README). Read in place, never copied or written.</summary>
internal static class Fixtures
{
    public static string Layout(string name) => LocalData.Layout(name);

    public static string Sample => LocalData.Sample;
}
