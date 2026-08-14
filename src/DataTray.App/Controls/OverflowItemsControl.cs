using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Reactive;

namespace DataTray.App.Controls;

/// <summary>
/// An <see cref="ItemsControl"/> laid out by an <see cref="OverflowPanel"/>, republishing that panel's
/// <see cref="OverflowPanel.HasOverflow"/> and <see cref="OverflowPanel.OverflowItems"/> at its own level.
/// </summary>
/// <remarks>
/// Only reason this exists: an <c>ItemsPanelTemplate</c> is its own name scope, so the "…" button beside
/// the strip cannot reach the panel with <c>{Binding #name}</c>. Forwarding here keeps that wiring
/// declarative instead of timing-dependent code-behind. A bar with static children (the query-window mode
/// bars) uses <see cref="OverflowPanel"/> directly and needs none of this.
/// </remarks>
public sealed class OverflowItemsControl : ItemsControl
{
    public static readonly DirectProperty<OverflowItemsControl, bool> HasOverflowProperty =
        AvaloniaProperty.RegisterDirect<OverflowItemsControl, bool>(nameof(HasOverflow), o => o._hasOverflow);

    public static readonly DirectProperty<OverflowItemsControl, IReadOnlyList<OverflowItem>> OverflowItemsProperty =
        AvaloniaProperty.RegisterDirect<OverflowItemsControl, IReadOnlyList<OverflowItem>>(
            nameof(OverflowItems), o => o._overflowItems);

    private IReadOnlyList<OverflowItem> _overflowItems = [];
    private bool _hasOverflow;
    private OverflowPanel? _panel;

    /// <summary>Without this the subclass finds no ControlTheme, so it gets no template, no
    /// ItemsPresenter and renders nothing — a failure a build never catches.</summary>
    protected override Type StyleKeyOverride => typeof(ItemsControl);

    public bool HasOverflow => _hasOverflow;

    public IReadOnlyList<OverflowItem> OverflowItems => _overflowItems;

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);

        // The presenter builds its panel during that first measure, so this is the earliest deterministic
        // point at which it exists. The subscriptions live as long as the control does.
        if (_panel is null && ItemsPanelRoot is OverflowPanel panel)
        {
            _panel = panel;
            panel.GetObservable(OverflowPanel.HasOverflowProperty)
                .Subscribe(new AnonymousObserver<bool>(v => SetAndRaise(HasOverflowProperty, ref _hasOverflow, v)));
            panel.GetObservable(OverflowPanel.OverflowItemsProperty)
                .Subscribe(new AnonymousObserver<IReadOnlyList<OverflowItem>>(
                    v => SetAndRaise(OverflowItemsProperty, ref _overflowItems, v)));
        }

        return size;
    }
}
