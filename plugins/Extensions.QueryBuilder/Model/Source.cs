using System.Text.Json.Serialization;

namespace DataTray.Extensions.QueryBuilder.Model;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(TableSource), "TableSource")]
[JsonDerivedType(typeof(CteSource), "CteSource")]
[JsonDerivedType(typeof(SubquerySource), "SubquerySource")]
[JsonDerivedType(typeof(JoinSource), "JoinSource")]
[JsonDerivedType(typeof(FragmentSource), "FragmentSource")]
public abstract record Source;
