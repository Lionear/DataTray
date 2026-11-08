using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using AvaloniaEdit;
using DataTray.App.ViewModels;
using DataTray.App.Views;
using DataTray.Sdk.Extensibility;

namespace DataTray.App.Tests;

/// <summary>While a pane owns the SQL, nothing typed or clicked in the editor may write it.</summary>
public class LinkedEditorViewTests : IClassFixture<HeadlessDrawing>
{
    [Theory]
    [InlineData(Key.Z)]            // undo would roll back the pane's write as if the user typed it
    [InlineData(Key.OemQuestion)]  // toggle line comment
    public void EditorKey_WhileLinked_LeavesSqlAlone(Key key)
    {
        var sql = HeadlessDrawing.Run(() =>
        {
            var (document, editor) = LinkedView();
            Press(editor, key);
            return document.Sql;
        });

        Assert.Equal("select 2", sql);
    }

    [Fact] // Redo after a re-link: the user undid while unlinked, then the pane took the SQL back.
    public void RedoKey_WhileLinked_LeavesSqlAlone()
    {
        var (afterUndo, afterRedo) = HeadlessDrawing.Run(() =>
        {
            var (document, editor) = LinkedView();
            document.EditAsSqlCommand.Execute(null);
            Press(editor, Key.Z);
            var undone = document.Sql;
            ((IQueryDocument)document).Link("Builder");
            Press(editor, Key.Y);
            return (undone, document.Sql);
        });

        Assert.NotEqual("select 2", afterUndo);
        Assert.Equal(afterUndo, afterRedo);
    }

    [Fact]
    public void GenerateGuid_WhileLinked_LeavesSqlAlone()
    {
        var sql = HeadlessDrawing.Run(() =>
        {
            var (document, editor) = LinkedView();
            var guidV4 = editor.ContextMenu?.Items.OfType<MenuItem>().Last().Items.OfType<MenuItem>().First();
            guidV4?.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            return document.Sql;
        });

        Assert.Equal("select 2", sql);
    }

    // A query tab in a view, linked to a pane that has written the SQL twice.
    private static (DocumentViewModel Document, TextEditor Editor) LinkedView()
    {
        SeedAppResources();
        var document = QueryDocumentFixture.NewDocument(out var connections, QueryDocumentFixture.Ready("master"));
        document.InitQuery(connections.List().First(c => c.Id == "c1"));
        var view = new DocumentView { DataContext = document };
        var window = new Window { Content = view };
        window.Show();
        window.UpdateLayout();   // applies the editor template, so keys bubble from TextArea as in the app
        var editor = view.FindControl<TextEditor>("SqlEditor") ?? throw new InvalidOperationException("no SqlEditor");
        IQueryDocument query = document;
        query.Link("Builder");
        query.SetSql("select 1");
        query.SetSql("select 2");
        return (document, editor);
    }

    // Headless has no App.axaml: seed the one app resource DocumentView.axaml resolves while loading, and the
    // Fluent + AvaloniaEdit themes so the editor gets its template (and the MenuItem theme the view is based on).
    private static void SeedAppResources()
    {
        var app = Application.Current ?? throw new InvalidOperationException("no Application");
        app.Resources["SEControlRadius"] = new CornerRadius(4);
        var styles = app.Styles;
        if (styles.Count == 0)
        {
            styles.Add(new FluentTheme());
            styles.Add(new StyleInclude(new Uri("avares://DataTray.App.Tests"))
            {
                Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml")
            });
        }
    }

    private static void Press(TextEditor editor, Key key) =>
        editor.TextArea.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = KeyModifiers.Control,
            Source = editor.TextArea
        });
}
