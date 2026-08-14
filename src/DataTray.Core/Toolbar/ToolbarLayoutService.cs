namespace DataTray.Core.Toolbar;

/// <summary>
/// The live toolbar layout: the catalog of everything that may appear (host actions plus whatever
/// plugins contributed at startup), the user's persisted order and visibility over it, and the resolve
/// rules that reconcile the two. Same shape as <see cref="Shortcuts.KeymapService"/> — catalog, persisted
/// user layer, and a settings pane over both — and equally UI-agnostic: no icons, no commands, just ids.
/// </summary>
public sealed class ToolbarLayoutService
{
    private readonly IToolbarLayoutStore _store;
    private readonly List<ToolbarActionEntry> _catalog;
    private IReadOnlyList<ToolbarLayoutItem> _saved;

    public ToolbarLayoutService(IToolbarLayoutStore store, IEnumerable<ToolbarActionEntry>? pluginEntries = null)
    {
        _store = store;
        _catalog = [.. ToolbarCatalog.Host, .. pluginEntries ?? []];
        _saved = store.Load();
    }

    /// <summary>Fired after <see cref="Apply"/> or <see cref="Reset"/> persists a change; the resolved
    /// layout has already updated, so the main window can rebuild its strip without a restart.</summary>
    public event Action? Changed;

    /// <summary>Every action that may appear in the toolbar, host entries first then plugins in load order.</summary>
    public IReadOnlyList<ToolbarActionEntry> Catalog => _catalog;

    public ToolbarActionEntry? Entry(string id) => _catalog.FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// Append plugin contributions to the catalog. Subsystem plugins are activated after the container is
    /// built, so their actions arrive later than the host's — hence a registration call rather than a
    /// constructor argument. Raises <see cref="Changed"/>, which is what makes the new buttons appear.
    /// </summary>
    public void RegisterPluginActions(IEnumerable<ToolbarActionEntry> entries)
    {
        var added = false;
        foreach (var entry in entries)
        {
            if (_catalog.Any(c => c.Id == entry.Id))
            {
                continue;
            }

            _catalog.Add(entry);
            added = true;
        }

        if (added)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The user's layout resolved against the catalog: saved entries in their saved order, then every
    /// catalog entry the layout does not mention, appended <em>visible</em>. Absent means new, not hidden —
    /// otherwise a freshly installed plugin's button would never appear. Saved ids the catalog cannot
    /// resolve are skipped here but kept in the file (see <see cref="Apply"/>).
    /// </summary>
    public IReadOnlyList<ToolbarLayoutItem> Resolve()
    {
        var result = new List<ToolbarLayoutItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in _saved)
        {
            if (_catalog.Any(c => c.Id == item.Id) && seen.Add(item.Id))
            {
                result.Add(item);
            }
        }

        foreach (var entry in _catalog)
        {
            if (seen.Add(entry.Id))
            {
                result.Add(new ToolbarLayoutItem(entry.Id, true));
            }
        }

        return result;
    }

    /// <summary>The catalog entries that are actually shown, in the user's order.</summary>
    public IReadOnlyList<ToolbarActionEntry> VisibleActions() =>
        [.. Resolve().Where(i => i.Visible).Select(i => _catalog.First(c => c.Id == i.Id))];

    /// <summary>
    /// Replaces the layout from the settings pane's list, which only ever holds ids the catalog resolved.
    /// Saved ids that did not resolve — a plugin that is disabled, mid-update or temporarily failing to
    /// load — are folded back in at the position they held, so a plugin blinking out never costs the user
    /// their arrangement.
    /// </summary>
    public void Apply(IReadOnlyList<ToolbarLayoutItem> layout)
    {
        var known = _catalog.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var next = layout.ToList();

        string? anchor = null;
        foreach (var saved in _saved)
        {
            if (known.Contains(saved.Id))
            {
                anchor = saved.Id;
                continue;
            }

            var index = next.FindIndex(i => i.Id == anchor);
            next.Insert(anchor is null ? 0 : index < 0 ? next.Count : index + 1, saved);
            anchor = saved.Id;
        }

        _saved = next;
        _store.Save(next);
        Changed?.Invoke();
    }

    /// <summary>Drops the user's layout entirely: catalog order, everything visible.</summary>
    public void Reset()
    {
        _saved = [];
        _store.Save([]);
        Changed?.Invoke();
    }
}
