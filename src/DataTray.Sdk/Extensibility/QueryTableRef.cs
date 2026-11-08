namespace DataTray.Sdk.Extensibility;

/// <summary>Names a table or view; <see cref="Database"/> and <see cref="Schema"/> are null when the engine has none.</summary>
public sealed record QueryTableRef(string? Database, string? Schema, string Name, bool IsView);
