using System.Diagnostics;
using System.Text;

namespace DataTray.Backends.Docker;

/// <summary>
/// The real <see cref="IDockerCli"/>: shells out to the <c>docker</c> / <c>docker compose</c> CLI (compose
/// has no managed API). Uses <see cref="ProcessStartInfo.ArgumentList"/> — never string-spliced argv (no
/// shell, no injection) — and reads asynchronously. Requires Docker installed and on PATH; when it isn't,
/// calls surface as failed results rather than throwing. (This is why the plugin declares the
/// <c>process</c> capability — disclosure that it starts external processes.)
/// </summary>
public sealed class DockerCli : IDockerCli, ISingletonService
{
    private const string Exe = "docker";

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            // `docker version` fails (non-zero) when the client can't reach the daemon — exactly the
            // "Docker not usable" signal we want, not just "the binary exists".
            var result = await RunAsync(null, ct, "version", "--format", "{{.Server.Version}}");
            return result.Success;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public Task<DockerResult> ComposeUpAsync(string projectDir, CancellationToken ct) =>
        RunAsync(projectDir, ct, "compose", "up", "-d");

    public Task<DockerResult> ComposeDownAsync(string projectDir, bool removeVolumes, CancellationToken ct) =>
        removeVolumes
            ? RunAsync(projectDir, ct, "compose", "down", "-v")
            : RunAsync(projectDir, ct, "compose", "down");

    public Task<DockerResult> StartAsync(string containerName, CancellationToken ct) =>
        RunAsync(null, ct, "start", containerName);

    public Task<DockerResult> StopAsync(string containerName, CancellationToken ct) =>
        RunAsync(null, ct, "stop", containerName);

    public async Task<ContainerStatus> InspectAsync(string containerName, CancellationToken ct)
    {
        // One inspect emits "status;health" (health = "none" when the image declares no healthcheck).
        var result = await RunAsync(null, ct,
            "inspect", "-f",
            "{{.State.Status}};{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}",
            containerName);

        if (!result.Success)
        {
            return ContainerStatus.Absent; // no such container
        }

        var parts = result.StdOut.Trim().Split(';');
        var health = parts.ElementAtOrDefault(1) switch
        {
            "healthy" => (bool?)true,
            "unhealthy" or "starting" => false,
            _ => null
        };

        return new ContainerStatus(ParseState(parts.ElementAtOrDefault(0)), health);
    }

    public async Task<string> LogsAsync(string containerName, int tailLines, CancellationToken ct)
    {
        var result = await RunAsync(null, ct, "logs", "--tail", tailLines.ToString(), containerName);
        // Docker splits container output across stdout and stderr; present both in order.
        return string.Join('\n',
            new[] { result.StdOut.TrimEnd(), result.StdErr.TrimEnd() }.Where(s => s.Length > 0));
    }

    /// <summary>
    /// The PATH the docker process searches, on macOS. A <c>.app</c> started from Finder or the Dock
    /// inherits launchd's PATH — <c>/usr/bin:/bin:/usr/sbin:/sbin</c> — which holds none of the places
    /// Docker Desktop puts its CLI, so <c>docker</c> is "not found" while the very same install works
    /// from a terminal. Linux (<c>/usr/bin/docker</c>) and Windows (installer extends the system PATH)
    /// don't have the problem, hence macOS-only.
    ///
    /// The dirs are <b>appended</b>: an inherited PATH still decides which docker wins, so launching
    /// from a terminal behaves exactly as before.
    /// </summary>
    public static string MacSearchPath(string? currentPath) =>
        string.Join(':', (currentPath ?? string.Empty)
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Concat([
                "/usr/local/bin", // Docker Desktop, "System" install
                "/opt/homebrew/bin", // Homebrew on Apple silicon
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker", "bin")
            ])
            .Distinct());

    /// <summary>
    /// The first <c>docker</c> that exists in <paramref name="searchPath"/>, as an absolute path, or
    /// <c>null</c> when none of the dirs holds one.
    ///
    /// This has to be a path and not a name: on Unix, .NET resolves a bare <see
    /// cref="ProcessStartInfo.FileName"/> against the <b>parent process's</b> PATH, so setting
    /// <see cref="ProcessStartInfo.Environment"/> does not affect the lookup at all — measured, both
    /// still throw <c>Win32Exception: No such file or directory</c>. The extended PATH is still handed
    /// to the child, because the docker CLI looks up its own credential helper
    /// (<c>docker-credential-desktop</c>, same dirs) through it; without that, pulls from a private
    /// registry fail in the same invisible way.
    /// </summary>
    public static string? ResolveOnPath(string searchPath) =>
        searchPath.Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, Exe))
            .FirstOrDefault(File.Exists);

    private static ContainerState ParseState(string? status) => status switch
    {
        "created" => ContainerState.Created,
        "running" => ContainerState.Running,
        "restarting" => ContainerState.Restarting,
        "paused" => ContainerState.Paused,
        "exited" => ContainerState.Exited,
        "dead" => ContainerState.Dead,
        _ => ContainerState.Absent
    };

    private static async Task<DockerResult> RunAsync(string? workingDir, CancellationToken ct, params string[] args)
    {
        // Resolved per call, not cached: installing Docker Desktop after seeing "not found" and pressing
        // retry is the normal way out of that state — a cached miss would survive it until a restart.
        var searchPath = OperatingSystem.IsMacOS()
            ? MacSearchPath(Environment.GetEnvironmentVariable("PATH"))
            : null;

        var psi = new ProcessStartInfo(searchPath is null ? Exe : ResolveOnPath(searchPath) ?? Exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (workingDir is not null)
        {
            psi.WorkingDirectory = workingDir;
        }

        if (searchPath is not null)
        {
            psi.Environment["PATH"] = searchPath;
        }

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the docker process.");

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return new DockerResult(process.ExitCode, await stdout, await stderr);
    }
}
