using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;

namespace DataTray.App.Controls;

/// <summary>
/// Lays children out in a single row and moves whatever does not fit into an overflow flyout. Avalonia
/// ships no toolbar-with-overflow control and no stock panel does this (a <c>WrapPanel</c> wraps, a
/// <c>DockPanel</c> clips), so this is that panel: it serves the application toolbar and each of the
/// query-window mode bars (SE-255 §4).
/// </summary>
/// <remarks>
/// <para>
/// The overflow button itself is <em>not</em> a child — it sits beside the panel and binds to
/// <see cref="HasOverflow"/> and <see cref="OverflowItems"/>. That keeps the panel usable as an
/// <c>ItemsControl.ItemsPanel</c> (whose <c>Children</c> the presenter owns) and out of the framework's
/// way, and it cannot oscillate: the fit decision is taken once from the full list of desired widths,
/// never from the post-collapse state, so hiding an item can never free the width that would bring it
/// back.
/// </para>
/// <para>
/// A child describes its flyout row with the attached <see cref="OverflowHeaderProperty"/> /
/// <see cref="OverflowIconProperty"/> / <see cref="OverflowCommandProperty"/> /
/// <see cref="OverflowDetailProperty"/>. A child that declares no header cannot be represented in the
/// flyout, so it is treated as pinned — losing a control silently is worse than a slightly crowded strip.
/// </para>
/// </remarks>
public sealed class OverflowPanel : Panel
{
    /// <summary>This child never overflows: it is subtracted from the budget up front. For controls that
    /// cannot function inside a flyout — you cannot pick a connection from a menu that closes when you
    /// click it, and a filter box you cannot see while typing is not a filter.</summary>
    public static readonly AttachedProperty<bool> IsPinnedProperty =
        AvaloniaProperty.RegisterAttached<OverflowPanel, Control, bool>("IsPinned");

    /// <summary>The label this child gets in the overflow flyout. Required for a child to be overflowable.</summary>
    public static readonly AttachedProperty<string?> OverflowHeaderProperty =
        AvaloniaProperty.RegisterAttached<OverflowPanel, Control, string?>("OverflowHeader");

    public static readonly AttachedProperty<Geometry?> OverflowIconProperty =
        AvaloniaProperty.RegisterAttached<OverflowPanel, Control, Geometry?>("OverflowIcon");

    public static readonly AttachedProperty<ICommand?> OverflowCommandProperty =
        AvaloniaProperty.RegisterAttached<OverflowPanel, Control, ICommand?>("OverflowCommand");

    /// <summary>Muted suffix in the flyout row — the owning plugin's name, or a shortcut hint.</summary>
    public static readonly AttachedProperty<string?> OverflowDetailProperty =
        AvaloniaProperty.RegisterAttached<OverflowPanel, Control, string?>("OverflowDetail");

    /// <summary>
    /// The flyout rows for a child that is itself a group of actions — an <c>ItemsControl</c> rendering a
    /// plugin's contributions, say. The group overflows as one child (it is measured as one), but it lands
    /// in the flyout as its individual actions rather than a single useless "plugin actions" row.
    /// </summary>
    public static readonly AttachedProperty<IEnumerable<OverflowItem>?> OverflowGroupProperty =
        AvaloniaProperty.RegisterAttached<OverflowPanel, Control, IEnumerable<OverflowItem>?>("OverflowGroup");

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<OverflowPanel, double>(nameof(Spacing), 8d);

    public static readonly DirectProperty<OverflowPanel, bool> HasOverflowProperty =
        AvaloniaProperty.RegisterDirect<OverflowPanel, bool>(nameof(HasOverflow), o => o._hasOverflow);

    public static readonly DirectProperty<OverflowPanel, IReadOnlyList<OverflowItem>> OverflowItemsProperty =
        AvaloniaProperty.RegisterDirect<OverflowPanel, IReadOnlyList<OverflowItem>>(
            nameof(OverflowItems), o => o._overflowItems);

    private readonly HashSet<Control> _overflowed = [];
    private IReadOnlyList<OverflowItem> _overflowItems = [];
    private bool _hasOverflow;

    public OverflowPanel()
    {
        // Children that did not fit are arranged off-screen rather than hidden: setting IsVisible would
        // invalidate this panel's own measure and re-run the decision against a shrunken child set.
        ClipToBounds = true;
    }

    public static bool GetIsPinned(Control control) => control.GetValue(IsPinnedProperty);

    public static void SetIsPinned(Control control, bool value) => control.SetValue(IsPinnedProperty, value);

    public static string? GetOverflowHeader(Control control) => control.GetValue(OverflowHeaderProperty);

    public static void SetOverflowHeader(Control control, string? value) => control.SetValue(OverflowHeaderProperty, value);

    public static Geometry? GetOverflowIcon(Control control) => control.GetValue(OverflowIconProperty);

    public static void SetOverflowIcon(Control control, Geometry? value) => control.SetValue(OverflowIconProperty, value);

    public static ICommand? GetOverflowCommand(Control control) => control.GetValue(OverflowCommandProperty);

    public static void SetOverflowCommand(Control control, ICommand? value) => control.SetValue(OverflowCommandProperty, value);

    public static string? GetOverflowDetail(Control control) => control.GetValue(OverflowDetailProperty);

    public static void SetOverflowDetail(Control control, string? value) => control.SetValue(OverflowDetailProperty, value);

    public static IEnumerable<OverflowItem>? GetOverflowGroup(Control control) => control.GetValue(OverflowGroupProperty);

    public static void SetOverflowGroup(Control control, IEnumerable<OverflowItem>? value) =>
        control.SetValue(OverflowGroupProperty, value);

    /// <summary>Gap between two visible children.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>True while at least one child does not fit — bound by the "…" button's visibility.</summary>
    public bool HasOverflow => _hasOverflow;

    /// <summary>The children that did not fit, projected to flyout rows in layout order.</summary>
    public IReadOnlyList<OverflowItem> OverflowItems => _overflowItems;

    /// <summary>
    /// The fit decision, split out from the layout pass so it can be tested without a display: walks the
    /// children in order and takes each while it still fits the budget left by the pinned ones. Returns a
    /// flag per child. Every child from the first non-fitting one onward overflows, so the user's order is
    /// also the priority order — what they put first survives longest as the window narrows.
    /// </summary>
    public static bool[] Fit(IReadOnlyList<double> widths, IReadOnlyList<bool> pinned, double spacing, double available)
    {
        var fits = new bool[widths.Count];
        if (widths.Count == 0)
        {
            return fits;
        }

        if (double.IsInfinity(available))
        {
            Array.Fill(fits, true);
            return fits;
        }

        var used = 0d;
        var count = 0;
        for (var i = 0; i < widths.Count; i++)
        {
            if (!pinned[i])
            {
                continue;
            }

            fits[i] = true;
            used += widths[i] + (count++ > 0 ? spacing : 0);
        }

        // Everything-fits is judged on the whole set, from the widths measured at full size — never from
        // what is left after a collapse. That is what makes the classic hide/show loop impossible.
        var everything = used;
        var running = count;
        for (var i = 0; i < widths.Count; i++)
        {
            if (!pinned[i])
            {
                everything += widths[i] + (running++ > 0 ? spacing : 0);
            }
        }

        if (everything <= available)
        {
            Array.Fill(fits, true);
            return fits;
        }

        for (var i = 0; i < widths.Count; i++)
        {
            if (pinned[i])
            {
                continue;
            }

            var next = used + widths[i] + (count > 0 ? spacing : 0);
            if (next > available)
            {
                // Degenerate case (not even one unpinned child fits) falls out here: everything from the
                // first non-fitting child onward goes to the flyout, and the strip is the pinned ones.
                break;
            }

            fits[i] = true;
            used = next;
            count++;
        }

        return fits;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children;
        var widths = new double[children.Count];
        var pinned = new bool[children.Count];
        var height = 0d;

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            widths[i] = child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);

            // Read after measuring: a ContentPresenter only realises its templated child during its own
            // measure pass, and that child is where an ItemTemplate puts the attached properties.
            var descriptor = Descriptor(child);
            pinned[i] = GetIsPinned(descriptor)
                || (GetOverflowHeader(descriptor) is null && GetOverflowGroup(descriptor) is null);
        }

        var fits = Fit(widths, pinned, Spacing, availableSize.Width);

        _overflowed.Clear();
        var items = new List<OverflowItem>();
        var used = 0d;
        var shown = 0;
        for (var i = 0; i < children.Count; i++)
        {
            if (fits[i])
            {
                used += widths[i] + (shown++ > 0 ? Spacing : 0);
                continue;
            }

            _overflowed.Add(children[i]);
            var descriptor = Descriptor(children[i]);
            if (GetOverflowGroup(descriptor) is { } group)
            {
                items.AddRange(group);
                continue;
            }

            items.Add(new OverflowItem(
                GetOverflowHeader(descriptor) ?? string.Empty,
                GetOverflowIcon(descriptor),
                GetOverflowCommand(descriptor),
                GetOverflowDetail(descriptor)));
        }

        // Only raise on a real change: these drive the "…" button's visibility, so republishing an
        // identical list on every measure would invalidate the parent's layout for nothing.
        if (!_overflowItems.SequenceEqual(items))
        {
            SetAndRaise(OverflowItemsProperty, ref _overflowItems, items);
        }

        SetAndRaise(HasOverflowProperty, ref _hasOverflow, items.Count > 0);

        return new Size(used, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0d;
        var first = true;
        foreach (var child in Children)
        {
            if (_overflowed.Contains(child))
            {
                // Parked far outside the (clipped) panel: no pixels, no pointer, and no measure churn.
                child.Arrange(new Rect(-100000, 0, child.DesiredSize.Width, finalSize.Height));
                continue;
            }

            if (!first)
            {
                x += Spacing;
            }

            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width;
            first = false;
        }

        return finalSize;
    }

    // An ItemsControl's children are ContentPresenters; the attached properties live on the template root
    // inside them. Everywhere else the child describes itself.
    private static Control Descriptor(Control child) =>
        child is ContentPresenter { Child: { } inner } ? inner : child;
}
