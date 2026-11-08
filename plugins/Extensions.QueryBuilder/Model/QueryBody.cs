using System.Text.Json.Serialization;

namespace DataTray.Extensions.QueryBuilder.Model;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(Select), "Select")]
[JsonDerivedType(typeof(SetOperation), "SetOperation")]
[JsonDerivedType(typeof(ValuesBody), "ValuesBody")]
[JsonDerivedType(typeof(NestedQuery), "NestedQuery")]
public abstract record QueryBody;
