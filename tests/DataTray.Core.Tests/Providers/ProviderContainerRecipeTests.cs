using DataTray.Core.Providers;
using DataTray.Providers.ClickHouse;
using DataTray.Providers.DragonflyDb;
using DataTray.Providers.Elasticsearch;
using DataTray.Providers.MongoDb;
using DataTray.Providers.MsSql;
using DataTray.Providers.MySql;
using DataTray.Providers.Postgres;
using DataTray.Providers.Redis;
using DataTray.Sdk;
using DataTray.Sdk.Provisioning;

namespace DataTray.Core.Tests.Providers;

// Since SE-176 the Docker plugin owns no recipe table: each containerisable engine's ContainerRecipe is the
// single source of truth and lives on its provider. These tests assert that content directly against the real
// providers (the Docker plugin's tests only prove the builder renders a given recipe correctly).
public class ProviderContainerRecipeTests
{
    private static IReadOnlyList<KeyValuePair<string, string>> Env(IDbProvider provider,
        string? database = null, string user = "admin", string password = "pw",
        params (string Key, string? Value)[] values)
    {
        var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (k, v) in values)
        {
            dict[k] = v;
        }

        return provider.ContainerRecipe!.Environment(new ContainerEnvInput(dict, database, user, password));
    }

    [Fact] // The bundled SQL Server provider declares its own recipe (SE-166 dogfood), pinned to 2025-latest.
    public void MsSql_declares_a_2025_recipe_with_mandatory_eula()
    {
        var recipe = new MsSqlProvider().ContainerRecipe;

        Assert.NotNull(recipe);
        Assert.Equal("mcr.microsoft.com/mssql/server", recipe!.Image);
        Assert.Equal("2025-latest", recipe.DefaultTag);
        Assert.Equal(1433, recipe.ContainerPort);
        Assert.True(recipe.DatabaseAfterStart); // a named database is created after the server starts

        var env = Env(new MsSqlProvider(), password: "pw");
        Assert.Contains(env, kv => kv is { Key: "ACCEPT_EULA", Value: "Y" });
        Assert.Contains(env, kv => kv is { Key: "MSSQL_SA_PASSWORD", Value: "pw" });
    }

    [Fact]
    public void Postgres_declares_the_three_postgres_env_vars()
    {
        var recipe = new PostgresProvider().ContainerRecipe;

        Assert.NotNull(recipe);
        Assert.Equal("postgres", recipe!.Image);
        Assert.Equal(5432, recipe.ContainerPort);

        var env = Env(new PostgresProvider(), database: "sales", user: "postgres", password: "pw");
        Assert.Contains(env, kv => kv is { Key: "POSTGRES_DB", Value: "sales" });
        Assert.Contains(env, kv => kv is { Key: "POSTGRES_USER", Value: "postgres" });
        Assert.Contains(env, kv => kv is { Key: "POSTGRES_PASSWORD", Value: "pw" });

        // No database given → the image's own default database name.
        Assert.Contains(Env(new PostgresProvider(), database: null, user: "postgres", password: "pw"),
            kv => kv is { Key: "POSTGRES_DB", Value: "postgres" });
    }

    [Fact] // Root user: only the root password; a non-root user additionally gets its own MYSQL_USER/PASSWORD.
    public void MySql_grants_a_dedicated_user_only_for_a_non_root_connection()
    {
        var recipe = new MySqlProvider().ContainerRecipe;
        Assert.NotNull(recipe);
        Assert.Equal("mysql", recipe!.Image);
        Assert.Equal(3306, recipe.ContainerPort);

        var root = Env(new MySqlProvider(), database: "app", user: "root", password: "pw");
        Assert.Contains(root, kv => kv is { Key: "MYSQL_ROOT_PASSWORD", Value: "pw" });
        Assert.Contains(root, kv => kv is { Key: "MYSQL_DATABASE", Value: "app" });
        Assert.DoesNotContain(root, kv => kv.Key == "MYSQL_USER");

        var appUser = Env(new MySqlProvider(), database: "app", user: "appuser", password: "pw");
        Assert.Contains(appUser, kv => kv is { Key: "MYSQL_USER", Value: "appuser" });
        Assert.Contains(appUser, kv => kv is { Key: "MYSQL_PASSWORD", Value: "pw" });
    }

    [Fact] // Mongo enables auth only when the connection actually carries a username.
    public void MongoDb_enables_auth_only_with_a_username()
    {
        var recipe = new MongoDbProvider().ContainerRecipe;
        Assert.NotNull(recipe);
        Assert.Equal("mongo", recipe!.Image);
        Assert.Equal(27017, recipe.ContainerPort);

        var withAuth = Env(new MongoDbProvider(), database: "shop", user: "admin", password: "pw",
            values: ("username", "admin"));
        Assert.Contains(withAuth, kv => kv is { Key: "MONGO_INITDB_ROOT_USERNAME", Value: "admin" });
        Assert.Contains(withAuth, kv => kv is { Key: "MONGO_INITDB_DATABASE", Value: "shop" });

        var noAuth = Env(new MongoDbProvider(), database: null, user: "admin", password: "pw");
        Assert.DoesNotContain(noAuth, kv => kv.Key == "MONGO_INITDB_ROOT_USERNAME");
    }

    [Fact] // Redis auth is a server flag (command), present only with a password; its "database" is a numeric index.
    public void Redis_requirepass_command_only_with_a_password()
    {
        var recipe = new RedisProvider().ContainerRecipe;
        Assert.NotNull(recipe);
        Assert.Equal("redis", recipe!.Image);
        Assert.False(recipe.NamedDatabase);

        var withPw = recipe.Command!(new ContainerEnvInput(new Dictionary<string, string?>(), null, "", "secret"));
        Assert.Equal(["redis-server", "--requirepass", "secret"], withPw);

        var noPw = recipe.Command!(new ContainerEnvInput(new Dictionary<string, string?>(), null, "", ""));
        Assert.Empty(noPw);
    }

    [Fact] // Dragonfly is Redis-wire-compatible: bare-flag command, wants an unlimited memlock ulimit.
    public void DragonflyDb_uses_bare_flags_and_memlock()
    {
        var recipe = new DragonflyDbProvider().ContainerRecipe;
        Assert.NotNull(recipe);
        Assert.Equal("docker.dragonflydb.io/dragonflydb/dragonfly", recipe!.Image);
        Assert.True(recipe.Memlock);
        Assert.False(recipe.NamedDatabase);

        var withPw = recipe.Command!(new ContainerEnvInput(new Dictionary<string, string?>(), null, "", "pw"));
        Assert.Equal(["--requirepass", "pw"], withPw);
    }

    [Fact] // Elasticsearch keeps its host port inside the connection's `url` (HostPortOverride).
    public void Elasticsearch_derives_the_host_port_from_the_url()
    {
        var recipe = new ElasticsearchProvider().ContainerRecipe;
        Assert.NotNull(recipe);
        Assert.Equal("docker.elastic.co/elasticsearch/elasticsearch", recipe!.Image);
        Assert.Equal(9200, recipe.ContainerPort);

        var env = Env(new ElasticsearchProvider(), password: "pw");
        Assert.Contains(env, kv => kv is { Key: "discovery.type", Value: "single-node" });
        Assert.Contains(env, kv => kv is { Key: "ELASTIC_PASSWORD", Value: "pw" });

        var url = new Dictionary<string, string?> { ["url"] = "https://localhost:9243" };
        Assert.Equal(9243, recipe.HostPortOverride!(url));
        Assert.Null(recipe.HostPortOverride!(new Dictionary<string, string?> { ["url"] = "https://localhost" }));
    }

    [Fact] // ClickHouse publishes the HTTP port (the driver speaks binary-over-HTTP, not the native 9000),
           // and needs access management on or its own user cannot run the CREATE USER/GRANT this provider builds.
    public void ClickHouse_publishes_the_http_port_and_enables_access_management()
    {
        var recipe = new ClickHouseProvider().ContainerRecipe;

        Assert.NotNull(recipe);
        Assert.Equal("clickhouse", recipe!.Image);
        Assert.Equal(8123, recipe.ContainerPort);
        Assert.Equal("/var/lib/clickhouse", recipe.DataPath);

        var env = Env(new ClickHouseProvider(), database: "analytics", user: "default", password: "pw");
        Assert.Contains(env, kv => kv is { Key: "CLICKHOUSE_USER", Value: "default" });
        Assert.Contains(env, kv => kv is { Key: "CLICKHOUSE_PASSWORD", Value: "pw" });
        Assert.Contains(env, kv => kv is { Key: "CLICKHOUSE_DB", Value: "analytics" });
        Assert.Contains(env, kv => kv is { Key: "CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT", Value: "1" });

        // No database given → the image keeps its own `default`, so the var is omitted entirely rather
        // than sent empty (which the image would create as a database literally named "").
        Assert.DoesNotContain(Env(new ClickHouseProvider(), database: null, user: "default", password: "pw"),
            kv => kv.Key == "CLICKHOUSE_DB");
    }

    [Fact] // The host catalog surfaces the real provider's recipe through the read-seam.
    public void Host_catalog_exposes_the_MsSql_recipe()
    {
        var registry = new DbProviderRegistry([new ProviderRegistration("sqlserver", new MsSqlProvider())]);

        var entry = Assert.Single(new HostProviderCatalog(registry).ContainerRecipes());
        Assert.Equal("sqlserver", entry.ProviderId);
        Assert.Equal("mcr.microsoft.com/mssql/server", entry.Recipe.Image);
    }
}
