using DataTray.App.Startup;

namespace DataTray.App.Tests;

[Collection(nameof(AppDataEnvironment))]
public sealed class ReducedMotionTests : IDisposable
{
    private readonly string? _previous = Environment.GetEnvironmentVariable(ReducedMotion.OverrideVariable);

    public void Dispose() => Environment.SetEnvironmentVariable(ReducedMotion.OverrideVariable, _previous);

    [Fact] // The override is how a reviewer (and the screenshot run) sees the static splash on any desktop.
    public void The_override_variable_requests_reduced_motion()
    {
        Environment.SetEnvironmentVariable(ReducedMotion.OverrideVariable, "1");

        Assert.True(ReducedMotion.IsRequested());
    }
}
