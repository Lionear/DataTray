namespace DataTray.Providers.MsSql;

/// <summary>
/// Whether a command could be read back, and what stopped it if not. A command carrying an option the editor
/// does not model yields no <see cref="Command"/> at all: opening the editor on it and saving would drop that
/// option, which changes what the step does without anyone touching the field.
/// </summary>
public sealed record SsisParseResult(SsisStepCommand? Command, IReadOnlyList<string> UnsupportedOptions)
{
    public bool CanEdit => Command is not null;
}
