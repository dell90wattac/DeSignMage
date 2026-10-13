using System.Runtime.CompilerServices;
using DeSignMage.TestSupport;
using DeSignMage.Text;

namespace DeSignMage.Files.Tests;

/// <summary>The sample layouts the tests read (untracked, in <c>local-testdata/layouts</c>; see the README) and real fonts, as the app installs them at startup.</summary>
internal static class TestFiles
{
    [ModuleInitializer]
    internal static void InstallFonts() => SkiaFontService.Install();

    public static string Sample => LocalData.Sample;

    public static string Layout(string name) => LocalData.Layout(name);

    /// <summary>The sample layouts the round trip runs over (read-only).</summary>
    public static IEnumerable<string> Layouts => LocalData.Layouts().Select(l => l.Name);
}
