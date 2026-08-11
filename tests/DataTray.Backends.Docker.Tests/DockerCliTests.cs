using System;
using System.IO;
using DataTray.Backends.Docker;

namespace DataTray.Backends.Docker.Tests;

/// <summary>
/// SE-243: a macOS <c>.app</c> started from Finder inherits launchd's PATH, which has no
/// <c>/usr/local/bin</c> — so Docker Desktop's CLI was "not found" while it worked from a terminal.
/// </summary>
public class DockerCliTests
{
    private static readonly string LaunchdPath = "/usr/bin:/bin:/usr/sbin:/sbin";

    [Fact]
    public void MacSearchPath_AddsTheDirsDockerDesktopInstallsInto()
    {
        var path = DockerCli.MacSearchPath(LaunchdPath).Split(':');

        Assert.Contains("/usr/local/bin", path);
        Assert.Contains("/opt/homebrew/bin", path);
    }

    [Fact]
    public void MacSearchPath_KeepsTheInheritedPathFirst()
    {
        // Appended, not prepended: launched from a terminal, the user's own PATH still decides which
        // docker wins — the fallback dirs only fill the gap when nothing was inherited.
        var path = DockerCli.MacSearchPath("/opt/mydocker/bin:" + LaunchdPath).Split(':');

        Assert.Equal("/opt/mydocker/bin", path[0]);
        Assert.True(Array.IndexOf(path, "/opt/mydocker/bin") < Array.IndexOf(path, "/usr/local/bin"));
    }

    [Fact]
    public void MacSearchPath_DoesNotRepeatADirThatIsAlreadyThere()
    {
        var path = DockerCli.MacSearchPath("/usr/local/bin:" + LaunchdPath).Split(':');

        Assert.Single(path, p => p == "/usr/local/bin");
    }

    [Fact]
    public void MacSearchPath_HandlesAnEmptyOrMissingPath()
    {
        Assert.Contains("/usr/local/bin", DockerCli.MacSearchPath(null).Split(':'));
        Assert.Contains("/usr/local/bin", DockerCli.MacSearchPath("").Split(':'));
    }

    // Resolving to an absolute path is the part that actually fixes it: .NET looks a bare FileName up in
    // the PARENT process's PATH on Unix, so handing the child an extended PATH changes nothing.

    [Fact]
    public void ResolveOnPath_ReturnsTheFirstDirThatHoldsADocker()
    {
        var empty = Directory.CreateTempSubdirectory().FullName;
        var withDocker = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(withDocker, "docker"), "");

        try
        {
            Assert.Equal(Path.Combine(withDocker, "docker"), DockerCli.ResolveOnPath($"{empty}:{withDocker}"));
        }
        finally
        {
            Directory.Delete(empty, true);
            Directory.Delete(withDocker, true);
        }
    }

    [Fact]
    public void ResolveOnPath_ReturnsNullWhenNoDirHoldsOne()
    {
        var empty = Directory.CreateTempSubdirectory().FullName;

        try
        {
            Assert.Null(DockerCli.ResolveOnPath($"{empty}:/no/such/dir"));
        }
        finally
        {
            Directory.Delete(empty, true);
        }
    }
}
