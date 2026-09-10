namespace DataTray.Tools.ErDiagram;

/// <summary>Where one table sits. <see cref="Rank"/> is the column (0 = leftmost), <see cref="Order"/> the
/// position within that column, top to bottom. Both are grid coordinates, not pixels: how wide a box is
/// depends on the detail level and the longest column name, which only the canvas knows.</summary>
public sealed record ErPlacement(string Key, int Rank, int Order);

/// <summary>A computed layout: a placement per node, plus the width of the widest column.</summary>
public sealed record ErLayoutResult(IReadOnlyList<ErPlacement> Placements, int RankCount, int WidestRank);

/// <summary>
/// Turns a graph into positions. The seam exists so the algorithm can be replaced without touching the
/// canvas: layout is a pure function from tables and foreign keys to grid coordinates, and the epic
/// (SE-82) expects to revisit the choice once real schemas have been drawn.
/// </summary>
public interface IErLayout
{
    ErLayoutResult Compute(ErGraph graph);
}

/// <summary>
/// Layered layout by dependency depth, left to right: a table nothing depends on sits at rank 0, and a
/// table's rank is one past the deepest table it references. That is the reading the approved mockup
/// describes — "what nothing points at sits on the left, what points at everything ends up on the right" —
/// and it is the only one of the three routes in SE-215 that adds no dependency.
///
/// <para>Within a rank, boxes are ordered by the barycentre heuristic: repeatedly place each table near
/// the average position of the tables it connects to. It is the standard first move for reducing edge
/// crossings and it is cheap; it does not minimise them, and is not meant to.</para>
///
/// <para><b>Cycles are expected, not exceptional.</b> A self-reference (<c>employees.manager_id</c>) is
/// ordinary schema design, and mutual references between two tables are common enough. Longest-path
/// ranking is undefined on a cycle and a naive recursion hangs, so back edges — those closing a cycle —
/// are ignored for ranking only. They are still drawn: dropping them from the layout costs a slightly
/// arbitrary left-right order among the tables in the cycle, which is unavoidable, since a cycle has no
/// dependency order to reflect.</para>
///
/// <para>Everything is deterministic, ties broken by table key. Re-running the layout on an unchanged
/// schema must give an identical diagram — a picture that reshuffles itself on every open cannot be
/// trusted to mean anything by its shape.</para>
/// </summary>
public sealed class LayeredErLayout : IErLayout
{
    /// <summary>Barycentre sweeps. Two passes captures nearly all of the improvement; more mostly shuffles.</summary>
    private const int Sweeps = 2;

    public ErLayoutResult Compute(ErGraph graph)
    {
        if (graph.Nodes.Count == 0)
        {
            return new ErLayoutResult([], 0, 0);
        }

        var dependencies = BuildDependencyMap(graph);
        var ranks = AssignRanks(graph, dependencies);
        var orders = OrderWithinRanks(graph, ranks);

        var placements = graph.Nodes
            .Select(n => new ErPlacement(n.Key, ranks[n.Key], orders[n.Key]))
            .OrderBy(p => p.Rank)
            .ThenBy(p => p.Order)
            .ToList();

        var rankCount = placements.Count == 0 ? 0 : placements.Max(p => p.Rank) + 1;
        var widest = placements.GroupBy(p => p.Rank).Max(g => g.Count());

        return new ErLayoutResult(placements, rankCount, widest);
    }

    /// <summary>Per table, the distinct in-scope tables it references, self-references excluded.</summary>
    private static Dictionary<string, List<string>> BuildDependencyMap(ErGraph graph)
    {
        var map = graph.Nodes.ToDictionary(n => n.Key, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var edge in graph.Edges.Where(e => !e.IsSelfReference))
        {
            var targets = map[edge.FromKey];
            if (!targets.Contains(edge.ToKey, StringComparer.OrdinalIgnoreCase))
            {
                targets.Add(edge.ToKey);
            }
        }

        return map;
    }

    /// <summary>
    /// Longest path to a table with no dependencies, computed with an explicit stack rather than
    /// recursion — a chain of a few hundred tables is not unthinkable and would be a stack overflow the
    /// user experiences as the app vanishing.
    ///
    /// <para>A node currently on the stack (grey) that is reached again closes a cycle; that edge is
    /// skipped, which is what breaks the cycle without unbounded growth.</para>
    /// </summary>
    private static Dictionary<string, int> AssignRanks(ErGraph graph, Dictionary<string, List<string>> dependencies)
    {
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var start in graph.Nodes.Select(n => n.Key))
        {
            if (ranks.ContainsKey(start))
            {
                continue;
            }

            // (key, expanded) — expanded marks the second visit, when every dependency has a rank.
            var stack = new Stack<(string Key, bool Expanded)>();
            stack.Push((start, false));

            while (stack.Count > 0)
            {
                var (key, expanded) = stack.Pop();

                if (expanded)
                {
                    onStack.Remove(key);

                    var rank = 0;
                    foreach (var dep in dependencies[key])
                    {
                        // A dependency without a rank yet is one we skipped as a back edge.
                        if (ranks.TryGetValue(dep, out var depRank))
                        {
                            rank = Math.Max(rank, depRank + 1);
                        }
                    }

                    ranks[key] = rank;
                    continue;
                }

                if (ranks.ContainsKey(key))
                {
                    continue;
                }

                onStack.Add(key);
                stack.Push((key, true));

                foreach (var dep in dependencies[key])
                {
                    if (!ranks.ContainsKey(dep) && !onStack.Contains(dep))
                    {
                        stack.Push((dep, false));
                    }
                }
            }
        }

        return ranks;
    }

    /// <summary>
    /// Orders each rank by the barycentre of its neighbours, alternating direction: a forward sweep places
    /// a table near the tables it references, a backward sweep near the tables referencing it. A table
    /// with no neighbours in the rank being read from keeps its current position, so isolated tables do
    /// not drift.
    /// </summary>
    private static Dictionary<string, int> OrderWithinRanks(ErGraph graph, Dictionary<string, int> ranks)
    {
        var byRank = graph.Nodes
            .GroupBy(n => ranks[n.Key])
            .OrderBy(g => g.Key)
            .ToDictionary(
                g => g.Key,
                g => g.Select(n => n.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList());

        var orders = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var rank in byRank.Values)
        {
            for (var i = 0; i < rank.Count; i++)
            {
                orders[rank[i]] = i;
            }
        }

        var outgoing = graph.Edges.Where(e => !e.IsSelfReference)
            .GroupBy(e => e.FromKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ToKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var incoming = graph.Edges.Where(e => !e.IsSelfReference)
            .GroupBy(e => e.ToKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(e => e.FromKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var rankNumbers = byRank.Keys.OrderBy(r => r).ToList();

        for (var sweep = 0; sweep < Sweeps; sweep++)
        {
            foreach (var rank in rankNumbers.Skip(1))
            {
                Reorder(byRank[rank], orders, outgoing);
            }

            foreach (var rank in Enumerable.Reverse(rankNumbers).Skip(1))
            {
                Reorder(byRank[rank], orders, incoming);
            }
        }

        return orders;
    }

    private static void Reorder(
        List<string> rank, Dictionary<string, int> orders, Dictionary<string, List<string>> neighbours)
    {
        // Barycentre of a table with no neighbours is its current position, which keeps it put.
        var barycentre = rank.ToDictionary(
            key => key,
            key =>
            {
                if (!neighbours.TryGetValue(key, out var linked) || linked.Count == 0)
                {
                    return (double)orders[key];
                }

                var positions = linked.Where(orders.ContainsKey).Select(k => (double)orders[k]).ToList();
                return positions.Count == 0 ? orders[key] : positions.Average();
            },
            StringComparer.OrdinalIgnoreCase);

        rank.Sort((a, b) =>
        {
            var byBarycentre = barycentre[a].CompareTo(barycentre[b]);
            return byBarycentre != 0 ? byBarycentre : StringComparer.OrdinalIgnoreCase.Compare(a, b);
        });

        for (var i = 0; i < rank.Count; i++)
        {
            orders[rank[i]] = i;
        }
    }
}
