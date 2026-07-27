using System;
using System.Collections.Generic;
using System.Linq;
using DataTray.Sdk.Provisioning;

namespace DataTray.Backends.Docker.Tests;

// The builder is purely provider-driven (SE-176) and ships no recipe table of its own, so its tests feed it
// FIXTURE recipes. These mirror the real engines' render-relevant shape (image, env, command, memlock, host-port
// override) closely enough to exercise every render branch — they are test inputs, not production truth. The
// engines' real recipe CONTENT is asserted against the actual providers in DataTray.Core.Tests.
internal static class RecipeFixtures
{
    public static readonly ContainerRecipe Postgres = new(
        "postgres", "16", 5432, "/var/lib/postgresql/data", "postgres", "changeme",
        e => Env(("POSTGRES_DB", e.Database ?? "postgres"), ("POSTGRES_USER", e.User), ("POSTGRES_PASSWORD", e.Password)));

    // Carries an ACCEPT_EULA=Y env (a YAML 1.1 bool keyword — must be quoted) and no database env.
    public static readonly ContainerRecipe SqlServer = new(
        "mcr.microsoft.com/mssql/server", "2025-latest", 1433, "/var/opt/mssql", "sa", "Str0ng!Passw0rd",
        e => Env(("ACCEPT_EULA", "Y"), ("MSSQL_SA_PASSWORD", e.Password), ("MSSQL_PID", "Developer")),
        DatabaseAfterStart: true);

    // A command that only appears with a password, and a numeric-index "database".
    public static readonly ContainerRecipe Redis = new(
        "redis", "7", 6379, "/data", "", "",
        _ => [], Command: e => Blank(e.Password) ? [] : ["redis-server", "--requirepass", e.Password],
        NamedDatabase: false);

    // Bare-flag command + an unlimited memlock ulimit.
    public static readonly ContainerRecipe Dragonfly = new(
        "docker.dragonflydb.io/dragonflydb/dragonfly", "latest", 6379, "/data", "", "",
        _ => [], Command: e => Blank(e.Password) ? [] : ["--requirepass", e.Password],
        Memlock: true, NamedDatabase: false);

    // Keeps its host port inside the connection's `url`, parsed via HostPortOverride.
    public static readonly ContainerRecipe Elasticsearch = new(
        "docker.elastic.co/elasticsearch/elasticsearch", "8.13.0", 9200, "/usr/share/elasticsearch/data", "elastic", "changeme",
        e => Env(("discovery.type", "single-node"), ("xpack.security.enabled", "true"), ("ELASTIC_PASSWORD", e.Password)),
        HostPortOverride: values =>
            Uri.TryCreate(Get(values, "url"), UriKind.Absolute, out var uri) && !uri.IsDefaultPort ? uri.Port : null);

    /// <summary>A builder wired with the five fixture engines — the default fixture for render tests.</summary>
    public static DockerComposeBuilder Builder() => new(
    [
        new ProviderRecipe("postgres", "PostgreSQL", Postgres),
        new ProviderRecipe("sqlserver", "SQL Server", SqlServer),
        new ProviderRecipe("redis", "Redis", Redis),
        new ProviderRecipe("dragonflydb", "Dragonfly", Dragonfly),
        new ProviderRecipe("elasticsearch", "Elasticsearch", Elasticsearch),
    ]);

    private static IReadOnlyList<KeyValuePair<string, string>> Env(params (string Key, string Value)[] pairs) =>
        pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)).ToList();

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var v) ? v : null;

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);
}
