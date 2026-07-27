using System.Security.Cryptography;
using System.Text;
using DataTray.Sdk.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DataTray.Mcp.Hosting;

/// <summary>Everything the host-owned MCP server needs, read from the top-level MCP settings.</summary>
public sealed record McpServerOptions(
    bool Enabled,
    int Port,
    bool RequireAuth,
    string? Token);

/// <summary>
/// The host-owned MCP server. It — not any plugin — opens the loopback HTTP listener, enforces the
/// transport-level security controls (hardcoded 127.0.0.1 bind, Origin/Host verification that never
/// depends on the auth toggle, optional constant-time bearer-token auth), and serves the tools contributed
/// by enabled MCP plugins. It only starts when enabled AND at least one tool exists — no tools means no
/// open port. All data access still goes through <see cref="IMcpHost"/>, which owns authorization.
/// </summary>
public sealed class McpServer(IMcpHost host, Action<string> audit)
{
    private WebApplication? _app;

    public bool IsRunning => _app is not null;

    /// <summary>Start serving <paramref name="tools"/> under <paramref name="options"/>. No-op (with a
    /// logged reason) when disabled or when there are no tools. Safe to call when already running (restart).</summary>
    public async Task StartAsync(McpServerOptions options, IReadOnlyList<McpToolDefinition> tools, CancellationToken ct)
    {
        await StopAsync(ct);

        if (!options.Enabled)
        {
            audit("[MCP] server not started: disabled.");
            return;
        }

        if (tools.Count == 0)
        {
            audit("[MCP] server not started: no tools registered.");
            return;
        }

        var serverTools = tools.Select(t => (McpServerTool)new PluginMcpTool(t, host)).ToList();

        var builder = WebApplication.CreateSlimBuilder();
        // Hardcoded loopback bind — IPv4 127.0.0.1 only. There is deliberately no setting that can widen
        // this to 0.0.0.0 (CRIT-3): the port must never be reachable off this machine.
        builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");
        builder.Logging.ClearProviders();

        builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "DataTray", Version = "1.0.0" })
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools(serverTools);

        var app = builder.Build();

        // Transport security — runs BEFORE the MCP endpoint, and the Origin/Host checks are unconditional
        // (they do not weaken when RequireAuth is off; when auth is off they are the only remaining defence
        // against a local browser page fetching the port — HIGH-4/CRIT-3).
        app.Use(async (context, next) =>
        {
            var request = context.Request;

            // Host header must be loopback — blunts DNS-rebinding (a rebound hostname resolving to 127.0.0.1).
            var reqHost = request.Host.Host;
            if (reqHost is not ("127.0.0.1" or "localhost" or "[::1]" or "::1"))
            {
                await Reject(context, StatusCodes.Status403Forbidden, "Bad host.");
                audit($"[MCP DENY] transport: bad host '{reqHost}'");
                return;
            }

            // A browser cross-origin fetch always carries an Origin header; a real MCP client does not. Any
            // Origin present is refused, and no CORS headers are ever emitted (no Access-Control-Allow-Origin).
            if (!string.IsNullOrEmpty(request.Headers.Origin))
            {
                await Reject(context, StatusCodes.Status403Forbidden, "Origin not allowed.");
                audit($"[MCP DENY] transport: origin '{request.Headers.Origin}'");
                return;
            }

            // POST bodies must be JSON (the MCP Streamable-HTTP request content type).
            if (HttpMethods.IsPost(request.Method)
                && request.ContentLength is > 0
                && request.ContentType is { } ctype
                && !ctype.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            {
                await Reject(context, StatusCodes.Status415UnsupportedMediaType, "JSON required.");
                return;
            }

            if (options.RequireAuth && !AuthOk(request, options.Token))
            {
                await Reject(context, StatusCodes.Status401Unauthorized, "Unauthorized.");
                audit("[MCP DENY] transport: missing/invalid bearer token");
                return;
            }

            await next();
        });

        app.MapMcp();

        await app.StartAsync(ct);
        _app = app;
        audit($"[MCP] server started on 127.0.0.1:{options.Port} ({(options.RequireAuth ? "auth" : "NO-AUTH")}), {serverTools.Count} tool(s).");
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_app is { } app)
        {
            _app = null;
            await app.StopAsync(ct);
            await app.DisposeAsync();
            audit("[MCP] server stopped.");
        }
    }

    // Constant-time bearer-token check (no early-out that could leak length/positions via timing).
    private static bool AuthOk(HttpRequest request, string? expected)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return false; // auth required but no token configured → fail closed
        }

        string? header = request.Headers.Authorization;
        const string prefix = "Bearer ";
        if (header is null || !header.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var provided = Encoding.UTF8.GetBytes(header[prefix.Length..].Trim());
        var wanted = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(provided, wanted);
    }

    private static async Task Reject(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync($$"""{"error":"{{message}}"}""");
    }
}
