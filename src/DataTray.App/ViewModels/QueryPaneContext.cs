using DataTray.Core.Plugins;
using DataTray.Sdk.Extensibility;
using DataTray.Sdk.Localization;

namespace DataTray.App.ViewModels;

/// <summary>What one open pane gets from its query tab. Owns the schema it was handed and disposes it on close.</summary>
internal sealed class QueryPaneContext(DocumentViewModel document, SubsystemQueryPane pane, IHostUi hostUi, IQuerySchema? schema)
    : IQueryPaneContext, IDisposable
{
    public IQueryDocument Document => document;

    public IQuerySchema? Schema => schema;

    public IHostUi HostUi => hostUi;

    public IPluginLocalizer Localizer => pane.Localizer;

    public string? State
    {
        get => document.GetPaneState(pane.PluginId);
        set => document.SetPaneState(pane.PluginId, value);
    }

    public Task<string?> GetServerVersionAsync(CancellationToken ct = default) =>
        Task.FromResult(document.ServerVersion);

    public IReadOnlyList<string> DialectKeywords => document.Dialect.Keywords.ToList();

    public IReadOnlyList<string> DialectFunctions => document.Dialect.Functions.Select(f => f.Name).ToList();

    public string QuoteIdentifier(string identifier) => document.Dialect.QuoteIdentifier(identifier);

    public void Dispose() => (schema as IDisposable)?.Dispose();
}
