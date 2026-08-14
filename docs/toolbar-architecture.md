# Toolbar architecture (design)

**Status:** implemented in 0.8.0. Part 1 (the catalog, `toolbar.json`, `OverflowPanel`,
Settings ▸ Toolbar) and part 2 (the plugin seams) both landed; the plugin-facing half is
documented in [`plugins/capabilities.md`](plugins/capabilities.md).

**Correction to §5, made during implementation.** This document was written against the
0.7.0 tree, where `ToolHostApi.Version` was 5, and concludes "5 → 6". On `develop` the
contract had already moved to **7** (v6 tool documents, v7 `NodePath` +
`IsActivityMonitor`), so the toolbar seams shipped as **v8**. The reasoning carries over
unchanged, with one nuance the analysis did not have to make: 7 is *unreleased* in the
stable sense — v0.7.0 carries 5 — which is what let `IsActivityMonitor` fold into it, but
7 is already out on the preview and nightly channels. Folding **new types** into a number
those hosts already carry is precisely the `ReflectionTypeLoadException` half-failure §5.4
describes, so a fold-in was not available here either. `MinimumSupported` stays 1, as §5
says.
**Revision:** fourth draft. The first draft shipped the application toolbar as a fixed
strip with a count-based overflow; review rejected both. The toolbar is now
**user-configurable and persisted**, and overflow is **measured against the available
width**. The third draft added §5, the host-API compatibility analysis for 0.8.0, and
corrected a factual error in §2.4 that the analysis exposed. The fourth records the
decision that came out of it: **`ToolHostApi` goes to v6, additively** —
`MinimumSupported` stays at 1, so every existing plugin keeps loading. §9 records what
all of that did to the earlier open questions.
**Scope:** the application toolbar at the top of the main window, the per-query-window
toolbar, and how plugins contribute actions to both.

This document is a decision record, not a guide. Once it is implemented, the
plugin-facing half moves into [`plugins/capabilities.md`](plugins/capabilities.md)
next to the other optional capabilities.

Mockup: `mockups/se-255-toolbar.html` (Depot project `sql-explorer`).

---

## 1. What exists today

### 1.1 The two toolbars

**Application toolbar** — `src/DataTray.App/Views/MainView.axaml`, the `Border` with
`SEToolbarBgBrush` in row 0. It currently holds two host actions ("New query tab",
the ⌘K "Go to object" quick-open) and a right-docked plugin-update badge. It was
deliberately trimmed to those two in SE-123, when History and Output moved to the
toggle on the edge their panel docks to.

**Query-window toolbar** — `src/DataTray.App/Views/DocumentView.axaml`, row 0. It is
not one toolbar but three, mutually exclusive, switched on the document mode:

| Mode | Contents |
|---|---|
| `Query` | Run / Stop, Run at cursor, Explain, Format, connection combo, database combo |
| `Browse` | prev/next page, row range, filter box, Apply, per-column filter boxes |
| `Monitor` | Refresh, auto-refresh interval |

A fourth bar (row 3, `ShowEditToolbar`) carries the grid-edit actions (Add row,
Delete row, Save, Discard, Export) and is shared by Query and Browse.

Neither toolbar has any extension point today, and neither is configurable.

### 1.2 The registration pattern plugins already use

There is one established pattern, repeated five times. Every existing seam has the
same three properties:

1. **An optional interface the host detects with an `is`-check** — no manifest
   change, no host API bump. `IPluginSettings`, `ICustomToolUi`,
   `IShortcutContributor`, `ICustomCellActionUi` all work this way, and the SE-164
   subsystem seams (`IPanelPlugin`, `IMenuPlugin`, `IConnectionMenuPlugin`,
   `IBackgroundPlugin`) add a capability gate on top.
2. **A declarative contribution record** — id, title, and a self-contained
   delegate. `MenuContribution`, `ConnectionMenuContribution`,
   `ShortcutContribution`. Records with delegates cross the plugin
   `AssemblyLoadContext` boundary cleanly; live view models would not.
3. **The host owns mounting and namespacing.** `SubsystemActivator.ActivateAll()`
   collects the capability-gated contributions, `App.axaml.cs` mounts them via
   `MainViewModel.AddSubsystemMenuItem` / `AddSubsystemPanel` /
   `AddConnectionMenuItem`, and the host namespaces every plugin-local id as
   `pluginId:localId`.

Where a contribution needs context, it is handed that context as a delegate
argument — `IHostUi` for a Tools-menu item, `ManagedConnectionInfo` **plus**
`IHostUi` for a connection context-menu item. There is no ambient service locator.

### 1.3 The user-customisation pattern the host already has

The keymap is the precedent for §3. `ShortcutCatalog` is a fixed list of bindable
commands with stable ids and factory defaults; `PluginShortcut` is the same thing
for plugin-contributed commands; `IKeymapStore` / `JsonKeymapStore` persist the
user's overrides to `keymap.json`; `KeymapService` resolves catalog + overrides into
what actually fires, and Settings ▸ Keyboard edits it.

**Catalog + persisted user layer + a settings pane over both is exactly what a
configurable toolbar needs.** §3 reuses that shape rather than inventing one.

---

## 2. Contribution architecture

### 2.1 One interface per surface

The two toolbars get two interfaces, mirroring how `IMenuPlugin` and
`IConnectionMenuPlugin` already split the two menu surfaces:

```csharp
namespace DataTray.Sdk.Extensibility;

/// One action a plugin adds to the application toolbar at the top of the main window.
public sealed record ToolbarContribution(
    string Id,
    string Title,
    Func<IHostUi, Task> InvokeAsync)
{
    /// Stroked vector geometry for the button's icon. Null renders a text-only button.
    public Geometry? Icon { get; init; }

    /// Tooltip; falls back to Title when null.
    public string? Tooltip { get; init; }

    /// Suggested key, Avalonia gesture syntax, "Mod" = Cmd/Ctrl. Null (the default)
    /// ships the action unbound — it is still bindable by the user (§3.4).
    public string? DefaultGesture { get; init; }
}

public interface IToolbarPlugin
{
    IReadOnlyList<ToolbarContribution> ToolbarItems { get; }
}
```

```csharp
/// One action a plugin adds to a query window's toolbar. AppliesTo decides per
/// document whether the button appears at all — the same shape as
/// ConnectionMenuContribution's applicability predicate.
public sealed record QueryToolbarContribution(
    string Id,
    string Title,
    Func<IQueryDocument, bool> AppliesTo,
    Func<IQueryDocument, IHostUi, Task> InvokeAsync)
{
    public Geometry? Icon { get; init; }
    public string? Tooltip { get; init; }
}

public interface IQueryToolbarPlugin
{
    IReadOnlyList<QueryToolbarContribution> QueryToolbarItems { get; }
}
```

Rejected: a single `ToolbarContribution` with a `Surface` enum. It saves one
interface but forces every action's delegate to accept a nullable document, so the
compiler stops helping and every plugin author writes the same null check. The
codebase already chose one-interface-per-surface for menus; consistency wins.

There is no `DefaultGesture` on the query-side record: the keymap has no notion of
document scope, so a gesture there would need a scope concept that does not exist.

### 2.2 What a query-toolbar action can see: `IQueryDocument`

This is the only genuinely new contract. It is the seam between a plugin and one
open query window, implemented by `DocumentViewModel`:

```csharp
namespace DataTray.Sdk.Extensibility;

public enum QueryDocumentKind { Query, Browse, Monitor }

public interface IQueryDocument
{
    /// Stable for the lifetime of the tab.
    string DocumentId { get; }

    QueryDocumentKind Kind { get; }

    /// The connection this tab runs against — non-secret values only, the same
    /// DTO IManagedConnections hands out. Null while the tab has no connection.
    ManagedConnectionInfo? Connection { get; }

    /// The database/catalog picked in the tab's switcher, when the engine has one.
    string? Database { get; }

    /// Snapshot of the editor text, and of the current selection (null when nothing
    /// is selected). Not live — read it inside your action, don't cache it.
    string Sql { get; }
    string? SelectedSql { get; }

    /// Replace the editor text (undoable, as if typed).
    void SetSql(string sql);

    /// Run the tab exactly as the Run button does; completes when the run finishes.
    Task RunAsync(CancellationToken ct = default);
}
```

`ManagedConnectionInfo`, not `ConnectionProfile`. `ConnectionProfile` carries the
connection string with credentials in it, and CONTRIBUTING §5 makes the credential
boundary explicit: a generic toolbar extension has no business holding a secret it
does not need in order to connect. Providers keep getting `ConnectionProfile`
through their own seams (`ICustomCellActionUi` et al.) because connecting is
exactly what they do.

The surface is deliberately small: read the SQL, rewrite the SQL, run it. That
covers the plausible v1 plugins (formatter, linter, snippet inserter, "explain this
query" assistant) without exposing the result grid, the paging state or the edit
buffer. Result-set access is listed under *not in v1* below.

### 2.3 Who may contribute

**Standing subsystem plugins only** (`type: "extension"`, `ISubsystemPlugin`) — the
same population that already contributes menus and panels. A subsystem plugin is
long-lived and holds an `IPluginRuntimeContext`, which is what a toolbar button
needs; a tool plugin is a one-shot action already reachable from the Tools menu,
and a provider reaches the user through its own node/cell seams.

The obvious counter-argument is that a *provider* is the natural owner of a query
toolbar button ("show actual execution plan" is a SQL-Server thing). That case is
covered without a second registration path: `AppliesTo` receives the document, so a
subsystem plugin scopes itself with
`doc => doc.Connection?.ProviderId == "mssql"`. If a provider-owned button turns out
to be needed, the provider loader adds the same `is`-check later — additive, no
redesign.

### 2.4 Capability and consent

A new capability string:

```csharp
public const string Toolbar = "toolbar";   // PluginCapabilities
```

Reusing `"menu"` would be cheaper (Docker's subsystem would inherit it for free,
no manifest churn), but it misrepresents the consent. `"menu"` is disclosed to the
user as *"Adds items to the top-bar menus"* — items behind a click, in a place the
user goes looking for them. A toolbar button is permanent chrome in the app's most
valuable strip. Those are different asks, so they get different strings.

Gating happens in `SubsystemActivator.ActivateAll()`, next to the existing checks:

```csharp
if (activation.Capabilities.Contains(PluginCapabilities.Toolbar)
    && activation.Plugin is IToolbarPlugin toolbar)
{
    toolbars.Add(toolbar);
}
// ... same for IQueryToolbarPlugin
```

`SubsystemActivationResult` grows two lists.

The capability *string* costs nothing across versions: it is a `const string`, so a
plugin referencing `PluginCapabilities.Toolbar` compiles the literal into itself, and
`plugin.json` carries a literal anyway. An older host reading an unknown capability
simply does not grant it.

The *interfaces* are a different question, and the honest answer is in §5. An earlier
draft of this document claimed the SE-164 seams set a precedent for adding contribution
interfaces without a host API bump. **That is wrong.** SE-164 opened `ToolHostApi` v3
specifically to carry `IPanelPlugin` / `IMenuPlugin` / `IBackgroundPlugin` /
`IConnectionMenuPlugin`; the "additive optional-interface check — no host API bump"
sentence in `IPanelPlugin`'s own doc comment is about the host's `is`-check being
additive within that version, not about the family arriving without one.

### 2.5 Host wiring

**Application toolbar.** Contributions no longer mount straight into the view.
They register into the action catalog (§3.1), and what the strip renders is the
resolved layout (§3.2). `MainViewModel` exposes:

```csharp
/// Every action that may appear in the toolbar, host and plugin alike.
public IReadOnlyList<ToolbarActionEntry> ToolbarCatalog { get; }

/// The user's resolved layout: visible actions, in the user's order. Bound by the view.
public ObservableCollection<ToolbarActionViewModel> ToolbarActions { get; }
```

`App.axaml.cs` registers plugin contributions into the catalog in the same startup
loop that mounts menus and panels; the resolve step then runs once, after every
plugin has had its say.

**Query toolbar.** The registered `QueryToolbarContribution`s live on
`MainViewModel` (it is what creates documents) and are passed to each
`DocumentViewModel` at construction. The document filters them with `AppliesTo`
into its own `PluginToolbarItems`, and re-filters when `Connection`, `Database` or
`Mode` changes — the same trigger set that already drives `HasDatabasePicker` and
the mode-switched bars. `DocumentView.axaml` renders one `ItemsControl` at the end
of each mode's row-0 bar.

Because `AppliesTo` gets the whole document, a plugin scopes to a mode
(`doc => doc.Kind == QueryDocumentKind.Query`), a provider, a database, or any
combination, and the host needs no per-plugin wiring.

**Icons.** `Geometry`, owned by the plugin, exactly as `IPanelPlugin.Icon` already
does — a plugin cannot reach host icon resources across the ALC boundary. The host
draws it `Stretch="Uniform"` and tints it with the theme. Two standing project
rules apply and are worth repeating here: no icon fonts (a third-party icon library
targeting Avalonia 11 crashes at runtime on the pre-release Avalonia 12 the app
builds against), and no emoji (they do not render on Linux). Vector `Path` only.

**Localisation.** `Title` and `Tooltip` are resolved by the plugin through its own
`IPluginRuntimeContext.Localizer` before it returns the contribution, matching
`IPanelPlugin.Title`. Consequence, and it is a real one: the strings are read once
at mount, so a live language switch does not re-label plugin toolbar buttons until
restart. That is already true of plugin menu items and panels; this design does not
make it worse, and fixing it is a separate change across all four seams.

**Threading.** `InvokeAsync` is called on the UI thread, like
`ShortcutContribution.ExecuteAsync`. Heavy work is the plugin's job to offload.

**Failure containment.** One contribution throwing must not take down the toolbar.
The mount loop wraps each invocation the way `SubsystemActivator` wraps
`Initialize`: catch, log to Output, leave the button alive.

---

## 3. The action catalog and the user's layout

The application toolbar is **the user's**, not the app's and not a plugin's. Which
actions it shows and in what order is a persisted user setting. This is modelled on
the keymap (§1.3), which solves the identical problem for keys.

### 3.1 The catalog

Every action that *may* appear in the toolbar is a catalog entry with a stable id:

```csharp
namespace DataTray.Core.Toolbar;

public enum ToolbarActionSource { Host, Plugin }

/// One thing that may sit in the application toolbar.
public sealed record ToolbarActionEntry(
    string Id,              // "NewQueryTab", or "pluginId:localId"
    string Title,
    ToolbarActionSource Source,
    string? PluginTitle);   // set for Source == Plugin, used to group the settings list
```

Host entries come from a fixed list, mirroring `ShortcutCatalog.All`:

```csharp
public static class ToolbarCatalog
{
    public static class Ids
    {
        public const string NewQueryTab = "NewQueryTab";
        public const string GoToObject  = "GoToObject";
    }

    /// Host actions in factory order. Also the default layout: all visible, this order.
    public static IReadOnlyList<ToolbarActionEntry> Host { get; } = [ /* ... */ ];
}
```

Two host entries today because that is what SE-123 left in the strip. The point of
the catalog is not that it is long — it is that "what can be in the toolbar" becomes
an addressable list, which is what both persistence and the settings pane need.
Host actions that live in menus today (Manage connections, Query log, Plugin store)
become catalog entries the moment someone wants them on the strip; that is a
one-line addition once this exists, and is not part of this ticket.

`GoToObject` is an entry like any other. When visible it renders as the quick-open
search field rather than a button, and when it overflows (§4) it renders in the
flyout as an ordinary item that opens the same quick-open. No special case in the
layout model, one special case in the item template.

### 3.2 The persisted layout

```csharp
public sealed record ToolbarLayoutItem(string Id, bool Visible);

/// Persists to toolbar.json, alongside keymap.json.
public interface IToolbarLayoutStore
{
    IReadOnlyList<ToolbarLayoutItem> Load();
    void Save(IReadOnlyList<ToolbarLayoutItem> layout);
}
```

Resolution, run once at startup and again whenever the user saves the settings pane:

1. Take the saved layout in its saved order. Drop nothing.
2. For each saved id that resolves against the catalog, emit it with its saved
   `Visible` flag.
3. Append every catalog entry the saved layout does not mention, **visible**, in
   catalog order (host entries first, then plugins in load order).
4. Saved ids that do not resolve are **skipped for rendering but kept in the file.**

Step 3 is what makes a freshly installed plugin's button actually appear: absent
from the layout means "new", not "hidden". A user who does not want it unticks it,
and that decision is then recorded.

Step 4 is the one that is easy to get wrong. A plugin that is disabled, mid-update,
or temporarily failing to load must not cost the user their arrangement. Dropping
unknown ids on save would silently reset a plugin's position every time it blinked
out. They stay in the file, inert, and light up again when the plugin returns.

### 3.3 Settings ▸ Toolbar

One list, in toolbar order: a checkbox for visibility, a drag handle for order, and
the owning plugin's name as a muted suffix on plugin entries. Plus **Reset to
defaults**, which deletes the layout and falls back to catalog order, all visible.

One list rather than the classic available/shown pair: visibility and order are two
properties of the same row, and a two-list transfer control makes reordering
awkward for exactly the operation people actually do — nudging one button left.

Everything is hideable, including "New query tab". Nothing becomes unreachable by
hiding it: the actions remain in the menus and remain bindable to a key (§3.4).

**Getting there: a gear at the end of the strip.** The toolbar is the one setting a
user wants to change while looking at it, so it gets its own way in — a small button
docked right, between the overflow "…" and the update badge, that opens Settings
pre-navigated to this pane. It is what VS Code and every browser do, and it is the
difference between "I could rearrange this" and "I would have to go and find where
that lives".

It is deliberately **not** a catalog entry, so it cannot be reordered or hidden.
That is not a special case begging to be generalised — it is the one control whose
whole job is to reach the place where hiding is undone. An action that can be hidden
cannot also be the way back. The same reasoning is why it is not in the overflow
flyout either: it sits outside the measured strip and is always present, at the cost
of a fixed ~29 px that the overflow budget never sees.

The glyph is the same one the Settings rail uses for this pane
(`Icons.SlidersHorizontal`), not a gear: the vendored Lucide set carries no cog, and
showing the icon of the pane you land on beats introducing a second settings symbol.

### 3.4 Every toolbar action is a bindable command

Because catalog entries already have stable ids, wiring them into the existing
keymap is close to free, and it is what makes hiding a button harmless. The host
registers each *plugin* toolbar contribution as a `PluginShortcut` under the same
namespaced id, adapting the delegate (`ct => invoke(hostUi)`); host entries get a
`ShortcutCatalog` command each. `ToolbarContribution.DefaultGesture` is then purely
a *suggestion* — null, the default, ships the action unbound but bindable, and the
user assigns a key in Settings ▸ Keyboard like any other.

Query-toolbar contributions are excluded, for the reason in §2.1.

---

## 4. Overflow is measured, not counted

The first draft capped the strip at four plugin buttons and pushed the rest into a
flyout. That is wrong on both ends: it hides buttons that fit on a wide monitor, and
it overflows a narrow window too late. Overflow is a function of **available width**,
so it is decided by measurement.

Avalonia ships no toolbar-with-overflow control (WPF's `ToolBar` had one; Avalonia
does not), and the behaviour cannot be expressed with existing panels — a
`WrapPanel` wraps instead of collapsing, a `DockPanel` clips. So this is a small
custom panel, roughly:

```csharp
/// Lays children out in a row; whatever does not fit moves to the overflow flyout.
public sealed class OverflowPanel : Panel
{
    /// Attached: this child never overflows (subtracted from the budget up front).
    public static readonly AttachedProperty<bool> IsPinnedProperty = /* ... */;

    protected override Size MeasureOverride(Size available) { /* below */ }
}
```

The measure pass:

1. Measure every child at infinite width; keep the desired widths.
2. Measure the overflow button; keep its width `wOverflow`.
3. Subtract the pinned children's widths from the available width.
4. If the remaining children all fit, show them all and collapse the overflow
   button.
5. Otherwise the budget is `available − pinned − wOverflow`. Walk the children in
   layout order, taking each while it still fits; every child from the first
   non-fitting one onward goes to the flyout.

Two things this gets right that a naive version does not:

- **No oscillation.** The decision is made once, from the full list of desired
  widths, never from the post-collapse state. The classic bug — hiding an item frees
  width, so it is shown again, so it does not fit, so it is hidden — cannot occur
  because the panel never re-measures itself in response to its own decision.
- **Room for the button that shows the overflow.** `wOverflow` is reserved as soon
  as anything overflows at all, so the "…" never itself causes the last item to be
  pushed out after the fact.

Degenerate case: if not even one unpinned child fits, everything goes to the flyout
and the strip is the pinned children plus "…". That is correct behaviour for a
window dragged very narrow, and needs no special casing.

**Pinning.** The query-window bar has controls that must never end up in a flyout —
you cannot pick a connection from a menu that closes when you click it. The
connection and database combos are pinned; so is the filter box in Browse mode.
Everything else, host and plugin alike, is overflowable. One attached bool, and it
prevents a real breakage rather than a hypothetical one.

**Both bars.** The same panel serves the application toolbar and each of the three
query-window mode bars. The query bar is *not* user-configurable — its contents are
mode-dependent and `AppliesTo`-filtered, so "which actions exist" is already not a
free choice, and a settings pane over it would be configuring something the app
keeps rewriting. Overflow, which is purely about width, applies to both.

**Ordering under overflow.** The user's configured order is also the priority order:
what the user put first survives longest as the window narrows. Hidden and
overflowed are different states — a hidden action is not in the toolbar at all and
does not appear in the flyout; the flyout contains only visible actions that did not
fit.

---

## 5. Host API compatibility (0.8.0)

**Decided: `ToolHostApi.Version` goes 5 → 6 for 0.8.0. `MinimumSupported` stays at 1.**
The bump is additive — every plugin built against v1–v5 keeps loading untouched — and it
is the same kind of bump v3 took to carry the SE-164 seams this design is modelled on.
§5.5 states what that concretely means to build.

The rest of this section is the analysis that produced that decision, kept because it
explains *why* a fold-in into v5 was not available and what the alternative would have
cost.

### 5.1 Where the numbers actually stand

Verified against the tags, not assumed:

| Contract | `Version` | `MinimumSupported` | First released as |
|---|---|---|---|
| `ToolHostApi` (tools **and** `extension` subsystems) | 5 | 1 | **v5 shipped in 0.5.0**, unchanged through 0.7.0 |
| `ProviderHostApi` | 27 | 23 | v27 in 0.4.0+ |
| `McpHostApi` | 2 | 1 | — |

Toolbar contributions come from subsystem plugins (§2.3), so `ToolHostApi` is the only
contract in play; `ProviderHostApi` and `McpHostApi` are untouched by this ticket. Two
different things get called "a bump", and the difference is what made the decision
straightforward once it was stated:

- Raising **`Version`** is *additive*. `IsCompatible` accepts
  `[MinimumSupported, Version]`, so every existing plugin keeps loading. v2→v3→v4→v5
  were all this kind.
- Raising **`MinimumSupported`** is *breaking* — older plugins get refused. It has
  never moved for `ToolHostApi` (it is still 1), and **nothing in this design needs it
  to move.**

So the design contains no breaking change. The only question was whether `Version` had
to move at all — and §5.2 to §5.4 show that it does, at which point taking it is cheap.

### 5.2 The rule this repo already learned twice

Both version files carry a hard-won note against folding post-release surface into an
already-released number. `ProviderHostApi` v27:

> These three were first (incorrectly) folded into v26 under the "one bump per release"
> rule — but that rule only permits folding into an *unreleased* dev number, and v26 was
> already released in 0.3.0. Corrected to v27 here: a 0.3.0 host accepts [23,26] and now
> refuses these plugins instead of loading and crashing on the missing members.

And `ToolHostApi` v5, on why it did not fold into v4:

> New number rather than a fold-in because v4 shipped in 0.4.0 — folding post-release
> surface into a released number is the SE-166 crash trap.

**`ToolHostApi` v5 shipped in 0.5.0.** By this repo's own rule, new SDK surface for
0.8.0 cannot be folded into v5.

### 5.3 What needs nothing, and what needs v6

Most of this ticket — including everything the second review asked for — touches no SDK
surface at all and therefore has **no version implication whatsoever**:

| Work | Lives in | Version impact |
|---|---|---|
| `ToolbarCatalog`, `ToolbarActionEntry`, `ToolbarLayoutItem` | `DataTray.Core` | none |
| `IToolbarLayoutStore` / `JsonToolbarLayoutStore`, `toolbar.json` | `DataTray.Core` | none |
| The §3.2 resolve rules | `DataTray.Core` | none |
| `OverflowPanel` + `IsPinned` (§4) | `DataTray.App` | none |
| Settings ▸ Toolbar pane | `DataTray.App` | none |
| `MainViewModel` / `DocumentViewModel` / XAML changes | `DataTray.App` | none |
| `PluginCapabilities.Toolbar` | `DataTray.Sdk`, but a `const string` | none (§2.4) |

What does need v6 is exactly the plugin-facing type surface:

`IToolbarPlugin`, `ToolbarContribution`, `IQueryToolbarPlugin`,
`QueryToolbarContribution`, `IQueryDocument`, `QueryDocumentKind`.

### 5.4 Why a fold-in into v5 was not an option

Traced through the loader rather than reasoned about in the abstract. A plugin built
against 0.8.0's SDK implementing `IToolbarPlugin` can only declare
`"hostApiVersion": 5`, because 5 is the highest number that exists. A 0.7.0 host accepts
`[1, 5]`, so it takes the plugin — and then `SubsystemPluginLoader.LoadOne` calls
`assembly.GetTypes()` on an assembly whose plugin type implements an interface that is
not in the 0.7.0 `DataTray.Sdk.dll`. That throws `ReflectionTypeLoadException`.

The good news is that the loader has a catch-all (`SubsystemPluginLoader.cs:102`), so
this is a degraded load rather than a crash. The bad news is what the degradation looks
like:

- **The whole plugin fails to load**, not just its toolbar contribution — its panel,
  menu items, background loop and managed connections go with it.
- The user sees `ex.Message`, which for this exception is *"Unable to load one or more
  of the requested types."* — instead of the accurate, actionable message the version
  gate would have produced: *"Extension 'x' targets host API v6, this host is v5."*
- **The Store still offers it.** `HostApiVersions.CompatFor` judges an `extension` entry
  against `ToolHostApi`'s window, so a 0.7.x host is told the plugin is compatible and
  downloads it before failing.

That is the SE-166 crash trap one notch milder, and it is precisely the outcome the two
comments in §5.2 were written to prevent. Opening v6 is what turns that opaque
half-failure into the loader's accurate refusal at
`SubsystemPluginLoader.cs:67` — *"Extension 'x' targets host API v6, this host is v5."*

### 5.5 The decision, and what it costs

**`ToolHostApi.Version` 5 → 6. `MinimumSupported` stays 1.** Ship the whole design in
0.8.0 — host side and plugin seams together. Concretely:

- **`src/DataTray.Sdk/Tools/ToolHostApi.cs`**: `Version` becomes 6, with a `// v6` note
  in the running comment block listing the surface it carries, in the same style as
  v3/v4/v5 — `IToolbarPlugin`, `ToolbarContribution`, `IQueryToolbarPlugin`,
  `QueryToolbarContribution`, `IQueryDocument`, `QueryDocumentKind`, and the `toolbar`
  capability. `MinimumSupported` is not touched, and the comment should say so and why.
- **Every existing plugin keeps loading.** `IsCompatible` accepts `[1, 6]`, so the v4
  Docker extension and every v1–v5 tool are unaffected. No recompilation, no
  re-release, no manifest edit for any plugin that does not want a toolbar button.
- **A plugin that *does* want one declares `"hostApiVersion": 6`** and adds `"toolbar"`
  to its `capabilities`. That is what makes an older host refuse it cleanly rather than
  half-load it.
- **The Store needs no change.** `HostApiVersions.CompatFor` already routes `extension`
  and `tool` entries to `ToolHostApi`'s window, so raising `Version` automatically makes
  the Store offer v6 plugins to 0.8.0+ hosts and withhold them from older ones.
- **A 0.7.x host stays correct.** It accepts `[1, 5]`, so it refuses a v6 plugin with
  the version message instead of the `ReflectionTypeLoadException` path of §5.4.

The one knock-on: `plugins/Backends.Docker` currently declares `hostApiVersion: 4`. It
only has to move if it actually adopts a toolbar contribution — and if it does, the
project's plugin release workflow applies (plugin version bump plus a Store release, not
just a code change).

Rejected alternative, recorded because it was close: splitting the ticket so §3 and §4
ship in 0.8.0 with no version movement at all and the seams wait for a later release.
That would have honoured "no bump" literally, but at the cost of shipping a toolbar
architecture whose entire plugin half stays on paper — and the bump it avoids is
additive, which is to say it breaks nothing.

---

## 6. Trade-offs

**One shared toolbar region vs. per-plugin sections.** Contributions append in
plugin load order behind a single separator, and from then on the user owns the
order. No per-plugin grouping in the strip; the settings list and the overflow
flyout are where the owning plugin is named.

**Persisting a layout at all.** It is a store, a catalog, a settings pane and a
resolve step — real weight for a two-button strip. It is worth it because the strip
only stays two buttons until plugins arrive, and because the alternative found in
review was a magic number that is wrong on every screen except the one it was
guessed on. The keymap already carries this exact shape, so the cost is mostly
following it.

**A custom panel for overflow.** Measured overflow cannot be built from Avalonia's
stock panels, so this is genuinely new code — one `MeasureOverride` and one
`ArrangeOverride`. Contained: one file, no plugin-facing surface, and both toolbars
use it.

**No `CanExecute`.** Buttons are always enabled while visible; `AppliesTo` handles
per-document applicability and the layout handles presence. A live
enabled/disabled state means change notification crossing the ALC boundary, which
none of the existing seams do. If a plugin's action is not valid right now, it says
so when clicked.

**Rejected: unifying toolbar items and shortcuts into one "command" record.** §3.4
makes every toolbar action bindable without touching `ShortcutContribution`, which
is a shipped SDK contract and is app-wide and window-scoped. Teaching that record
document scope and applicability would be a breaking change for a tidiness gain;
sharing the id instead gets the benefit at no cost.

---

## 7. Not in v1

- **Result-set access from a query-toolbar action.** Reading the current grid, its
  selection, or the pending edit buffer. Add when a plugin needs it; the shape is
  unclear until then.
- **Toggle/stateful buttons** (a pressed state a plugin drives). Same change-
  notification problem as `CanExecute`.
- **A configurable query-window toolbar.** Its contents are mode-dependent and
  predicate-filtered; see the *Both bars* note in §4.
- **Plugins reordering or removing host actions.** A plugin appends to the catalog.
  Only the user reorders, and only through the settings pane.
- **Toolbars on the other document bars** (the row-3 grid-edit toolbar). Row 0 only,
  per mode.
- **Custom toolbar groups/separators the user can insert.** Order and visibility
  only.

---

## 8. Implementation order

All of it ships in 0.8.0 (§5.5). The two parts are a merge order, not a gate: Part 1
touches no SDK surface and lands on its own, so it does not have to wait for the v6
review.

**Part 1 — no SDK surface.**

1. Core: `ToolbarCatalog`, `ToolbarActionEntry`, `ToolbarLayoutItem`,
   `IToolbarLayoutStore` / `JsonToolbarLayoutStore`, and the resolve step of §3.2.
   Pure logic, no UI — the resolve rules (new action appended visible, unknown id kept
   but not rendered) are the part worth a test file of its own.
2. App: `OverflowPanel` + the `IsPinned` attached property (§4).
3. App: catalog registration, `MainViewModel.ToolbarActions`, and the strip in
   `MainView.axaml` on the new panel.
4. App: Settings ▸ Toolbar pane.
5. App: the three query-window mode bars move onto `OverflowPanel`, combos and the
   Browse filter box pinned.

At this point the toolbar is configurable, persisted and resize-correct, with the host's
own two actions in it. Nothing in the SDK has changed.

**Part 2 — the plugin seams (`ToolHostApi` v6).**

6. SDK: `ToolbarContribution`, `IToolbarPlugin`, `QueryToolbarContribution`,
   `IQueryToolbarPlugin`, `IQueryDocument`, `QueryDocumentKind`,
   `PluginCapabilities.Toolbar`, and `ToolHostApi.Version = 6` with its `// v6` comment
   entry (§5.5). `MinimumSupported` stays 1.
7. Core: two lists on `SubsystemActivationResult`, two gated checks in
   `SubsystemActivator`.
8. App: plugin contributions register into the catalog from `App.axaml.cs`;
   `DocumentViewModel` implements `IQueryDocument` and filters its own items.
9. App: register each application-toolbar action into the keymap (§3.4).
10. A reference implementation, and the docs move into `plugins/capabilities.md`.

`DataTray.Core.Tests` already covers the activator's capability gating in
`Extensibility/DockerSubsystemIntegrationTests.cs`, so the step-7 toolbar gate belongs
in that file.

**On step 10.** `plugins/Providers.Template` is the natural home for a new capability
example and is wrong for this one: it is `type: "provider"`, gated by `ProviderHostApi`,
and §2.3 restricts toolbar contributions to subsystem plugins. The only `extension` in
the repo is `plugins/Backends.Docker`, which already carries the panel, menu, background
and connection-menu seams — so either it gains a toolbar contribution (and takes a
plugin version bump plus a Store release with it), or the repo gains an
`Extensions.Template` that is to `extension` plugins what `Providers.Template` is to
providers. The second is the better long-term answer and is a ticket of its own; the
first is enough to prove the seam.

---

## 9. What review changed

Two questions were left open in the first draft. Both are now closed, by the same
two decisions.

**"Is four the right overflow threshold?" — the question is void.** There is no
threshold. §4 replaces it with measurement against the available width, which is
what the question was really asking for. The count was a guess made against one
window size and one set of label lengths, and would have been wrong in a different
language before it was wrong on a different monitor.

**"Should `ToolbarContribution` carry an optional `DefaultGesture`?" — yes, but it
stopped being the interesting part.** Making the toolbar configurable forced every
action to have a stable, addressable id (§3.1). Once that exists, registering each
action into the existing keymap is a few lines (§3.4), so *every* toolbar action is
bindable whether or not the plugin suggests a key. `DefaultGesture` survives as a
suggestion rather than as the mechanism, and the duplicate-declaration problem it
was meant to solve is gone.

**One question the review created.** Hiding "New query tab" is now possible, and
the app's only remaining accelerator for it is the menu and ⌘T. That is consistent
with how every configurable toolbar behaves, and the reset button is one click, but
it is the first setting in the app that lets a user remove a primary action from
view. Worth a look at the settings pane before it ships rather than a rule in
advance.

*Partly answered by the gear in §3.3.* The sharp end of that question was not
"can a user hide something they want" but "can they find their way back". With a
permanent, unhideable entry point at the end of the strip, undoing a mistake is one
click from where the mistake is visible, rather than a hunt through Settings. What
remains is a judgement call about the pane itself, which is still worth Rick's eyes.

**What the 0.8.0 constraint changed.** Checking the design against "no version bump"
turned up a factual error in it: §2.4 claimed the SE-164 seams added contribution
interfaces without a host API bump, and they did not — SE-164 opened `ToolHostApi` v3
precisely to carry them. Corrected in §2.4, and §5 now works the question through
against the real numbers instead of that assumption.

The analysis put a real choice on the table: ship only the host-side half in 0.8.0 with
no version movement, or take an additive `Version` 5→6 that `MinimumSupported` does not
follow. **Resolved in favour of the bump** — v6, `MinimumSupported` unchanged, whole
design in 0.8.0. Nothing that exists today stops loading, and the alternative would have
shipped a toolbar architecture whose plugin half stayed on paper. §5.5 has the specifics.

Checking that decision also turned up a second, smaller error: §8's reference-plugin step
pointed at `Providers.Template`, which is a `provider` and therefore cannot carry a seam
§2.3 restricts to subsystem plugins. Corrected there.
