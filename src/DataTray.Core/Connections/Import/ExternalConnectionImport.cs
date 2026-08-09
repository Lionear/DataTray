using System.Text.Json;
using System.Xml.Linq;

namespace DataTray.Core.Connections.Import;

/// <summary>
/// One connection found in another client's config, already mapped onto a DataTray provider.
/// <see cref="SkipReason"/> non-null means it was found but cannot be imported — kept in the list on
/// purpose, so an import that covers only part of the file says so instead of silently dropping rows.
/// </summary>
/// <param name="HasPassword">True when the source file spelled the password out and it is carried in
/// <see cref="Values"/>. The picker shows this per row, because whether a password came along differs per
/// client and a silent difference is the kind that bites on first connect.</param>
public sealed record DiscoveredConnection(
    string Source,
    string Name,
    string? Folder,
    string? ProviderId,
    IReadOnlyDictionary<string, string?> Values,
    string? SkipReason = null,
    bool HasPassword = false,
    string? SecretRef = null)
{
    public bool CanImport => ProviderId is not null && SkipReason is null;

    /// <summary>True when this connection's password is in the OS credential store and could be fetched —
    /// the source handed over a handle for it (<see cref="SecretRef"/>) and none came in the file itself.
    /// Drives the "fetch passwords too" offer (SE-238).</summary>
    public bool HasFetchableSecret => SecretRef is not null && !HasPassword && CanImport;
}

/// <summary>
/// Reads saved connections out of the other database clients on this machine so they don't have to be
/// retyped (SE-233, SE-237).
///
/// <b>The password rule, which decides what each reader may do:</b> a password that the source file
/// spells out in plain text is carried over — it is already readable to anyone who can read the file, and
/// skipping it would only make the user retype what we just parsed. A password held in another
/// application's secret store is <b>not</b>: neither an OS keychain entry (DataGrip, MongoDB Compass) nor
/// a vendor-encrypted file (DBeaver's <c>credentials-config.json</c>, sealed with a key hardcoded in
/// DBeaver itself) is opened here. Those connections arrive without a password and the connection dialog
/// asks once, on first connect.
///
/// Whatever does come along travels in <c>Values</c> under the provider's own secret field key, so
/// <c>ConnectionService.Save</c> puts it in the OS keychain and never in the config file.
/// </summary>
public static class ExternalConnectionImport
{
    /// <summary>JDBC subprotocol → DataTray provider manifest id. Anything absent here is reported as
    /// "unsupported engine" rather than guessed at.</summary>
    private static readonly Dictionary<string, string> ProviderBySubprotocol = new(StringComparer.OrdinalIgnoreCase)
    {
        ["postgresql"] = "postgres",
        ["postgres"] = "postgres",
        ["mysql"] = "mysql",
        ["mariadb"] = "mysql",
        ["sqlserver"] = "sqlserver",
        ["jtds"] = "sqlserver",
        ["sqlite"] = "sqlite",
        ["duckdb"] = "duckdb",
        ["clickhouse"] = "clickhouse",
        ["mongodb"] = "mongodb",
        // Compass writes SRV URIs for Atlas: the host is a DNS name whose port comes from the SRV record,
        // so there is no port to import — the provider falls back to its default.
        ["mongodb+srv"] = "mongodb",
        ["redis"] = "redis"
    };

    // A canonical concept and the field keys a provider might declare for it, best first. Providers agree
    // on host/port/database/username today; the aliases keep a third-party provider that named things
    // slightly differently from importing as a blank form.
    private static readonly (string Concept, string[] Keys)[] FieldAliases =
    [
        ("host", ["host", "server", "hostname"]),
        ("port", ["port"]),
        ("database", ["database", "db", "catalog"]),
        ("username", ["username", "user", "login"]),
        ("path", ["path", "file", "filename"]),
        ("password", ["password", "pass", "pwd"])
    ];

    /// <summary>
    /// Every connection found in the default locations of every client this knows about, for this OS.
    /// <paramref name="fieldKeysOf"/> maps a provider id to the field keys that provider declares, or
    /// null when it isn't installed — one delegate answers both "can we import this?" and "under which
    /// key does this provider want the host?".
    /// </summary>
    public static IReadOnlyList<DiscoveredConnection> Discover(Func<string, IReadOnlyList<string>?> fieldKeysOf)
    {
        var found = new List<DiscoveredConnection>();

        Scan(DataGripFiles(), text => FromDataGrip(text, fieldKeysOf));
        Scan(DBeaverFiles(), text => FromDBeaver(text, fieldKeysOf));
        Scan(PgServiceFiles(), text => FromPgService(text, fieldKeysOf));
        Scan(MySqlWorkbenchFiles(), text => FromMySqlWorkbench(text, fieldKeysOf));
        Scan(MssqlSettingsFiles(), text => FromMssqlSettings(text, fieldKeysOf));
        Scan(CompassFiles(), text => FromCompass(text, fieldKeysOf));

        return found;

        void Scan(IEnumerable<string> files, Func<string, IReadOnlyList<DiscoveredConnection>> parse)
        {
            foreach (var file in files)
            {
                found.AddRange(Read(file, parse));
            }
        }
    }

    /// <summary>
    /// The service name <paramref name="source"/> filed <paramref name="secretRef"/>'s password under in the
    /// OS credential store, or null when that client doesn't use one (SE-238).
    /// </summary>
    /// <remarks>
    /// DataGrip: JetBrains' PasswordSafe writes one entry per data-source, keyed on the very
    /// <c>uuid</c> that <c>dataSources.xml</c> already gives us. Verified on Fedora 44 + KWallet against a
    /// real DataGrip profile: three of four data-sources had a matching entry and the fourth — the one with
    /// no saved password — had none. The em dash and the spaces around it are part of the name.
    /// macOS and Windows use the same string as the keychain service / Credential Manager target; that
    /// follows the same PasswordSafe convention but has not been observed on those platforms.
    /// </remarks>
    public static string? SecretServiceName(string source, string secretRef) => source switch
    {
        "DataGrip" => $"IntelliJ Platform DB — {secretRef}",
        _ => null
    };

    /// <summary>
    /// Fetch the passwords that live in the OS credential store rather than in the client's own file, and
    /// fold them into the rows they belong to (SE-238). Separate from <see cref="Discover"/> on purpose:
    /// discovery only reads config files, and nothing touches a credential store until the user asks for it.
    /// </summary>
    /// <remarks>
    /// Every failure is an ordinary outcome — store locked, user declined, no entry — and leaves the row
    /// exactly as discovery produced it: importable, without a password, saying so.
    /// </remarks>
    public static IReadOnlyList<DiscoveredConnection> WithStoredPasswords(
        IReadOnlyList<DiscoveredConnection> found,
        IForeignSecretLookup lookup,
        Func<string, IReadOnlyList<string>?> fieldKeysOf)
    {
        var enriched = new List<DiscoveredConnection>(found.Count);

        foreach (var connection in found)
        {
            enriched.Add(connection.HasFetchableSecret
                         && SecretServiceName(connection.Source, connection.SecretRef!) is { } service
                         && Find(service) is { Length: > 0 } password
                ? WithPassword(connection, password, fieldKeysOf)
                : connection);
        }

        return enriched;

        string? Find(string service)
        {
            try
            {
                return lookup.Find(service);
            }
            catch (Exception)
            {
                // A backend that throws (no secret service running, a broken CLI) must not take the whole
                // import down with it — the rest of the rows are still perfectly importable.
                return null;
            }
        }
    }

    // Put a fetched password under the provider's own secret field key, so ConnectionService.Save routes it
    // to DataTray's keychain and never to the config file — the same path a hand-typed password takes.
    private static DiscoveredConnection WithPassword(
        DiscoveredConnection connection,
        string password,
        Func<string, IReadOnlyList<string>?> fieldKeysOf)
    {
        if (fieldKeysOf(connection.ProviderId!) is not { } fieldKeys)
        {
            return connection;
        }

        var aliases = FieldAliases.First(a => a.Concept == "password").Keys;
        if (aliases.FirstOrDefault(alias => fieldKeys.Contains(alias, StringComparer.OrdinalIgnoreCase)) is not { } key)
        {
            // The provider declares no password field at all (a file-backed engine, say). Nothing to carry.
            return connection;
        }

        var values = new Dictionary<string, string?>(connection.Values) { [key] = password };
        return connection with { Values = values, HasPassword = true };
    }

    /// <summary>libpq's connection-service file.</summary>
    public static IReadOnlyList<string> PgServiceFiles() =>
        Existing(OperatingSystem.IsWindows()
            ? [Path.Combine(AppData, "postgresql", ".pg_service.conf")]
            : [Path.Combine(Home, ".pg_service.conf")]);

    /// <summary>MySQL Workbench's saved connections.</summary>
    public static IReadOnlyList<string> MySqlWorkbenchFiles() =>
        Existing(OperatingSystem.IsWindows()
            ? [Path.Combine(AppData, "MySQL", "Workbench", "connections.xml")]
            : OperatingSystem.IsMacOS()
                ? [Path.Combine(Home, "Library", "Application Support", "MySQL", "Workbench", "connections.xml")]
                : [Path.Combine(Home, ".mysql", "workbench", "connections.xml")]);

    /// <summary>Azure Data Studio and VS Code user settings, which may hold an <c>mssql.connections</c> array.</summary>
    public static IReadOnlyList<string> MssqlSettingsFiles() =>
        Existing(EditorConfigRoots().Select(root => Path.Combine(root, "User", "settings.json")));

    /// <summary>MongoDB Compass keeps one JSON file per saved connection.</summary>
    public static IReadOnlyList<string> CompassFiles() =>
        CompassRoots()
            .Select(root => Path.Combine(root, "Connections"))
            .SelectMany(dir => EnumerateFiles(dir, "*.json"))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Every <c>dataSources.xml</c> a JetBrains IDE might have written. DataGrip normally stores its
    /// data sources <b>per project</b>, in <c>&lt;project&gt;/.idea/dataSources.xml</c> — the IDE-wide
    /// <c>options/dataSources.xml</c> is usually absent — so the recent-projects list of every installed
    /// IDE is walked as well. Without that the DataGrip half finds nothing on a typical machine.
    /// </summary>
    public static IReadOnlyList<string> DataGripFiles() =>
        JetBrainsRoots()
            .SelectMany(SubDirectories)
            .Select(dir => Path.Combine(dir, "options"))
            .SelectMany(options => new[] { Path.Combine(options, "dataSources.xml") }
                .Concat(RecentProjects(Path.Combine(options, "recentProjects.xml"))
                    .Select(project => Path.Combine(project, ".idea", "dataSources.xml"))))
            .Where(File.Exists)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    // Project paths out of an IDE's recentProjects.xml. Paths are stored with JetBrains' $USER_HOME$ macro.
    private static IEnumerable<string> RecentProjects(string recentProjectsFile)
    {
        if (!File.Exists(recentProjectsFile))
        {
            return [];
        }

        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return XDocument.Load(recentProjectsFile)
                .Descendants("entry")
                .Select(entry => (string?)entry.Attribute("key"))
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key!.Replace("$USER_HOME$", home, StringComparison.Ordinal))
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return [];
        }
    }

    /// <summary>DBeaver <c>data-sources.json</c> across every workspace and project under DBeaverData.</summary>
    public static IReadOnlyList<string> DBeaverFiles() =>
        DBeaverRoots()
            .SelectMany(root => EnumerateFiles(root, "data-sources.json"))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Parse a DataGrip <c>dataSources.xml</c>.</summary>
    public static IReadOnlyList<DiscoveredConnection> FromDataGrip(
        string xml, Func<string, IReadOnlyList<string>?> fieldKeysOf)
    {
        var doc = XDocument.Parse(xml);
        var found = new List<DiscoveredConnection>();

        foreach (var source in doc.Descendants("data-source"))
        {
            var name = (string?)source.Attribute("name") ?? source.Element("name")?.Value ?? "(unnamed)";
            var url = source.Element("jdbc-url")?.Value;
            var user = source.Element("user-name")?.Value;

            if (string.IsNullOrWhiteSpace(url))
            {
                found.Add(new DiscoveredConnection("DataGrip", name, null, null, EmptyValues, "no JDBC URL stored"));
                continue;
            }

            // The data-source's uuid is also the handle to its password in the OS credential store — see
            // DataGripSecretService. Recorded here, resolved later and only if asked (SE-238).
            var uuid = (string?)source.Attribute("uuid");
            found.Add(Map("DataGrip", name, null, url, user, fieldKeysOf) with { SecretRef = uuid });
        }

        return found;
    }

    /// <summary>Parse a DBeaver <c>data-sources.json</c>.</summary>
    public static IReadOnlyList<DiscoveredConnection> FromDBeaver(
        string json, Func<string, IReadOnlyList<string>?> fieldKeysOf)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("connections", out var connections)
            || connections.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var found = new List<DiscoveredConnection>();
        foreach (var entry in connections.EnumerateObject())
        {
            var node = entry.Value;
            var name = Text(node, "name") ?? entry.Name;
            var folder = Text(node, "folder");

            if (!node.TryGetProperty("configuration", out var config) || config.ValueKind != JsonValueKind.Object)
            {
                found.Add(new DiscoveredConnection("DBeaver", name, folder, null, EmptyValues, "no configuration stored"));
                continue;
            }

            var url = Text(config, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                found.Add(new DiscoveredConnection("DBeaver", name, folder, null, EmptyValues, "no JDBC URL stored"));
                continue;
            }

            // DBeaver also keeps host/port/database as their own fields — they win over the URL when set,
            // because a user who edited them by hand leaves the URL stale.
            var overrides = new Dictionary<string, string?>();
            Put(overrides, "host", Text(config, "host"));
            Put(overrides, "port", Text(config, "port"));
            Put(overrides, "database", Text(config, "database"));

            found.Add(Map("DBeaver", name, folder, url, Text(config, "user"), fieldKeysOf, overrides));
        }

        return found;
    }

    /// <summary>
    /// Split a JDBC URL into the canonical concepts (host/port/database/path). Public for the tests —
    /// this is where every engine's URL shape is absorbed.
    /// </summary>
    public static (string? Subprotocol, IReadOnlyDictionary<string, string?> Values) ParseJdbcUrl(string url)
    {
        var values = new Dictionary<string, string?>();
        var rest = url.Trim();
        if (rest.StartsWith("jdbc:", StringComparison.OrdinalIgnoreCase))
        {
            rest = rest["jdbc:".Length..];
        }

        var colon = rest.IndexOf(':');
        if (colon < 0)
        {
            return (null, values);
        }

        var subprotocol = rest[..colon];
        rest = rest[(colon + 1)..];

        // jtds:sqlserver://host:port/db — the driver name is the second segment.
        if (subprotocol.Equals("jtds", StringComparison.OrdinalIgnoreCase)
            && rest.IndexOf(':') is var inner and > 0)
        {
            rest = rest[(inner + 1)..];
        }

        if (!rest.StartsWith("//", StringComparison.Ordinal))
        {
            // File-backed engines: jdbc:sqlite:/var/db/app.db, jdbc:duckdb:C:\data\app.duckdb
            Put(values, "path", StripQuery(rest));
            return (subprotocol, values);
        }

        rest = rest[2..];

        // SQL Server appends its properties with ';' instead of a query string; everything else uses '?'.
        var properties = string.Empty;
        var semicolon = rest.IndexOf(';');
        if (semicolon >= 0)
        {
            properties = rest[(semicolon + 1)..];
            rest = rest[..semicolon];
        }

        rest = StripQuery(rest);

        var slash = rest.IndexOf('/');
        var authority = slash >= 0 ? rest[..slash] : rest;
        if (slash >= 0)
        {
            Put(values, "database", rest[(slash + 1)..]);
        }

        // A comma-separated host list (replica sets, failover partners) — take the first, the rest is
        // an advanced setting the user re-adds by hand.
        if (authority.IndexOf(',') is var comma and >= 0)
        {
            authority = authority[..comma];
        }

        // user:password@host — Mongo and Redis URLs may carry credentials inline. Both are taken: an
        // inline password is plain text in a plain file, which is the case the rule allows.
        if (authority.LastIndexOf('@') is var at and >= 0)
        {
            var userInfo = authority[..at];
            authority = authority[(at + 1)..];
            var passwordSeparator = userInfo.IndexOf(':');
            if (passwordSeparator >= 0)
            {
                Put(values, "username", userInfo[..passwordSeparator]);
                Put(values, "password", Uri.UnescapeDataString(userInfo[(passwordSeparator + 1)..]));
            }
            else
            {
                Put(values, "username", userInfo);
            }
        }

        var portSeparator = authority.LastIndexOf(':');
        if (portSeparator >= 0 && int.TryParse(authority[(portSeparator + 1)..], out _))
        {
            Put(values, "port", authority[(portSeparator + 1)..]);
            authority = authority[..portSeparator];
        }

        Put(values, "host", authority);

        foreach (var property in properties.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = property.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = property[..equals].Trim();
            var value = property[(equals + 1)..].Trim();
            if (key.Equals("databaseName", StringComparison.OrdinalIgnoreCase)
                || key.Equals("database", StringComparison.OrdinalIgnoreCase))
            {
                Put(values, "database", value);
            }
            else if (key.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                Put(values, "username", value);
            }
        }

        return (subprotocol, values);
    }

    private static readonly IReadOnlyDictionary<string, string?> EmptyValues = new Dictionary<string, string?>();

    private static DiscoveredConnection Map(
        string source,
        string name,
        string? folder,
        string url,
        string? user,
        Func<string, IReadOnlyList<string>?> fieldKeysOf,
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var (subprotocol, parsed) = ParseJdbcUrl(url);
        if (subprotocol is null || !ProviderBySubprotocol.TryGetValue(subprotocol, out var providerId))
        {
            return new DiscoveredConnection(source, name, folder, null, EmptyValues,
                $"unsupported engine '{subprotocol ?? url}'");
        }

        var canonical = new Dictionary<string, string?>(parsed);
        Put(canonical, "username", user);
        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                Put(canonical, key, value);
            }
        }

        return MapCanonical(source, name, folder, providerId, canonical, fieldKeysOf);
    }

    /// <summary>
    /// Turn canonical concepts (host/port/database/username/path) into a connection for
    /// <paramref name="providerId"/>. The clients that store their settings as plain fields rather than a
    /// URL come straight here; <see cref="Map"/> is the same thing with a JDBC URL parsed in front of it.
    /// </summary>
    private static DiscoveredConnection MapCanonical(
        string source,
        string name,
        string? folder,
        string providerId,
        IReadOnlyDictionary<string, string?> canonical,
        Func<string, IReadOnlyList<string>?> fieldKeysOf)
    {
        if (fieldKeysOf(providerId) is not { } fieldKeys)
        {
            return new DiscoveredConnection(source, name, folder, null, EmptyValues,
                $"provider '{providerId}' is not installed");
        }

        // Translate the canonical concepts to the keys this provider actually declares; anything it
        // doesn't ask for is dropped rather than stored under a key nothing reads.
        var values = new Dictionary<string, string?>();
        foreach (var (concept, aliases) in FieldAliases)
        {
            if (!canonical.TryGetValue(concept, out var value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (aliases.FirstOrDefault(alias => fieldKeys.Contains(alias, StringComparer.OrdinalIgnoreCase)) is { } key)
            {
                values[key] = value;
            }
        }

        if (values.Count == 0)
        {
            return new DiscoveredConnection(source, name, folder, null, EmptyValues,
                $"none of the stored settings fit provider '{providerId}'");
        }

        // The password rides along in Values under the provider's own secret field key, so
        // ConnectionService.Save routes it to the keychain and keeps it out of the config file — the same
        // path a hand-typed password takes.
        var hasPassword = canonical.TryGetValue("password", out var password)
            && !string.IsNullOrEmpty(password)
            && values.ContainsValue(password);

        return new DiscoveredConnection(source, name, folder, providerId, values, SkipReason: null, hasPassword);
    }

    // ---------------------------------------------------------------------------------------------
    // SE-237: the clients that store plain fields or a connection string instead of a JDBC URL. Each
    // one only has to reach the canonical concepts; MapCanonical does the rest.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// libpq's connection-service file: <c>[name]</c> sections of <c>key=value</c>. Always PostgreSQL —
    /// the file has no notion of another engine.
    /// </summary>
    public static IReadOnlyList<DiscoveredConnection> FromPgService(
        string text, Func<string, IReadOnlyList<string>?> fieldKeysOf)
    {
        var found = new List<DiscoveredConnection>();
        string? service = null;
        var settings = new Dictionary<string, string?>();

        void Flush()
        {
            if (service is not null)
            {
                found.Add(MapCanonical("pg_service", service, null, "postgres", settings, fieldKeysOf));
            }
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                Flush();
                service = line[1..^1].Trim();
                settings = new Dictionary<string, string?>();
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0 || service is null)
            {
                continue;
            }

            // libpq's own names: dbname is the database, user is the user. A password here sits in plain
            // text in the user's own file, so it comes along.
            var key = line[..equals].Trim().ToLowerInvariant();
            var value = line[(equals + 1)..].Trim();
            switch (key)
            {
                case "host" or "hostaddr": Put(settings, "host", value); break;
                case "port": Put(settings, "port", value); break;
                case "dbname": Put(settings, "database", value); break;
                case "user": Put(settings, "username", value); break;
                case "password": Put(settings, "password", value); break;
            }
        }

        Flush();
        return found;
    }

    /// <summary>
    /// MySQL Workbench's <c>connections.xml</c>: a GRT object list where each connection carries its
    /// settings in a <c>parameterValues</c> dictionary. Always MySQL.
    /// </summary>
    public static IReadOnlyList<DiscoveredConnection> FromMySqlWorkbench(
        string xml, Func<string, IReadOnlyList<string>?> fieldKeysOf)
    {
        var doc = XDocument.Parse(xml);
        var found = new List<DiscoveredConnection>();

        foreach (var connection in doc.Descendants("value")
                     .Where(v => (string?)v.Attribute("struct-name") == "db.mgmt.Connection"))
        {
            var name = Keyed(connection, "name") ?? "(unnamed)";
            var parameters = connection.Elements("value")
                .FirstOrDefault(v => (string?)v.Attribute("key") == "parameterValues");

            var settings = new Dictionary<string, string?>();
            if (parameters is not null)
            {
                Put(settings, "host", Keyed(parameters, "hostName"));
                Put(settings, "port", Keyed(parameters, "port"));
                Put(settings, "database", Keyed(parameters, "schema"));
                Put(settings, "username", Keyed(parameters, "userName"));
            }

            found.Add(MapCanonical("Workbench", name, null, "mysql", settings, fieldKeysOf));
        }

        return found;

        static string? Keyed(XElement parent, string key) =>
            parent.Elements("value").FirstOrDefault(v => (string?)v.Attribute("key") == key)?.Value is { } value
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
    }

    /// <summary>
    /// The <c>mssql.connections</c> array in an Azure Data Studio or VS Code <c>settings.json</c>. Always
    /// SQL Server. Settings files are JSONC — comments and trailing commas are tolerated.
    /// </summary>
    public static IReadOnlyList<DiscoveredConnection> FromMssqlSettings(
        string json, Func<string, IReadOnlyList<string>?> fieldKeysOf)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        if (!doc.RootElement.TryGetProperty("mssql.connections", out var connections)
            || connections.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var found = new List<DiscoveredConnection>();
        foreach (var node in connections.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Object))
        {
            var server = Text(node, "server");
            var name = Text(node, "profileName") ?? server ?? "(unnamed)";

            var settings = new Dictionary<string, string?>();
            var (host, port) = SplitSqlServerServer(server);
            Put(settings, "host", host);
            Put(settings, "port", port);
            Put(settings, "database", Text(node, "database"));
            Put(settings, "username", Text(node, "user"));

            // Present only when the user declined the editor's credential store, and then it is plain text.
            Put(settings, "password", Text(node, "password"));

            found.Add(MapCanonical("Azure Data Studio", name, null, "sqlserver", settings, fieldKeysOf));
        }

        return found;
    }

    /// <summary>
    /// One MongoDB Compass connection file. Compass stores a <c>mongodb://</c> URI, which
    /// <see cref="ParseJdbcUrl"/> already understands, and keeps its credentials in a separate encrypted
    /// <c>connectionSecrets</c> field that is never touched here.
    /// </summary>
    public static IReadOnlyList<DiscoveredConnection> FromCompass(
        string json, Func<string, IReadOnlyList<string>?> fieldKeysOf)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("connectionInfo", out var info))
        {
            return [];
        }

        var url = info.TryGetProperty("connectionOptions", out var options) ? Text(options, "connectionString") : null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return [];
        }

        var name = (info.TryGetProperty("favorite", out var favorite) ? Text(favorite, "name") : null)
            ?? Text(info, "id")
            ?? "(unnamed)";

        return [Map("Compass", name, null, url, user: null, fieldKeysOf)];
    }

    // SQL Server writes its endpoint as one string: "host", "host,1433", "host\INSTANCE" or "tcp:host,1433".
    // Only the host and the port are portable; a named instance is resolved by the SQL Browser, which the
    // provider's own field can't express, so the instance is dropped and the host kept.
    private static (string? Host, string? Port) SplitSqlServerServer(string? server)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            return (null, null);
        }

        var value = server.Trim();
        if (value.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["tcp:".Length..];
        }

        string? port = null;
        if (value.LastIndexOf(',') is var comma and >= 0)
        {
            port = value[(comma + 1)..].Trim();
            value = value[..comma];
        }

        if (value.IndexOf('\\') is var backslash and > 0)
        {
            value = value[..backslash];
        }

        return (value, port);
    }

    private static string StripQuery(string value)
    {
        var query = value.IndexOf('?');
        return query >= 0 ? value[..query] : value;
    }

    private static void Put(IDictionary<string, string?> values, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[key] = value.Trim();
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static IReadOnlyList<string> Existing(IEnumerable<string> candidates) =>
        candidates.Where(File.Exists).Distinct(StringComparer.Ordinal).ToList();

    // Both editors use the same layout; VS Code is included because the mssql extension it shares with
    // Azure Data Studio writes its profiles into the same setting.
    private static IEnumerable<string> EditorConfigRoots()
    {
        string[] editors = ["azuredatastudio", "Code", "Code - Insiders", "VSCodium"];
        foreach (var editor in editors)
        {
            yield return OperatingSystem.IsMacOS()
                ? Path.Combine(Home, "Library", "Application Support", editor)
                : Path.Combine(AppData, editor);
        }
    }

    private static IEnumerable<string> CompassRoots()
    {
        const string compass = "MongoDB Compass";
        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(Home, "Library", "Application Support", compass);
            yield break;
        }

        yield return Path.Combine(AppData, compass);

        // The Linux build is commonly a flatpak, which redirects the whole config tree.
        if (!OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Home, ".var", "app", "com.mongodb.Compass", "config", compass);
        }
    }

    private static IEnumerable<string> JetBrainsRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JetBrains");

        // .NET maps ApplicationData to ~/.config on macOS too, but JetBrains uses the native location.
        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Library", "Application Support", "JetBrains");
        }
    }

    private static IEnumerable<string> DBeaverRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DBeaverData");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Library", "DBeaverData");
        }
        else
        {
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DBeaverData");
        }
    }

    private static IEnumerable<string> SubDirectories(string root)
    {
        try
        {
            return Directory.Exists(root) ? Directory.EnumerateDirectories(root) : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root, string pattern)
    {
        try
        {
            // AttributesToSkip defaults to Hidden|System, and DBeaver's config lives in a dot-directory
            // (workspace6/<project>/.dbeaver) — which is hidden on Unix, so the default skips it entirely.
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, pattern, new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.None
                })
                : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    // A config file that is locked, half-written or from a newer format must not take the whole scan down.
    private static IReadOnlyList<DiscoveredConnection> Read(
        string file, Func<string, IReadOnlyList<DiscoveredConnection>> parse)
    {
        try
        {
            return parse(File.ReadAllText(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or System.Xml.XmlException)
        {
            return [];
        }
    }
}
