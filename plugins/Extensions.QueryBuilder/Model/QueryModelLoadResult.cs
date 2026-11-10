using System.Diagnostics.CodeAnalysis;

namespace DataTray.Extensions.QueryBuilder.Model;

/// <summary>A loaded statement, or the readable reason the saved model could not be loaded (then Statement is null).</summary>
public sealed record QueryModelLoadResult(Statement? Statement, string? Error)
{
    [MemberNotNullWhen(true, nameof(Statement))]
    public bool Succeeded => Statement is not null;
}
