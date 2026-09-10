using DataTray.Sdk;
using DataTray.Providers.MsSql;

namespace DataTray.Core.Tests.Providers;

/// <summary>
/// The <c>ALTER DATABASE</c> statements behind the editable Database Properties dialog (SE-262). Worth
/// pinning because the failure mode is silent: a wrong clause does not error, it changes a different
/// setting on someone's database, and nothing in the UI would say so.
/// </summary>
public class DatabaseOptionWriterTests
{
    private static readonly ISqlDialect Dialect = new MsSqlProvider().Dialect;

    private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private static IReadOnlyList<string> Alter(
        IReadOnlyDictionary<string, string> was, IReadOnlyDictionary<string, string> now, bool rollback = false) =>
        DatabaseOptionWriter.Alter(Dialect, "Fitting", was, now, rollback);

    [Fact]
    public void Only_changed_rows_are_written()
    {
        var was = Values(("autoClose", "OFF"), ("autoShrink", "OFF"));
        var now = Values(("autoClose", "ON"), ("autoShrink", "OFF"));

        Assert.Equal(["ALTER DATABASE [Fitting] SET AUTO_CLOSE ON"], Alter(was, now));
    }

    [Fact]
    public void An_untouched_page_writes_nothing()
    {
        var same = Values(("autoClose", "OFF"), ("recovery", "FULL"));

        Assert.Empty(Alter(same, same));
    }

    [Fact]
    public void A_choice_is_written_as_the_keyword_ALTER_DATABASE_takes_not_the_one_shown()
    {
        var statements = Alter(Values(("recovery", "FULL")), Values(("recovery", "BULK_LOGGED")));

        Assert.Equal(["ALTER DATABASE [Fitting] SET RECOVERY BULK_LOGGED"], statements);
    }

    [Fact]
    public void Target_recovery_time_carries_its_unit()
    {
        var statements = Alter(Values(("targetRecovery", "0")), Values(("targetRecovery", "60")));

        Assert.Equal(["ALTER DATABASE [Fitting] SET TARGET_RECOVERY_TIME = 60 SECONDS"], statements);
    }

    [Fact]
    public void The_database_name_is_quoted()
    {
        var statements = DatabaseOptionWriter.Alter(
            Dialect, "my database", Values(("autoClose", "OFF")), Values(("autoClose", "ON")), false);

        Assert.StartsWith("ALTER DATABASE [my database] SET", statements.Single());
    }

    // ── The options that need everyone else out ──────────────────────────────────────────────────────

    [Fact]
    public void An_exclusive_option_is_left_out_entirely_until_the_user_agrees_to_disconnect_sessions()
    {
        // Not emitted without the clause: SQL Server does not refuse these while others are connected, it
        // blocks indefinitely, which reads as a hung application rather than as a refused change.
        var statements = Alter(Values(("readOnly", "READ_WRITE")), Values(("readOnly", "READ_ONLY")));

        Assert.Empty(statements);
    }

    [Fact]
    public void With_agreement_an_exclusive_option_carries_ROLLBACK_IMMEDIATE()
    {
        var statements = Alter(
            Values(("readOnly", "READ_WRITE")), Values(("readOnly", "READ_ONLY")), rollback: true);

        Assert.Equal(["ALTER DATABASE [Fitting] SET READ_ONLY WITH ROLLBACK IMMEDIATE"], statements);
    }

    [Fact]
    public void A_non_exclusive_option_never_gets_the_clause_even_when_the_box_is_ticked()
    {
        // Disconnecting every session to flip AUTO_CLOSE would be a far bigger action than the one asked for.
        var statements = Alter(Values(("autoClose", "OFF")), Values(("autoClose", "ON")), rollback: true);

        Assert.Equal(["ALTER DATABASE [Fitting] SET AUTO_CLOSE ON"], statements);
    }

    [Fact]
    public void Restrict_access_and_the_broker_are_exclusive_too()
    {
        Assert.True(DatabaseOptionWriter.NeedsExclusiveAccess(
            Values(("userAccess", "MULTI_USER")), Values(("userAccess", "SINGLE_USER"))));
        Assert.True(DatabaseOptionWriter.NeedsExclusiveAccess(
            Values(("broker", "DISABLE_BROKER")), Values(("broker", "ENABLE_BROKER"))));
        Assert.True(DatabaseOptionWriter.NeedsExclusiveAccess(
            Values(("rcsi", "OFF")), Values(("rcsi", "ON"))));
    }

    [Fact]
    public void An_unchanged_exclusive_option_does_not_ask_for_exclusive_access()
    {
        var same = Values(("readOnly", "READ_ONLY"));

        Assert.False(DatabaseOptionWriter.NeedsExclusiveAccess(same, same));
    }

    [Fact]
    public void Every_declared_option_has_a_placeholder_for_its_value()
    {
        // A clause missing {0} silently emits the literal option name with no value — valid-looking T-SQL
        // that means something else.
        Assert.All(DatabaseOptionWriter.Options, o => Assert.Contains("{0}", o.Clause));
    }

    // ── Extended properties ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Database_level_properties_take_no_level_arguments()
    {
        var statements = DatabaseOptionWriter.ExtendedProperties(
            new Dictionary<string, string>(), Values(("Owner", "team-a")));

        Assert.Equal(["EXEC sp_addextendedproperty @name = N'Owner', @value = N'team-a'"], statements);
    }

    [Fact]
    public void A_changed_property_is_updated_and_a_gone_one_dropped()
    {
        var statements = DatabaseOptionWriter.ExtendedProperties(
            Values(("Owner", "team-a"), ("Ticket", "SE-1")), Values(("Owner", "team-b")));

        Assert.Contains("EXEC sp_updateextendedproperty @name = N'Owner', @value = N'team-b'", statements);
        Assert.Contains("EXEC sp_dropextendedproperty @name = N'Ticket'", statements);
    }

    [Fact]
    public void A_quote_in_a_property_cannot_end_the_literal()
    {
        var statements = DatabaseOptionWriter.ExtendedProperties(
            new Dictionary<string, string>(), Values(("Owner", "O'Brien")));

        Assert.Contains("N'O''Brien'", statements.Single());
    }

    // ── Autogrowth ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Autogrowth_states_the_unit_the_user_picked()
    {
        Assert.Equal(
            "ALTER DATABASE [Fitting] MODIFY FILE (NAME = N'Fitting_Data', FILEGROWTH = 64MB, MAXSIZE = UNLIMITED)",
            DatabaseOptionWriter.ModifyFile(Dialect, "Fitting", "Fitting_Data", 64, false, null));

        Assert.Contains("FILEGROWTH = 10%",
            DatabaseOptionWriter.ModifyFile(Dialect, "Fitting", "Fitting_Data", 10, true, null));
    }

    [Fact]
    public void A_growth_of_zero_turns_autogrowth_off_rather_than_emitting_no_unit()
    {
        // "FILEGROWTH = 0MB" is not how the engine spells "do not grow", and 0 with a unit is rejected.
        Assert.Contains("FILEGROWTH = 0,",
            DatabaseOptionWriter.ModifyFile(Dialect, "Fitting", "Fitting_Data", 0, false, null));
    }

    [Fact]
    public void A_limited_maximum_is_stated_in_megabytes()
    {
        Assert.Contains("MAXSIZE = 500MB",
            DatabaseOptionWriter.ModifyFile(Dialect, "Fitting", "Fitting_Data", 64, false, 500));
    }
}
