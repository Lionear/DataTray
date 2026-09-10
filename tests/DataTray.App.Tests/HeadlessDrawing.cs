using Avalonia;
using Avalonia.Headless;

namespace DataTray.App.Tests;

/// <summary>
/// Minimal Avalonia platform for view-model tests. <c>NodeIcons</c> parses its Lucide path data into
/// <c>Geometry</c> in a static constructor, and <c>Geometry.Parse</c> needs an
/// <c>IPlatformRenderInterface</c> to exist — so touching any tree node without this throws a
/// <c>TypeInitializationException</c> that has nothing to do with what is under test. Headless drawing
/// satisfies it without a window, a render loop, or a display.
/// </summary>
public sealed class HeadlessDrawing
{
    private static readonly object Gate = new();
    private static bool _started;

    public HeadlessDrawing()
    {
        lock (Gate)
        {
            if (_started)
            {
                return;
            }

            AppBuilder.Configure<Application>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .SetupWithoutStarting();
            _started = true;
        }
    }
}
