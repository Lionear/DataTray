using Avalonia.Controls;
using Avalonia.Media;
using DataTray.Sdk.Connections;
using DataTray.Sdk.Schema;

namespace DataTray.Sdk.Ui;

/// <summary>
/// What a tool's document view is given: the connection it was launched on, and the few host actions a
/// long-lived view needs. Deliberately narrower than <see cref="IToolUiContext"/> — there are no field
/// values to read or write, because a document is not collecting input for a run.
/// </summary>
/// <remarks>
/// There is no <c>QueryAsync</c> here on purpose. A document gets the <see cref="Provider"/> and
/// <see cref="Profile"/>, which is what the schema-reading helpers in <c>plugins/Shared.Schema</c> already
/// take, so a view that wants the schema builds a reader rather than hand-writing SQL through the host.
/// </remarks>
public interface IToolDocumentContext
{
    /// <summary>The provider for the launched connection, and a profile with keychain secrets resolved —
    /// the same pair <see cref="Tools.IToolPlugin.ExecuteAsync"/> would have been handed.</summary>
    IDbProvider Provider { get; }

    ConnectionProfile Profile { get; }

    /// <summary>The provider's id ("postgres", "sqlite", …). Carried separately because
    /// <see cref="IDbProvider"/> does not name itself, and the schema-reading helpers in
    /// <c>plugins/Shared.Schema</c> need it to pick a dialect — the same reason
    /// <see cref="Tools.ToolExecutionContext"/> carries it beside the provider.</summary>
    string ProviderId { get; }

    /// <summary>The node the tool was launched on, or null at the connection root.</summary>
    DbNodeRef? Node { get; }

    /// <summary>The plugin's own localizer, for the view's labels.</summary>
    Localization.IPluginLocalizer Localizer { get; }

    /// <summary>Rename the tab. The host seeds it with the tool's title; a view whose content narrows
    /// (a diagram of one schema rather than a database) can say so.</summary>
    void SetTitle(string title);

    /// <summary>Open a query tab on this document's connection and database, pre-filled — the same handoff
    /// a tool run gets through <see cref="Tools.IToolHost.OpenQueryEditor"/>.</summary>
    void OpenQueryEditor(string sql);

    /// <summary>Close this tab.</summary>
    void CloseDocument();

    /// <summary>Show a save-file picker; returns the chosen path, or null if cancelled. The counterpart of
    /// <see cref="IToolUiContext.PickSaveFileAsync"/> — a document that can be saved or exported needs it
    /// as much as a dialog does, and it was simply missing from the first cut of this seam.</summary>
    Task<string?> PickSaveFileAsync(string suggestedName, params string[] extensions);

    /// <summary>Show an open-file picker; returns the chosen path, or null if cancelled.</summary>
    Task<string?> PickOpenFileAsync(params string[] extensions);
}

/// <summary>
/// Optional capability a tool plugin may implement to open as a <b>tab in the main window</b> rather than
/// a dialog. The host builds the tab and hands it the view; the plugin owns everything inside it.
///
/// <para>The distinction is lifetime, not looks. A tool dialog collects input, runs, reports and closes —
/// the host's generic chrome exists for exactly that shape. A document is something the user keeps open
/// while working on something else: an ER diagram (SE-82) is read alongside the queries it explains, and
/// a dialog that has to be dismissed to type a query is the wrong container for it.</para>
/// </summary>
/// <remarks>
/// <para>Same ALC/type-identity contract as the other UI seams (<see cref="ICustomToolUi"/>,
/// <see cref="Extensibility.IPanelPlugin"/>): this assembly and Avalonia are shared across the plugin's
/// load context, so the returned control has one type identity with the host.</para>
///
/// <para>A tool implementing this is never asked for <c>ToolField</c>s and its
/// <see cref="Tools.IToolPlugin.ExecuteAsync"/> is not called — opening the tab <i>is</i> the action.
/// <see cref="Tools.IToolPlugin.Target"/> still gates which nodes and providers offer the menu entry.</para>
///
/// <para><b>Closing.</b> If the returned control implements <see cref="IDisposable"/>, the host disposes it
/// when the tab closes. A document holds things a dialog never does — a schema snapshot, a timer — and
/// without this they would live as long as the app.</para>
///
/// <para><b>Not restored on restart.</b> The host persists query tabs only. A document tab would have to
/// re-read the schema to come back, and restoring it silently on every launch is a cost the user did not
/// ask for; reopening from the menu is one click.</para>
/// </remarks>
public interface IToolDocumentUi
{
    /// <summary>Build the tab's content. Called once, when the tab is opened. Reopening the tool on the
    /// same connection, database and node focuses the existing tab instead of calling this again.</summary>
    Control CreateDocument(IToolDocumentContext context);

    /// <summary>Optional stroked glyph for the tab strip, drawn like the host's own tab icons. Null falls
    /// back to the generic tool glyph, since a plugin cannot reach host icon resources across the ALC.</summary>
    Geometry? Icon => null;
}
