using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

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

            // Avalonia's compositor checks the thread it was set up on, so a window can only be built there.
            // Set up on a thread of our own that keeps the dispatcher running, and let tests Run() on it.
            using var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                AppBuilder.Configure<Application>()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                    .SetupWithoutStarting();
                ready.Set();
                Dispatcher.UIThread.MainLoop(CancellationToken.None);
            }) { IsBackground = true, Name = "Avalonia UI (tests)" };
            thread.Start();
            ready.Wait();
            _started = true;
        }
    }

    /// <summary>Runs <paramref name="work"/> on the Avalonia UI thread, as anything that builds a window must.</summary>
    public static T Run<T>(Func<T> work) => Dispatcher.UIThread.Invoke(work);
}
