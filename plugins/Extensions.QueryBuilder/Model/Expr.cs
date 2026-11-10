using System.Text.Json.Serialization;

namespace DataTray.Extensions.QueryBuilder.Model;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ColumnRef), "ColumnRef")]
[JsonDerivedType(typeof(Star), "Star")]
[JsonDerivedType(typeof(Literal), "Literal")]
[JsonDerivedType(typeof(Unary), "Unary")]
[JsonDerivedType(typeof(Binary), "Binary")]
[JsonDerivedType(typeof(Between), "Between")]
[JsonDerivedType(typeof(Like), "Like")]
[JsonDerivedType(typeof(IsNull), "IsNull")]
[JsonDerivedType(typeof(InList), "InList")]
[JsonDerivedType(typeof(InSubquery), "InSubquery")]
[JsonDerivedType(typeof(Exists), "Exists")]
[JsonDerivedType(typeof(Quantified), "Quantified")]
[JsonDerivedType(typeof(ScalarSubquery), "ScalarSubquery")]
[JsonDerivedType(typeof(FunctionCall), "FunctionCall")]
[JsonDerivedType(typeof(WindowCall), "WindowCall")]
[JsonDerivedType(typeof(Case), "Case")]
[JsonDerivedType(typeof(Cast), "Cast")]
[JsonDerivedType(typeof(Concat), "Concat")]
[JsonDerivedType(typeof(Fragment), "Fragment")]
public abstract record Expr;
