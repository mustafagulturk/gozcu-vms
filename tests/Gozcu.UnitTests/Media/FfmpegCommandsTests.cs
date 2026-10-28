using Gozcu.Media.Ffmpeg;

namespace Gozcu.UnitTests.Media;

public class FfmpegCommandsTests
{
    private static readonly Uri Camera = new("rtsp://mediamtx:8554/cam1");

    [Fact]
    public void Monitor_reads_stream_without_decoding()
    {
        var args = FfmpegCommands.Monitor(Camera);

        AssertPair(args, "-rtsp_transport", "tcp");
        AssertPair(args, "-i", Camera.ToString());
        AssertPair(args, "-c", "copy");
        AssertPair(args, "-f", "null");
        AssertPair(args, "-progress", "pipe:1");
        AssertPair(args, "-timeout", "5000000");
    }

    [Fact]
    public void Segment_recorder_copies_codec_and_aligns_segments_to_clock()
    {
        var args = FfmpegCommands.SegmentRecorder(Camera, "/recordings/cam1", "cam1", TimeSpan.FromSeconds(60));

        AssertPair(args, "-c", "copy");
        AssertPair(args, "-f", "segment");
        AssertPair(args, "-segment_time", "60");
        AssertPair(args, "-segment_atclocktime", "1");
        AssertPair(args, "-strftime", "1");
        AssertPair(args, "-segment_list", "pipe:1");
        AssertPair(args, "-segment_list_type", "csv");
        Assert.Equal(Path.Combine("/recordings/cam1", "cam1_%Y%m%dT%H%M%SZ.mp4"), args[^1]);
        Assert.DoesNotContain("libx264", args);
    }

    [Fact]
    public void Segment_duration_is_formatted_invariantly()
    {
        var args = FfmpegCommands.SegmentRecorder(Camera, "/r", "cam1", TimeSpan.FromSeconds(2.5));
        AssertPair(args, "-segment_time", "2.5");
    }

    [Fact]
    public void Segment_duration_below_one_second_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FfmpegCommands.SegmentRecorder(Camera, "/r", "cam1", TimeSpan.FromMilliseconds(500)));
    }

    private static void AssertPair(IReadOnlyList<string> args, string option, string value)
    {
        var index = args.ToList().IndexOf(option);
        Assert.True(index >= 0, $"{option} bulunamadı");
        Assert.Equal(value, args[index + 1]);
    }
}
