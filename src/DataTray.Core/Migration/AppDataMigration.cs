namespace DataTray.Core.Migration;

/// <summary>What <see cref="AppDataMigration.Migrate"/> did, so startup can log it.</summary>
public enum AppDataMigrationOutcome
{
    /// <summary>No pre-rename folder — a fresh install, or one that already migrated and cleaned up.</summary>
    NothingToDo,

    /// <summary>The new root already existed. Left untouched: it is the newer of the two.</summary>
    AlreadyMigrated,

    /// <summary>The pre-rename folder was copied to the new root.</summary>
    Copied,

    /// <summary>Copy failed part-way. The partial copy is removed; the old folder is still intact.</summary>
    Failed,
}

public sealed record AppDataMigrationResult(
    AppDataMigrationOutcome Outcome,
    int FilesCopied = 0,
    Exception? Error = null);

/// <summary>
/// Copies the pre-rename data folder (<c>Lionear/SqlExplorer</c>) to the DataTray root on first run
/// after the rename (SE-202/SE-206).
/// </summary>
/// <remarks>
/// <para>
/// Copy, not move: a tester who goes back to a SQL Explorer build still finds their connections where
/// that build looks for them. The cost is that the two roots then diverge — edits made in DataTray are
/// invisible to the old build and vice versa — so the old folder gets a breadcrumb file explaining what
/// happened and which folder is now live.
/// </para>
/// <para>
/// Best-effort by contract: this runs before anything else at startup, and no failure here may prevent
/// the app from opening. A failed copy is rolled back so the next start retries from a clean slate
/// rather than inheriting half a data folder — a half-copied <c>connections.json</c> is worse than none,
/// because the app would treat it as authoritative.
/// </para>
/// </remarks>
public static class AppDataMigration
{
    internal const string BreadcrumbName = "MOVED-TO-DATATRAY.txt";

    /// <summary>Migrates using the real roots. Never throws.</summary>
    public static AppDataMigrationResult Migrate() => Migrate(AppPaths.LegacyRoot, AppPaths.Root);

    /// <summary>Testable overload: same logic against arbitrary folders. Never throws.</summary>
    public static AppDataMigrationResult Migrate(string legacyRoot, string newRoot)
    {
        try
        {
            if (!Directory.Exists(legacyRoot))
            {
                return new AppDataMigrationResult(AppDataMigrationOutcome.NothingToDo);
            }

            // The new root winning is deliberate. Once DataTray has written anything, it is the live copy;
            // re-running the migration would overwrite current data with a pre-rename snapshot. That makes
            // this idempotent, which matters because it runs on every single startup.
            if (Directory.Exists(newRoot))
            {
                return new AppDataMigrationResult(AppDataMigrationOutcome.AlreadyMigrated);
            }

            var copied = CopyTree(legacyRoot, newRoot);
            WriteBreadcrumb(legacyRoot, newRoot);
            return new AppDataMigrationResult(AppDataMigrationOutcome.Copied, copied);
        }
        catch (Exception ex)
        {
            TryRollback(newRoot);
            return new AppDataMigrationResult(AppDataMigrationOutcome.Failed, Error: ex);
        }
    }

    private static int CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        var copied = 0;

        foreach (var file in Directory.EnumerateFiles(source))
        {
            // Don't copy a breadcrumb from an earlier attempt into the new root.
            if (Path.GetFileName(file) == BreadcrumbName)
            {
                continue;
            }

            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
            copied++;
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            copied += CopyTree(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }

        return copied;
    }

    private static void WriteBreadcrumb(string legacyRoot, string newRoot)
    {
        var text =
            $"""
             This folder belonged to SQL Explorer, which is now called DataTray.

             Its contents were copied to:
                 {newRoot}

             DataTray reads and writes that folder from now on; this one is no longer updated. It was kept
             rather than deleted so an older SQL Explorer build still starts with your connections intact.
             Once you are sure you won't go back, this folder can be deleted.

             Note that the two are now independent: changes made in DataTray do not appear here.
             """;

        File.WriteAllText(Path.Combine(legacyRoot, BreadcrumbName), text);
    }

    private static void TryRollback(string newRoot)
    {
        try
        {
            if (Directory.Exists(newRoot))
            {
                Directory.Delete(newRoot, recursive: true);
            }
        }
        catch
        {
            // Rollback is a courtesy. If it fails the next start sees an existing new root and reports
            // AlreadyMigrated — wrong, but the old folder is still there and recoverable by hand.
        }
    }
}
