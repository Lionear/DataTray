using DataTray.Core.Migration;

namespace DataTray.Core.Tests.Migration;

/// <summary>
/// Covers the copy of the pre-rename app data folder (SE-206). The stakes here are a user's connections
/// and query history, so the cases that matter are the ones where the migration must decline to act:
/// re-running it must never overwrite live data, and a failure must never leave a half-populated root
/// behind for the app to treat as authoritative.
/// </summary>
public sealed class AppDataMigrationTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), "datatray-migration-tests-" + Guid.NewGuid().ToString("N"));

    private string Legacy => Path.Combine(_tempRoot, "SqlExplorer");

    private string Current => Path.Combine(_tempRoot, "DataTray");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // A leaked temp folder is not worth failing a test over.
        }
    }

    [Fact]
    public void Copies_files_and_nested_folders_from_the_legacy_root()
    {
        Directory.CreateDirectory(Path.Combine(Legacy, "plugins", "redis"));
        File.WriteAllText(Path.Combine(Legacy, "connections.json"), """{"connections":[]}""");
        File.WriteAllText(Path.Combine(Legacy, "plugins", "redis", "plugin.json"), "{}");

        var result = AppDataMigration.Migrate(Legacy, Current);

        Assert.Equal(AppDataMigrationOutcome.Copied, result.Outcome);
        Assert.Equal(2, result.FilesCopied);
        Assert.Equal("""{"connections":[]}""", File.ReadAllText(Path.Combine(Current, "connections.json")));
        Assert.True(File.Exists(Path.Combine(Current, "plugins", "redis", "plugin.json")));
    }

    [Fact]
    public void Leaves_the_legacy_root_in_place_so_an_older_build_still_starts()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "connections.json"), "{}");

        AppDataMigration.Migrate(Legacy, Current);

        Assert.True(File.Exists(Path.Combine(Legacy, "connections.json")));
    }

    [Fact]
    public void Drops_a_breadcrumb_naming_the_folder_that_is_now_live()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "settings.json"), "{}");

        AppDataMigration.Migrate(Legacy, Current);

        var breadcrumb = File.ReadAllText(Path.Combine(Legacy, "MOVED-TO-DATATRAY.txt"));
        Assert.Contains(Current, breadcrumb, StringComparison.Ordinal);
    }

    /// <summary>
    /// The migration runs on every startup, so this is the common case after the first run — and the one
    /// where acting would destroy data by restoring a pre-rename snapshot over the live folder.
    /// </summary>
    [Fact]
    public void Never_overwrites_an_existing_root()
    {
        Directory.CreateDirectory(Legacy);
        Directory.CreateDirectory(Current);
        File.WriteAllText(Path.Combine(Legacy, "connections.json"), "stale");
        File.WriteAllText(Path.Combine(Current, "connections.json"), "live");

        var result = AppDataMigration.Migrate(Legacy, Current);

        Assert.Equal(AppDataMigrationOutcome.AlreadyMigrated, result.Outcome);
        Assert.Equal("live", File.ReadAllText(Path.Combine(Current, "connections.json")));
    }

    [Fact]
    public void Is_idempotent_across_repeated_runs()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "connections.json"), "original");

        AppDataMigration.Migrate(Legacy, Current);
        File.WriteAllText(Path.Combine(Current, "connections.json"), "edited since");
        var second = AppDataMigration.Migrate(Legacy, Current);

        Assert.Equal(AppDataMigrationOutcome.AlreadyMigrated, second.Outcome);
        Assert.Equal("edited since", File.ReadAllText(Path.Combine(Current, "connections.json")));
    }

    [Fact]
    public void A_fresh_install_has_nothing_to_migrate()
    {
        var result = AppDataMigration.Migrate(Legacy, Current);

        Assert.Equal(AppDataMigrationOutcome.NothingToDo, result.Outcome);
        Assert.False(Directory.Exists(Current));
    }

    /// <summary>
    /// A partial copy must not survive: the next startup would find an existing root, report
    /// AlreadyMigrated and run against whatever fraction of the data happened to land.
    /// </summary>
    [Fact]
    public void Rolls_back_a_partial_copy_so_the_next_start_retries_cleanly()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "connections.json"), "{}");

        // A file where the destination directory needs to go: creating it throws mid-copy.
        Directory.CreateDirectory(Path.GetDirectoryName(Current)!);
        File.WriteAllText(Current, "not a directory");

        var result = AppDataMigration.Migrate(Legacy, Current);

        Assert.Equal(AppDataMigrationOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Error);
        Assert.True(File.Exists(Path.Combine(Legacy, "connections.json")));
    }

    [Fact]
    public void Does_not_carry_an_earlier_breadcrumb_into_the_new_root()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(Legacy, "MOVED-TO-DATATRAY.txt"), "from an earlier attempt");

        AppDataMigration.Migrate(Legacy, Current);

        Assert.False(File.Exists(Path.Combine(Current, "MOVED-TO-DATATRAY.txt")));
    }
}
