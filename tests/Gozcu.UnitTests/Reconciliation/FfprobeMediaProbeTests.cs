using Gozcu.Media.Probing;
using Gozcu.Media.Processes;

namespace Gozcu.UnitTests.Reconciliation;

public class FfprobeMediaProbeTests
{
    [Fact]
    public void Parses_duration_and_first_video_codec()
    {
        var result = FfprobeMediaProbe.Parse(
            """
            { "programs": [], "streams": [ { "codec_name": "aac", "codec_type": "audio" }, { "codec_name": "h264", "codec_type": "video" } ],
              "format": { "duration": "59.960000" } }
            """);

        Assert.True(result.IsPlayable);
        Assert.Equal("h264", result.VideoCodec);
        Assert.Equal(TimeSpan.FromSeconds(59.96), result.Duration);
    }

    [Fact]
    public void File_without_video_stream_or_duration_is_not_playable()
    {
        Assert.False(FfprobeMediaProbe.Parse("""{ "streams": [ { "codec_name": "aac", "codec_type": "audio" } ], "format": { "duration": "5.0" } }""").IsPlayable);
        Assert.False(FfprobeMediaProbe.Parse("""{ "streams": [ { "codec_name": "h264", "codec_type": "video" } ], "format": { } }""").IsPlayable);
        Assert.False(FfprobeMediaProbe.Parse("not json").Readable);
    }

    [Fact]
    public async Task Nonzero_exit_means_unreadable_with_last_error_line()
    {
        var probe = new FfprobeMediaProbe(new ScriptedRunner(1, stderr: "[mov,mp4,m4a,3gp,3g2,mj2 @ 0x1] moov atom not found"));

        var result = await probe.ProbeAsync("/r/cam1/x.mp4", CancellationToken.None);

        Assert.False(result.Readable);
        Assert.Contains("moov atom not found", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_throws_instead_of_reporting_corrupt()
    {
        var probe = new FfprobeMediaProbe(new ScriptedRunner(0, hang: true), timeout: TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() => probe.ProbeAsync("/r/cam1/x.mp4", CancellationToken.None));
    }

    private sealed class ScriptedRunner(int exitCode, string? stderr = null, bool hang = false) : IProcessRunner
    {
        public async Task<int> RunAsync(ProcessSpec spec, Action<string> onStandardOutput, Action<string> onStandardError, CancellationToken cancellationToken)
        {
            Assert.Equal("ffprobe", spec.FileName);
            Assert.Equal("/r/cam1/x.mp4", spec.Arguments[^1]);
            if (hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (stderr is not null)
            {
                onStandardError(stderr);
            }

            return exitCode;
        }
    }
}
