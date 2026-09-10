namespace DataTray.Providers.MsSql;

/// <summary>One package in SSISDB, addressed the way the catalog addresses it.</summary>
internal sealed record SsisPackageRef(string Folder, string Project, string Package)
{
    /// <summary>The path a step's command carries: <c>\SSISDB\folder\project\package.dtsx</c>.</summary>
    public string Path => $@"\SSISDB\{Folder}\{Project}\{Package}";
}
