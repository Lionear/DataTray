namespace DataTray.Providers.MsSql;

/// <summary>One connection manager pointed somewhere else for this step.</summary>
public sealed record SsisConnectionOverride(string Name, string Value);
