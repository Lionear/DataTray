using DataTray.Sdk.Localization;

namespace DataTray.Sdk.Extensibility;

/// <summary>What the host hands a query pane: its document, the schema, dialect facts and persisted state.</summary>
public interface IQueryPaneContext
{
    /// <summary>The query tab the pane lives in.</summary>
    IQueryDocument Document { get; }

    /// <summary>Schema of the tab's connection/database (see <see cref="IQuerySchema"/>); null when the plugin
    /// does not declare the <c>schema</c> capability.</summary>
    IQuerySchema? Schema { get; }

    /// <summary>Dialogs and notifications.</summary>
    IHostUi HostUi { get; }

    /// <summary>Localizer for the plugin's own strings.</summary>
    IPluginLocalizer Localizer { get; }

    /// <summary>Opaque pane state, persisted with the open tab and handed back after a restart.</summary>
    string? State { get; set; }

    /// <summary>The server version string of the tab's connection, or null when unknown.</summary>
    Task<string?> GetServerVersionAsync(CancellationToken ct = default);

    /// <summary>Reserved words of the tab's SQL dialect.</summary>
    IReadOnlyList<string> DialectKeywords { get; }

    /// <summary>Built-in function names of the tab's SQL dialect.</summary>
    IReadOnlyList<string> DialectFunctions { get; }

    /// <summary>Quotes an identifier the way the tab's dialect requires.</summary>
    string QuoteIdentifier(string identifier);
}
