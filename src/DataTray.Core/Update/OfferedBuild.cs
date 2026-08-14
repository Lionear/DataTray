namespace DataTray.Core.Update;

/// <summary>
/// A build a channel is offering, in the terms the UI needs to describe it.
/// <para>
/// Deliberately a plain record rather than the updater's own type: this lives in Core, which knows
/// only the SDK, and there is no reason for the domain layer to depend on the packaging library.
/// Everything Velopack-shaped stays inside <c>VelopackUpdateService</c>.
/// </para>
/// </summary>
/// <param name="Version">The full version stamp of the offered build, e.g. <c>0.8.0-nightly.99</c>.</param>
/// <param name="SizeBytes">Download size of the full package; 0 when the feed does not say.</param>
/// <param name="NotesMarkdown">Release notes carried in the package, or null when it has none.</param>
public sealed record OfferedBuild(string Version, long SizeBytes, string? NotesMarkdown);
