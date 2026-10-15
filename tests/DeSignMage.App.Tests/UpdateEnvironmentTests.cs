using DeSignMage.App.Updates;
using DeSignMage.Feedback;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Where the installer puts the program versus where the person's own files are: they must never meet.</summary>
public class UpdateDataSafetyTests
{
    [Fact]
    public void The_installer_folder_is_never_the_data_folder_nor_inside_it_nor_around_it()
    {
        string packId = UpdatePaths.PackId;
        Assert.False(string.IsNullOrWhiteSpace(packId), "the package id must be built into the program (UpdatePackId in Directory.Build.props)");
        string install = UpdatePaths.InstallRoot(packId);
        string data = AppPaths.Default.Root;
        Assert.True(UpdatePaths.AreSeparate(install, data), $"{install} and {data} overlap: an uninstall would delete the person's files");
        Assert.NotEqual("DeSignMage", packId);
    }

    [Theory]
    [InlineData(@"C:\L\DeSignMage", @"C:\L\DeSignMage", false)]
    [InlineData(@"C:\L\DeSignMage", @"C:\L\DeSignMage\sub", false)]
    [InlineData(@"C:\L\DeSignMage\sub", @"C:\L\DeSignMage", false)]
    [InlineData(@"C:\L\DeSignMage\", @"c:\l\designmage", false)]
    [InlineData(@"C:\L\DeSignMageApp", @"C:\L\DeSignMage", true)]
    [InlineData(@"C:\L\DeSignMage", @"C:\L\DeSignMageApp", true)]
    public void The_overlap_check_compares_whole_folder_names(string install, string data, bool separate) =>
        Assert.Equal(separate, UpdatePaths.AreSeparate(install, data));

    [Fact]
    public void Settings_outbox_crash_files_and_logs_survive_an_update_and_an_uninstall()
    {
        string local = Path.Combine(Path.GetTempPath(), "dsm-update-data-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // A fake %LocalAppData% with the program's data folder and the installer's folder side by side.
            var paths = new AppPaths(Path.Combine(local, "DeSignMage"));
            string install = Path.Combine(local, UpdatePaths.PackId);
            Directory.CreateDirectory(Path.Combine(install, "current"));
            File.WriteAllText(Path.Combine(install, "current", "DeSignMage.exe"), "old program");
            Directory.CreateDirectory(paths.Outbox);
            Directory.CreateDirectory(paths.Crash);
            Directory.CreateDirectory(paths.Logs);
            File.WriteAllText(Path.Combine(paths.Outbox, "report-1.json"), "{}");
            Directory.CreateDirectory(Path.Combine(paths.Crash, "crash-1"));
            File.WriteAllText(Path.Combine(paths.Crash, "crash-1", "design.dsm"), "emergency copy");
            new SettingsStore(paths.SettingsFile).Set("ui.textScale", 1.5);

            // An update replaces the program folder; an uninstall deletes the whole installer folder.
            Directory.Delete(Path.Combine(install, "current"), recursive: true);
            Directory.CreateDirectory(Path.Combine(install, "current"));
            File.WriteAllText(Path.Combine(install, "current", "DeSignMage.exe"), "new program");
            Directory.Delete(install, recursive: true);

            Assert.Equal(1.5, new SettingsStore(paths.SettingsFile).Get("ui.textScale", 1.0));
            Assert.True(File.Exists(Path.Combine(paths.Outbox, "report-1.json")));
            Assert.True(File.Exists(Path.Combine(paths.Crash, "crash-1", "design.dsm")));
            Assert.True(Directory.Exists(paths.Logs));
        }
        finally
        {
            if (Directory.Exists(local)) Directory.Delete(local, recursive: true);
        }
    }
}

/// <summary>Installed copy or portable/development copy, and which server is asked.</summary>
public class UpdateSourceAndPortableTests
{
    [Fact]
    public void A_copy_that_was_not_installed_by_the_setup_program_is_recognised_as_portable()
    {
        // The real update library, pointed at a folder: no network. The test program is certainly not an installed copy.
        string folder = Path.Combine(Path.GetTempPath(), "dsm-update-source-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var client = new VelopackUpdateClient(new UpdateSourceSpec(UpdateSourceKind.Directory, folder));
            Assert.False(client.IsInstalled);
            Assert.Null(client.DownloadedUpdate());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task The_real_client_of_a_portable_copy_makes_the_gate_start_normally()
    {
        string folder = Path.Combine(Path.GetTempPath(), "dsm-update-source-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var gate = new UpdateGate(new VelopackUpdateClient(new UpdateSourceSpec(UpdateSourceKind.Directory, folder)), new FakeGuard(), _ => { });
            var d = await gate.DecideAsync(TestContext.Current.CancellationToken);
            Assert.Equal(StartupAction.StartNormally, d.Action);
            Assert.Contains("not an installed copy", d.Reason);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Updates_come_from_the_public_releases_unless_another_address_or_folder_is_given()
    {
        Assert.Equal(new UpdateSourceSpec(UpdateSourceKind.GitHub, UpdateSourceSpec.DefaultRepository), UpdateSourceSpec.Resolve(null));
        Assert.Equal(UpdateSourceKind.GitHub, UpdateSourceSpec.Resolve("  ").Kind);
        Assert.Equal(UpdateSourceKind.GitHub, UpdateSourceSpec.Resolve("https://github.com/someone/else").Kind);
        Assert.Equal(UpdateSourceKind.Web, UpdateSourceSpec.Resolve("https://example.org/updates").Kind);
        Assert.Equal(new UpdateSourceSpec(UpdateSourceKind.Directory, @"C:\feed"), UpdateSourceSpec.Resolve(@"C:\feed"));
    }

    [Fact]
    public void The_default_repository_is_the_public_one_named_in_the_project_properties()
    {
        Assert.Equal("https://github.com/dell90wattac/DeSignMage", UpdateSourceSpec.DefaultRepository);
    }
}

/// <summary>"Fresh start" = no other copy running: two named handles per login session.</summary>
public class InstanceGuardTests
{
    private static string Name() => "DeSignMage.Test." + Guid.NewGuid().ToString("N");

    [Fact]
    public void The_first_copy_is_alone_a_second_one_is_not_and_it_is_alone_again_after_both_end()
    {
        string name = Name();
        using (var first = new InstanceGuard(name))
        {
            Assert.True(first.IsOnlyInstance);
            using var second = new InstanceGuard(name);
            Assert.False(second.IsOnlyInstance);
        }
        using var again = new InstanceGuard(name);
        Assert.True(again.IsOnlyInstance);
    }

    [Fact]
    public void A_copy_showing_the_update_window_is_seen_by_copies_that_start_later()
    {
        string name = Name();
        using var first = new InstanceGuard(name);
        Assert.False(first.UpdateRunningElsewhere);
        first.SetUpdating(true);
        using (var second = new InstanceGuard(name)) Assert.True(second.UpdateRunningElsewhere);
        first.SetUpdating(false);
        using (var third = new InstanceGuard(name)) Assert.False(third.UpdateRunningElsewhere);
    }

    [Fact]
    public void Different_names_never_meet()
    {
        using var a = new InstanceGuard(Name());
        using var b = new InstanceGuard(Name());
        Assert.True(a.IsOnlyInstance);
        Assert.True(b.IsOnlyInstance);
    }
}
