using DataTray.App.ViewModels;

namespace DataTray.App.Startup;

/// <summary>Runs the startup steps in order on the splash. A step that throws ends the run in the splash's
/// failure state instead of taking the process down.</summary>
public static class StartupRunner
{
    public static async Task<bool> RunAsync(
        SplashViewModel splash, IReadOnlyList<StartupWork> work, Action<string> log, CancellationToken ct = default)
    {
        var progress = new MarshalledProgress(SynchronizationContext.Current, detail =>
        {
            if (!ct.IsCancellationRequested)
            {
                splash.ReportDetail(detail);
            }
        });

        for (var i = 0; i < work.Count; i++)
        {
            var step = work[i];
            if (ct.IsCancellationRequested)
            {
                log($"start: run superseded before step {step.Key}");
                return false;
            }

            splash.Report(new StartupStep(step.Key, i, work.Count));
            var started = splash.Clock.GetUtcNow();
            try
            {
                await step.RunAsync(progress);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log($"start: step {step.Key} FAILED {ex.GetType().Name}: {ex.Message}");
                splash.Fail(ex);
                return false;
            }

            catch (Exception) when (ct.IsCancellationRequested)
            {
                return false; // a superseded run's failure is no longer the user's problem
            }

            log($"start: step {step.Key} {(int)(splash.Clock.GetUtcNow() - started).TotalMilliseconds} ms");
        }

        if (ct.IsCancellationRequested)
        {
            log("start: run superseded before completing");
            return false;
        }

        splash.Complete();
        return true;
    }

    // Progress<T> would post to the thread pool when there is no context (tests); this reports inline there,
    // and marshals to the UI context when a worker reports.
    private sealed class MarshalledProgress(SynchronizationContext? context, Action<string> report) : IProgress<string>
    {
        public void Report(string value)
        {
            if (context is null || SynchronizationContext.Current == context)
            {
                report(value);
            }
            else
            {
                context.Post(_ => report(value), null);
            }
        }
    }
}
