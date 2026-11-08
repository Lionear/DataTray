using System.Text.Json.Serialization;

namespace DataTray.Extensions.QueryBuilder.Model;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ValuesSource), "ValuesSource")]
[JsonDerivedType(typeof(QuerySource), "QuerySource")]
[JsonDerivedType(typeof(DefaultValuesSource), "DefaultValuesSource")]
public abstract record InsertSource;
