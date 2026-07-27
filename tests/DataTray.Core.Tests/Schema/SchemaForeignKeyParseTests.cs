using DataTray.Core.Schema;

namespace DataTray.Core.Tests.Schema;

// The FK JOIN hints (SE-149 phase 3) rely on parsing the "column → refTable.refColumn" detail string every
// provider's FK loader emits back into structured data.
public class SchemaForeignKeyParseTests
{
    [Fact]
    public void Parses_the_shared_provider_detail_format()
    {
        var fk = SchemaCache.ParseForeignKey("user_id → users.id");

        Assert.NotNull(fk);
        Assert.Equal("user_id", fk!.Column);
        Assert.Equal("users", fk.ReferencedTable);
        Assert.Equal("id", fk.ReferencedColumn);
    }

    [Fact]
    public void Trims_surrounding_whitespace()
    {
        var fk = SchemaCache.ParseForeignKey("  order_id →  orders.id ");
        Assert.Equal(("order_id", "orders", "id"), (fk!.Column, fk.ReferencedTable, fk.ReferencedColumn));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("user_id references users")] // no arrow
    [InlineData("user_id → users")]          // no column on the target
    [InlineData("→ users.id")]               // no local column
    public void Returns_null_for_an_unrecognised_shape(string? detail) =>
        Assert.Null(SchemaCache.ParseForeignKey(detail));
}
