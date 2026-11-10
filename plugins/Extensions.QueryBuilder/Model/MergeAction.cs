using System.Text.Json.Serialization;

namespace DataTray.Extensions.QueryBuilder.Model;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(MergeUpdate), "MergeUpdate")]
[JsonDerivedType(typeof(MergeInsert), "MergeInsert")]
[JsonDerivedType(typeof(MergeDelete), "MergeDelete")]
public abstract record MergeAction;
