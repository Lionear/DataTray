namespace DataTray.App.ViewModels;

/// <summary>
/// The sidebar tree filter (SE-285): hide every node that neither matches the typed text nor has a
/// matching descendant, so what is left is the path to each hit. Ancestors of a hit stay visible —
/// finding <c>OrderLine</c> is useless if you can't see which database it lives in — and everything
/// under a hit stays visible too, so a match you found is still browsable.
///
/// It deliberately never forces a lazy load: a branch that was never expanded holds nodes nobody has
/// fetched, and expanding the whole tree on every keystroke is not free against a production server.
/// That is why the count it returns is "of the loaded nodes", and why this is a different thing from
/// ⌘K quick-open (which jumps to one object and closes again) rather than a replacement for it.
///
/// Lives apart from <see cref="MainViewModel"/> purely so the walk can be tested on its own.
/// </summary>
public static class SidebarTreeFilter
{
    /// <summary>
    /// Apply <paramref name="filter"/> to the loaded tree under <paramref name="roots"/>.
    /// <paramref name="onVisit"/> is called once per node walked, so the caller can hook a node it
    /// hasn't seen before (the owner uses it to watch for lazily loaded children).
    /// </summary>
    /// <returns>How many nodes matched, and how many were searched (i.e. are loaded).</returns>
    public static (int Matches, int Loaded) Apply(
        IEnumerable<TreeNodeViewModel> roots,
        string filter,
        Action<TreeNodeViewModel>? onVisit = null)
    {
        var matches = 0;
        var loaded = 0;
        foreach (var root in roots)
        {
            Walk(root, filter, parentMatched: false, onVisit, ref matches, ref loaded);
        }

        return (matches, loaded);
    }

    /// <summary>Undo a filter: everything visible again. Cheaper than re-walking with empty text.</summary>
    public static void Reveal(IEnumerable<TreeNodeViewModel> roots)
    {
        foreach (var node in roots)
        {
            node.IsFilterVisible = true;
            node.SetHighlight(null);
            Reveal(node.Children);
        }
    }

    // True when this node stays visible: it matched, a loaded descendant did, or an ancestor matched.
    private static bool Walk(
        TreeNodeViewModel node, string filter, bool parentMatched,
        Action<TreeNodeViewModel>? onVisit, ref int matches, ref int loaded)
    {
        onVisit?.Invoke(node);

        if (node.IsPlaceholder)
        {
            // The "…" row belongs to its parent's lazy load, not to the filter — never hide or count it.
            node.IsFilterVisible = true;
            return false;
        }

        node.SetHighlight(filter);
        loaded++;
        var self = node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        if (self)
        {
            matches++;
        }

        var keepSubtree = parentMatched || self;
        var childHit = false;
        foreach (var child in node.Children)
        {
            childHit |= Walk(child, filter, keepSubtree, onVisit, ref matches, ref loaded);
        }

        // Open the way to a deeper hit. childHit can only be true when a real (non-placeholder) child
        // matched, which means this node's children are already loaded — so this never triggers a fetch.
        if (childHit)
        {
            node.IsExpanded = true;
        }

        node.IsFilterVisible = keepSubtree || childHit;
        return node.IsFilterVisible;
    }
}
