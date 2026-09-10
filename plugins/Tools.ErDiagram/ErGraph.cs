namespace DataTray.Tools.ErDiagram;

/// <summary>One table in the drawn scope. <see cref="Key"/> is <see cref="TableDef.Key"/> — schema-qualified
/// and the identity everything else in the graph refers to.</summary>
public sealed record ErNode(string Key, TableDef Table);

/// <summary>
/// One foreign key between two tables that are <i>both</i> in scope. <see cref="IsSelfReference"/> marks
/// the <c>employees.manager_id → employees.id</c> case: a real relation to draw, but not a dependency —
/// it says nothing about where the box belongs, and feeding it to the ranking would make a table depend
/// on itself.
/// </summary>
public sealed record ErEdge(string FromKey, string ToKey, ForeignKeyDef ForeignKey)
{
    public bool IsSelfReference => string.Equals(FromKey, ToKey, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The tables to draw and the relations between them, resolved from a <see cref="SchemaSnapshot"/> subset.
///
/// <para>The scope is whatever the picker selected, so a foreign key routinely points at a table that is
/// not being drawn. Those are dropped from <see cref="Edges"/> — there is nothing to draw a line to — but
/// counted in <see cref="RelationsOutOfScope"/>, because "6 tables · 5 relations" in the status bar is a
/// claim about the diagram, and silently discarding the other four would make it a lie the user cannot
/// see. A caller that wants to offer "+ Related" has the same information.</para>
/// </summary>
public sealed class ErGraph
{
    private ErGraph(IReadOnlyList<ErNode> nodes, IReadOnlyList<ErEdge> edges, int relationsOutOfScope)
    {
        Nodes = nodes;
        Edges = edges;
        RelationsOutOfScope = relationsOutOfScope;
    }

    /// <summary>Tables to draw, ordered by <see cref="ErNode.Key"/> so every downstream step starts from a
    /// stable sequence — the layout must not depend on the order the reader happened to return tables in.</summary>
    public IReadOnlyList<ErNode> Nodes { get; }

    /// <summary>Relations where both ends are drawn, including self-references.</summary>
    public IReadOnlyList<ErEdge> Edges { get; }

    /// <summary>Foreign keys pointing outside the drawn scope. Not drawable, but worth reporting.</summary>
    public int RelationsOutOfScope { get; }

    public static ErGraph Build(IEnumerable<TableDef> tables)
    {
        var nodes = tables
            .Select(t => new ErNode(t.Key, t))
            .DistinctBy(n => n.Key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var inScope = nodes.ToDictionary(n => n.Key, n => n.Key, StringComparer.OrdinalIgnoreCase);

        var edges = new List<ErEdge>();
        var outOfScope = 0;

        foreach (var node in nodes)
        {
            foreach (var fk in node.Table.ForeignKeys)
            {
                var target = Qualify(fk, node.Table);

                // Resolve through the dictionary rather than using the composed string, so the edge carries
                // the key exactly as the target node spells it — the two can differ in case, and every
                // later lookup is by key.
                if (inScope.TryGetValue(target, out var resolved))
                {
                    edges.Add(new ErEdge(node.Key, resolved, fk));
                }
                else
                {
                    outOfScope++;
                }
            }
        }

        return new ErGraph(nodes, edges, outOfScope);
    }

    /// <summary>
    /// The key a foreign key points at. <see cref="ForeignKeyDef.RefSchema"/> is empty on engines without
    /// schemas (SQLite), and readers also leave it empty for a same-schema reference on engines that have
    /// them — so an empty value means "the referencing table's schema", not "no schema".
    /// </summary>
    private static string Qualify(ForeignKeyDef fk, TableDef from)
    {
        var schema = string.IsNullOrEmpty(fk.RefSchema) ? from.Schema : fk.RefSchema;
        return string.IsNullOrEmpty(schema) ? fk.RefTable : $"{schema}.{fk.RefTable}";
    }
}
