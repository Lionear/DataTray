using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DataTray.App.Controls;
using DataTray.App.ViewModels;

namespace DataTray.App.Views;

/// <summary>
/// SE-300: the Messages log of a query tab. Keeps the newest line in view while following; scrolling up pauses
/// that (the log counts what arrives meanwhile), and Follow, End or scrolling back to the bottom resumes it.
/// </summary>
public partial class QueryMessagesView : UserControl
{
    private QueryMessageLog? _log;
    private ScrollViewer? _scroller;

    public QueryMessagesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach((DataContext as DocumentViewModel)?.Messages);
        Log.TemplateApplied += (_, _) => AttachScroller();
        Log.KeyDown += OnLogKeyDown;
    }

    private void Attach(QueryMessageLog? log)
    {
        if (_log is not null)
        {
            _log.Visible.CollectionChanged -= OnLinesChanged;
            _log.PropertyChanged -= OnLogPropertyChanged;
        }

        _log = log;
        if (_log is not null)
        {
            _log.Visible.CollectionChanged += OnLinesChanged;
            _log.PropertyChanged += OnLogPropertyChanged;
            ScrollToEndIfFollowing();
        }
    }

    private void AttachScroller()
    {
        if (_scroller is not null)
        {
            _scroller.ScrollChanged -= OnScrollChanged;
        }

        _scroller = Log.Scroll as ScrollViewer;
        if (_scroller is not null)
        {
            _scroller.ScrollChanged += OnScrollChanged;
        }
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollToEndIfFollowing();

    private void OnLogPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueryMessageLog.IsFollowing))
        {
            ScrollToEndIfFollowing();
        }
    }

    // Growth of the list moves the extent, not the offset, so only an upward offset change is the user
    // leaving the bottom; arriving back at the bottom by scrolling down picks following up again.
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_log is null || _scroller is null)
        {
            return;
        }

        var atBottom = _scroller.Offset.Y + _scroller.Viewport.Height >= _scroller.Extent.Height - 4;
        if (e.OffsetDelta.Y < 0 && !atBottom)
        {
            _log.IsFollowing = false;
        }
        else if (e.OffsetDelta.Y > 0 && atBottom)
        {
            _log.IsFollowing = true;
        }
    }

    private void ScrollToEndIfFollowing()
    {
        if (_log is { IsFollowing: true })
        {
            // After layout, so the new lines are measured and the end is the real end.
            Dispatcher.UIThread.Post(() => _scroller?.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    private void OnLogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.End && _log is not null)
        {
            _log.IsFollowing = true;
        }
    }

    private void OnResumeFollowClick(object? sender, RoutedEventArgs e)
    {
        if (_log is not null)
        {
            _log.IsFollowing = true;
        }
    }

    // The selection if there is one, otherwise everything the filter shows — in log order either way.
    private void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_log is null || DataContext is not DocumentViewModel document)
        {
            return;
        }

        var selected = Log.SelectedItems?.OfType<QueryMessageEntry>().ToHashSet() ?? [];
        var lines = selected.Count > 0 ? _log.Visible.Where(selected.Contains) : _log.Visible;
        _ = CopyFeedback.CopyAsync(this, _log.CopyText(lines), document.Loc["CopiedToClipboard"]);
    }

    private void OnClearClick(object? sender, RoutedEventArgs e) => _log?.Clear();
}
