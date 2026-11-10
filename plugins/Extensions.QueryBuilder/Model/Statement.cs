using System.Text.Json.Serialization;

namespace DataTray.Extensions.QueryBuilder.Model;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(QueryStatement), "QueryStatement")]
[JsonDerivedType(typeof(InsertStatement), "InsertStatement")]
[JsonDerivedType(typeof(UpdateStatement), "UpdateStatement")]
[JsonDerivedType(typeof(DeleteStatement), "DeleteStatement")]
[JsonDerivedType(typeof(MergeStatement), "MergeStatement")]
public abstract record Statement;
