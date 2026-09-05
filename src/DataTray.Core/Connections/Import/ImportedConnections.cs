namespace DataTray.Core.Connections.Import;

/// <summary>
/// Turns picked <see cref="DiscoveredConnection"/>s into saved connections. Shared because two callers
/// import: the Connection Manager's toolbar (SE-233) and the first-run wizard (SE-239). Keeping the naming
/// and the save shape in one place is what stops the two from drifting — a name collision handled in one
/// and not the other would silently overwrite a connection in whichever path missed it.
/// </summary>
public static class ImportedConnections
{
    /// <summary>Saves each chosen connection under a name that doesn't collide with an existing one, and
    /// returns the new ids in the order they were saved (the caller usually selects the last).</summary>
    public static IReadOnlyList<string> SaveAll(ConnectionService connections, IReadOnlyList<DiscoveredConnection> chosen)
    {
        var taken = connections.List().Select(c => c.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var ids = new List<string>(chosen.Count);

        foreach (var connection in chosen)
        {
            var name = UniqueName(connection.Name, taken);
            taken.Add(name);

            var id = Guid.NewGuid().ToString("N");
            // No password: the other clients keep those in their own credential stores, so the field is left
            // empty and the prompt happens on first connect.
            connections.Save(id, name, connection.ProviderId!, connection.Values, folder: connection.Folder);
            ids.Add(id);
        }

        return ids;
    }

    /// <summary>"Prod", then "Prod (2)", "Prod (3)" — the first name that is free.</summary>
    public static string UniqueName(string name, IReadOnlySet<string> taken)
    {
        if (!taken.Contains(name))
        {
            return name;
        }

        var suffix = 2;
        while (taken.Contains($"{name} ({suffix})"))
        {
            suffix++;
        }

        return $"{name} ({suffix})";
    }
}
