using Avalonia.Controls;
using DataTray.Sdk.Ddl;

namespace DataTray.Sdk.Ui;

/// <summary>
/// Optional Route-B capability an <c>IDbProvider</c> may also implement to own the "New …" dialog for a
/// <see cref="DbObjectKind"/> it declares in <c>CreateCapabilities</c>, replacing the host's generic
/// spec-collecting one. SQL Server's Index Properties is the first user: the same dialog serves both
/// creating an index and editing one, and only the provider can offer the options — included columns,
/// filters, filegroups — that the shared <c>CreateObjectSpec</c> deliberately does not model.
/// </summary>
/// <remarks>
/// The host asks before it falls back to its own dialog, per kind: a provider that owns the Index dialog
/// still gets the generic one for Table and Schema. A provider that does not implement this, or answers
/// false, is unaffected — which is why Postgres, MySQL and SQLite keep the generic New Index dialog.
///
/// Unlike the generic flow there is no <c>CreateObjectSpec</c> and no returned SQL: the view runs its own
/// DDL through the provider it is handed, the same way <see cref="ICustomSecurityUi"/>'s does. The host
/// refreshes the tree once the dialog closes.
///
/// Additive optional-interface check — no host API bump needed, same precedent as
/// <see cref="ICustomConnectionUi"/> and <see cref="ICustomNodeInfoUi"/>.
/// </remarks>
public interface ICustomCreateUi
{
    /// <summary>True when this provider brings its own dialog for creating <paramref name="kind"/>.</summary>
    bool HasCreateUiFor(DbObjectKind kind);

    /// <summary>Dialog title for that create view (e.g. "New Index").</summary>
    string CreateTitle(DbObjectKind kind);

    /// <summary>Build the create view. <paramref name="context"/>'s <c>Node</c> is the folder the "New …"
    /// item was invoked on, and its <c>NodePath</c> the ancestry that identifies the parent object.</summary>
    Control BuildCreateView(DbObjectKind kind, NodeInfoContext context);
}
