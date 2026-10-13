namespace DeSignMage.Feedback.Tests;

public class LegacyMigrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("migrate-").FullName;
    private string Local => Path.Combine(_root, "local");
    private string Roaming => Path.Combine(_root, "roaming");
    private string OldLocal => Path.Combine(Local, LegacyMigration.LegacyFolder);
    private string NewLocal => Path.Combine(Local, "DeSignMage");
    private string OldRoaming => Path.Combine(Roaming, LegacyMigration.LegacyFolder);
    private string NewRoaming => Path.Combine(Roaming, "DeSignMage");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void The_whole_folder_moves_when_the_new_one_does_not_exist_yet_and_logs_are_renamed()
    {
        Write(Path.Combine(OldLocal, "feedback", "settings.json"), "{\"name\":\"x\"}");
        Write(Path.Combine(OldLocal, "feedback", "outbox", "r1", "report.json"), "{}");
        Write(Path.Combine(OldLocal, "crash", "crash-1", "report.txt"), "boom");
        Write(Path.Combine(OldLocal, "logs", LegacyMigration.LegacyLogName + ".log"), "current");
        Write(Path.Combine(OldLocal, "logs", LegacyMigration.LegacyLogName + ".1.log"), "older");
        Write(Path.Combine(OldRoaming, "recent.json"), "[\"C:\\\\a.dsm\"]");

        var result = LegacyMigration.Run(Local, Roaming);

        Assert.Null(result.Error);
        Assert.Equal(6, result.Moved);
        Assert.False(Directory.Exists(OldLocal));
        Assert.False(Directory.Exists(OldRoaming));
        Assert.Equal("{\"name\":\"x\"}", File.ReadAllText(Path.Combine(NewLocal, "feedback", "settings.json")));
        Assert.True(File.Exists(Path.Combine(NewLocal, "feedback", "outbox", "r1", "report.json")));
        Assert.Equal("boom", File.ReadAllText(Path.Combine(NewLocal, "crash", "crash-1", "report.txt")));
        Assert.Equal("current", File.ReadAllText(Path.Combine(NewLocal, "logs", "designmage.log")));
        Assert.Equal("older", File.ReadAllText(Path.Combine(NewLocal, "logs", "designmage.1.log")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(NewLocal, "logs"), LegacyMigration.LegacyLogName + "*"));
        Assert.Equal("[\"C:\\\\a.dsm\"]", File.ReadAllText(Path.Combine(NewRoaming, "recent.json")));
    }

    [Fact]
    public void When_the_new_folder_exists_only_missing_files_come_over_and_nothing_is_overwritten()
    {
        Write(Path.Combine(OldLocal, "feedback", "settings.json"), "old settings");
        Write(Path.Combine(OldLocal, "feedback", "outbox", "r1", "report.json"), "old report");
        Write(Path.Combine(NewLocal, "feedback", "settings.json"), "new settings");

        var result = LegacyMigration.Run(Local, Roaming);

        Assert.Null(result.Error);
        Assert.Equal(1, result.Moved);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("new settings", File.ReadAllText(Path.Combine(NewLocal, "feedback", "settings.json")));
        Assert.Equal("old report", File.ReadAllText(Path.Combine(NewLocal, "feedback", "outbox", "r1", "report.json")));
        // what was skipped stays where it was
        Assert.Equal("old settings", File.ReadAllText(Path.Combine(OldLocal, "feedback", "settings.json")));
    }

    [Fact]
    public void A_recent_files_list_that_already_exists_is_kept()
    {
        Write(Path.Combine(OldRoaming, "recent.json"), "old");
        Write(Path.Combine(NewRoaming, "recent.json"), "new");

        LegacyMigration.Run(Local, Roaming);

        Assert.Equal("new", File.ReadAllText(Path.Combine(NewRoaming, "recent.json")));
    }

    [Fact]
    public void Nothing_to_do_and_running_twice_are_both_fine()
    {
        var none = LegacyMigration.Run(Local, Roaming);
        Assert.Equal(new LegacyMigration.Result(0, 0, null), none);

        Write(Path.Combine(OldLocal, "logs", LegacyMigration.LegacyLogName + ".log"), "x");
        Assert.Equal(1, LegacyMigration.Run(Local, Roaming).Moved);
        Assert.Equal(new LegacyMigration.Result(0, 0, null), LegacyMigration.Run(Local, Roaming));
        Assert.True(File.Exists(Path.Combine(NewLocal, "logs", "designmage.log")));
    }

    [Fact]
    public void A_locked_file_is_skipped_not_fatal()
    {
        Write(Path.Combine(OldLocal, "a.txt"), "a");
        Write(Path.Combine(OldLocal, "b.txt"), "b");
        Write(Path.Combine(NewLocal, "keep.txt"), "k");   // forces the file-by-file path
        using var locked = new FileStream(Path.Combine(OldLocal, "a.txt"), FileMode.Open, FileAccess.Read, FileShare.None);

        var result = LegacyMigration.Run(Local, Roaming);

        Assert.Null(result.Error);
        Assert.Equal(1, result.Moved);
        Assert.Equal(1, result.Skipped);
        Assert.True(File.Exists(Path.Combine(NewLocal, "b.txt")));
        Assert.True(File.Exists(Path.Combine(OldLocal, "a.txt")));
    }
}
