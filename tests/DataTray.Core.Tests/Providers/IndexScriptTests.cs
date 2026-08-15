using DataTray.Sdk;
using DataTray.Sdk.Ddl;
using DataTray.Sdk.Schema;
using DataTray.Sdk.Ui;
using DataTray.Providers.MsSql;

namespace DataTray.Core.Tests.Providers;

/// <summary>
/// The T-SQL behind SQL Server's Index Properties dialog (SE-252). The single rule this file exists to
/// protect: <c>DROP_EXISTING</c> silently resets every option the statement does not restate, so a rebuild
/// that leaves one out quietly undoes a setting the user never opened.
/// </summary>
public class IndexScriptTests
{
    private static readonly ISqlDialect Dialect = new MsSqlProvider().Dialect;

    private static IndexDefinition Index(params IndexColumn[] columns) => new()
    {
        Schema = "app",
        Table = "Fitting",
        Name = "IX_Fitting_Name",
        Columns = columns.Length > 0 ? columns : [new IndexColumn("Name")]
    };

    [Fact]
    public void A_new_index_names_its_type_and_spells_out_every_sort_order()
    {
        var sql = IndexScript.Create(Dialect, Index(), dropExisting: false);

        Assert.StartsWith(
            "CREATE NONCLUSTERED INDEX [IX_Fitting_Name] ON [app].[Fitting] ([Name] ASC) WITH (", sql);
    }

    [Fact]
    public void Included_columns_and_a_filter_land_between_the_key_list_and_the_options()
    {
        var index = Index(new IndexColumn("Slot", Descending: true), new IndexColumn("Note", Included: true)) with
        {
            Filter = "([Slot]>(0))"
        };

        var sql = IndexScript.Create(Dialect, index, dropExisting: false);

        Assert.Contains("([Slot] DESC) INCLUDE ([Note]) WHERE ([Slot]>(0)) WITH (", sql);
    }

    [Fact]
    public void Every_option_is_restated_on_a_rebuild_even_when_the_user_never_opened_the_options_page()
    {
        // The whole point of the ticket: DROP_EXISTING resets what it is not told, so an index that was
        // padded, non-recomputing and page-lock-free has to say so again or it comes back on defaults.
        var index = Index() with
        {
            PadIndex = true,
            FillFactor = 70,
            StatisticsNoRecompute = true,
            AllowPageLocks = false,
            OptimizeForSequentialKey = true
        };

        var sql = IndexScript.Create(Dialect, index, dropExisting: true);

        Assert.Contains("PAD_INDEX = ON", sql);
        Assert.Contains("STATISTICS_NORECOMPUTE = ON", sql);
        Assert.Contains("ALLOW_ROW_LOCKS = ON", sql);
        Assert.Contains("ALLOW_PAGE_LOCKS = OFF", sql);
        Assert.Contains("OPTIMIZE_FOR_SEQUENTIAL_KEY = ON", sql);
        Assert.Contains("FILLFACTOR = 70", sql);
        Assert.Contains("DROP_EXISTING = ON", sql);
    }

    [Fact]
    public void A_default_fill_factor_is_left_out_because_zero_is_not_a_legal_FILLFACTOR()
    {
        Assert.DoesNotContain("FILLFACTOR", IndexScript.Create(Dialect, Index(), dropExisting: false));
    }

    [Fact]
    public void OPTIMIZE_FOR_SEQUENTIAL_KEY_is_omitted_where_the_server_does_not_parse_it()
    {
        // Null means "server predates 2019". Emitting it OFF there fails the whole batch at parse time,
        // which would break every rebuild on 2016/2017 rather than degrade.
        var sql = IndexScript.Create(Dialect, Index() with { OptimizeForSequentialKey = null }, dropExisting: true);

        Assert.DoesNotContain("OPTIMIZE_FOR_SEQUENTIAL_KEY", sql);
    }

    [Fact]
    public void IGNORE_DUP_KEY_stays_off_on_a_non_unique_index_where_the_server_rejects_it()
    {
        var sql = IndexScript.Create(Dialect, Index() with { IgnoreDupKey = true, Unique = false }, dropExisting: false);

        Assert.Contains("IGNORE_DUP_KEY = OFF", sql);
    }

    [Fact]
    public void A_partition_scheme_carries_the_column_it_partitions_on()
    {
        // Without the column the ON clause is a syntax error; falling back to the bare name would move a
        // partitioned index onto a single filegroup instead.
        var sql = IndexScript.Create(
            Dialect, Index() with { DataSpace = "ps_ByMonth", PartitionColumn = "CreatedOn" }, dropExisting: true);

        Assert.EndsWith("ON [ps_ByMonth] ([CreatedOn])", sql);
    }

    [Fact]
    public void An_unchanged_index_produces_no_statements_at_all()
    {
        var original = Index();

        Assert.Empty(IndexScript.Alter(Dialect, original, Index()));
    }

    [Fact]
    public void A_column_list_that_matches_by_value_still_counts_as_unchanged()
    {
        // Records compare a list by reference, so an index read from the catalog would never equal the one
        // the dialog holds — and pressing OK on an untouched dialog would rebuild it every time.
        var original = Index(new IndexColumn("Name"), new IndexColumn("Slot", Descending: true));
        var wanted = Index(new IndexColumn("Name"), new IndexColumn("Slot", Descending: true));

        Assert.Empty(IndexScript.Alter(Dialect, original, wanted));
    }

    [Fact]
    public void Renaming_alone_is_an_sp_rename_and_not_a_rebuild()
    {
        var statements = IndexScript.Alter(Dialect, Index(), Index() with { Name = "IX_Fitting_Label" });

        Assert.Equal(
            ["EXEC sp_rename N'[app].[Fitting].[IX_Fitting_Name]', N'IX_Fitting_Label', N'INDEX'"],
            statements);
    }

    [Fact]
    public void A_rename_plus_a_change_renames_first_so_the_rebuild_addresses_the_new_name()
    {
        var wanted = Index(new IndexColumn("Slot")) with { Name = "IX_Fitting_Slot" };

        var statements = IndexScript.Alter(Dialect, Index(), wanted);

        Assert.Equal(2, statements.Count);
        Assert.StartsWith("EXEC sp_rename", statements[0]);
        Assert.Contains("[IX_Fitting_Slot]", statements[1]);
        Assert.Contains("DROP_EXISTING = ON", statements[1]);
    }

    [Fact]
    public void Changing_a_key_column_rebuilds_in_place_with_DROP_EXISTING()
    {
        var statements = IndexScript.Alter(Dialect, Index(), Index(new IndexColumn("Slot")));

        Assert.Single(statements);
        Assert.Contains("DROP_EXISTING = ON", statements[0]);
    }

    [Fact]
    public void Switching_between_clustered_and_nonclustered_drops_and_recreates_instead()
    {
        // DROP_EXISTING refuses some of those conversions, and a statement the server rejects is worse than
        // the table rebuild the change costs either way.
        var statements = IndexScript.Alter(Dialect, Index(), Index() with { Clustered = true });

        Assert.Equal(2, statements.Count);
        Assert.Equal("DROP INDEX [IX_Fitting_Name] ON [app].[Fitting]", statements[0]);
        Assert.Contains("CREATE CLUSTERED INDEX", statements[1]);
        Assert.DoesNotContain("DROP_EXISTING", statements[1]);
    }

    [Fact]
    public void A_new_index_is_one_CREATE_with_no_DROP_EXISTING()
    {
        var statements = IndexScript.Alter(Dialect, original: null, Index());

        Assert.Single(statements);
        Assert.DoesNotContain("DROP_EXISTING", statements[0]);
    }

    [Fact]
    public void An_index_with_no_key_columns_is_refused_before_it_reaches_the_server()
    {
        var index = Index(new IndexColumn("Note", Included: true));

        Assert.Throws<InvalidOperationException>(() => IndexScript.Create(Dialect, index, dropExisting: false));
    }

    [Fact]
    public void A_filtered_index_scripts_with_SET_QUOTED_IDENTIFIER_ON()
    {
        // SqlClient sets it, so what OK runs needs no preamble — but the script is meant to be pasted
        // somewhere else, and sqlcmd does not, where the failure names indexed views and computed columns
        // and never mentions the filter.
        var script = IndexScript.Script(Dialect, original: null, Index() with { Filter = "([Slot]>(0))" });

        Assert.StartsWith("SET QUOTED_IDENTIFIER ON;", script);
        Assert.Contains("\r\nGO\r\n", script);
    }

    [Fact]
    public void An_unfiltered_index_scripts_without_the_preamble()
    {
        Assert.StartsWith("CREATE ", IndexScript.Script(Dialect, original: null, Index()));
    }

    [Fact]
    public void Scripting_an_untouched_index_says_so_rather_than_offering_an_empty_tab()
    {
        Assert.Equal("-- Nothing to change.", IndexScript.Script(Dialect, Index(), Index()));
    }

    [Fact]
    public void SQL_Server_owns_the_New_Index_dialog_and_leaves_the_other_kinds_to_the_host()
    {
        // The menu item is gated on this: answering true for a kind with no view would replace a working
        // generic dialog with nothing.
        var provider = new MsSqlProvider();

        Assert.True(provider.HasCreateUiFor(DbObjectKind.Index));
        Assert.False(provider.HasCreateUiFor(DbObjectKind.Table));
        Assert.False(provider.HasCreateUiFor(DbObjectKind.Schema));
        Assert.False(provider.HasCreateUiFor(DbObjectKind.Database));
    }

    [Fact]
    public void An_index_node_gets_the_properties_dialog_and_it_owns_its_own_buttons()
    {
        // Asserted through the interface: InfoViewOwnsActionBar is a default member, so a declaration that
        // stops fulfilling it still compiles and still reads true on the class while the host sees false —
        // and the dialog would come back with both OK/Cancel and a host Close row.
        ICustomNodeInfoUi provider = new MsSqlProvider();
        var index = new DbNodeRef(DbNodeKind.Index, "IX_Fitting_Name");

        Assert.True(provider.HasInfoFor(index));
        Assert.True(provider.InfoViewOwnsActionBar(index));
        Assert.Equal("Index Properties - IX_Fitting_Name", provider.InfoTitle(index));

        // A job's properties page still saves per page, so it keeps the host's Close row — the flag is per
        // node kind, not per provider.
        Assert.False(provider.InfoViewOwnsActionBar(new DbNodeRef(DbNodeKind.AgentJob, "Nightly")));
    }
}
