using System.Windows.Input;
using Avalonia.Media;

namespace DataTray.App.Controls;

/// <summary>
/// One entry of an <see cref="OverflowPanel"/>'s overflow flyout, projected from a child that did not
/// fit. <paramref name="Detail"/> is the muted suffix on the right — the owning plugin's name for a
/// contributed action, or a shortcut hint for a host one. A null <paramref name="Command"/> renders as a
/// plain label (the row-range readout in Browse mode is exactly that).
/// </summary>
public sealed record OverflowItem(
    string Header,
    Geometry? Icon,
    ICommand? Command,
    string? Detail);
