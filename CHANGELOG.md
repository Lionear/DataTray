# Changelog

All notable changes to DataTray are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to
[Semantic Versioning](https://semver.org/). Add finished work under `## [Unreleased]`; releasing a
`v<semver>` tag rolls that section into a dated version heading — see
[CONTRIBUTING.md](CONTRIBUTING.md#changelog).

## [Unreleased]

_Nothing yet._

## [0.8.0] - 2026-08-16

### Added

- **A plugin can open its own tab.** Until now a tool plugin got a dialog: collect input, run, report,
  close. That is the wrong container for anything you want to keep open while working — so a tool may now
  open as a tab in the main window instead, beside your query tabs, with its own content, title and icon.
  Reopening the same tool on the same target brings you back to the tab you already have rather than
  stacking another. Nothing changes for existing tools. The first plugin to use it will be the ER diagram.
- **Bring your connections over from DataGrip or DBeaver instead of retyping them.** The Connection Manager
  toolbar has a new import button: it scans the places both clients keep their config — every installed
  JetBrains IDE's `dataSources.xml`, every DBeaver workspace's `data-sources.json` — and shows what it found
  with a tick box per connection. Host, port, database, user and DBeaver's folder come along; the engine is
  read from the JDBC URL, so PostgreSQL, MySQL/MariaDB, SQL Server and SQLite all land on the right provider
  with their fields filled.
- **Passwords are deliberately not imported.** DataGrip keeps them in the IDE keychain and DBeaver in its own
  encrypted credentials file — both are another application's secret store, and DataTray does not read them.
  An imported connection arrives without a password and asks for it once, on first connect.
- Anything found but not importable — an engine DataTray has no provider for, a provider whose plugin is not
  installed, an entry with no JDBC URL — is still listed with the reason, so a partial import says so rather
  than quietly skipping rows.
- **SQL Server Agent jobs can be started, stopped, enabled and disabled**, the way SSMS does it from a job's
  context menu. The four actions sit straight on a job's context menu, next to *Delete Job…* and with *New
  Job…* on the Agent Jobs folder — no submenu in between. Nothing to fill in, so the dialog is a short
  explanation, a button and the log Agent's answer lands in. The job editor (steps, schedules,
  notifications) is not part of this.
- **The Agent Jobs folder says when the Agent service is not running**, instead of looking live while every
  action on it comes back refused. A job's configuration sits in msdb whether or not Agent runs, so the jobs
  themselves still list — the folder just tells you nothing will fire.
- **The job list says what it is worth saying at a glance.** A job that is switched off reads *disabled*, a
  job whose last run failed, retried or was cancelled carries that as a badge, and hovering any job shows
  when it last ran. A job that has never run stays unlabelled rather than being reported as failed.
- **Agent jobs have a Properties dialog.** Right-click a job ▸ Properties… opens the same page-rail dialog
  Database Properties uses. **General** shows what the job is and lets you change it — enabled, description,
  category and owner are editable and saved back to Agent. **History** lists every run Agent still holds,
  job outcomes and step rows together, with the message that says why a run ended the way it did. No cap of
  our own: Agent's own retention already bounds what is there.
- **Job steps can be edited, not just read.** The **Steps** page adds, changes, deletes and reorders a job's
  steps: name, type, the account it runs as, database, command, output file, retry attempts and interval, and
  what happens on success and on failure — including jumping to a specific step. The type list comes from the
  server rather than a fixed set, so a box without SSIS or replication does not offer them.
- **Schedules can be created, changed and removed** from the **Schedules** page: daily, weekly, monthly on a
  day or on a relative day ("the second Friday"), one time, or tied to Agent starting or the CPUs going idle
  — each either at one moment or repeating inside a window. The page writes the sentence it is about to save
  as you build it, so "every 2 weeks on Mon, Wed, Fri, every 30 minutes between 08:00:00 and 17:00:00" is
  something you read rather than something you infer from six numeric fields. Removing a schedule detaches it
  and only deletes it when no other job still uses it.
- **Alerts, Notifications and Targets complete the dialog.** **Alerts** creates and edits the alerts that
  respond by running the job — on an error number, an error severity, or a performance condition, optionally
  narrowed to one database and a phrase in the message. **Notifications** sets who Agent tells and when, per
  channel. **Targets** shows which servers run the job, and says so plainly when none does: a job with no
  target server is configured but will never fire.
- **Jobs can be created and deleted from the tree.** *New Job…* on the Agent Jobs folder makes a job and
  targets it at the local server in one step, so it is not born in that never-fires state. *Delete Job…* on a
  job removes it with its steps and history.
- **SSIS job steps have a real editor.** On the **Steps** page of an Agent job, picking `SSIS` as the type
  now replaces the command box with the fields the command is actually made of: where the package lives —
  the SSIS catalog, the file system, or either package store — the package itself, the environment to run it
  against, the logging level, the 32-bit runtime flag, and per-connection-manager overrides. Catalog packages
  are picked from a browser rather than typed, folder to project to package. The `dtexec` command is built
  from those fields and shown read-only, because it is a generated argument string in which every escaped
  quote matters and a hand-typed mistake only surfaces when the job runs.
- **Existing SSIS steps are read back into that editor**, which matters more than the editor does: nearly
  every one of them was written by SSMS. A step whose command uses an option the editor does not model keeps
  its text box with the command untouched, and says which option that was — a dropped option would change
  what the step does without anyone touching the field. An environment reference the catalog no longer has is
  reported by id instead of quietly resetting to none, which is the most common way an SSIS step is broken
  and the one whose run-time error points nowhere.
- **The Run as list now only offers proxies that may run the selected type.** A proxy is granted per
  subsystem, and offering one without the grant produced a step that failed to save or could not start. Where
  a step has no proxy the SSIS editor names the account it will actually use — the Agent service account,
  which usually has no rights on SSISDB — instead of the unhelpfully vague "(default)".
- **The connection import reaches four more clients.** Alongside DataGrip and DBeaver it now also finds:
  - **`.pg_service.conf`** — libpq's connection-service file, where each `[section]` is a named PostgreSQL
    service.
  - **MySQL Workbench** — the saved connections in `connections.xml`.
  - **Azure Data Studio and VS Code** — the `mssql.connections` profiles in your user settings. The SQL
    Server way of writing one endpoint as `host,1433`, `tcp:host,1433` or `host\INSTANCE` is split into the
    host and port fields; a named instance is dropped, since resolving it needs the SQL Browser.
  - **MongoDB Compass** — its saved connections, including Atlas `mongodb+srv://` URIs (which carry no port
    to import, so the provider's default stands).
- **A password that the source file spells out now comes along**, so you don't retype what DataTray just
  read: the `password=` line in `.pg_service.conf`, an inline `user:password@host` in a URI, and the
  `password` an editor writes when you decline its credential store. It goes straight to your OS keychain,
  the same place a hand-typed password lands, and never into the config file.
- **A password held in another application's secret store is still left alone** — not the OS keychain
  entries DataGrip and MongoDB Compass use, and not DBeaver's encrypted credentials file. Those
  connections arrive without one and ask on first connect.
- Because that differs per client, each row in the picker says whether a password is coming with it.
- **The import can now fetch the passwords DataGrip keeps in your system's credential store**, so a
  DataGrip connection arrives ready to use instead of asking you to retype what it already had. Nothing is
  fetched while DataTray reads config files: the picker shows a separate *"Also fetch N passwords…"*
  button, and pressing it is the opt-in. Your operating system still decides — it may prompt, and it may
  refuse; both simply mean the connection comes in without a password, exactly as before.
- Whatever comes back goes straight into DataTray's own keychain, the same place a hand-typed password
  lands, and never into a config file.
- **DBeaver is deliberately still left alone.** Its passwords are sealed with a key that ships inside
  DBeaver itself rather than held by your operating system, so nothing there is asked on your behalf —
  which is the whole basis on which reading another client's passwords is reasonable.
- **A first launch now welcomes you instead of leaving you with a tray icon.** A four-step wizard —
  welcome, engine, connection, done — runs once on a fresh profile and never again. Skip is on every step,
  and skipping counts as an answer: it won't ask again.
- **Choosing an engine is where the plugin model is introduced**, because it's where it answers a question
  you're already asking. The four included engines sit above the ones the Plugin Store offers, and picking
  one of those opens the store to install it.
- Plugins load when DataTray starts, so an engine installed during onboarding needs a restart before you
  can connect with it. The wizard says so, restarts, and **comes back to the step and engine you were on**
  rather than starting over.
- **The wizard offers to take over your existing connections** the moment you reach the engine step —
  anyone installing a database client already has one, and DataTray reads six of them. Rows that can't be
  imported (a MongoDB connection with no MongoDB plugin, say) are listed and disabled rather than hidden.
- The connection step asks for the engine's basic fields and nothing else. Colour, folder, read-only mode
  and AI access are all real settings, and all of them belong in the Connection Manager rather than on
  someone's first screen.
- The last step names the three things nobody finds on their own: DataTray keeps running in the tray after
  you close its window, AI access is off for every connection until you turn it on, and there are more
  engines and tools under *Tools › Plugin Store*.
- **"Copy as HTML" now produces a formatted table, and you pick the look.** Settings → Query → *HTML table
  style* offers a filled header (the new default), hairlines, a filled header with striped rows, or the plain
  table it used to be. The styled variants right-align numeric columns, spell NULL out in grey so an empty
  cell stays distinguishable from the text "NULL", and carry their own font — all of it inline, because
  Outlook renders with Word's engine and ignores a stylesheet. The same setting applies to the HTML export.
- **Activity Monitor for SQL Server, modelled on SSMS.** Right-click a SQL Server connection ▸ Activity Monitor —
  the same entry point as before — for a live tab: four graphs (% Processor Time, Waiting Tasks, Database I/O,
  Batch Requests/sec) over five collapsible grids — Processes (all fifteen SSMS columns, with Kill Process),
  Resource Waits, Data File I/O, Recent Expensive Queries and Active Expensive Queries. Every grid sorts and
  filters, and the whole tab refreshes on a timer (10 seconds by default, 5s–60s or off). Read-only apart
  from Kill Process, which asks first.
- On SQL Server this replaces the previous one-grid Activity Monitor, which showed ten columns of the same
  session list. Postgres and MySQL keep theirs unchanged.
- **Index maintenance from the tree (SQL Server).** The Indexes node under a table now offers Rebuild All,
  Reorganize All and Disable All straight on its context menu, and a single index offers Rebuild,
  Reorganize, Disable and Drop the way SSMS has them — no submenu in between. Each one confirms against the
  table's **current fragmentation** — index, type, fragmentation percentage and page count, read from
  `sys.dm_db_index_physical_stats` as the dialog opens — so a bulk rebuild is decided on which indexes are
  actually fragmented rather than on the table's name. Disabling
  and dropping ask for confirmation first; dropping an index that backs a primary key or unique constraint
  says so, and names the constraint to drop instead, rather than passing on SQL Server's "an explicit DROP
  INDEX is not allowed".
- Tool plugins are now told which node they were launched under, not just its name — an index tool can tell
  which table it is acting on. Tools that ignore it are unaffected (tool API 7). A tool's own dialog view
  is told the same thing, so it can show live data about that object (tool API 8).
- **New Index….** Right-click a table's Indexes node to create one: a name, its key columns in order (each
  ascending or descending, picked from the table's own columns) and whether it is unique. The generated
  `CREATE INDEX` is shown before it runs and can be edited first — which is also where a SQL Server user
  adds `CLUSTERED`, or anyone writes an index on an expression. Available on SQL Server, PostgreSQL, MySQL
  and SQLite. Until now DataTray could create databases, schemas, tables and columns, but there was no path
  to a `CREATE INDEX` anywhere in the app.
- **Index Properties for SQL Server.** SQL Server's Indexes node now opens SSMS' Index Properties dialog
  instead of the generic New Index form: key columns in order with a sort direction each, included columns,
  clustered or nonclustered, and unique. The same dialog opens on an existing index from Properties…, so an
  index can be changed rather than dropped and rebuilt by hand — and **Script** hands the T-SQL to a query
  tab without running it. Editing an index re-emits its full option set, because SQL Server's
  `DROP_EXISTING` silently resets every setting a rebuild does not restate; without that, changing one
  column would quietly revert fill factor, page locks and statistics settings nobody had touched.
  PostgreSQL, MySQL and SQLite keep the generic New Index dialog unchanged.
- **Index options, storage and filter.** Locking, duplicate handling, statistics recompute, sequential-key
  optimisation, fill factor and padding are all editable, as are the filegroup or partition scheme the index
  lives on and its filter predicate. Every setting says when it takes effect — **on OK** in place, **rebuilds**
  the index, or **next rebuild** only and stored nowhere — a distinction SSMS leaves invisible and which is
  the difference between a metadata change and reading every page of the index. Flipping only an in-place
  setting now runs `ALTER INDEX … SET` rather than rebuilding for it.
- **Index fragmentation and extended properties.** A Fragmentation page showing how fragmented the index is
  and how much space it takes — opening on the cheap scan, with the full one behind a button that says up
  front how many pages and megabytes it will read, since SSMS's version of this page scans the whole index
  before it shows you anything. Extended properties are editable as a Name/Value grid, written on OK with
  the rest of the dialog rather than the moment you type them.
- **The toolbar at the top of the window is yours now.** *Settings ▸ Toolbar* lists every action that can
  sit there: drag a row to reorder it, untick one to hide it. The arrangement is saved to `toolbar.json`
  beside your keymap, and *Reset to defaults* puts it back in one click. Hiding a button takes nothing
  away — the action stays in the menus and can still be given a keyboard shortcut.
- **Toolbars now follow the window instead of getting cut off.** The application toolbar and each query
  window's toolbar measure what fits at the current width and move the rest into a "…" menu beside them —
  so a narrow window keeps every action reachable rather than clipping it. The controls you cannot operate
  from a menu that closes when you click it — the connection and database pickers, the Browse filter box —
  stay put whatever the width. Your order is also the priority order: what you put first survives longest
  as the window narrows.
- **Plugins can put buttons in both toolbars.** A plugin asks for the new `toolbar` permission at install —
  listed separately from `menu`, because a button in the toolbar is not the same request as an item in a
  menu — and its actions then join the same list everyone else is in: you decide whether to show them,
  where they sit, and what key they answer to. A query-window button decides for itself which tabs it
  belongs on, so an SQL Server-only action simply is not there on a PostgreSQL tab.
- **Local Containers 0.5.0** puts *New container* one click away in the application toolbar instead of two
  in the Tools menu. Update it from *Tools ▸ Plugin Store*; it needs this release of DataTray.
- **The SQL Server Activity Monitor says which server you are looking at.** The build and the operating
  system under it — "Microsoft SQL Server 2025 (RTM-CU6) - 17.0.4055.5 (X64) · Linux (Ubuntu 24.04.4 LTS)"
  — sit at the right-hand end of the monitor's toolbar, with the full `@@VERSION` on the tooltip.
- **Database Properties gains the pages and rows SSMS has.** For SQL Server: a **Configurations** page
  (database-scoped configurations, with their secondary values) and a **Transaction Log Shipping** page,
  plus a log-shipping row on General. Filegroups now shows `Autogrow All Files` and the FILESTREAM and
  memory-optimized sections it used to filter out entirely. Options fills in the rows that were simply
  absent — the whole Cursor and ANSI blocks, database state, target recovery time, delayed durability,
  trustworthy, parameterization, FILESTREAM and the rest of Service Broker. Query Store gains its Capture
  Policy section and a current-disk-usage bar.
- **A result set can be shown as something other than a grid.** Next to the result-set tabs there is now a
  *View* switcher — Grid, plus whatever viewer plugins apply to what you are looking at. The grid stays the
  default and is always the first entry; a viewer is read-only, so switching to one never puts your pending
  edits at risk. The switcher only appears when there is something to switch to.
- **Viewers are a plugin type**, the fourth alongside providers, tools and MCP plugins. A viewer declares
  `type: "viewer"` and gets handed a read-only snapshot of the current result set; it decides for itself
  whether it can render one, so it drops out of the switcher on a result set it has nothing to say about. It
  follows the tab from there — turning a browse page or letting a monitor refresh updates the viewer in place
  instead of rebuilding it, so its scroll position and expanded nodes survive.
- **Basic Result Viewers** is the first one — a store-only plugin carrying two renderers, neither of which
  adds a dependency to DataTray:
  - **JSON** shows a row as an object tree, and parses a text cell that itself holds JSON into a subtree
    rather than one long escaped string — the reason to reach for it on a table with a `jsonb` column. It
    opens on the first row and follows the grid from there, so switching to it shows your data rather than
    asking you to pick some first.
  - **Image** decodes the selected row's binary column to a picture (PNG, JPEG, GIF, BMP). A BLOB column holds
    arbitrary bytes, so bytes that are not a picture say so in place rather than making the view vanish
    mid-browse.
- **See the schema as a diagram.** A new **store-only** tool plugin — install it from *Tools › Plugin
  Store*; it is not bundled with the app. Right-click a connection, database or schema on Postgres,
  MySQL, SQL Server or SQLite and pick *ER Diagram*, and it opens as a tab beside your queries rather
  than as a dialog, so you can read it while you work. It opens on a picker rather than on a canvas:
  a schema with two hundred tables drawn blind is a hairball nobody reads, so you choose what to draw
  and **+ Related** pulls in what a table connects to, one hop at a time. Tables are laid out by
  dependency depth, left to right, with the foreign keys drawn between the columns that hold them.
- **A diagram can be saved, reopened and exported.** Saving records which tables you drew and nothing
  about what is inside them, so reopening reads the database as it is *now* — and says so when a table
  you had drawn no longer exists, rather than quietly leaving it out. Export writes PNG for pasting, or
  SVG when you want the table names to stay searchable text.
The Activity Monitor's Processes, Recent Expensive Queries and Active Expensive Queries grids each have a
Database dropdown again, listing the databases that currently have rows. It stacks with the free-text
filter beside it, so "user processes in Sales" is one grid away, and it survives the auto-refresh.

Double-clicking a row in either query grid opens the whole statement in a window, with the server's own
line breaks and indentation, selectable and with a Copy button — the grids collapse a query onto one line
to keep the rows readable, which recognises a statement but cannot read one.
- **The Activity Monitor can be narrowed to one database, or to just the blocking sessions.** Two new
  toolbar controls: a **Database** dropdown listing the databases that actually have sessions right now,
  and a **Blocking only** checkbox that keeps the blocked sessions *and* the sessions blocking them — so
  the culprit is on screen next to the victim, not filtered away. Both filter the snapshot you already
  have, so they apply instantly and survive auto-refresh. On SQL Server both are available; Postgres and
  MySQL get the Database filter (their session views have no blocker column).

### Changed

- **The plugin-cyan in the first-run wizard is readable in the light theme.** The Store tiles, their
  "Install" labels and the plugin bullet used one fixed cyan that was too pale on a light background and too
  dark on a dark one. Each theme now has its own, and the brand colours behind them live in one place instead
  of being repeated per view.
- **Updates are delivered by Velopack.** The download, the installer and the update feed are now built by one
  tool, so an update installs itself and restarts the app on every platform — including macOS, which used to
  hand you a disk image to finish by hand. Installs on the previous update method are carried across
  automatically: the build they are offered is a Velopack build, so everything after that one is handled by
  the new updater. Windows shows the new installer once during that step, and macOS goes through the disk
  image one last time.
- **The Windows download is no longer a single self-extracting file.** The updater replaces an application
  folder and patches it file by file, which a packed single executable makes impossible. Pick the
  `-Setup.exe` for a per-user install with a Start-menu entry, or the `-Portable.zip` to keep it loose.
- **An older Windows installation is now pointed out.** The previous installer and the new one use different
  folders, so the old copy stays behind with a working Start-menu shortcut that opens the previous version on
  the same data. DataTray offers to remove it once, and leaves it alone if you would rather keep both. Your
  connections, settings and installed plugins live elsewhere and are never touched.
- **Database Properties can change things now, not just show them.** For SQL Server the Options page is
  editable — recovery model, the auto-* settings, page verify, the ANSI and cursor blocks, trustworthy,
  parameterization, delayed durability, target recovery time, read-only, restrict access, snapshot
  isolation and the broker — as are database-level extended properties and each file's autogrowth and
  maximum size. Everything is written when you press **OK**, so a dialog you cancel leaves nothing behind,
  and only the settings you actually changed are issued. The four options SQL Server cannot change while
  other sessions are connected say so and stay unwritten until you explicitly agree to disconnect them:
  left to itself, SQL Server does not refuse those, it waits indefinitely, which looks exactly like the
  application hanging.

### Removed

- **"Roll back to the previous version" is gone.** It only ever worked on the Linux AppImage, where the
  previous build was kept beside the running one, and the new updater has no equivalent. To go back, install
  the older version from the releases page.
- **The update dialog no longer shows the build date and commit.** Those came from a manifest DataTray
  published itself; the new update feed does not carry them. The version and the release notes stay.

### Fixed

- **DataTray starts even when an old SQL Explorer install is running.** The two shared one
  single-instance lock, left over from the rename, so launching DataTray while the older app was open
  did nothing visible — DataTray quietly exited and raised the *other* app's window instead. They now
  run side by side, which is what having separate settings folders already implied.
- **"Copy as HTML" now pastes as a real table.** The result went onto the clipboard as plain text, so
  Outlook, Word and other rich-text targets pasted the markup instead of a formatted table — a paste
  target picks by clipboard format, not by what the text happens to look like. The copy now carries the
  platform's own HTML format alongside the plain text, so text-only targets still get the markup.
- **On macOS, DataTray is called DataTray in the menu bar.** It used to say "Avalonia Application" next
  to the Apple logo, and in **About …**, **Hide …** and **Quit …** — the one place the app's name is
  not taken from the bundle.
- **Local Containers finds Docker on macOS.** The panel reported "Docker was not found on this machine"
  with Docker Desktop running and working. An app started from Finder or the Dock inherits a minimal
  search path that contains none of the places Docker Desktop puts its command-line tool, so DataTray
  looked in four directories that never hold it. It now also looks where Docker Desktop and Homebrew
  actually install — which fixes pulls from a private registry on macOS too, since the credential helper
  lives in the same place.
- **The macOS build is signed again.** Bundled plugins brought native libraries for platforms DataTray
  does not run on — Android, iOS, WebAssembly — and a per-architecture copy of the SQL Server driver in
  folders macOS mistakes for nested app bundles. Signing refused the whole app over it. The payload now
  carries only what the target platform uses, which also makes the download smaller.
- Query editor: a string literal holding a mail address or a URL is no longer coloured as a link — string literals now always get the string colour.
- **The SQL Server Activity Monitor no longer dies on "An item with the same key has already been
  added".** SQL Server keeps one row of query statistics per cached *plan*, so the same statement shows up
  several times over — once per database a shared batch ran in, once more after a recompile, once each for
  a serial and a parallel plan. Recent Expensive Queries treated those rows as separate queries, which
  split one query's cost across several rows and, when two of them collided, failed the whole refresh. The
  rows for a statement are now summed into one, so the grid shows each query once with its full cost.
- **"% Processor Time" in the SQL Server Activity Monitor works on SQL Server on Linux.** The graph read a
  perfmon counter the resource governor fills in, which comes back flat zero on some Linux builds — so the
  one graph that answers "is this server busy" sat at nothing while the server worked. It is now measured
  from the engine's own CPU accounting, the same on either platform, and differenced between two refreshes
  like every other rate in the tab. It therefore says nothing on the first refresh and a figure from the
  second on.
- **Database Properties ▸ Permissions showed rows that meant different things but looked identical.** It
  listed every grant in the database — including one per table, view and column — while never showing which
  object each was on, so a dozen rows reading `public / SELECT / Object Or Column` were a dozen different
  grants. It now shows permissions on the database itself, which is what this dialog is about and what SSMS
  shows here, and names the grantor as well as the grantee.
- **Context menus have a visible edge again, and no longer open with a stray line at the top.** On a
  dark theme the popup was the same near-black as the panel behind it and Linux draws no shadow around
  it, so the tree rows a menu half-covered looked like part of the menu — the right-click menu on an
  Indexes folder read as a jumble of half-words and commands. Menus now carry a hairline border. The
  separator that groups a node's own actions (Rebuild All, Reorganize All, …) is also gone from the top
  of those menus: everything above it is hidden on exactly the nodes that have such actions, so it was
  dividing nothing.
- **The SQL Server Activity Monitor's grids update, and their column headers sort.** Every grid in the tab
  was showing the values it was first drawn with: the cells were bound to the row in a way that never
  announced a change, so ten seconds of new figures arrived behind a screen that kept the old ones — and a
  click on a column header reordered rows nobody could see move. Both are fixed together. Numeric columns
  now also sort by value rather than by digits, including the ones written with a unit ("1,234 ms" no
  longer sorts between "1 ms" and "2 ms"), and rows a sorted column cannot tell apart keep their order
  instead of reshuffling on every refresh.
The Activity Monitor's Recent Expensive Queries grid showed an empty Database for almost every row.
`sys.dm_exec_sql_text` only fills in a database for compiled objects and leaves it NULL for ad-hoc
batches, which is most of that grid; the database a query actually ran against is on the plan, and is now
read from there. The same statement executed against several databases is therefore several rows again —
one per database, each with its own cost — instead of one row labelled with an arbitrary one of them.
- **The Activity Monitor stops rebuilding its grid on every refresh.** Each auto-refresh threw the whole
  column layout away and built it back identically, so column widths you had dragged reset every five
  seconds, and a refresh landing at the wrong moment could take the app down with it — a crash reported
  as "it dies when I click the Activity Monitor again" that nobody could reproduce. The refresh now
  replaces only the rows unless the columns genuinely changed. A monitor tab you are not looking at no
  longer queries the server at all, and comes back with fresh sessions the moment you return to it.
- **A row action can no longer act on a session that has just been refreshed away.** With the context
  menu open across an auto-refresh, *Kill* and *Cancel Query* still pointed at the row from the previous
  snapshot.
- **A crash now leaves something behind.** An unhandled error took the app down without writing anything,
  so a crash you could not reproduce was a dead end for whoever had to look into it. It is now appended
  to `restart.log` in the app's settings folder, stack trace and all.

## [0.7.0] - 2026-07-29

### Added

- **DuckDB is a supported engine.** A store-only provider plugin — install it from *Tools › Plugin Store*; it
  is not bundled with the app. It opens a `.duckdb` file, or an in-memory database (under Advanced) when you
  only want a scratchpad, and puts its schemas, tables, views, sequences, columns and indexes in the tree,
  with row counts and column counts on the nodes. Queries, scripts, `EXPLAIN`, *View Definition*, and DDL
  Create for schemas and tables work as they do for the other SQL engines. The download is ~82 MB: DuckDB is
  an embedded engine, so the plugin carries the engine itself for every platform DataTray runs on.
- **A query can read a file directly**, which is the reason to reach for DuckDB in the first place:
  `SELECT * FROM 'events.parquet'`, `read_csv_auto('data.csv')` and `read_json_auto(…)` all just work in a
  query tab, against an in-memory connection with no database file at all. Such a result set is read-only —
  there is no table behind it to write to.
- **Editing works for a plain browse.** DuckDB's driver does not tell us which table a result set came from,
  so the provider works it out from the query itself: double-click a table, filter it, sort it or page
  through it and the grid stays editable, provided the table has a primary key and the query still selects
  it. Anything more — a join, an aggregate, a view, a file — comes back read-only rather than risking a save
  against the wrong rows.
- Cancelling a long query genuinely stops it. DuckDB is an analytics engine, so a query that runs for a
  minute is normal rather than a mistake, and its driver ignores the usual cancellation signal; the provider
  bridges that so the Cancel button does what it says.
- **ClickHouse is a supported engine.** A store-only provider plugin — install it from *Tools › Plugin
  Store*; it is not bundled with the app. It connects over ClickHouse's HTTP interface
  (port 8123, or 8443 with the protocol set to `https` for ClickHouse Cloud) and puts its databases, tables,
  views, columns and data-skipping indexes in the schema tree, with on-disk sizes and row counts on the
  nodes. Queries, scripts, `EXPLAIN`, the database switcher, *View Definition*, DDL Create for databases and
  tables, the Activity Monitor and user/role management all work as they do for the other SQL engines. Spin
  a local server up from the *Local Containers* plugin like any other engine — the provider ships its own
  container recipe.
- Three things follow from ClickHouse being columnar rather than row-oriented, and are worth knowing rather
  than discovering: result grids are **read-only** (the HTTP protocol carries no primary-key metadata to
  trace a result back to one table, and ClickHouse edits rows through asynchronous `ALTER TABLE … UPDATE`
  mutations, which a generated `UPDATE … WHERE key = …` would not express); a multi-statement script is sent
  **one statement per request**, because the server rejects a batched body outright; and a batch is **not
  transactional**, since ClickHouse has no transaction for ordinary MergeTree work — a failure leaves the
  statements before it applied.
- Generated `CREATE TABLE` DDL declares `ENGINE = MergeTree` with an `ORDER BY` over the columns marked as
  primary key (`tuple()` when none are), and expresses a nullable column as `Nullable(T)` — ClickHouse
  columns are NOT NULL unless the type says otherwise, the opposite of every other engine here.

### Changed

- **Containers DataTray starts are now labelled `kontena.source=datatray`**, not `sqlexplorer`. The
  Kontena desktop app shows them under the new name; containers created before this release keep the
  old label and are still recognised. Anything else filtering on the old value needs updating.

## [0.6.1] - 2026-07-27

### Changed

- **Downloads are named `DataTray-<version>-…` instead of `LionearDataTray-<version>-…`.** The prefix
  repeated something the download URL already says, and it read as part of the product name. The
  installer, the portable zip, the AppImage and the DMG all lose it. Releases published before this
  keep the filenames they shipped with, and the in-app updater is unaffected — it resolves builds
  through the release manifest, not by guessing a filename.

## [0.6.0] - 2026-07-27

### Added

- **Connections can reach a database through an SSH tunnel.** A database that only listens inside a private
  network no longer needs a hand-rolled `ssh -L` next to DataTray: switch on *Connect through an SSH
  tunnel* under Advanced, fill in the bastion's host, user and either a password or a private key, and the
  connection is forwarded through it. The tunnel opens on first use, is shared by every connection taking the
  same route, and closes when you disconnect or quit. Test does the same thing, so a tunnel can be checked
  before the connection is saved.
- SSH passwords and key passphrases are stored in the OS keychain like every other connection secret, never
  in the connection file. Filling in the server's SHA256 host-key fingerprint pins it — a bastion presenting
  a different key is then refused instead of trusted.
- The section only appears for engines that connect to a host, and needs nothing from the provider: existing
  provider plugins gained tunnelling without a change or a rebuild.
- **The result grid's cell editor now matches the column's type.** A boolean column edits as a checkbox
  and a date column as a date picker, instead of typing `true` or a date as text and hoping it parses. A
  column that accepts NULL gets a three-state checkbox, so clearing it is still a NULL rather than a
  `false`; a date cell can be cleared back to NULL the same way. Picking a date keeps the cell's existing
  time of day, so editing the date half of a timestamp no longer drops the time. Every other column type
  keeps the text editor it had.
- **Star a query to keep it.** Every row in the History panel has a star; starred queries are kept in
  their own store, so they stay in the list after *Clear history* and when the history rolls over — a row
  that history no longer holds simply shows no row count or duration, and still opens in a query tab on
  double-click. The star in the panel header narrows the list to starred queries only.
- **Star a connection to keep it within reach.** Right-click a connection → *Add to favorites*, and it
  appears in a Favorites section pinned to the top of the sidebar, whatever folder it lives in. By default
  it stays visible in its own folder as well — the section is a shortcut list. Settings → General → *Keep
  favorites in their folder too* turns that off, and a starred connection then moves to Favorites instead
  of being shown twice. Temporary connections (created by an AI client over MCP) can't be starred, since
  they don't outlive the session.

### Changed

- **The date editor's calendar now follows the app's own theme.** The day grid, the month header and the
  navigation arrows used to come straight from the Fluent theme, so the panel around them matched the rest
  of the app while the calendar inside it did not. Day cells, hover and the month header now use the app's
  colours, the selected day is a filled accent cell instead of a thin blue ring, and today is marked with
  accent-coloured text rather than a solid block that competed with the selection.
- The flyout is also noticeably smaller: Fluent sizes a calendar for fingertips, which in a grid cell's
  popup dwarfed everything around it.
- **SQL Explorer is now called DataTray.** Same application, new name — the window title, the tray tooltip,
  the About dialog, the plugin contract and the published downloads all say DataTray, and the macOS bundle
  is `DataTray.app`. Upgrading over an existing SQL Explorer install replaces it in place rather than
  leaving a second copy behind.
- **Your settings move to the DataTray folder by themselves.** On first start after the rename, DataTray
  copies the old SQL Explorer folder — connections, query history, favourites, keymap, open tabs and
  installed plugins — to its own. Saved passwords move too, the first time each connection needs one.
- The old folder is left where it is rather than deleted, so an older SQL Explorer build still starts with
  everything intact. It gets a `MOVED-TO-DATATRAY.txt` note pointing at the folder that is now live. The
  two stop tracking each other from that point on, so changes made in DataTray won't show up in the old
  build — delete the old folder once you're sure you won't go back.
- **New application icon.** DataTray now carries its own mark — a database cylinder with a puzzle piece —
  in the window, the tray, the taskbar, the About dialog and the installers, replacing the one it inherited
  from SQL Explorer.

### Fixed

- **A NULL cell no longer turns into an empty string just because you passed through its editor.** In the
  editable grid, opening a NULL cell and leaving it without typing could write an empty string back — on a
  text column that is a real change, and it saved as `''` instead of leaving the NULL. Empty text over a
  NULL cell now leaves the NULL alone; *Set empty* in the cell's right-click menu writes an empty string
  deliberately, next to the existing *Set NULL*.
- **Adding a row now writes a NULL you asked for.** Columns you leave untouched are still left to the
  database, so defaults and auto-increment keys work as before — but a cell you explicitly set to NULL is
  written as NULL, where it used to be dropped from the INSERT and silently replaced by the column's
  default.

## [0.5.0] - 2026-07-26

### Added

- **Schema Diff now reads secondary indexes and supports SQLite.** A migration includes the `CREATE INDEX` /
  `DROP INDEX` work it used to silently skip, and SQLite databases can be compared at all (read through
  `sqlite_master` and PRAGMA rather than `information_schema`). Indexes an engine creates behind a primary
  key or unique constraint are left out, so they aren't dropped twice.
- **Copy Table — right-click a table and copy it to another connection and database.** A store-only tool
  plugin. Choose structure + data, structure only or data only, all rows or the first N, whether to keep the
  source's identity/sequence values, and whether to bring the table's indexes and foreign keys along. Either
  *run the copy* — creating and filling the table on the target, with a live checklist that shows which step
  failed if one does — or *open it as a script* on the target to review the SQL first; the tool remembers
  which you used last. Rows are copied in batches, so a large table shows real progress instead of one long
  wait, and indexes and foreign keys are created once the rows are in: a foreign key pointing at a table the
  copy didn't bring along is reported as skipped rather than failing a copy that otherwise landed. Postgres,
  MySQL, SQL Server and SQLite, with source and target on the same engine — copying between different engines
  needs type mapping between dialects and is not attempted.
- **Tools can own their whole dialog** — a tool plugin's own view may now render the run's progress and result
  itself (stepped checklist with per-step detail and progress, and its own footer buttons) instead of the
  generic checklist and action bar. Copy Table is the first tool to use it; every other tool is unchanged.
- **Switching to a release channel that's behind you now says so, and offers the switch.** Moving from Nightly
  to Stable while Stable is on an older version used to do nothing visible: an update notification never
  offers a lower version — correctly — so there was no signal and no way through. Picking such a channel now
  asks outright, naming both versions, and "Switch & downgrade" queues that build for install. Automatic
  update checks still never present an older build as an update.
- **Generate Scripts (store-only tool plugin) — script a whole database as `CREATE` statements.** Right-click a
  database or a connection and get every table as DDL, optionally with its indexes and foreign keys, and
  optionally preceded by `DROP TABLE`. The script opens in a query tab, or is written to a `.sql` file for
  checking into a repository. Foreign keys are emitted after every table exists and drops run in reverse, so
  the file runs top to bottom; tables come out in a stable order, so re-running produces the same file.
  Postgres, MySQL, SQL Server and SQLite.

### Changed

- **Changelog entries are now written as fragments** under `changelog.d/`, one file per change, instead
  of appending to `CHANGELOG.md` directly. Nothing changes about the released changelog — the release
  folds the fragments into it — but two branches can no longer collide on the same line while both are
  in flight. See `changelog.d/README.md`.

### Fixed

- **`varchar(max)` columns are no longer copied as `varchar(1)`.** SQL Server reports the MAX variants as a
  length of -1, which was read as "no length" — and a bare `varchar` in a `CREATE TABLE` means one character
  on SQL Server. The copied or recreated column held a single character and every insert failed with "String
  or binary data would be truncated". `varchar(max)`, `nvarchar(max)` and `varbinary(max)` now come across
  intact, and types whose name already fixes their length (`text`, `longtext`, `mediumblob`, …) no longer get
  an invalid length appended.
- **A migration no longer drops a table's auto-numbering.** Recreating a table on the target lost its
  MySQL `AUTO_INCREMENT` or SQL Server `IDENTITY` — the script ran, but the table was subtly wrong and the
  next insert failed or wrote an empty key. Auto-numbered columns are now read and recreated on every
  engine, and a column that gained or lost its auto-numbering is called out in the migration, since no
  engine can switch that in place.
- **Schema Diff no longer reports constraints the engine named itself as changes.** Two SQL Server databases
  with the same schema carry different invented names for the same unique constraint or foreign key
  (`UQ__customer__AB6E6164DF5AECAE`), so every one of them was dropped and recreated — correct, but it
  buried the real changes. Constraints left unmatched by name are now paired up by what they actually
  describe, which also reads a deliberately renamed constraint as no structural change.
- **A script no longer dumps every row of every table — and every result tab gets its own Previous/Next.**
  `SELECT * FROM a; SELECT * FROM b;` returned both tables in full, because paging only ever applied to a
  single SELECT. When a script is nothing but SELECTs, each result tab now pages independently: the tab shows
  which rows you're looking at ("rows 201–400"), Previous/Next move just that tab, and switching tabs moves
  the page bar to where that tab is. A script that mixes SELECTs with other statements can't map tabs to
  statements safely, so it has no page bar — but its SELECTs are still bounded to one page each on the
  server, and the Output panel says so. Statements with their own `TOP`/`LIMIT` and non-SELECTs run exactly
  as written, and the whole thing follows the existing "Page query results" setting.
- **A query that ends in a semicolon can be paged again.** `SELECT * FROM Donations;` failed with "Incorrect
  syntax near the keyword 'ORDER'" (and the equivalent on every other engine), because paging appends its
  `ORDER BY … OFFSET … FETCH` / `LIMIT` *after* the statement — semicolon and all. The terminator is now
  dropped before the page is built, and a stray extra semicolon no longer costs you the page bar either.
- **Schema Diff against MySQL compared the wrong things.** Two MySQL databases diffed as "drop everything,
  recreate everything", because MySQL's schema *is* the database, and foreign keys came out referencing the
  same column several times. Both are corrected, and a MySQL migration now applies cleanly.
- **Schema Diff produced scripts that couldn't run.** A Postgres `serial` column was recreated with a
  `DEFAULT nextval(…)` pointing at a sequence that doesn't exist on the target; `DROP INDEX` was emitted in
  Postgres form for every engine, though MySQL and SQL Server need the table named. Generated migrations for
  Postgres, MySQL, SQL Server and SQLite are now verified end-to-end against live engines.

## [0.4.0] - 2026-07-21

### Fixed

- **Editing a connection no longer wipes its fields** — changing a connection's AI access, read-only or
  other settings could reset host / port / database / username to the provider defaults and drop the saved
  password. Editing now preserves the stored values, and setting AI access from the tree no longer
  round-trips (and so can't clear) the password.
- **Startup restores the tab you left on** — with "Restore tabs on startup", the previously *selected* tab is
  now reselected instead of always landing on the last one in the row.
- Small UI polish: the results **Export** action now reads as a button (not a text link); and the **AI activity**
  panel's toggle only appears while the MCP server is running (it live-appears/disappears as you start/stop the
  server).
- **Plugin Store "Update All" now clears its badges** — updating every plugin at once staged the updates
  correctly but the rows kept showing "update available" as if nothing happened; they now show as staged
  (restart required), matching the per-plugin Update button.
- **Staged plugin updates apply more reliably on restart** — a blocked rollback-backup folder could leave the
  old plugin version in place across every restart. The swap now falls back to replacing the current copy so
  the update still applies, and a swap that genuinely can't complete is logged instead of failing silently.
- **"What's new" notes no longer overflow the window** — long release notes in the app- and plugin-update
  changelog dialogs wrapped off the right edge and ran past the bottom; the text now wraps to the window width
  and scrolls vertically.
- The plugin-update notification now uses the Lucide icon set (a crisp refresh / download glyph) instead of a
  Unicode symbol that could render as a missing-glyph box on some systems.

### Added

- **Allow multiple instances** (Settings → General) — off by default, launching the app again brings the running
  window to the front (the single-instance behaviour). Turn it on to let each launch open its own independent
  window — handy for keeping two databases, or dev and prod, side by side. Takes effect on the next launch.
- **Script table data as INSERT** — right-click a table → *SQL commands ▸ INSERT (with data)* to generate real
  `INSERT` statements from the table's rows (Top 100, Top 1000, or all rows) into a new query tab, ready to run on
  another connection. Unlike the existing INSERT scaffold (which uses `:name` placeholders), this writes the actual
  values — dialect-correct for booleans, binary and dates — and never auto-runs.
- **Schema Diff tool** — a new first-party tool compares this database against a second one you pick — another
  connection and one of its databases — and generates the migration (an ALTER script that would make this one
  match the other), opening it in a new query tab on this connection/database so you review and run it in the
  normal editor. It diffs tables, columns (type / nullability / default), primary keys, unique constraints and
  foreign keys, and produces dialect-correct DDL for Postgres, MySQL and SQL Server. Reads via
  `information_schema`, so the picker offers same-provider connections only; SQLite and cross-engine diffs are
  not covered yet. Built on new plugin-SDK seams — `ToolFieldType.ConnectionPicker` and `DatabasePicker` plus
  `IToolHost.ListConnections()` / `ListDatabasesAsync()` / `OpenConnection()` / `OpenQueryEditor()` — so any
  tool can take a second connection and database and hand generated SQL to a query tab. Installs from the
  Plugin Store (not bundled with the app).
- **Icons in SQL completion** — each suggestion in the code-completion popup now carries an icon for its kind
  (table, column, function, foreign-key join condition, keyword), reusing the shared Lucide glyphs from the
  schema tree so a table reads the same in both places. The type / signature / join-condition detail alongside
  each item is unchanged.
- **Containers are tagged for Kontena** — containers created by the Local Containers plugin now carry
  `kontena.managed=true` / `kontena.source=sqlexplorer` labels (in both the compose file and the `docker run`
  snippet), so the Kontena desktop app can recognise them as SQL-Explorer-managed and leave them alone.
- **Query Log shows why it's empty** — when logging is off (or only one source is enabled), the Query Log
  window now shows a banner explaining it, instead of just an empty list.
- **Paged query results** — running a single `SELECT` with no `TOP`/`LIMIT` of its own now shows the results one
  page at a time with Previous/Next (DataGrip/DBeaver-style, default 200 rows/page), so a stray
  `SELECT * FROM big_table` doesn't pull the whole table at once; the row-range indicator shows which rows
  you're viewing. Queries with their own `TOP`/`LIMIT`, other statement types and multi-statement scripts run
  unchanged. Toggle and page size live under Settings → Query.
- **Scope-aware SQL completion** — code completion now understands query structure instead of scanning for
  `FROM`/`JOIN` with a regex. It resolves aliases through CTEs (`WITH x AS (…)`) and derived tables
  (`(SELECT …) d`), suggests the columns of the sources actually in scope, offers CTE names alongside real
  tables after `FROM`/`JOIN`, and never suggests from another statement in the editor. Expression positions
  (SELECT list, WHERE, …) now also suggest the engine's **built-in functions** with their signature —
  Postgres, MySQL, SQL Server and SQLite each ship their own catalogue (plugins declare theirs via the new
  `ISqlDialect.Functions`). And right after `JOIN … ON`, it offers the **foreign-key join condition** between
  the tables in scope (e.g. `o.user_id = u.id`) as the top suggestion.
- **Service auto-registration for plugins and the host** (plugin SDK) — classes can opt into dependency
  injection by implementing a lifetime marker (`ISingletonService` / `ITransientService` / `IScopedService`)
  instead of being wired up by hand. Extensions that declare the new `services` capability get their own
  services registered and resolvable via `IPluginRuntimeContext.Services`, scoped so a plugin can add
  services but never replace or read the app's. Plugin host API is now **v4**; extensions built for earlier
  versions keep loading.
- **Panel plugins can supply a toggle icon** (plugin SDK) — `IPanelPlugin.Icon` lets an extension's docked
  panel show its own glyph on the bottom bar instead of the generic default. The Local Containers panel now
  uses a container icon.
- **Provider-declared container recipes** (plugin SDK) — a database provider can declare how to spin up an
  empty local container matching its engine (`IDbProvider.ContainerRecipe`: image, port, data path, and the
  environment/command that carry credentials). The Local Containers plugin reads every installed provider's
  recipe through a new read-only `providers` capability, so a third-party engine becomes containerisable with
  no change to the host. Every first-party engine now ships its own recipe, so the plugin is purely
  provider-driven: the recipe travels with the engine and is the single source of truth.

### Changed

- **Double-click a result cell to open its value in a window** — long text and JSON are shown pretty-printed in
  a standalone, resizable window you can copy from, and several can be open side by side. This replaces the
  always-on strips below the grid (the click-to-view cell value and the selection count/sum/avg summary), which
  are gone.
- The connection tree's **AI access** submenu now marks the active level (None / Read-only / Read-write)
  with a check, so the current setting is visible at a glance instead of having to remember it.
- **Refreshed icon set** — the schema tree, tabs, toolbars and Settings now use a consistent
  [Lucide](https://lucide.dev)-based line-icon set, drawn as crisp vectors that tint with the theme (no
  icon font, no bundled raster assets). The AI-activity panel gets its own icon.
- **New local SQL Server containers use the 2025 image** — the Local Containers "create" flow now defaults
  to `mcr.microsoft.com/mssql/server:2025-latest` (was 2022). Every first-party provider (PostgreSQL, MySQL,
  SQL Server, MongoDB, Redis, DragonflyDB, Elasticsearch) now declares its own container recipe, so the
  recipe travels with the engine instead of being hardcoded in the Local Containers plugin.

## [0.3.0] - 2026-07-19

### Added

- **Open & save queries as `.sql` files** — `Ctrl+O` to open (or drag a `.sql` file onto the window),
  `Ctrl+S` to save, plus Save As and a File ▸ Recent menu. Tabs show a `●` dirty marker and remember
  their file across sessions, and closing a tab or the app offers to save unsaved changes — a
  preference in Settings ▸ Startup turns that prompt off. Saving pending grid-row edits back to the
  database moved from `Ctrl+S` to **`Ctrl+Shift+S`**.
- Configurable **update-check interval** — choose how often the app checks for a new release.
- A shared **"Copied" confirmation** for copy actions, shown bottom-centre.
- **SQL formatting options** in Settings — keyword casing (UPPERCASE / lowercase / preserve) and
  indent width.
- **Proactive plugin-update notifications** — an ambient top-bar badge and a persistent, actionable
  notification when compatible updates are available for your installed plugins, without opening the
  Plugin Store, plus a **per-plugin changelog** (from the notification or any updatable Store row).
  An opt-in **Auto-apply on restart** policy can stage compatible, non-pinned updates silently, and
  updates that need a newer app are shown ("Update app…") instead of hidden. Off / Notify / Auto in
  Settings ▸ Plugins.
- **Plugin Store "Updates" section** — installed plugins with an available update are grouped at the
  top of the Installed tab under "Updates", so you no longer have to hunt for which ones can update
  (they no longer also appear in the list below).
- **`extension` plugin type** — plugins are no longer only one-shot providers and tools: an
  `extension` plugin can run as a long-lived subsystem that contributes its own bottom panel,
  background work, Tools-menu items and managed connections, each behind a per-capability consent
  shown when you install it.
- **AI can create connections over MCP** — with the MCP server on and the new "Let the AI create
  connections" setting enabled (off by default), an AI client can list the available providers and
  create or delete database connections. Fail-closed: creation is refused unless you opt in, only
  loopback hosts are allowed until you add more, persistent connections are capped at read-write, and
  every create/delete is audited. New connections land in an "MCP" folder; temporary ones are
  session-only and cleared when the app closes.
- **AI-activity panel** — a bottom tool panel (toggled from the status bar) showing what an AI does
  over MCP: each call, the connection, and whether it was allowed or denied.
- **AI access on the connection tree** — connections carry "AI" and "Temporary" badges, and a
  right-click **AI access** submenu sets the level (None / Read-only / Read-write) or excludes a
  connection from the AI without opening the Connection Manager.
- **One bottom panel at a time** — a Settings ▸ Appearance toggle (on by default) so opening a bottom
  panel (Output, Containers, AI activity) closes the others instead of stacking them.
- **Search the Settings** — a search box above the category rail filters categories by name or by the
  settings inside them (e.g. "token" surfaces MCP, "theme" surfaces Appearance).

### Changed

- The **Plugin Store type filter** is now a dropdown instead of a row of chips — more compact and it
  scales as new plugin types are added.

- Release notes and the in-app updater now read the curated `CHANGELOG.md` instead of the raw git
  log, so each release describes what changed for you rather than listing commit subjects.
- **App and plugin update checks now log to the Output panel** (channel + result), so you can see when
  a check runs and what it found.
- The **SQL formatter** now indents SELECT column lists, parenthesised subqueries and JOIN/AND/OR
  conditions, instead of only breaking clauses onto their own lines. **SQL Server** gets a dedicated
  T-SQL formatter (Microsoft's official ScriptDom parser); the other engines use the improved generic
  engine.

### Fixed

- The app updater no longer offers a **lower version on another channel as an "update"** — switching
  channels only surfaces a build with an equal-or-higher core version, so a `0.3.0` build is never
  prompted to "update" to `0.2.0-preview`.
- Nightly and preview builds now stamp the version from the branch they are built from, so the About
  dialog no longer shows a mismatched build version.
- The bottom tool panels (Output, Containers, AI activity) can now be **resized** by dragging their
  top edge — previously dragging did nothing, or left an empty band above the status bar.
- **"Restart app"** (and the in-app updater's relaunch) now reliably brings the app back: the new
  instance no longer connects to the still-closing old one, defers to it and exits — which could leave
  no window at all. It also relaunches correctly when the app runs through the dotnet muxer.

## [0.2.0] - 2026-07-18

### Added

- **In-app updater** with release channels (Stable / Preview / Nightly): in-place update with
  rollback, a periodic update check, and an inline update bar that downloads and installs from within
  the app.
- **About / diagnostics dialog**: system information, installed-plugin list, host API contracts, and
  copy-to-clipboard.
- **Third-party notices** generated from the NuGet dependency closure and shipped as
  `THIRD-PARTY-NOTICES.md`.
- **Windows installer** (per-user, no admin) alongside the versioned `.zip`; artifact names now carry
  the version.
- **Elasticsearch query sweep** for exploring an index without hand-writing every query.
- Show the connected engine's **server version** in the UI.

### Changed

- Plugins are matched against a host-API **version range** and tracked by build version; the Plugin
  Store judges MCP plugins against the MCP host-API window.
- Plugin sources moved into Settings, with an HTTPS requirement for sources and downloads.

### Security

- Redact secrets from MCP query results.
- Hardened the build pipeline against command injection and unverified external tools.

## [0.1.0] - 2026-07-17

Initial baseline — the first working SQL Explorer.

### Added

- **Cross-platform desktop app** (Windows / Linux / macOS) built on Avalonia, with a runtime
  NL ⇄ EN language switch.
- **Providers as isolated plugins** loaded from `plugins/` — the host ships no database drivers.
  Bundled engines: PostgreSQL, MySQL / MariaDB, SQL Server and SQLite, plus MongoDB, Redis and
  DragonflyDB through a non-SQL provider seam.
- **Plugin Store**: browse, install, update and version-pin provider and tool plugins from
  configurable sources.
- **Schema tree** (server → database → schema → tables / views / columns), extended with procedures,
  functions and triggers, DDL scripting and per-folder object counts.
- **Query tab**: SQL editor with syntax highlighting, schema-aware completion (Ctrl+Space),
  quick-open object search (Ctrl+K), execute-selection / at-cursor, multiple result sets, EXPLAIN and
  cancellable queries.
- **Browse tab**: page through a table without writing SQL — paging, a WHERE filter and column-header
  sort.
- **Editable result grid with a reviewable save flow**: edit, add or delete rows, preview the
  generated INSERT / UPDATE / DELETE, and run them in a single transaction (enabled only for
  single-table results with a primary key).
- **Import / export**: CSV / JSON / SQL export and CSV import; a cell value viewer with JSON
  pretty-print; selection aggregation (count / sum / avg / min / max).
- **Connection manager** with nested folders, drag-to-reorder, a per-connection colour flag and a
  read-only safe mode; **secure credential storage** in the OS keychain with an optional master
  password.
- **Query history and logging**: persistent, searchable history with re-run, an opt-in query log, and
  an Output panel for feedback and errors.
- **Universal Backup & Restore** tool with per-object schema / data selection and a streaming `.lbak`
  format for large objects.
- **SQL Server admin tools**: login / user management and provider-supplied advanced connection and
  properties UIs.
- **Host-owned MCP server** exposing read query access to AI assistants.
- **Configurable keyboard shortcuts** with a plugin shortcut SDK.
- **Multi-platform build pipeline** (Windows installer + zip, Linux AppImage, macOS DMG) publishing
  rolling nightly and preview releases.

[Unreleased]: https://github.com/Lionear/DataTray/compare/v0.8.0...HEAD
[0.8.0]: https://github.com/Lionear/DataTray/compare/v0.7.0...v0.8.0
[0.7.0]: https://github.com/Lionear/DataTray/compare/v0.6.1...v0.7.0
[0.6.1]: https://github.com/Lionear/DataTray/compare/v0.6.0...v0.6.1
[0.6.0]: https://github.com/Lionear/DataTray/compare/v0.5.0...v0.6.0
[0.5.0]: https://github.com/Lionear/SqlExplorer/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/Lionear/SqlExplorer/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/Lionear/SqlExplorer/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/Lionear/SqlExplorer/releases/tag/v0.2.0
