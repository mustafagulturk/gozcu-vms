using Gozcu.Media.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Gozcu.UnitTests.Media;

public class ProcessSupervisorTests
{
    private static readonly ProcessSpec Spec = new("ffmpeg", ["-version"]);

    [Fact]
    public async Task Restarts_process_with_growing_backoff_until_cancelled()
    {
        var time = new FakeTimeProvider();
        var runner = new ScriptedRunner(exitCodes: [1, 1, 1]);
        var handler = new RecordingHandler();
        var policy = new ReconnectPolicy { InitialDelay = TimeSpan.FromSeconds(1), JitterRatio = 0 };
        var supervisor = new ProcessSupervisor(runner, policy, time, NullLogger.Instance);
        using var cts = new CancellationTokenSource();

        var run = supervisor.RunAsync(() => Spec, handler, cts.Token);

        await handler.WaitForExitsAsync(1);
        Assert.Equal(TimeSpan.FromSeconds(1), handler.Delays[0]);
        time.Advance(TimeSpan.FromSeconds(1));

        await handler.WaitForExitsAsync(2);
        Assert.Equal(TimeSpan.FromSeconds(2), handler.Delays[1]);
        time.Advance(TimeSpan.FromSeconds(2));

        await handler.WaitForExitsAsync(3);
        Assert.Equal(TimeSpan.FromSeconds(4), handler.Delays[2]);

        cts.Cancel();
        await run;

        Assert.Equal(3, handler.Starts);
        Assert.True(handler.Stopped);
    }

    [Fact]
    public async Task Backoff_resets_after_process_ran_stably()
    {
        var time = new FakeTimeProvider();
        var handler = new RecordingHandler();
        // İlk çalıştırma 60 sn sürer (kararlı), sonra hemen çıkar.
        var runner = new ScriptedRunner(exitCodes: [0, 0], onRun: run => { if (run == 1) time.Advance(TimeSpan.FromSeconds(60)); });
        var policy = new ReconnectPolicy { InitialDelay = TimeSpan.FromSeconds(1), JitterRatio = 0, StableAfter = TimeSpan.FromSeconds(30) };
        var supervisor = new ProcessSupervisor(runner, policy, time, NullLogger.Instance);
        using var cts = new CancellationTokenSource();

        var run = supervisor.RunAsync(() => Spec, handler, cts.Token);
        await handler.WaitForExitsAsync(1);
        Assert.Equal(TimeSpan.FromSeconds(1), handler.Delays[0]);

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Start_failure_is_reported_and_retried()
    {
        var time = new FakeTimeProvider();
        var handler = new RecordingHandler();
        var runner = new ScriptedRunner(exitCodes: [], throwOnRun: new InvalidOperationException("ffmpeg yok"));
        var supervisor = new ProcessSupervisor(runner, new ReconnectPolicy { JitterRatio = 0 }, time, NullLogger.Instance);
        using var cts = new CancellationTokenSource();

        var run = supervisor.RunAsync(() => Spec, handler, cts.Token);
        await handler.WaitForExitsAsync(1);

        Assert.Null(handler.ExitCodes[0]);
        Assert.IsType<InvalidOperationException>(handler.Errors[0]);

        cts.Cancel();
        await run;
    }

    private sealed class ScriptedRunner(int[] exitCodes, Action<int>? onRun = null, Exception? throwOnRun = null) : IProcessRunner
    {
        private int _runs;

        public async Task<int> RunAsync(ProcessSpec spec, Action<string> onStandardOutput, Action<string> onStandardError, CancellationToken cancellationToken)
        {
            var run = Interlocked.Increment(ref _runs);
            onRun?.Invoke(run);
            if (throwOnRun is not null)
            {
                throw throwOnRun;
            }

            if (run <= exitCodes.Length)
            {
                return exitCodes[run - 1];
            }

            // Senaryo bitti: iptal edilene kadar çalışıyormuş gibi bekle.
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class RecordingHandler : ISupervisedProcessHandler
    {
        private readonly object _lock = new();

        public int Starts { get; private set; }

        public bool Stopped { get; private set; }

        public List<TimeSpan> Delays { get; } = [];

        public List<int?> ExitCodes { get; } = [];

        public List<Exception?> Errors { get; } = [];

        public void OnStarted() => Starts++;

        public void OnStandardOutput(string line)
        {
        }

        public void OnStandardError(string line)
        {
        }

        public void OnExited(int? exitCode, Exception? error, TimeSpan retryDelay)
        {
            lock (_lock)
            {
                ExitCodes.Add(exitCode);
                Errors.Add(error);
                Delays.Add(retryDelay);
            }
        }

        public void OnStopped() => Stopped = true;

        public async Task WaitForExitsAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (_lock)
                {
                    if (Delays.Count >= count)
                    {
                        return;
                    }
                }

                await Task.Delay(10);
            }

            throw new TimeoutException($"{count} çıkış bekleniyordu, {Delays.Count} gerçekleşti");
        }
    }
}
