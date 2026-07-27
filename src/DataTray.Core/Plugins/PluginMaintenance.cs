namespace DataTray.Core.Plugins;

/// <summary>
/// Applies the changes the Store staged while the previous run was live. Must run at startup <b>before</b>
/// any plugin is loaded, since the non-collectible load contexts can't swap a plugin's files mid-process
/// (Notes §4.2 — "no real unload"). It:
/// <list type="number">
/// <item>deletes the folders of anything marked <see cref="PluginPendingAction.Remove"/>;</item>
/// <item>promotes every staged <c>&lt;id&gt;.next</c> to <c>&lt;id&gt;</c>, demoting the current copy to a
/// single <c>&lt;id&gt;.prev</c> backup (an update or a rollback both land here);</item>
/// <item>clears the leftover <see cref="PluginPendingAction.Install"/> markers so a fresh install loads
/// normally next time.</item>
/// </list>
/// Best-effort — a single failing folder never blocks startup.
/// </summary>
public static class PluginMaintenance
{
    private const string StagingDir = ".staging";
    private const string NextSuffix = ".next";
    private const string PrevSuffix = ".prev";

    /// <summary>
    /// Runs the staged install/remove work. Returns the ids whose staged <c>&lt;id&gt;.next</c> could not be
    /// promoted (still-locked files, an undeletable slot) so the caller can surface/log them — the update
    /// stays staged and is retried next startup. An empty list means everything applied.
    /// </summary>
    public static IReadOnlyList<string> ApplyPending(IPluginStateStore stateStore, string userRoot)
    {
        // 1. Removals first, so a removed id's staged .next is gone before the swap scan runs.
        foreach (var (id, entry) in stateStore.GetAll())
        {
            if (entry.Pending == PluginPendingAction.Remove)
            {
                TryDelete(Path.Combine(userRoot, id));
                TryDelete(Path.Combine(userRoot, id + NextSuffix));
                TryDelete(Path.Combine(userRoot, id + PrevSuffix));
                stateStore.Remove(id);
            }
        }

        // 2. Promote staged replacements: <id>.next -> <id>, keeping the old <id> as <id>.prev.
        var failed = PromoteStaged(userRoot);

        // 3. Clear the "restart needed" markers left by a fresh install.
        foreach (var (id, entry) in stateStore.GetAll())
        {
            if (entry.Pending == PluginPendingAction.Install)
            {
                stateStore.Save(id, entry with { Pending = PluginPendingAction.None });
            }
        }

        return failed;
    }

    private static IReadOnlyList<string> PromoteStaged(string userRoot)
    {
        if (!Directory.Exists(userRoot))
        {
            return [];
        }

        var failed = new List<string>();

        foreach (var nextDir in Directory.EnumerateDirectories(userRoot))
        {
            var name = Path.GetFileName(nextDir);
            if (name == StagingDir || !name.EndsWith(NextSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var id = name[..^NextSuffix.Length];
            var target = Path.Combine(userRoot, id);
            var prev = target + PrevSuffix;

            try
            {
                if (Directory.Exists(target))
                {
                    // Free the target slot. Preferably demote the current copy to a single .prev backup —
                    // but a stuck backup (an undeletable old .prev, or a demote that fails) must never
                    // block the update forever: fall back to deleting the current copy so the staged
                    // version can still take its place. We lose only the rollback backup, not the update.
                    if (!TryDelete(prev) || !TryMove(target, prev))
                    {
                        if (!TryDelete(target))
                        {
                            failed.Add(id);
                            continue;   // slot still occupied; leave .next staged and retry next startup.
                        }
                    }
                }

                Directory.Move(nextDir, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leave the staged folder in place; the plugin keeps running its current version and the
                // swap is retried next startup rather than half-applied.
                failed.Add(id);
            }
        }

        return failed;
    }

    private static bool TryMove(string from, string to)
    {
        try
        {
            Directory.Move(from, to);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left on disk; the state entry is still dropped so it won't load, and the next
            // install/remove will overwrite it. Don't let a locked file block startup.
            return false;
        }
    }
}
