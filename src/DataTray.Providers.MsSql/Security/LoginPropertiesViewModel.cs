using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Data.SqlClient;
using DataTray.Sdk;
using DataTray.Sdk.Ui;

namespace DataTray.Providers.MsSql.Security;

/// <summary>
/// Backs the SQL Server login view (Route B). Self-contained: it reads databases through the provider and
/// runs its own DDL via <see cref="Microsoft.Data.SqlClient"/> against the profile's connection string.
/// v1 focuses on creating a login (General + Server Roles + User Mapping); editing prefills the name only,
/// with full state prefill a follow-up.
/// </summary>
public sealed class LoginPropertiesViewModel : INotifyPropertyChanged
{
    // Fixed SQL Server principals — no need to query for them; public is always a member.
    private static readonly string[] FixedServerRoles =
        ["sysadmin", "securityadmin", "serveradmin", "setupadmin", "processadmin", "diskadmin", "dbcreator", "bulkadmin"];

    private static readonly string[] FixedDbRoles =
        ["db_owner", "db_securityadmin", "db_accessadmin", "db_backupoperator", "db_ddladmin",
         "db_datareader", "db_datawriter", "db_denydatareader", "db_denydatawriter"];

    private readonly SecurityUiContext _context;
    private readonly string _connectionString;

    // Snapshots taken after prefill so Apply can emit the delta (ADD MEMBER vs DROP MEMBER, CREATE USER
    // vs DROP USER) instead of a full CREATE LOGIN. Empty for the New-Login branch.
    private HashSet<string> _originalServerRoles = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, MappingSnapshot> _originalMappings = new(StringComparer.OrdinalIgnoreCase);
    private string? _originalDefaultDatabase;

    // Suppresses RecomputePreview() during a bulk prefill so we don't build the preview N times mid-load.
    private bool _prefillInProgress;

    public LoginPropertiesViewModel(SecurityUiContext context)
    {
        _context = context;
        _connectionString = context.Profile.ConnectionString;
        IsNew = context.Action == SecurityUiAction.NewLogin;
        _loginName = context.Target?.Name ?? string.Empty;

        ServerRoles = new ObservableCollection<RoleRow>(
            FixedServerRoles.Select(r => new RoleRow(r, RecomputePreview)));

        RecomputePreview();
    }

    public bool IsNew { get; }
    public string PrimaryAction => IsNew ? "Create" : "Apply";

    // --- General ---
    private string _loginName;
    public string LoginName { get => _loginName; set { if (Set(ref _loginName, value)) RecomputePreview(); } }

    private bool _isSqlAuth = true;
    public bool IsSqlAuth
    {
        get => _isSqlAuth;
        set { if (Set(ref _isSqlAuth, value)) { OnPropertyChanged(nameof(IsWindowsAuth)); RecomputePreview(); } }
    }
    public bool IsWindowsAuth { get => !_isSqlAuth; set => IsSqlAuth = !value; }

    private string _password = string.Empty;
    public string Password { get => _password; set { if (Set(ref _password, value)) RecomputePreview(); } }

    private string _confirmPassword = string.Empty;
    public string ConfirmPassword { get => _confirmPassword; set { if (Set(ref _confirmPassword, value)) RecomputePreview(); } }

    public ObservableCollection<string> Databases { get; } = [];

    private string? _defaultDatabase;
    public string? DefaultDatabase { get => _defaultDatabase; set { if (Set(ref _defaultDatabase, value)) RecomputePreview(); } }

    private bool _enforcePolicy = true;
    public bool EnforcePolicy { get => _enforcePolicy; set { if (Set(ref _enforcePolicy, value)) RecomputePreview(); } }

    // --- Server roles ---
    public ObservableCollection<RoleRow> ServerRoles { get; }

    // --- User mapping ---
    public ObservableCollection<MappingRow> Mappings { get; } = [];

    private MappingRow? _selectedMapping;
    public MappingRow? SelectedMapping
    {
        get => _selectedMapping;
        set { if (Set(ref _selectedMapping, value)) OnPropertyChanged(nameof(SelectedDbRoles)); }
    }

    public IReadOnlyList<RoleRow> SelectedDbRoles => _selectedMapping?.DbRoles ?? [];

    // --- SQL preview + status ---
    private string _sqlPreview = string.Empty;
    public string SqlPreview { get => _sqlPreview; private set => Set(ref _sqlPreview, value); }

    private string? _status;
    public string? Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>Set by the view so buttons can close the hosting window.</summary>
    public Action? CloseRequested { get; set; }

    /// <summary>Loads databases (and roles per database) once the view is shown, and — for an existing
    /// login — prefills the current server/db-role membership and per-database user mapping so Apply can
    /// emit the delta instead of a full CREATE.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            _prefillInProgress = true;

            var dbs = await _context.Provider.GetDatabasesAsync(_context.Profile, CancellationToken.None);
            foreach (var db in dbs)
            {
                Databases.Add(db);
                Mappings.Add(new MappingRow(db, FixedDbRoles, RecomputePreview));
            }

            if (IsNew)
            {
                DefaultDatabase = dbs.Contains("master") ? "master" : dbs.FirstOrDefault();
            }
            else
            {
                await PrefillFromServerAsync();
            }

            SelectedMapping = Mappings.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            _prefillInProgress = false;
            RecomputePreview();
        }
    }

    // Read the existing login from sys.server_principals + sys.server_role_members, and per-database
    // mapping from sys.database_principals + sys.database_role_members (matched by SID). Anything we
    // find gets stamped onto the VM and snapshotted for the diff-Apply.
    private async Task PrefillFromServerAsync()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // --- Login basis ---
        byte[]? sid = null;
        await using (var cmd = new SqlCommand(
            "SELECT sid, type_desc, default_database_name FROM sys.server_principals WHERE name = @name AND type IN ('S','U','G');", connection))
        {
            cmd.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 128).Value = LoginName;
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                sid = reader["sid"] as byte[];
                var typeDesc = reader["type_desc"] as string ?? "SQL_LOGIN";
                IsSqlAuth = typeDesc == "SQL_LOGIN";
                var defaultDb = reader["default_database_name"] as string;
                if (!string.IsNullOrEmpty(defaultDb) && Databases.Contains(defaultDb))
                {
                    DefaultDatabase = defaultDb;
                }
                _originalDefaultDatabase = DefaultDatabase;
            }
        }

        // --- Server-role membership ---
        await using (var cmd = new SqlCommand(@"
            SELECT r.name FROM sys.server_role_members m
            JOIN sys.server_principals r ON r.principal_id = m.role_principal_id
            JOIN sys.server_principals u ON u.principal_id = m.member_principal_id
            WHERE u.name = @name;", connection))
        {
            cmd.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 128).Value = LoginName;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var role = reader.GetString(0);

                // Snapshot only roles the UI can represent. A user-defined server role has no checkbox, so it
                // could never appear in the Apply-time diff's "current" set — snapshotting it anyway made the
                // diff classify it as removed and emit a DROP MEMBER the user never asked for (SE-129).
                if (ServerRoles.FirstOrDefault(r => string.Equals(r.Name, role, StringComparison.OrdinalIgnoreCase)) is { } row)
                {
                    _originalServerRoles.Add(role);
                    row.SetCheckedSilent(true);
                }
            }
        }

        if (sid is null)
        {
            return; // login not found (renamed/dropped externally) — leave mappings blank
        }

        // --- Per-database mapping (user + db-role membership) ---
        foreach (var mapping in Mappings)
        {
            try
            {
                await PrefillMappingAsync(sid, mapping);
            }
            catch
            {
                // A single unreachable database (offline / no perms) shouldn't block the whole prefill —
                // that mapping just stays unchecked and the diff on Apply treats it as "add".
            }
        }
    }

    private async Task PrefillMappingAsync(byte[] loginSid, MappingRow mapping)
    {
        var builder = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = mapping.Database };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();

        string? userName = null;
        await using (var cmd = new SqlCommand("SELECT name FROM sys.database_principals WHERE sid = @sid;", connection))
        {
            cmd.Parameters.Add("@sid", System.Data.SqlDbType.VarBinary, 85).Value = loginSid;
            var result = await cmd.ExecuteScalarAsync();
            userName = result as string;
        }

        if (userName is null)
        {
            _originalMappings[mapping.Database] = new MappingSnapshot(false, string.Empty, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            return;
        }

        mapping.SetMappedSilent(true);
        mapping.SetUserNameSilent(userName);

        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new SqlCommand(@"
            SELECT r.name FROM sys.database_role_members m
            JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
            JOIN sys.database_principals u ON u.principal_id = m.member_principal_id
            WHERE u.name = @user;", connection))
        {
            cmd.Parameters.Add("@user", System.Data.SqlDbType.NVarChar, 128).Value = userName;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var role = reader.GetString(0);

                // Same reason as the server-role snapshot above: a custom database role has no checkbox, so
                // keeping it out of the snapshot is what stops the diff from revoking it (SE-129).
                if (mapping.DbRoles.FirstOrDefault(r => string.Equals(r.Name, role, StringComparison.OrdinalIgnoreCase)) is { } row)
                {
                    roles.Add(role);
                    row.SetCheckedSilent(true);
                }
            }
        }

        _originalMappings[mapping.Database] = new MappingSnapshot(true, userName, roles);
    }

    private void RecomputePreview()
    {
        if (_prefillInProgress)
        {
            return;
        }

        SqlPreview = IsNew ? BuildCreateScript(maskPassword: true) : BuildEditScript();
    }

    /// <summary>Builds the CREATE script for display. Apply does not run this — <see cref="ApplyCreateAsync"/>
    /// composes its own statements — which is why <paramref name="maskPassword"/> is safe here.</summary>
    /// <param name="maskPassword">Replaces the password literal with dots. The preview sits next to a masked
    /// password box, so echoing the plaintext there defeated that masking on screen shares and screenshots
    /// (SE-132). Must never be false for anything that reaches the server.</param>
    private string BuildCreateScript(bool maskPassword)
    {
        var name = QuoteId(LoginName);
        var sql = new StringBuilder();

        if (IsSqlAuth)
        {
            var password = maskPassword ? "N'••••••••'" : QuoteStr(Password);
            sql.Append($"CREATE LOGIN {name} WITH PASSWORD = {password}");
            if (!string.IsNullOrEmpty(DefaultDatabase)) sql.Append($", DEFAULT_DATABASE = {QuoteId(DefaultDatabase!)}");
            sql.Append($", CHECK_POLICY = {(EnforcePolicy ? "ON" : "OFF")};");
        }
        else
        {
            sql.Append($"CREATE LOGIN {name} FROM WINDOWS");
            if (!string.IsNullOrEmpty(DefaultDatabase)) sql.Append($" WITH DEFAULT_DATABASE = {QuoteId(DefaultDatabase!)}");
            sql.Append(';');
        }

        foreach (var role in ServerRoles.Where(r => r.IsChecked))
        {
            sql.Append($"\nALTER SERVER ROLE {QuoteId(role.Name)} ADD MEMBER {name};");
        }

        foreach (var map in Mappings.Where(m => m.IsMapped))
        {
            var user = QuoteId(string.IsNullOrWhiteSpace(map.UserName) ? LoginName : map.UserName);
            sql.Append($"\nUSE {QuoteId(map.Database)};");
            sql.Append($"\nCREATE USER {user} FOR LOGIN {name};");
            foreach (var dbRole in map.DbRoles.Where(r => r.IsChecked))
            {
                sql.Append($"\nALTER ROLE {QuoteId(dbRole.Name)} ADD MEMBER {user};");
            }
        }

        return sql.ToString();
    }

    // Diff between the snapshot taken during prefill and the current VM state — Apply runs exactly this.
    private string BuildEditScript()
    {
        var name = QuoteId(LoginName);
        var sql = new StringBuilder();

        // Server login itself: default-database change (v1: no password reset — deliberate scope).
        if (!string.Equals(_originalDefaultDatabase, DefaultDatabase, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(DefaultDatabase))
        {
            sql.Append($"ALTER LOGIN {name} WITH DEFAULT_DATABASE = {QuoteId(DefaultDatabase!)};");
        }

        // Server-role membership diff.
        var currentServerRoles = ServerRoles.Where(r => r.IsChecked).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var added in currentServerRoles.Where(r => !_originalServerRoles.Contains(r)))
        {
            AppendLine(sql, $"ALTER SERVER ROLE {QuoteId(added)} ADD MEMBER {name};");
        }
        foreach (var removed in _originalServerRoles.Where(r => !currentServerRoles.Contains(r)))
        {
            AppendLine(sql, $"ALTER SERVER ROLE {QuoteId(removed)} DROP MEMBER {name};");
        }

        // Per-database mapping diff (add user / drop user / add role / drop role).
        foreach (var mapping in Mappings)
        {
            var snapshot = _originalMappings.TryGetValue(mapping.Database, out var s)
                ? s
                : new MappingSnapshot(false, string.Empty, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            AppendMappingDiff(sql, mapping, snapshot, name);
        }

        return sql.Length == 0 ? "-- no changes" : sql.ToString();
    }

    private void AppendMappingDiff(StringBuilder sql, MappingRow mapping, MappingSnapshot snapshot, string quotedLogin)
    {
        var currentUser = string.IsNullOrWhiteSpace(mapping.UserName) ? LoginName : mapping.UserName;
        var currentRoles = mapping.DbRoles.Where(r => r.IsChecked).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Case A: was mapped, now unmapped → DROP USER.
        if (snapshot.Mapped && !mapping.IsMapped)
        {
            AppendUse(sql, mapping.Database);
            AppendLine(sql, $"DROP USER {QuoteId(snapshot.UserName)};");
            return;
        }

        // Case B: was not mapped, now mapped → CREATE USER + add roles.
        if (!snapshot.Mapped && mapping.IsMapped)
        {
            AppendUse(sql, mapping.Database);
            AppendLine(sql, $"CREATE USER {QuoteId(currentUser)} FOR LOGIN {quotedLogin};");
            foreach (var role in currentRoles)
            {
                AppendLine(sql, $"ALTER ROLE {QuoteId(role)} ADD MEMBER {QuoteId(currentUser)};");
            }
            return;
        }

        // Case C: still mapped — role diff only. (User-rename is out of scope for v1 of SE-116.)
        if (!snapshot.Mapped)
        {
            return;
        }

        var added = currentRoles.Where(r => !snapshot.DbRoles.Contains(r)).ToList();
        var removed = snapshot.DbRoles.Where(r => !currentRoles.Contains(r)).ToList();
        if (added.Count == 0 && removed.Count == 0)
        {
            return;
        }

        AppendUse(sql, mapping.Database);
        foreach (var role in added)
        {
            AppendLine(sql, $"ALTER ROLE {QuoteId(role)} ADD MEMBER {QuoteId(snapshot.UserName)};");
        }
        foreach (var role in removed)
        {
            AppendLine(sql, $"ALTER ROLE {QuoteId(role)} DROP MEMBER {QuoteId(snapshot.UserName)};");
        }
    }

    private static void AppendUse(StringBuilder sql, string database) => AppendLine(sql, $"USE {QuoteId(database)};");

    private static void AppendLine(StringBuilder sql, string line)
    {
        if (sql.Length > 0) sql.Append('\n');
        sql.Append(line);
    }

    /// <summary>Runs the login DDL (CREATE for new, diff for edit), then the per-database mapping in each
    /// database's own context. Splits on <c>USE [db];</c> so per-db statements land on the right connection.</summary>
    public async Task<bool> ApplyAsync()
    {
        try
        {
            Status = null;

            if (string.IsNullOrWhiteSpace(LoginName))
            {
                Status = "Enter a login name.";
                return false;
            }
            if (IsNew && IsSqlAuth && Password != ConfirmPassword)
            {
                Status = "The passwords do not match.";
                return false;
            }

            return IsNew ? await ApplyCreateAsync() : await ApplyEditAsync();
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            return false;
        }
    }

    private async Task<bool> ApplyCreateAsync()
    {
        var name = QuoteId(LoginName);
        var loginScript = new StringBuilder();
        if (IsSqlAuth)
        {
            loginScript.Append($"CREATE LOGIN {name} WITH PASSWORD = {QuoteStr(Password)}");
            if (!string.IsNullOrEmpty(DefaultDatabase)) loginScript.Append($", DEFAULT_DATABASE = {QuoteId(DefaultDatabase!)}");
            loginScript.Append($", CHECK_POLICY = {(EnforcePolicy ? "ON" : "OFF")};");
        }
        else
        {
            loginScript.Append($"CREATE LOGIN {name} FROM WINDOWS");
            if (!string.IsNullOrEmpty(DefaultDatabase)) loginScript.Append($" WITH DEFAULT_DATABASE = {QuoteId(DefaultDatabase!)}");
            loginScript.Append(';');
        }
        foreach (var role in ServerRoles.Where(r => r.IsChecked))
        {
            loginScript.Append($"\nALTER SERVER ROLE {QuoteId(role.Name)} ADD MEMBER {name};");
        }

        await using (var connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var cmd = new SqlCommand(loginScript.ToString(), connection);
            await cmd.ExecuteNonQueryAsync();
        }

        foreach (var map in Mappings.Where(m => m.IsMapped))
        {
            var user = QuoteId(string.IsNullOrWhiteSpace(map.UserName) ? LoginName : map.UserName);
            var mapScript = new StringBuilder($"CREATE USER {user} FOR LOGIN {name};");
            foreach (var dbRole in map.DbRoles.Where(r => r.IsChecked))
            {
                mapScript.Append($"\nALTER ROLE {QuoteId(dbRole.Name)} ADD MEMBER {user};");
            }

            await RunInDatabaseAsync(map.Database, mapScript.ToString());
        }

        return true;
    }

    // Runs the same diff BuildEditScript produced, per-database context where needed (USE [db]; splits).
    private async Task<bool> ApplyEditAsync()
    {
        var script = BuildEditScript();
        if (script == "-- no changes")
        {
            return true;
        }

        // Split on `USE [db];` markers into (database, statements) chunks; anything before the first
        // marker runs on the server (default) connection.
        var chunks = SplitByUseStatements(script);

        await using (var connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            if (!string.IsNullOrWhiteSpace(chunks.Server))
            {
                await using var cmd = new SqlCommand(chunks.Server, connection);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        foreach (var (database, body) in chunks.PerDatabase)
        {
            await RunInDatabaseAsync(database, body);
        }

        return true;
    }

    private async Task RunInDatabaseAsync(string database, string body)
    {
        var builder = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = database };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new SqlCommand(body, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    // Simple splitter: walks the script line by line; a `USE [name];` line switches the active database.
    private static (string Server, IReadOnlyList<(string Database, string Body)> PerDatabase) SplitByUseStatements(string script)
    {
        var server = new StringBuilder();
        var perDb = new List<(string, string)>();
        var currentDb = (string?)null;
        var currentBody = new StringBuilder();

        foreach (var rawLine in script.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("USE [", StringComparison.OrdinalIgnoreCase))
            {
                if (currentDb is not null && currentBody.Length > 0)
                {
                    perDb.Add((currentDb, currentBody.ToString()));
                }

                currentDb = ParseUseDatabase(line)
                    ?? throw new InvalidOperationException($"Could not parse the database name from: {line}");
                currentBody.Clear();
                continue;
            }

            if (currentDb is null)
            {
                if (server.Length > 0) server.Append('\n');
                server.Append(rawLine);
            }
            else
            {
                if (currentBody.Length > 0) currentBody.Append('\n');
                currentBody.Append(rawLine);
            }
        }

        if (currentDb is not null && currentBody.Length > 0)
        {
            perDb.Add((currentDb, currentBody.ToString()));
        }

        return (server.ToString(), perDb);
    }

    /// <summary>Reads the database name out of a <c>USE [name];</c> line, undoing <see cref="QuoteId"/>'s
    /// escaping. Returns null when the line has no closing bracket.</summary>
    private static string? ParseUseDatabase(string line)
    {
        // Must mirror QuoteId: inside the brackets a literal ] is written as ]], so the closing bracket is the
        // first ] that is not doubled. Scanning for the first ] instead would stop halfway through a name like
        // `a]b` (written `[a]]b]`), yielding "a" — and RunInDatabaseAsync would then happily target a different
        // database of that name without any error (SE-131).
        var name = new StringBuilder();
        for (var i = "USE [".Length; i < line.Length; i++)
        {
            if (line[i] != ']')
            {
                name.Append(line[i]);
                continue;
            }

            if (i + 1 < line.Length && line[i + 1] == ']')
            {
                name.Append(']');
                i++;
                continue;
            }

            return name.ToString();
        }

        return null;
    }

    private static string QuoteId(string name) => $"[{name.Replace("]", "]]")}]";
    private static string QuoteStr(string value) => $"N'{value.Replace("'", "''")}'";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>Original per-database mapping state captured during prefill so Apply can emit the delta.</summary>
public sealed record MappingSnapshot(bool Mapped, string UserName, HashSet<string> DbRoles);

/// <summary>A checkable role membership row (server or database), notifying the VM to re-preview on toggle.</summary>
public sealed class RoleRow : INotifyPropertyChanged
{
    private readonly Action _onChange;
    public RoleRow(string name, Action onChange) { Name = name; _onChange = onChange; }

    public string Name { get; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set { if (_isChecked != value) { _isChecked = value; OnPropertyChanged(nameof(IsChecked)); _onChange(); } }
    }

    /// <summary>Prefill-time setter that skips the re-preview callback (bulk load path).</summary>
    public void SetCheckedSilent(bool value)
    {
        if (_isChecked == value) return;
        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One database's mapping: whether the login is mapped, the user name, and its db-role memberships.</summary>
public sealed class MappingRow : INotifyPropertyChanged
{
    private readonly Action _onChange;
    public MappingRow(string database, string[] dbRoles, Action onChange)
    {
        Database = database;
        _onChange = onChange;
        DbRoles = dbRoles.Select(r => new RoleRow(r, onChange)).ToList();
    }

    public string Database { get; }
    public IReadOnlyList<RoleRow> DbRoles { get; }

    private bool _isMapped;
    public bool IsMapped
    {
        get => _isMapped;
        set { if (_isMapped != value) { _isMapped = value; OnPropertyChanged(nameof(IsMapped)); _onChange(); } }
    }

    private string _userName = string.Empty;
    public string UserName
    {
        get => _userName;
        set { if (_userName != value) { _userName = value; OnPropertyChanged(nameof(UserName)); _onChange(); } }
    }

    public void SetMappedSilent(bool value)
    {
        if (_isMapped == value) return;
        _isMapped = value;
        OnPropertyChanged(nameof(IsMapped));
    }

    public void SetUserNameSilent(string value)
    {
        if (_userName == value) return;
        _userName = value;
        OnPropertyChanged(nameof(UserName));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
