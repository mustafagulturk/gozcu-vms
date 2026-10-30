using System.Diagnostics;

namespace Gozcu.IntegrationTests.Infrastructure;

/// <summary>Ana makinedeki ffmpeg ile test deseni üretip MediaMTX'e RTSP olarak yayınlar.</summary>
public sealed class FakeCameraPublisher : IAsyncDisposable
{
    private readonly Uri _target;
    private Process? _process;

    public FakeCameraPublisher(Uri target) => _target = target;

    public void Start()
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        string[] args =
        [
            "-hide_banner", "-loglevel", "error", "-re",
            "-f", "lavfi", "-i", "testsrc=size=640x360:rate=25",
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-pix_fmt", "yuv420p", "-g", "25",
            "-f", "rtsp", "-rtsp_transport", "tcp", _target.ToString(),
        ];
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        _process = Process.Start(startInfo)!;
        _process.BeginErrorReadLine();
    }

    public async Task StopAsync()
    {
        if (_process is null || _process.HasExited)
        {
            return;
        }

        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync();
        _process.Dispose();
        _process = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
