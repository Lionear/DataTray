namespace DataTray.Providers.MsSql;

/// <summary>Where an SSIS package lives, which decides the dtexec verb the command is built around.</summary>
public enum SsisPackageSource
{
    /// <summary>SSISDB — <c>/ISSERVER</c>. The only source with environments and a logging level.</summary>
    Catalog,

    /// <summary>A .dtsx file on the Agent machine — <c>/FILE</c>.</summary>
    FileSystem,

    /// <summary>The legacy store in msdb — <c>/SQL</c>.</summary>
    MsdbStore,

    /// <summary>The legacy managed-folder store — <c>/DTS</c>.</summary>
    ManagedFolderStore
}
