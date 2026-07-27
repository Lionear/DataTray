namespace DataTray.Core.Connections;

/// <summary>Persists the non-secret part of saved connections (see <see cref="SavedConnection"/>).</summary>
public interface IConnectionStore
{
    IReadOnlyList<SavedConnection> GetAll();

    /// <summary>Manual sort index per folder path (full "/"-joined path). Absent path = alphabetical.</summary>
    IReadOnlyDictionary<string, int> GetFolderOrder();

    void Save(SavedConnection connection);

    void Delete(string id);

    /// <summary>Atomic rewrite of the whole store — used by drag-to-reorder flows where many
    /// <see cref="SavedConnection.SortOrder"/> values shift at once and folder-order changes together.</summary>
    void SaveAll(IReadOnlyList<SavedConnection> connections, IReadOnlyDictionary<string, int> folderOrder);
}
