namespace DataTray.Extensions.QueryBuilder.Generation;

/// <summary>A model node the dialect cannot express; Code is stable, Message is for the user.</summary>
public sealed record GenerationProblem(string Code, string Message, object Node);
