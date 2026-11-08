namespace DataTray.Sdk.Extensibility;

/// <summary>One item a plugin adds to a table/view node's context menu; <see cref="AppliesTo"/> filters per node.</summary>
public sealed record NodeMenuContribution(
    string Id,
    string Title,
    Func<NodeMenuTarget, bool> AppliesTo,
    Func<NodeMenuTarget, IHostUi, Task> InvokeAsync);
