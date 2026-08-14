namespace DataTray.Sdk.Tools;

/// <summary>
/// Versioning gate between the host and non-provider plugins (tools + standing-subsystem extensions),
/// separate from <c>ProviderHostApi</c> so the plugin kinds evolve independently. A plugin's
/// <c>plugin.json</c> declares the version it was built for; the loader refuses one this host cannot satisfy.
/// </summary>
public static class ToolHostApi
{
    // v2 (2026-07-14): added IToolPlugin.MenuPath (default []) — tools can declare a nested submenu path
    //                  (Tools ▸ Shrink ▸ Database) instead of only appearing flat under Tools. Also
    //                  IToolUiContext.QueryAsync (Route-B live-data hook). Both additive.
    // v3 (2026-07-19): the extensibility family (SE-164) — the standing-subsystem plugin type
    //                  (type: "extension"), loaded via this same contract. Adds, in DataTray.Sdk.Extensibility:
    //                  ISubsystemPlugin + IPluginRuntimeContext + IPluginStorage + the capability model
    //                  (PluginCapabilities), IManagedConnections (incl. All()) + ManagedConnectionInfo, IHostUi,
    //                  and the contribution seams IPanelPlugin / IMenuPlugin / IBackgroundPlugin /
    //                  IConnectionMenuPlugin. Additive: classic tools are untouched.
    // v4 (2026-07-20): the "services" capability (SE-171) — a plugin that declares it gets its
    //                  marker-annotated services (ISingletonService/ITransientService/IScopedService, in
    //                  DataTray.Sdk.Extensibility) auto-registered in the host container, and a scoped
    //                  resolver on IPluginRuntimeContext.Services (new member). Additive for existing plugins;
    //                  a plugin that *uses* the seam must declare v4 so an older host refuses it rather than
    //                  crashing on the missing member.
    //   also in v4 (2026-07-20): the "providers" capability (SE-166) — a plugin that declares it gets a
    //                  read-only IProviderCatalog on IPluginRuntimeContext.Providers (new member) listing
    //                  installed providers that declared a container recipe (IDbProvider.ContainerRecipe). Lets
    //                  the Docker plugin containerise third-party engines. Folded into the still-unreleased v4
    //                  rather than opening v5: the whole 0.4.0 dev cycle accumulates additive subsystem surface
    //                  under one version, bumped once at release. Additive — a plugin without the capability
    //                  gets null and degrades to its built-in table.
    //   also in v4 (2026-07-20): the connection-picker seam (SE-99) — a new ToolFieldType.ConnectionPicker
    //                  lets a tool take a *second* saved connection, and IToolHost gains ListConnections() +
    //                  OpenConnection(id) (returning a runnable ToolConnection) so a cross-connection tool
    //                  (SchemaDiff, CopyTable) can open it. A companion ToolFieldType.DatabasePicker picks a
    //                  database on that connection (IToolHost.ListDatabasesAsync + OpenConnection's database
    //                  arg), since a server hosts many. IToolHost.OpenQueryEditor(sql) lets a tool hand its
    //                  generated SQL to a new query tab on the primary connection instead of running DDL itself
    //                  (SchemaDiff uses this). Default interface impls (empty/null) keep older hosts and
    //                  non-dialog IToolHost implementors compiling; folded into the unreleased v4.
    // v5 (2026-07-21): Copy Table (SE-188/SE-100). IToolHost gains OpenQueryEditorOn(connectionId, database,
    //                  sql) — the destination counterpart of OpenQueryEditor, so a copy/migration tool can
    //                  script to the *picked* connection rather than the launched one — and SetPluginSetting(
    //                  key, value), the write counterpart of GetPluginSetting, so a tool can remember a choice
    //                  (Copy Table remembers its last run/script mode) across runs. Both are additive default
    //                  no-ops. New number rather than a fold-in because v4 shipped in 0.4.0 — folding post-release
    //                  surface into a released number is the SE-166 crash trap. MinimumSupported stays 1.
    //   also in v5 (2026-07-21): IToolUiContext (Route B) gains ListConnections() + ListDatabasesAsync(id, ct),
    //                  mirroring the IToolHost pickers, so a tool's own view can build a destination
    //                  connection/database dropdown (Copy Table's custom view). Default impls (empty) keep
    //                  existing custom views compiling.
    //   also in v5 (2026-07-22): the lifecycle-owning Route-B view — IToolDialogLifecycle (+ ToolRunOutcome)
    //                  in DataTray.Sdk.Ui. A custom view that implements it renders the run's progress and
    //                  completion itself and the host hides its generic checklist/log/progress bar/action bar.
    //                  Companion additions: IToolUiContext.Localizer/RunAsync()/CancelRun()/CloseDialog() (so
    //                  the view can drive the run from its own buttons) and ToolProgress.Detail (the short
    //                  right-aligned note per step). All additive defaults; a view that ignores them keeps the
    //                  host-rendered chrome. Folded into the still-unreleased v5.
    // v6 (2026-07-31): tool documents (SE-216). IToolDocumentUi + IToolDocumentContext in DataTray.Sdk.Ui:
    //                  a tool that implements it opens as a tab in the main window instead of a dialog, and
    //                  owns the whole tab's content. Needed because the ER diagram (SE-82) is read alongside
    //                  the queries it explains — a dialog that must be dismissed to type a query is the wrong
    //                  container. Purely additive (a new optional interface, discovered with an is-check); no
    //                  existing tool is affected. A new number rather than a fold-in because v5 shipped —
    //                  copy-table 0.3.0 declares 5 — and folding post-release surface into a released number
    //                  is the SE-166 crash trap. MinimumSupported stays 1.
    //   also in v6 (2026-07-31): IToolDocumentContext gains PickSaveFileAsync/PickOpenFileAsync, mirroring
    //                  IToolUiContext's. A document that can be saved, opened or exported (SE-225/SE-226)
    //                  needs a file picker as much as a dialog does; the first cut of the seam simply
    //                  lacked it. Folded into 6 rather than given a 7 because 6 has not shipped — the
    //                  SE-166 trap is folding into a *released* number, not an unreleased one.
    // v7 (2026-08-13): ToolExecutionContext gains NodePath — the ancestry from the connection root down to
    //                  the launch node, the same list providers already receive. Needed by the index tools
    //                  (SE-249): an index is named within its table and an "Indexes" folder is named
    //                  nothing at all, so a tool that only knows a kind and a name cannot tell which table
    //                  it was asked to act on, and guessing from the index name is wrong the moment two
    //                  tables share one. Additive: an optional constructor parameter defaulting to empty,
    //                  and a tool that ignores it is unaffected. MinimumSupported stays 1.
    //   also in v7 (2026-08-13): IToolPlugin.IsActivityMonitor (SE-251) — a tool may declare that it is the
    //                  connection's Activity Monitor, so the host's existing "Activity Monitor…" item opens
    //                  it and the tool is left out of the node's Tools submenu. SE-248 moved SQL Server's
    //                  monitor into a plugin and, with it, one level deeper in the menu; a feature changing
    //                  owner should not change place. Folded into 7 rather than given an 8 because 7 has
    //                  not shipped — v0.7.0 (2026-07-30) carries tool API 5. Additive default false.
    //   also in v7 (2026-08-13): IToolPlugin.IsNodeAction (SE-253) — the general form of the above, for the
    //                  case where the host has no menu item to redirect. A tool that is one of the node's own
    //                  actions (SE-249's Rebuild/Reorganize/Disable/Drop on an index) renders directly on the
    //                  node's context menu instead of inside its Tools submenu. Same reasoning as SE-251 and
    //                  the same fold-in rule: 7 has not shipped. Additive default false.
    public const int Version = 7;

    /// <summary>Oldest plugin ABI this host still loads. Every bump has been additive (v2 tool defaults, v3
    /// extensibility seams, v4 the services + providers capabilities), so older tools keep loading on a newer
    /// host.</summary>
    public const int MinimumSupported = 1;

    public static bool IsCompatible(int pluginVersion) =>
        pluginVersion >= MinimumSupported && pluginVersion <= Version;
}
