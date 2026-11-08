namespace DataTray.Sdk.Extensibility;

/// <summary>A column; <see cref="Type"/> is the engine's type name, null when unknown.</summary>
public sealed record QueryColumn(string Name, string? Type);
