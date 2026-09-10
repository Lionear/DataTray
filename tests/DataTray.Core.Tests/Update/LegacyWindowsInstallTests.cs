using DataTray.Infrastructure.Update;

namespace DataTray.Core.Tests.Update;

/// <summary>
/// Covers the one part of <see cref="LegacyWindowsInstall"/> that is testable off Windows: pulling the
/// executable out of Inno's <c>UninstallString</c>. Getting this wrong does not throw — it yields a path
/// that does not exist, the notice never appears, and the stale Start-menu entry stays behind silently.
/// </summary>
public class LegacyWindowsInstallTests
{
    // The shape every default install actually has: per-user, so the path runs through the profile
    // directory, so it contains spaces, so Inno quotes it.
    [Fact]
    public void Quoted_path_with_spaces_is_unwrapped() =>
        Assert.Equal(
            @"C:\Users\Rick Bonkestoter\AppData\Local\Programs\DataTray\unins000.exe",
            LegacyWindowsInstall.ParseUninstallerPath(
                "\"C:\\Users\\Rick Bonkestoter\\AppData\\Local\\Programs\\DataTray\\unins000.exe\""));

    [Fact]
    public void Quoted_path_keeps_only_the_program_not_its_switches() =>
        Assert.Equal(
            @"C:\Program Files\DataTray\unins000.exe",
            LegacyWindowsInstall.ParseUninstallerPath(
                "\"C:\\Program Files\\DataTray\\unins000.exe\" /VERYSILENT"));

    [Fact]
    public void Unquoted_path_is_taken_as_is() =>
        Assert.Equal(
            @"C:\DataTray\unins000.exe",
            LegacyWindowsInstall.ParseUninstallerPath(@"C:\DataTray\unins000.exe"));

    [Fact]
    public void Unquoted_path_drops_trailing_switches() =>
        Assert.Equal(
            @"C:\DataTray\unins000.exe",
            LegacyWindowsInstall.ParseUninstallerPath(@"C:\DataTray\unins000.exe /VERYSILENT /NORESTART"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"")]
    [InlineData("\"\"")]
    public void Nothing_usable_yields_null(string? value) =>
        Assert.Null(LegacyWindowsInstall.ParseUninstallerPath(value));

    // Off Windows the registry is never touched at all, so this must answer without throwing on the
    // Linux and macOS builds — and on the CI runners, which are both.
    [Fact]
    public void Find_is_a_no_op_off_windows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Null(LegacyWindowsInstall.FindUninstaller());
    }
}
