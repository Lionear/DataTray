namespace DataTray.Tools.ErDiagram;

/// <summary>
/// Which tables get drawn. A schema with two hundred tables cannot be drawn blind — the result is a
/// hairball nobody reads — so the diagram opens on a picker with nothing selected rather than on a canvas
/// (SE-217, and what the approved mockup shows).
/// </summary>
public static class ErScope
{
    /// <summary>
    /// Grow a selection by one hop along foreign keys, in <b>both</b> directions: tick <c>orders</c>, press
    /// it, and <c>customers</c> (which orders points at) and <c>order_items</c> (which points at orders)
    /// both come along. Following only the outgoing direction would be the more obvious reading and the
    /// less useful one — half of what makes a table interesting is what depends on it.
    ///
    /// <para>One hop, applied to the selection as it stands. Pressing it again grows another ring, which is
    /// how a user walks outward from one table at their own pace instead of choosing a depth up front.</para>
    /// </summary>
    /// <param name="tables">Every table in the schema, not only the selected ones.</param>
    /// <param name="selected">Keys currently ticked (<see cref="TableDef.Key"/>).</param>
    public static IReadOnlyCollection<string> ExpandOneHop(
        IReadOnlyList<TableDef> tables, IReadOnlyCollection<string> selected)
    {
        var byKey = tables
            .DistinctBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(t => t.Key, t => t, StringComparer.OrdinalIgnoreCase);

        var grown = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);

        // Snapshot first: growing while iterating would walk the new ring too, which is every hop, not one.
        var seed = grown.ToList();

        foreach (var key in seed)
        {
            if (!byKey.TryGetValue(key, out var table))
            {
                continue;
            }

            // Outgoing: what this table points at.
            foreach (var fk in table.ForeignKeys)
            {
                var target = Qualify(fk, table);
                if (byKey.TryGetValue(target, out var referenced))
                {
                    grown.Add(referenced.Key);
                }
            }

            // Incoming: what points at this table.
            foreach (var other in tables)
            {
                if (other.ForeignKeys.Any(fk =>
                        string.Equals(Qualify(fk, other), key, StringComparison.OrdinalIgnoreCase)))
                {
                    grown.Add(other.Key);
                }
            }
        }

        return grown;
    }

    /// <summary>Same rule as <c>ErGraph</c>: an empty <c>RefSchema</c> means the referencing table's own
    /// schema, not "no schema" — SQLite has none, and the other readers leave it empty for a same-schema
    /// reference.</summary>
    private static string Qualify(ForeignKeyDef fk, TableDef from)
    {
        var schema = string.IsNullOrEmpty(fk.RefSchema) ? from.Schema : fk.RefSchema;
        return string.IsNullOrEmpty(schema) ? fk.RefTable : $"{schema}.{fk.RefTable}";
    }
}
