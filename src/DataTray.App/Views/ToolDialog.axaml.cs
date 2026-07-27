using System.Collections;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DataTray.App.ViewModels;
using DataTray.Sdk.Tools;

namespace DataTray.App.Views;

public partial class ToolDialog : Window
{
    public ToolDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ToolDialogViewModel vm)
            {
                // The VM is the IToolHost; the view supplies the pickers (it owns the StorageProvider).
                vm.SaveFilePicker = PickSaveFileAsync;
                vm.OpenFilePicker = PickOpenFileAsync;
                vm.ConfirmRequested = ShowConfirmAsync;
                vm.CloseRequested = Close;

                // Keep the log panel pinned to the newest line as the tool reports progress.
                vm.Log.CollectionChanged -= OnLogChanged;
                vm.Log.CollectionChanged += OnLogChanged;
            }
        };
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (sender is IList { Count: > 0 } list)
        {
            // Scroll after the item is realised — post to the dispatcher so the list has updated first.
            Dispatcher.UIThread.Post(() => LogList.ScrollIntoView(list.Count - 1));
        }
    }

    // Browse for a File tool-field: save or open picker per the field's SaveFile flag, via the VM host.
    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ToolFieldInput input } || DataContext is not IToolHost host)
        {
            return;
        }

        var extensions = input.Field.FileExtensions?.ToArray() ?? [];
        var path = input.Field.SaveFile
            ? await host.PickSaveFileAsync(SuggestedName(input), extensions)
            : await host.PickOpenFileAsync(extensions);

        if (path is not null)
        {
            input.Value = path;
        }
    }

    private string SuggestedName(ToolFieldInput input)
    {
        // A path already typed/picked wins; otherwise default to the target's name (the selected
        // database/table) so the save dialog pre-fills e.g. "MyDatabase" instead of a generic "backup".
        if (input.Value is { Length: > 0 } value)
        {
            return Path.GetFileName(value);
        }

        var target = (DataContext as ToolDialogViewModel)?.TargetName;
        return string.IsNullOrWhiteSpace(target) ? input.Field.Default ?? "backup" : Sanitize(target);
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private async Task<string?> PickSaveFileAsync(string suggestedName, string[] extensions)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            FileTypeChoices = BuildTypes(extensions)
        });
        return file?.TryGetLocalPath() ?? file?.Path.ToString();
    }

    private async Task<string?> PickOpenFileAsync(string[] extensions)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = BuildTypes(extensions)
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() ?? files[0].Path.ToString() : null;
    }

    private static IReadOnlyList<FilePickerFileType>? BuildTypes(string[] extensions) =>
        extensions.Length == 0
            ? null
            : [new FilePickerFileType(string.Join("/", extensions).ToUpperInvariant()) { Patterns = extensions.Select(e => $"*.{e}").ToArray() }];

    private async Task<bool> ShowConfirmAsync(string title, string message)
    {
        var loc = (DataContext as ToolDialogViewModel)?.Loc;
        var dialog = new ConfirmDialog(title, message, loc?["Yes"] ?? "Yes", loc?["No"] ?? "No");
        return await dialog.ShowDialog<bool>(this);
    }
}
