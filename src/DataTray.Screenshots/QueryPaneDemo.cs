using Avalonia.Controls;
using DataTray.App.ViewModels;
using DataTray.Core.Plugins;
using DataTray.Sdk.Extensibility;
using DataTray.Sdk.Localization;

namespace DataTray.Screenshots;

/// <summary>A tiny in-process query pane for the query-pane scene: a table list and a button that links the editor.</summary>
internal static class QueryPaneDemo
{
    public const string GeneratedSql = "SELECT id, name, city\nFROM customers\nORDER BY name;";

    /// <summary>Mounts the demo pane on <paramref name="document"/> and opens it; "linked" also generates SQL.</summary>
    public static void Apply(DocumentViewModel document, string state)
    {
        var plugin = new DemoPlugin(state == "error");
        var pane = new SubsystemQueryPane("demo-pane", "Demo pane", new HashSet<string>(), new KeyLocalizer(), plugin);
        document.MountQueryPanes([pane], new NoHostUi(), _ => null);
        document.TogglePaneCommand.Execute(document.QueryPanes[0]);
        if (state == "linked")
        {
            plugin.Created!.Generate();
        }
    }

    private sealed class DemoPlugin(bool fail) : IQueryPanePlugin
    {
        public DemoPane? Created { get; private set; }
        public string PaneId => "demo";
        public string Title => "Demo pane";
        public bool AppliesTo(IQueryDocument document) => true;

        public Control CreatePane(IQueryPaneContext context)
        {
            if (fail)
            {
                throw new InvalidOperationException("The demo pane could not load its tables.");
            }

            return Created = new DemoPane(context.Document);
        }
    }

    internal sealed class DemoPane : UserControl
    {
        private readonly IQueryDocument _document;

        public DemoPane(IQueryDocument document)
        {
            _document = document;
            var button = new Button { Content = "Generate SQL" };
            button.Click += (_, _) => Generate();
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(12),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Tables", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    new TextBlock { Text = "customers\norders\nproducts" },
                    button,
                },
            };
        }

        public void Generate()
        {
            _document.Link("Demo pane");
            _document.SetSql(GeneratedSql);
        }
    }

    private sealed class NoHostUi : IHostUi
    {
        public Task ShowDialogAsync(string title, Control content) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
    }

    private sealed class KeyLocalizer : IPluginLocalizer
    {
        public string this[string key] => key;
        public bool Contains(string key) => false;
        public string Get(string key, params object[] args) => key;
    }
}
