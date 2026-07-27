namespace DataTray.Sdk.Security;

/// <summary>
/// One input in the generic "New User…" dialog, declared by a provider — the same declarative Route-A
/// shape as <c>ConnectionField</c>/<c>ToolField</c> so the host can render a form without knowing the
/// engine. Which fields exist is entirely per-engine (MSSQL just needs a password, MySQL also a host,
/// Postgres a set of role attributes).
/// </summary>
public sealed record UserField(
    string Key,
    string Label,
    UserFieldType Type = UserFieldType.Text,
    bool Required = false,
    string? Default = null,
    IReadOnlyList<string>? Choices = null,
    string? Hint = null);

/// <summary>The input control the host renders for a <see cref="UserField"/>.</summary>
public enum UserFieldType
{
    Text,
    Password,
    Choice,
    Bool
}
