using System.Text.Json;

namespace DataTray.Sdk.Mcp;

/// <summary>
/// The contract for a <c>type: "mcp"</c> plugin: it contributes MCP <b>tools</b> to the host's single,
/// host-owned MCP server. The plugin never runs the server, never opens a socket, never sees a connection
/// string — the host owns the transport (loopback listener, auth, Origin check, audit) and the
/// authorization boundary (<see cref="IMcpHost"/>). A tool handler can only act through the
/// <see cref="IMcpHost"/> it is handed, so even a third-party tool plugin is confined to the same
/// reachability/write-guard/caps the built-in tools obey. The first-party <c>sql-explorer-mcp</c> plugin
/// ships the four core tools (list_connections/get_schema/run_query/explain_query) this way.
/// </summary>
/// <remarks>
/// The host starts its MCP server only when at least one enabled provider contributes at least one tool —
/// no tools means no listener and no open port.
/// </remarks>
public interface IMcpToolProvider
{
    /// <summary>The tools this plugin contributes to the host MCP server.</summary>
    IReadOnlyList<McpToolDefinition> GetTools();
}

/// <summary>
/// One MCP tool a plugin contributes. <see cref="InputSchema"/> is a JSON-Schema object (as text)
/// describing the tool's arguments; <see cref="Handler"/> receives the parsed arguments and the host's
/// <see cref="IMcpHost"/>, does its work only through that host, and returns a plain object the host
/// serializes as the tool result. A handler signals refusal by throwing <see cref="McpAccessException"/>
/// (or any exception) — the host maps it to an MCP tool error.
/// </summary>
public sealed record McpToolDefinition(
    string Name,
    string Description,
    string InputSchema,
    Func<JsonElement, IMcpHost, CancellationToken, Task<object?>> Handler);
