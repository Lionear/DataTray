namespace DataTray.Providers.MsSql;

/// <summary>
/// An environment a project may be run against. The id is what the command stores; the name is what a person
/// can act on, which is why a reference that no longer resolves has to be reported rather than shown as 12.
/// </summary>
internal sealed record SsisEnvironmentRef(int ReferenceId, string Environment, string Folder)
{
    public string Label => $@"{Environment} — \SSISDB\{Folder}\{Environment}";
}
