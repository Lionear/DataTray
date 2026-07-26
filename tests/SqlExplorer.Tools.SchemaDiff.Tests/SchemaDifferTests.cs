using SqlExplorer.Plugins.Schema;
using SqlExplorer.Tools.SchemaDiff;

namespace SqlExplorer.Tools.SchemaDiff.Tests;

public class SchemaDifferTests
{
    private static ColumnDef Col(string name, string type = "integer", bool nullable = false, string? def = null, int ord = 1)
        => new(name, type, nullable, def, ord);

    private static TableDef Table(
        string name,
        IReadOnlyList<ColumnDef>? columns = null,
        PrimaryKeyDef? pk = null,
        IReadOnlyList<IndexDef>? indexes = null,
        IReadOnlyList<ForeignKeyDef>? fks = null,
        IReadOnlyList<UniqueDef>? uniques = null,
        string schema = "public")
        => new(schema, name, columns ?? [Col("id")], pk, indexes ?? [], fks ?? [], uniques ?? []);

    private static SchemaSnapshot Snap(params TableDef[] tables) => new(tables);

    [Fact]
    public void Identical_schemas_produce_no_changes()
    {
        var a = Snap(Table("users", [Col("id"), Col("name", "text", ord: 2)]));
        var b = Snap(Table("users", [Col("id"), Col("name", "text", ord: 2)]));

        Assert.Empty(SchemaDiffer.Diff(a, b));
    }

    [Fact]
    public void Table_only_in_target_is_created()
    {
        var changes = SchemaDiffer.Diff(Snap(), Snap(Table("users")));

        var create = Assert.IsType<CreateTable>(Assert.Single(changes));
        Assert.Equal("users", create.Def.Name);
    }

    [Fact]
    public void Table_only_in_source_is_dropped()
    {
        var changes = SchemaDiffer.Diff(Snap(Table("users")), Snap());

        var drop = Assert.IsType<DropTable>(Assert.Single(changes));
        Assert.Equal("users", drop.Def.Name);
    }

    [Fact]
    public void Added_and_dropped_columns_are_detected()
    {
        var from = Snap(Table("t", [Col("id"), Col("old", "text", ord: 2)]));
        var to = Snap(Table("t", [Col("id"), Col("new", "text", ord: 2)]));

        var changes = SchemaDiffer.Diff(from, to);

        Assert.Contains(changes, c => c is AddColumn { Column.Name: "new" });
        Assert.Contains(changes, c => c is DropColumn { Column.Name: "old" });
    }

    [Theory]
    [InlineData("integer", "bigint", false, false, null, null)]      // type change
    [InlineData("integer", "integer", false, true, null, null)]       // nullability change
    [InlineData("integer", "integer", false, false, null, "0")]        // default change
    public void Column_changes_produce_AlterColumn(
        string fromType, string toType, bool fromNull, bool toNull, string? fromDef, string? toDef)
    {
        var from = Snap(Table("t", [Col("c", fromType, fromNull, fromDef)]));
        var to = Snap(Table("t", [Col("c", toType, toNull, toDef)]));

        var alter = Assert.IsType<AlterColumn>(Assert.Single(SchemaDiffer.Diff(from, to)));
        Assert.Equal("c", alter.To.Name);
    }

    [Fact]
    public void Changed_primary_key_is_dropped_then_added()
    {
        var from = Snap(Table("t", pk: new PrimaryKeyDef("pk_t", ["id"])));
        var to = Snap(Table("t", pk: new PrimaryKeyDef("pk_t", ["id", "tenant"])));

        var changes = SchemaDiffer.Diff(from, to);

        Assert.Contains(changes, c => c is DropPrimaryKey);
        Assert.Contains(changes, c => c is AddPrimaryKey);
        Assert.True(changes.ToList().FindIndex(c => c is DropPrimaryKey)
                    < changes.ToList().FindIndex(c => c is AddPrimaryKey));
    }

    [Fact]
    public void Changed_unique_constraint_is_dropped_then_added()
    {
        var from = Snap(Table("t", uniques: [new UniqueDef("uq", ["a"])]));
        var to = Snap(Table("t", uniques: [new UniqueDef("uq", ["a", "b"])]));

        var changes = SchemaDiffer.Diff(from, to);

        Assert.Contains(changes, c => c is DropUnique);
        Assert.Contains(changes, c => c is AddUnique);
    }

    [Fact]
    public void Foreign_keys_are_added_last_and_dropped_first()
    {
        var fk = new ForeignKeyDef("fk_o_u", ["user_id"], "public", "users", ["id"]);
        var from = Snap(
            Table("users"),
            Table("orders", [Col("id"), Col("user_id", ord: 2)], fks: [fk]));
        // `to` drops the FK (orders has none) and adds a brand-new table with its own FK.
        var newFk = new ForeignKeyDef("fk_x", ["user_id"], "public", "users", ["id"]);
        var to = Snap(
            Table("users"),
            Table("orders", [Col("id"), Col("user_id", ord: 2)]),
            Table("audit", [Col("id"), Col("user_id", ord: 2)], fks: [newFk]));

        var changes = SchemaDiffer.Diff(from, to);
        var kinds = changes.Select(c => c.GetType().Name).ToList();

        // The dropped FK comes before the created table; the added FK comes after it.
        Assert.True(kinds.IndexOf(nameof(DropForeignKey)) < kinds.IndexOf(nameof(CreateTable)));
        Assert.True(kinds.IndexOf(nameof(CreateTable)) < kinds.IndexOf(nameof(AddForeignKey)));
    }

    [Fact]
    public void Case_insensitive_table_and_column_matching()
    {
        var from = Snap(Table("Users", [Col("Id")]));
        var to = Snap(Table("users", [Col("id")]));

        Assert.Empty(SchemaDiffer.Diff(from, to));
    }

    // --- Auto-numbering (SE-190) ---

    [Fact]
    public void Auto_numbering_gained_or_lost_is_a_column_change()
    {
        var plain = Col("id");
        var auto = plain with { IsIdentity = true };

        Assert.Single(SchemaDiffer.Diff(Snap(Table("t", [plain])), Snap(Table("t", [auto]))));
        Assert.Single(SchemaDiffer.Diff(Snap(Table("t", [auto])), Snap(Table("t", [plain]))));
        Assert.Empty(SchemaDiffer.Diff(Snap(Table("t", [auto])), Snap(Table("t", [auto]))));
    }

    // --- System-generated constraint names (SE-191) ---

    [Fact]
    public void A_unique_constraint_the_engine_named_itself_is_matched_by_its_columns()
    {
        // Two SQL Server databases built from the same script: same constraint, different invented names.
        var from = Snap(Table("customers", [Col("id"), Col("email", "nvarchar(255)", ord: 2)],
            uniques: [new UniqueDef("UQ__customer__AB6E6164DF5AECAE", ["email"])]));
        var to = Snap(Table("customers", [Col("id"), Col("email", "nvarchar(255)", ord: 2)],
            uniques: [new UniqueDef("UQ__customer__AB6E61648BC85C55", ["email"])]));

        Assert.Empty(SchemaDiffer.Diff(from, to));
    }

    [Fact]
    public void A_foreign_key_the_engine_named_itself_is_matched_by_its_definition()
    {
        var from = Snap(Table("orders", [Col("id"), Col("customer_id", ord: 2)],
            fks: [new ForeignKeyDef("FK__orders__customer__2A4B4B5E", ["customer_id"], "public", "customers", ["id"])]));
        var to = Snap(Table("orders", [Col("id"), Col("customer_id", ord: 2)],
            fks: [new ForeignKeyDef("FK__orders__customer__72910220", ["customer_id"], "public", "customers", ["id"])]));

        Assert.Empty(SchemaDiffer.Diff(from, to));
    }

    [Fact]
    public void A_unique_constraint_on_different_columns_is_still_a_change()
    {
        // The fallback matches definitions, not names — a genuinely different constraint stays a change.
        var from = Snap(Table("customers", [Col("id"), Col("email", "text", ord: 2)],
            uniques: [new UniqueDef("uq_a", ["email"])]));
        var to = Snap(Table("customers", [Col("id"), Col("email", "text", ord: 2)],
            uniques: [new UniqueDef("uq_b", ["id"])]));

        var changes = SchemaDiffer.Diff(from, to);
        Assert.Contains(changes, c => c is DropUnique);
        Assert.Contains(changes, c => c is AddUnique);
    }

    [Fact]
    public void Two_unique_constraints_pair_up_one_for_one_rather_than_collapsing()
    {
        var from = Snap(Table("t", [Col("a"), Col("b", ord: 2)],
            uniques: [new UniqueDef("UQ__t__1", ["a"]), new UniqueDef("UQ__t__2", ["b"])]));
        var to = Snap(Table("t", [Col("a"), Col("b", ord: 2)],
            uniques: [new UniqueDef("UQ__t__9", ["b"]), new UniqueDef("UQ__t__8", ["a"])]));

        Assert.Empty(SchemaDiffer.Diff(from, to));
    }

    [Fact]
    public void A_renamed_index_is_still_a_change_because_an_index_name_is_always_chosen()
    {
        // CREATE INDEX always names its index, so unlike a constraint, a differing name is a real difference.
        var from = Snap(Table("t", [Col("a")], indexes: [new IndexDef("ix_old", false, ["a"])]));
        var to = Snap(Table("t", [Col("a")], indexes: [new IndexDef("ix_new", false, ["a"])]));

        var changes = SchemaDiffer.Diff(from, to);
        Assert.Contains(changes, c => c is DropIndex);
        Assert.Contains(changes, c => c is AddIndex);
    }
}
