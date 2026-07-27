using System.Text.Json;
using System.Text.Json.Nodes;

namespace DataTray.Core.Migration;

public sealed record PluginIdMigrationResult(IReadOnlyList<string> Changed)
{
    public bool DidAnything => Changed.Count > 0;
}

/// <summary>
/// Renames the first-party MCP plugin's id from <c>sql-explorer-mcp</c> to <c>datatray-mcp</c> (SE-206).
/// </summary>
/// <remarks>
/// <para>
/// A plugin id is not a label: it keys the enabled/disabled state, the plugin's settings, its version pin
/// and its private data folder. Changing the id in <c>plugin.json</c> alone would present the plugin as
/// brand new — default settings, default enabled state — while the old entries linger forever under a
/// name nothing recognises.
/// </para>
/// <para>
/// Runs at startup after <see cref="AppDataMigration"/> and before anything reads those files. Rewrites
/// only when the old key is present and the new one is not, so it is idempotent and never overwrites
/// state that already belongs to the new id. Best-effort throughout: a failure here must not stop the
/// app from opening, and the worst case is a plugin that comes back with default settings.
/// </para>
/// </remarks>
public static class PluginIdMigration
{
    private const string OldId = "sql-explorer-mcp";
    private const string NewId = "datatray-mcp";

    /// <summary>Files that are a flat <c>{ pluginId: value }</c> map.</summary>
    private static readonly string[] KeyedFiles =
        ["plugins-state.json", "plugin-settings.json", "plugin-pins.json"];

    /// <summary>Folders with one subfolder per plugin id.</summary>
    private static readonly string[] KeyedDirs = ["plugins", "plugin-data"];

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Migrates under the real app data root. Never throws.</summary>
    public static PluginIdMigrationResult Migrate() => Migrate(AppPaths.Root, OldId, NewId);

    /// <summary>Testable overload against an arbitrary root. Never throws.</summary>
    public static PluginIdMigrationResult Migrate(string root, string oldId, string newId)
    {
        var changed = new List<string>();

        foreach (var file in KeyedFiles)
        {
            if (TryRekeyFile(Path.Combine(root, file), oldId, newId))
            {
                changed.Add(file);
            }
        }

        foreach (var dir in KeyedDirs)
        {
            if (TryRenameDir(Path.Combine(root, dir), oldId, newId))
            {
                changed.Add($"{dir}/{oldId}");
            }
        }

        return new PluginIdMigrationResult(changed);
    }

    private static bool TryRekeyFile(string path, string oldId, string newId)
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject map)
            {
                return false;
            }

            // The new key winning matters: once the plugin has written state under its new id, that is
            // the live entry and the stale one must not clobber it.
            if (!map.TryGetPropertyValue(oldId, out var value) || map.ContainsKey(newId))
            {
                return false;
            }

            map.Remove(oldId);
            map[newId] = value?.DeepClone();
            File.WriteAllText(path, map.ToJsonString(WriteOptions));
            return true;
        }
        catch
        {
            // A corrupt or unreadable file degrades to "not migrated" — the same way the stores
            // themselves degrade to empty rather than crashing.
            return false;
        }
    }

    private static bool TryRenameDir(string parent, string oldId, string newId)
    {
        try
        {
            var source = Path.Combine(parent, oldId);
            var destination = Path.Combine(parent, newId);
            if (!Directory.Exists(source) || Directory.Exists(destination))
            {
                return false;
            }

            Directory.Move(source, destination);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
