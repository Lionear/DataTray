using System.Globalization;
using System.Text;

namespace DataTray.Providers.MsSql;

/// <summary>
/// An SSIS Agent step's command: the fields it is built from, the reader that recovers them from a command
/// SSMS wrote, and the builder that writes one back. Pure and public, because the string is a dtexec argument
/// list where every escaped quote is load-bearing and a mistake only shows up when the job runs.
/// </summary>
/// <remarks>
/// The quoting is not uniform and that is the point of doing it here rather than in a text box. A package path
/// is wrapped twice — <c>"\"…\""</c> — while a <c>/CONNECTION</c> name is wrapped once and its value twice, and
/// a <c>/Par</c> name is wrapped twice while its value is wrapped once. Those are the shapes SSMS produces.
/// </remarks>
public sealed record SsisStepCommand
{
    // Server options the catalog reads out of the command rather than from the step's fields.
    private const string LoggingLevelOption = "$ServerOption::LOGGING_LEVEL";
    private const string SynchronizedOption = "$ServerOption::SYNCHRONIZED";

    // A connection manager override is a parameter named CM.<connection manager>.ConnectionString.
    private const string ConnectionParameterPrefix = "CM.";
    private const string ConnectionParameterSuffix = ".ConnectionString";

    /// <summary>Options every Agent-written step carries that hold nothing the editor asks for.</summary>
    private static readonly HashSet<string> IgnoredOptions =
        new(StringComparer.OrdinalIgnoreCase) { "/CALLERINFO", "/REPORTING", "/CHECKPOINTING" };

    public SsisPackageSource Source { get; init; }

    public string PackagePath { get; init; } = string.Empty;

    public string Server { get; init; } = string.Empty;

    /// <summary>
    /// The environment reference id, not its name. Turning 12 into "PROD" needs
    /// <c>catalog.environment_references</c>, which the page does; losing the id is what leaves a step failing
    /// at run time with nothing to go on.
    /// </summary>
    public int? EnvironmentReference { get; init; }

    /// <summary>0 none, 1 basic, 2 performance, 3 verbose, 4 runtime lineage. Catalog only.</summary>
    public int? LoggingLevel { get; init; }

    public bool Use32BitRuntime { get; init; }

    public bool WaitForCompletion { get; init; } = true;

    public string? PackagePassword { get; init; }

    public IReadOnlyList<SsisConnectionOverride> ConnectionOverrides { get; init; } = [];

    /// <summary>True where the source has a catalog behind it, so environments and logging apply.</summary>
    public bool IsCatalog => Source == SsisPackageSource.Catalog;

    // ── Reading ──────────────────────────────────────────────────────────────────────────────────────

    public static SsisParseResult Parse(string command)
    {
        var tokens = Tokenize(command);
        var unsupported = new List<string>();
        var overrides = new List<SsisConnectionOverride>();

        SsisPackageSource? source = null;
        string path = string.Empty, server = string.Empty;
        string? password = null;
        int? environment = null, logging = null;
        bool use32Bit = false, wait = true;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith('/'))
            {
                continue;
            }

            // Every option below except /X86 takes the next token as its value.
            var value = i + 1 < tokens.Count && !tokens[i + 1].StartsWith('/') ? tokens[i + 1] : null;

            switch (token.ToUpperInvariant())
            {
                case "/ISSERVER": source = SsisPackageSource.Catalog; path = Unquote(value); break;
                case "/FILE": source = SsisPackageSource.FileSystem; path = Unquote(value); break;
                case "/SQL": source = SsisPackageSource.MsdbStore; path = Unquote(value); break;
                case "/DTS": source = SsisPackageSource.ManagedFolderStore; path = Unquote(value); break;
                case "/SERVER": server = Unquote(value); break;
                case "/DECRYPT": password = Unquote(value); break;
                case "/X86": use32Bit = true; break;

                case "/ENVREFERENCE":
                    environment = int.TryParse(Unquote(value), out var reference) ? reference : null;
                    break;

                case "/CONNECTION":
                    var (name, connection) = SplitPair(value);
                    overrides.Add(new SsisConnectionOverride(Unquote(name), Unquote(connection)));
                    break;

                case "/PAR":
                    ReadParameter(value, overrides, ref logging, ref wait, unsupported);
                    break;

                default:
                    if (!IgnoredOptions.Contains(token))
                    {
                        unsupported.Add(token.ToUpperInvariant());
                    }

                    break;
            }
        }

        // No package means nothing to fill the fields from — an empty command, or someone's prose.
        if (source is null || unsupported.Count > 0)
        {
            return new SsisParseResult(null, unsupported);
        }

        return new SsisParseResult(
            new SsisStepCommand
            {
                Source = source.Value,
                PackagePath = path,
                Server = server,
                EnvironmentReference = environment,
                LoggingLevel = logging,
                Use32BitRuntime = use32Bit,
                WaitForCompletion = wait,
                PackagePassword = password,
                ConnectionOverrides = overrides
            },
            []);
    }

    /// <summary>
    /// A <c>/Par</c> is three different things by name: a server option, a connection manager override, or a
    /// package parameter the editor does not model and will not silently drop.
    /// </summary>
    private static void ReadParameter(
        string? value, List<SsisConnectionOverride> overrides,
        ref int? logging, ref bool wait, List<string> unsupported)
    {
        var (rawName, rawValue) = SplitPair(value);
        var name = Unquote(rawName);
        var parameter = Unquote(rawValue);

        // The name carries its type in brackets — LOGGING_LEVEL(Int16) — which is noise for matching.
        var bare = name.Split('(')[0];

        if (bare == LoggingLevelOption)
        {
            logging = int.TryParse(parameter, out var level) ? level : null;
        }
        else if (bare == SynchronizedOption)
        {
            wait = !string.Equals(parameter, "False", StringComparison.OrdinalIgnoreCase);
        }
        else if (name.StartsWith(ConnectionParameterPrefix, StringComparison.OrdinalIgnoreCase)
                 && name.EndsWith(ConnectionParameterSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var manager = name[ConnectionParameterPrefix.Length..^ConnectionParameterSuffix.Length];
            overrides.Add(new SsisConnectionOverride(manager, parameter));
        }
        else
        {
            unsupported.Add($"/Par {name}");
        }
    }

    // ── Writing ──────────────────────────────────────────────────────────────────────────────────────

    public static string Build(SsisStepCommand command)
    {
        var parts = new List<string>
        {
            $"{Verb(command.Source)} {Wrapped(command.PackagePath)}"
        };

        if (command.Source != SsisPackageSource.FileSystem && command.Server.Length > 0)
        {
            parts.Add($"/SERVER {Wrapped(command.Server)}");
        }

        if (command.PackagePassword is { Length: > 0 } password)
        {
            parts.Add($"/DECRYPT {password}");
        }

        if (command.IsCatalog)
        {
            if (command.EnvironmentReference is { } reference)
            {
                parts.Add($"/ENVREFERENCE {reference}");
            }

            // In the catalog a connection manager is a parameter: the name is wrapped twice, the value once.
            parts.AddRange(command.ConnectionOverrides.Select(o =>
                $"/Par {Wrapped($"{ConnectionParameterPrefix}{o.Name}{ConnectionParameterSuffix}")};{Quoted(o.Value)}"));

            if (command.LoggingLevel is { } level)
            {
                parts.Add($"/Par {Wrapped($"{LoggingLevelOption}(Int16)")};{level.ToString(CultureInfo.InvariantCulture)}");
            }

            parts.Add($"/Par {Wrapped($"{SynchronizedOption}(Boolean)")};{(command.WaitForCompletion ? "True" : "False")}");
        }
        else
        {
            // Outside the catalog it is /CONNECTION, and the wrapping is the other way round.
            parts.AddRange(command.ConnectionOverrides.Select(o =>
                $"/CONNECTION {Quoted(o.Name)};{Wrapped(o.Value)}"));
        }

        if (command.Use32BitRuntime)
        {
            parts.Add("/X86");
        }

        // What Agent writes for every step: the catalog reports through the server, the rest checkpoint off.
        parts.Add(command.IsCatalog ? "/CALLERINFO SQLAGENT /REPORTING E" : "/CHECKPOINTING OFF /REPORTING E");

        return string.Join(' ', parts);
    }

    private static string Verb(SsisPackageSource source) => source switch
    {
        SsisPackageSource.Catalog => "/ISSERVER",
        SsisPackageSource.FileSystem => "/FILE",
        SsisPackageSource.MsdbStore => "/SQL",
        _ => "/DTS"
    };

    // ── Quoting ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A value wrapped once: <c>"value"</c>.</summary>
    private static string Quoted(string value) => $"\"{value}\"";

    /// <summary>A value wrapped twice, the shape a path takes: <c>"\"value\""</c>.</summary>
    private static string Wrapped(string value) => $"\"\\\"{value}\\\"\"";

    /// <summary>Strips both layers of wrapping, whichever of the two a value happens to carry.</summary>
    private static string Unquote(string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var text = Strip(value).Replace("\\\"", "\"");
        return Strip(text);
    }

    private static string Strip(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    // ── Splitting ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits an option's argument on the first semicolon outside quotes. Only the first: a connection string
    /// is full of semicolons and they all belong to the value.
    /// </summary>
    private static (string Name, string Value) SplitPair(string? value)
    {
        if (value is null)
        {
            return (string.Empty, string.Empty);
        }

        var quoted = false;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length && value[i + 1] == '"')
            {
                i++;
            }
            else if (value[i] == '"')
            {
                quoted = !quoted;
            }
            else if (value[i] == ';' && !quoted)
            {
                return (value[..i], value[(i + 1)..]);
            }
        }

        return (value, string.Empty);
    }

    /// <summary>
    /// Splits the command into arguments on whitespace outside quotes. An escaped quote does not close a
    /// quoted run, which is what keeps a path with a space in it one argument.
    /// </summary>
    private static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (c == '\\' && i + 1 < command.Length && command[i + 1] == '"')
            {
                token.Append(c).Append(command[++i]);
            }
            else if (c == '"')
            {
                quoted = !quoted;
                token.Append(c);
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (token.Length > 0)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                }
            }
            else
            {
                token.Append(c);
            }
        }

        if (token.Length > 0)
        {
            tokens.Add(token.ToString());
        }

        return tokens;
    }
}
