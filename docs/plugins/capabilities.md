← [Plugins overview](../PLUGINS.md)

## Optional capabilities (settings UI & keyboard shortcuts)

These are **optional interfaces any plugin may add** — provider or tool. The host
detects each with an `is`-check at load (the same pattern for all of them), so a
plugin opts in simply by implementing the interface; nothing changes in the
manifest. A plugin can implement several at once — `TemplateProvider`
(`plugins/Providers.Template`) implements all three as the reference example.

### Persistent settings — Route A (`IPluginSettings`)

Plugin-wide values the user sets once (a path to an external binary, a default
folder) that apply to every use of the plugin. Declare fields; the host renders a
generic form in Settings ▸ Plugins and persists the values to
`plugin-settings.json` keyed by plugin id.

```csharp
public sealed class MyTool : IToolPlugin, IPluginSettings
{
    public IReadOnlyList<PluginSettingField> SettingsFields { get; } =
    [
        new("binaryPath", "Executable path", PluginSettingFieldType.File, Group: "Paths"),
        new("outputDir",  "Default output folder", PluginSettingFieldType.Folder, Group: "Paths"),
        new("logLevel",   "Log level", PluginSettingFieldType.Choice,
            Default: "info", Choices: ["debug", "info", "warn", "error"], Group: "Behaviour"),
        new("verbose",    "Verbose output", PluginSettingFieldType.Bool, Group: "Behaviour"),
    ];
    // ... IToolPlugin members ...
}
```

`PluginSettingFieldType` is `Text | Bool | Choice | File | Folder`. `Group`
sections a single pane under headers. At run time a tool reads a saved value with
`context.Host.GetPluginSetting("binaryPath")`.

### Persistent settings — Route B (`ICustomPluginSettingsUi`)

When settings are interdependent, supply your own Avalonia view for the pane
instead of the generated form:

```csharp
public interface ICustomPluginSettingsUi
{
    Control CreateSettingsView(IPluginSettingsContext context); // read/write by key
}
```

Values still flow through `IPluginSettingsContext.GetValue/SetValue`, so the host
persists them the same way regardless of route. A plugin may implement Route A,
Route B, or both (the host prefers the custom view when present).

### Keyboard shortcuts (`IShortcutContributor`)

Register global shortcuts that appear in Settings ▸ Keyboard under the plugin's
own section, where the user can rebind or clear them. They share the host's live
conflict detection, persistence (`keymap.json`) and rebinding with the built-in
shortcuts.

```csharp
public sealed class MyTool : IToolPlugin, IShortcutContributor
{
    public IReadOnlyList<ShortcutContribution> Shortcuts { get; } =
    [
        new("run", "Run my tool", "Mod+Shift+B", ct => RunAsync(ct)),
        new("secondary", "Secondary action", DefaultGesture: null, ct => DoOtherAsync(ct)),
    ];
}

public sealed record ShortcutContribution(
    string Id,                // unique within the plugin; the host namespaces it as pluginId:Id
    string Title,             // label in the shortcut list
    string? DefaultGesture,   // Avalonia gesture syntax; null = ships unbound
    Func<CancellationToken, Task> ExecuteAsync);
```

Key points:

- **`Mod` token** in a default gesture maps to the platform primary modifier —
  **Cmd on macOS, Ctrl on Windows/Linux** — so `"Mod+Shift+B"` ships as ⌘⇧B on a
  Mac and Ctrl+Shift+B elsewhere. Use plain modifiers (`Ctrl`, `Shift`, `Alt`)
  when you deliberately want the same key on every platform.
- **`DefaultGesture: null`** ships the command unbound; the user assigns a key.
- **Ids are namespaced** by the host (`pluginId:localId`), so two plugins can use
  the same local id without clashing. Keep your local id stable — it is persisted.
- The callback is **self-contained** and runs on the UI thread; capture whatever
  state you need when you build the contribution, and offload heavy work yourself.
  (Shortcuts are window-scoped; a plugin cannot bind an editor-only key.)

### Toolbar buttons (`IToolbarPlugin` / `IQueryToolbarPlugin`)

**Standing-subsystem plugins only** (`type: "extension"`), gated by the
`toolbar` capability — a separate consent string from `menu`, because a menu item
sits behind a click where the user goes looking for it and a toolbar button is
permanent chrome. Requires `"hostApiVersion": 8`.

Two surfaces, two interfaces, mirroring how `IMenuPlugin` and
`IConnectionMenuPlugin` already split the two menu surfaces.

```csharp
public sealed class ContainersExtension : ISubsystemPlugin, IToolbarPlugin
{
    public IReadOnlyList<ToolbarContribution> ToolbarItems =>
    [
        new("new-container", _ctx.Localizer["NewContainer"], ui => ShowCreateDialogAsync(ui))
        {
            Icon           = ContainerGeometry,
            Tooltip        = _ctx.Localizer["NewContainerTip"],
            DefaultGesture = "Mod+Shift+K",   // optional, and only a suggestion
        },
    ];
}
```

The query-window surface adds an `AppliesTo` predicate and is handed the
document, so it decides per tab whether it appears at all:

```csharp
public sealed class PlansExtension : ISubsystemPlugin, IQueryToolbarPlugin
{
    public IReadOnlyList<QueryToolbarContribution> QueryToolbarItems =>
    [
        new("actual-plan", _ctx.Localizer["ActualPlan"],
            doc => doc.Kind == QueryDocumentKind.Query && doc.Connection?.ProviderId == "mssql",
            (doc, ui) => CaptureAsync(doc, ui)) { Icon = PlanGeometry },
    ];
}
```

`IQueryDocument` is deliberately small — read the SQL (`Sql`, `SelectedSql`),
rewrite it (`SetSql`), run it (`RunAsync`), plus `Kind`, `Connection` and
`Database` to scope against. Enough for a formatter, a linter, a snippet
inserter or a query assistant; the result grid, the paging state and the pending
edit buffer stay out until a plugin needs them.

Key points:

- **The application toolbar is the user's.** A contribution proposes a button; whether
  it is shown and where it sits is settled in Settings ▸ Toolbar. A plugin cannot claim
  the front, reorder or remove host actions, or exempt itself from the overflow flyout.
  A newly installed plugin's button *does* appear by default — absent from the saved
  layout means new, not hidden.
- **Ids are namespaced** (`pluginId:localId`) and persisted, in the toolbar layout and
  in the keymap. Keep the local id stable.
- **Every application-toolbar action is rebindable** in Settings ▸ Keyboard, whether or
  not you set `DefaultGesture`. `null` ships it unbound but bindable. There is no
  `DefaultGesture` on the query surface: the keymap has no notion of document scope.
- **`ManagedConnectionInfo`, not a connection profile** — non-secret field values only.
  A generic toolbar extension has no business holding a credential it does not need in
  order to connect.
- **Icons are a `Geometry` you own** (host icon resources are unreachable across the ALC
  boundary), drawn `Stretch="Uniform"` and tinted with the theme. No icon fonts, no
  emoji — see the icon note under *Theming a Route B view*. `null` renders text-only.
- **Titles and tooltips are yours to localise** through your own
  `IPluginRuntimeContext.Localizer` before you return the contribution. They are read
  once at mount, so a live language switch does not re-label them until restart — the
  same as plugin menu items and panels today.
- **`InvokeAsync` runs on the UI thread** and one throwing contribution does not take
  the toolbar down. Offload heavy work yourself.
- **No disabled state and no toggle buttons.** A button is either there or it is not;
  `AppliesTo` decides per-document applicability. A live enabled/pressed flag would mean
  change notification across the ALC boundary, which no existing seam does. If the
  action is not valid right now, say so when it is clicked.

The design record — including what was rejected and why — is
[Toolbar architecture](../toolbar-architecture.md).
`plugins/Backends.Docker` is the reference implementation.

### Referencing Avalonia for a Route B view

Route B capabilities (`ICustomToolUi`, `ICustomPluginSettingsUi`) return an
Avalonia `Control`. Add Avalonia to the plugin `.csproj` so it compiles, but keep
the host's copy authoritative across the ALC boundary:

```xml
<PackageReference Include="Avalonia" Version="12.0.5" ExcludeAssets="runtime" />
```

A plugin that only uses declarative Route A (no custom view) needs no Avalonia
reference at all.

### Authoring a Route B view in XAML

`CreateView`/`CreateSettingsView`/`CreateAdvancedView` just need to return an
Avalonia `Control` — how you build it is up to you. Writing it as a `.axaml`
`UserControl` with code-behind is fine and works the same as building it by
hand in C#: the Avalonia XAML compiler turns `.axaml` into an `InitializeComponent()`
call that constructs the same `Control` tree at compile time, so it resolves
types against the same `ExcludeAssets="runtime"` Avalonia reference described
above — no extra ALC risk.

### Theming a Route B view

The host applies its theme via Avalonia's `ThemeVariant` system
(`Theme.axaml`), and a plugin's `Control` is hosted directly inside the app's
visual tree, so it inherits ambient values (fonts, foreground, background)
without doing anything special. Standard controls (`TextBox`, `Button`, ...)
already look right for free.

To deliberately match host chrome — panel backgrounds, the accent color,
status colors — reference the host's published theme brushes with
`DynamicResource` (not `StaticResource`, or the control won't react to a
live dark/light switch):

| Key | Use |
| --- | --- |
| `SEWindowBgBrush` / `SEPanelBgBrush` / `SESecondaryBgBrush` / `SEToolbarBgBrush` | Surface backgrounds |
| `SEHairlineBrush` / `SEHoverBgBrush` / `SESelectionBgBrush` | Borders and interactive states |
| `SETextPrimaryBrush` / `SETextSecondaryBrush` / `SETextFaintBrush` | Text |
| `SEAccentBrush` / `SEAccentHoverBrush` / `SEAccentPressedBrush` / `SEAccentFgBrush` | Accent color and its states |
| `SEStatusConnectedBrush` / `SEStatusErrorBrush` / `SEStatusBusyBrush` / `SEStatusWaitingBrush` | Status indicators |
| `SEControlRadius` / `SEPanelRadius` / `SEHairlineThickness` / `SEMonoFont` | Corner radius, border thickness, monospace font |

These are a public, stable contract — safe to depend on across host versions.
Anything not in this table is a host implementation detail and may change
without notice; don't reference it from a plugin.
