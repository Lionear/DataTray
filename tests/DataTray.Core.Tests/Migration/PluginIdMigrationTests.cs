using System.Text.Json;
using System.Text.Json.Nodes;
using DataTray.Core.Migration;

namespace DataTray.Core.Tests.Migration;

/// <summary>
/// Covers the sql-explorer-mcp -> datatray-mcp plugin id rename (SE-206). A plugin id keys enabled state,
/// settings, version pin and private data, so getting this wrong silently resets a plugin to defaults.
/// </summary>
public sealed class PluginIdMigrationTests : IDisposable
{
    private const string OldId = "sql-explorer-mcp";
    private const string NewId = "datatray-mcp";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "datatray-pluginid-tests-" + Guid.NewGuid().ToString("N"));

    public PluginIdMigrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Leaked temp folders are not worth failing a test over.
        }
    }

    private void WriteJson(string name, string json) => File.WriteAllText(Path.Combine(_root, name), json);

    private JsonObject ReadJson(string name) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(_root, name)))!;

    [Fact]
    public void Rekeys_state_settings_and_pins()
    {
        WriteJson("plugins-state.json", $$$"""{"{{{OldId}}}":{"Enabled":false,"Pending":"none"},"redis":{"Enabled":true}}""");
        WriteJson("plugin-settings.json", $$$"""{"{{{OldId}}}":{"port":9001}}""");
        WriteJson("plugin-pins.json", $$$"""{"{{{OldId}}}":"0.2.0"}""");

        var result = PluginIdMigration.Migrate(_root, OldId, NewId);

        Assert.Equal(3, result.Changed.Count);
        Assert.False((bool)ReadJson("plugins-state.json")[NewId]!["Enabled"]!);
        Assert.Equal(9001, (int)ReadJson("plugin-settings.json")[NewId]!["port"]!);
        Assert.Equal("0.2.0", (string?)ReadJson("plugin-pins.json")[NewId]);
    }

    /// <summary>The disabled state is the one users notice: a reset turns a plugin they switched off back on.</summary>
    [Fact]
    public void Carries_the_disabled_state_across_rather_than_resetting_it()
    {
        WriteJson("plugins-state.json", $$$"""{"{{{OldId}}}":{"Enabled":false,"Pending":"none"}}""");

        PluginIdMigration.Migrate(_root, OldId, NewId);

        var state = ReadJson("plugins-state.json");
        Assert.False(state.ContainsKey(OldId));
        Assert.False((bool)state[NewId]!["Enabled"]!);
    }

    [Fact]
    public void Leaves_other_plugins_untouched()
    {
        WriteJson("plugins-state.json", $$$"""{"{{{OldId}}}":{"Enabled":true},"redis":{"Enabled":false}}""");

        PluginIdMigration.Migrate(_root, OldId, NewId);

        Assert.False((bool)ReadJson("plugins-state.json")["redis"]!["Enabled"]!);
    }

    [Fact]
    public void Renames_the_plugin_and_plugin_data_folders()
    {
        Directory.CreateDirectory(Path.Combine(_root, "plugins", OldId));
        Directory.CreateDirectory(Path.Combine(_root, "plugin-data", OldId));
        File.WriteAllText(Path.Combine(_root, "plugin-data", OldId, "cache.json"), "{}");

        PluginIdMigration.Migrate(_root, OldId, NewId);

        Assert.True(Directory.Exists(Path.Combine(_root, "plugins", NewId)));
        Assert.True(File.Exists(Path.Combine(_root, "plugin-data", NewId, "cache.json")));
        Assert.False(Directory.Exists(Path.Combine(_root, "plugin-data", OldId)));
    }

    [Fact]
    public void Is_idempotent()
    {
        WriteJson("plugin-settings.json", $$$"""{"{{{OldId}}}":{"port":9001}}""");

        PluginIdMigration.Migrate(_root, OldId, NewId);
        var second = PluginIdMigration.Migrate(_root, OldId, NewId);

        Assert.False(second.DidAnything);
        Assert.Equal(9001, (int)ReadJson("plugin-settings.json")[NewId]!["port"]!);
    }

    /// <summary>
    /// Once the plugin has written state under its new id that entry is live, and a leftover old key
    /// must not overwrite it.
    /// </summary>
    [Fact]
    public void Never_overwrites_state_already_held_by_the_new_id()
    {
        WriteJson("plugin-settings.json", $$$"""{"{{{OldId}}}":{"port":1},"{{{NewId}}}":{"port":2}}""");

        PluginIdMigration.Migrate(_root, OldId, NewId);

        Assert.Equal(2, (int)ReadJson("plugin-settings.json")[NewId]!["port"]!);
    }

    [Fact]
    public void A_fresh_install_has_nothing_to_migrate()
    {
        var result = PluginIdMigration.Migrate(_root, OldId, NewId);

        Assert.False(result.DidAnything);
    }

    [Fact]
    public void A_corrupt_file_is_skipped_rather_than_throwing()
    {
        WriteJson("plugin-settings.json", "{ not json");
        WriteJson("plugin-pins.json", $$$"""{"{{{OldId}}}":"0.2.0"}""");

        var result = PluginIdMigration.Migrate(_root, OldId, NewId);

        Assert.Equal(["plugin-pins.json"], result.Changed);
    }

    [Fact]
    public void Writes_valid_indented_json_back()
    {
        WriteJson("plugin-pins.json", $$$"""{"{{{OldId}}}":"0.2.0"}""");

        PluginIdMigration.Migrate(_root, OldId, NewId);

        var text = File.ReadAllText(Path.Combine(_root, "plugin-pins.json"));
        Assert.Contains("\n", text, StringComparison.Ordinal);
        Assert.Equal("0.2.0", JsonDocument.Parse(text).RootElement.GetProperty(NewId).GetString());
    }
}
