using DataTray.Sdk;
using DataTray.Sdk.Connections;
using DataTray.Sdk.Localization;
using DataTray.Sdk.Schema;
using DataTray.Sdk.Ui;

namespace DataTray.App.ViewModels;

/// <summary>
/// The host's side of <see cref="IToolDocumentContext"/>: what a plugin-owned tab (SE-216) is allowed to
/// reach back for. Everything the plugin can do to the host arrives as a callback the caller supplied, so
/// this type holds no reference to <see cref="MainViewModel"/> — a document lives until the user closes
/// it, and handing a plugin the whole window for that long is a wider door than it needs.
/// </summary>
public sealed class ToolDocumentContext(
    IDbProvider provider,
    string providerId,
    ConnectionProfile profile,
    DbNodeRef? node,
    IPluginLocalizer localizer,
    Action<string> setTitle,
    Action<string> openQueryEditor,
    Action closeDocument,
    Func<string, string[], Task<string?>> pickSaveFile,
    Func<string[], Task<string?>> pickOpenFile) : IToolDocumentContext
{
    public IDbProvider Provider { get; } = provider;

    public string ProviderId { get; } = providerId;

    public ConnectionProfile Profile { get; } = profile;

    public DbNodeRef? Node { get; } = node;

    public IPluginLocalizer Localizer { get; } = localizer;

    public void SetTitle(string title)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            setTitle(title);
        }
    }

    public void OpenQueryEditor(string sql) => openQueryEditor(sql);

    public void CloseDocument() => closeDocument();

    public Task<string?> PickSaveFileAsync(string suggestedName, params string[] extensions) =>
        pickSaveFile(suggestedName, extensions);

    public Task<string?> PickOpenFileAsync(params string[] extensions) => pickOpenFile(extensions);
}
