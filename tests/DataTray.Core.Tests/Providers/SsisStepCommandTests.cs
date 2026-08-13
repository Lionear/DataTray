using DataTray.Providers.MsSql;

namespace DataTray.Core.Tests.Providers;

/// <summary>
/// An SSIS step's command is a generated dtexec argument string. Nearly every one that reaches DataTray was
/// written by SSMS, so reading one back matters more than the editor does — and a round trip that drops an
/// option changes what the step does without anyone touching that field. The strings below are the shapes
/// SSMS produces.
/// </summary>
public class SsisStepCommandTests
{
    // What SSMS writes for a catalog package with an environment, verbose logging and one connection override.
    private const string CatalogCommand =
        """
        /ISSERVER "\"\SSISDB\Finance\NightlyLoad\LoadDimCustomer.dtsx\"" /SERVER "\"SQL01\PROD\"" /ENVREFERENCE 12 /Par "\"CM.Staging.ConnectionString\"";"Data Source=SQL01\STAGE;Initial Catalog=Staging;Integrated Security=SSPI;" /Par "\"$ServerOption::LOGGING_LEVEL(Int16)\"";3 /Par "\"$ServerOption::SYNCHRONIZED(Boolean)\"";True /CALLERINFO SQLAGENT /REPORTING E
        """;

    // A legacy package on disk: encrypted, 32-bit, one connection override.
    private const string FileSystemCommand =
        """
        /FILE "\"D:\SSIS\Packages\LegacyImport.dtsx\"" /DECRYPT secret /CONNECTION "Staging";"\"Data Source=SQL01\STAGE;Initial Catalog=Staging;Integrated Security=SSPI;\"" /X86 /CHECKPOINTING OFF /REPORTING E
        """;

    // ── Reading one back ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_catalog_command_yields_its_package_and_server()
    {
        var step = Parsed(CatalogCommand);

        Assert.Equal(SsisPackageSource.Catalog, step.Source);
        Assert.Equal(@"\SSISDB\Finance\NightlyLoad\LoadDimCustomer.dtsx", step.PackagePath);
        Assert.Equal(@"SQL01\PROD", step.Server);
    }

    [Fact]
    public void The_environment_reference_survives_as_its_id()
    {
        // An id, not a name: turning 12 into "PROD" needs catalog.environment_references, which is the
        // page's job. Losing the id here is what makes a step fail at run time with nothing to go on.
        Assert.Equal(12, Parsed(CatalogCommand).EnvironmentReference);
    }

    [Fact]
    public void The_logging_level_comes_out_of_its_server_option()
    {
        Assert.Equal(3, Parsed(CatalogCommand).LoggingLevel);
    }

    [Fact]
    public void A_catalog_connection_override_is_read_from_its_CM_parameter()
    {
        var overrides = Parsed(CatalogCommand).ConnectionOverrides;

        var single = Assert.Single(overrides);
        Assert.Equal("Staging", single.Name);
        Assert.Equal(@"Data Source=SQL01\STAGE;Initial Catalog=Staging;Integrated Security=SSPI;", single.Value);
    }

    [Fact]
    public void A_file_system_command_yields_its_path_password_and_runtime()
    {
        var step = Parsed(FileSystemCommand);

        Assert.Equal(SsisPackageSource.FileSystem, step.Source);
        Assert.Equal(@"D:\SSIS\Packages\LegacyImport.dtsx", step.PackagePath);
        Assert.Equal("secret", step.PackagePassword);
        Assert.True(step.Use32BitRuntime);
    }

    [Fact]
    public void A_legacy_connection_override_is_read_from_its_CONNECTION_option()
    {
        var single = Assert.Single(Parsed(FileSystemCommand).ConnectionOverrides);

        Assert.Equal("Staging", single.Name);
        Assert.Equal(@"Data Source=SQL01\STAGE;Initial Catalog=Staging;Integrated Security=SSPI;", single.Value);
    }

    [Theory]
    [InlineData("""/SQL "\"\Maintenance Plans\Nightly\"" /SERVER "\"SQL01\"" /CHECKPOINTING OFF""", SsisPackageSource.MsdbStore)]
    [InlineData("""/DTS "\"\File System\Nightly\"" /SERVER "\"SQL01\"" /CHECKPOINTING OFF""", SsisPackageSource.ManagedFolderStore)]
    public void The_verb_decides_the_source(string command, SsisPackageSource expected)
    {
        Assert.Equal(expected, Parsed(command).Source);
    }

    // ── Refusing to read one back ────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_option_the_editor_does_not_model_blocks_editing()
    {
        // Opening the editor on this and saving would silently drop /CONFIGFILE and /SET. Better to hand
        // back the text box than to rewrite a step into something that does less than it did.
        var result = SsisStepCommand.Parse(
            """
            /FILE "\"D:\SSIS\Recon.dtsx\"" /CONFIGFILE "\"D:\SSIS\recon.dtsConfig\"" /SET \Package.Variables[User::RunDate].Value;2026-08-13 /CHECKPOINTING OFF /REPORTING E
            """);

        Assert.False(result.CanEdit);
        Assert.Null(result.Command);
        Assert.Equal(new[] { "/CONFIGFILE", "/SET" }, result.UnsupportedOptions);
    }

    [Fact]
    public void A_command_without_a_package_blocks_editing()
    {
        // A step whose command is empty, or hand-written prose. Nothing to fill the fields from.
        Assert.False(SsisStepCommand.Parse("").CanEdit);
        Assert.False(SsisStepCommand.Parse("run the nightly load").CanEdit);
    }

    [Fact]
    public void The_options_Agent_always_writes_are_recognised_rather_than_rejected()
    {
        // /CALLERINFO, /REPORTING and /CHECKPOINTING appear in every step SSMS writes and carry nothing the
        // editor asks for. Treating them as unsupported would send every real step down the text fallback.
        Assert.True(SsisStepCommand.Parse(CatalogCommand).CanEdit);
        Assert.True(SsisStepCommand.Parse(FileSystemCommand).CanEdit);
    }

    // ── Writing one ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_catalog_step_rebuilds_the_command_SSMS_wrote()
    {
        Assert.Equal(CatalogCommand, SsisStepCommand.Build(Parsed(CatalogCommand)));
    }

    [Fact]
    public void A_file_system_step_rebuilds_the_command_SSMS_wrote()
    {
        Assert.Equal(FileSystemCommand, SsisStepCommand.Build(Parsed(FileSystemCommand)));
    }

    [Fact]
    public void An_environment_of_none_writes_no_reference()
    {
        var step = Parsed(CatalogCommand) with { EnvironmentReference = null };

        Assert.DoesNotContain("/ENVREFERENCE", SsisStepCommand.Build(step));
    }

    [Fact]
    public void Waiting_for_the_package_is_written_as_the_synchronized_option()
    {
        var step = Parsed(CatalogCommand) with { WaitForCompletion = false };

        Assert.Contains("""/Par "\"$ServerOption::SYNCHRONIZED(Boolean)\"";False""", SsisStepCommand.Build(step));
    }

    [Fact]
    public void The_32_bit_flag_is_written_for_a_catalog_package_too()
    {
        var step = Parsed(CatalogCommand) with { Use32BitRuntime = true };

        Assert.Contains("/X86", SsisStepCommand.Build(step));
    }

    [Fact]
    public void A_password_is_only_written_when_there_is_one()
    {
        var step = Parsed(FileSystemCommand) with { PackagePassword = null };

        Assert.DoesNotContain("/DECRYPT", SsisStepCommand.Build(step));
    }

    // A path with a space is the case that breaks a builder which forgets to quote.
    [Fact]
    public void A_path_with_spaces_stays_one_argument()
    {
        var step = new SsisStepCommand
        {
            Source = SsisPackageSource.FileSystem,
            PackagePath = @"D:\SSIS Packages\Nightly Load.dtsx"
        };

        Assert.Equal(@"D:\SSIS Packages\Nightly Load.dtsx", Parsed(SsisStepCommand.Build(step)).PackagePath);
    }

    private static SsisStepCommand Parsed(string command)
    {
        var result = SsisStepCommand.Parse(command);
        Assert.True(result.CanEdit, $"expected a parsable command, got unsupported: {string.Join(", ", result.UnsupportedOptions)}");
        return result.Command!;
    }
}
