using System.Text.Json;

namespace DataTray.Extensions.QueryBuilder.Tests;

public class PluginManifestTests
{
    [Fact]
    public void PluginManifest_DeclaresHostApi9AndCapabilities()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "plugin.json"));
        var root = JsonDocument.Parse(json).RootElement;

        var actual = new object[]
        {
            root.GetProperty("id").GetString()!,
            root.GetProperty("type").GetString()!,
            root.GetProperty("hostApiVersion").GetInt32(),
            string.Join(",", root.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString())),
        };

        Assert.Equal(new object[] { "query-builder", "extension", 9, "query-pane,schema,storage" }, actual);
    }
}
