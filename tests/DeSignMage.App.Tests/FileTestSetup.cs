using System.Runtime.CompilerServices;
using DeSignMage.App.Shell;

namespace DeSignMage.App.Tests;

/// <summary>Tests never touch the real "recent files" list in %APPDATA%.</summary>
internal static class FileTestSetup
{
    [ModuleInitializer]
    internal static void Init() => RecentFiles.StoragePath = Path.Combine(Path.GetTempPath(), $"dsm-recent-test-{Environment.ProcessId}.json");
}
