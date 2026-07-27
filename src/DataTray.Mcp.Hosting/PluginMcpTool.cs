using System.Text.Json;
using DataTray.Sdk.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DataTray.Mcp.Hosting;

/// <summary>
/// Adapts one plugin-contributed <see cref="McpToolDefinition"/> into the MCP SDK's <see cref="McpServerTool"/>
/// so the host-owned server can serve it. The tool's JSON-Schema string becomes the protocol input schema,
/// and each call parses the arguments into a <see cref="JsonElement"/>, invokes the plugin handler with the
/// host's <see cref="IMcpHost"/> (the only thing the handler can act through), and serializes the result. A
/// refusal (<see cref="McpAccessException"/>) or any error becomes an MCP tool error, never a transport crash.
/// </summary>
public sealed class PluginMcpTool : McpServerTool
{
    private readonly McpToolDefinition _definition;
    private readonly IMcpHost _host;

    public PluginMcpTool(McpToolDefinition definition, IMcpHost host)
    {
        _definition = definition;
        _host = host;
        ProtocolTool = new Tool
        {
            Name = definition.Name,
            Description = definition.Description,
            InputSchema = JsonSerializer.Deserialize<JsonElement>(definition.InputSchema)
        };
    }

    public override Tool ProtocolTool { get; }

    public override IReadOnlyList<object> Metadata => [];

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken)
    {
        // Arguments arrive as a name→JsonElement map; re-materialise them as a single JSON object for the
        // plugin handler (transport-neutral — the handler never sees the SDK's request type).
        var arguments = request.Params?.Arguments is { } dict
            ? JsonSerializer.SerializeToElement(dict)
            : JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>());

        try
        {
            var result = await _definition.Handler(arguments, _host, cancellationToken);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result, JsonOptions) }]
            };
        }
        catch (McpAccessException ex)
        {
            return Error(ex.Message);
        }
        catch (OperationCanceledException)
        {
            return Error("The query was cancelled (timeout or shutdown).");
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    private static CallToolResult Error(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
}
