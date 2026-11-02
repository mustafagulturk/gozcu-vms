using Gozcu.Media.Ffmpeg;

namespace Gozcu.UnitTests.Media;

public class AnalysisFramesCommandTests
{
    private static readonly Uri Camera = new("rtsp://mediamtx:8554/cam1");

    [Fact]
    public void Decodes_thins_letterboxes_and_writes_raw_bgr_to_stdout()
    {
        var args = FfmpegCommands.AnalysisFrames(Camera, 2, 416, 416);

        AssertPair(args, "-rtsp_transport", "tcp");
        AssertPair(args, "-timeout", "5000000");
        AssertPair(args, "-i", Camera.ToString());
        AssertPair(args, "-map", "0:v:0");
        AssertPair(args, "-vf", "fps=2,scale=416:416:force_original_aspect_ratio=decrease,pad=416:416:0:0:color=0x727272");
        AssertPair(args, "-pix_fmt", "bgr24");
        AssertPair(args, "-f", "rawvideo");
        Assert.Equal("pipe:1", args[^1]);
        Assert.Contains("-an", args);
        Assert.Contains("-nostats", args);
        Assert.Contains("-nostdin", args);
        Assert.DoesNotContain("copy", args);
    }

    [Fact]
    public void Uses_info_loglevel_so_input_stream_line_is_printed()
    {
        var args = FfmpegCommands.AnalysisFrames(Camera, 2, 416, 416);
        AssertPair(args, "-loglevel", "info");

        // Giriş seçenekleri -i'den önce, çıkış seçenekleri sonra gelmeli.
        var list = args.ToList();
        Assert.True(list.IndexOf("-rtsp_transport") < list.IndexOf("-i"));
        Assert.True(list.IndexOf("-vf") > list.IndexOf("-i"));
    }

    [Fact]
    public void Fractional_rate_non_square_size_and_socket_timeout_are_formatted_invariantly()
    {
        var args = FfmpegCommands.AnalysisFrames(Camera, 0.5, 640, 384, TimeSpan.FromSeconds(2));

        AssertPair(args, "-vf", "fps=0.5,scale=640:384:force_original_aspect_ratio=decrease,pad=640:384:0:0:color=0x727272");
        AssertPair(args, "-timeout", "2000000");
    }

    [Theory]
    [InlineData(0, 416, 416)]
    [InlineData(-1, 416, 416)]
    [InlineData(61, 416, 416)]
    [InlineData(2, 8, 416)]
    [InlineData(2, 416, 0)]
    public void Invalid_arguments_are_rejected(double fps, int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FfmpegCommands.AnalysisFrames(Camera, fps, width, height));
    }

    [Fact]
    public void Input_stream_line_is_parsed_and_output_line_also_matches_so_only_first_must_be_used()
    {
        const string input = "  Stream #0:0: Video: h264 (High), yuv420p(progressive), 1280x720 [SAR 1:1 DAR 16:9], 25 fps, 25 tbr, 90k tbn";
        const string output = "  Stream #0:0: Video: rawvideo (BGR[24] / 0x18524742), bgr24(pc, gbr/unknown/unknown, progressive), 416x416 [SAR 1:1 DAR 1:1], q=2-31, 8306 kb/s, 2 fps, 2 tbn";

        Assert.True(StreamInfo.TryParse(input, out var info));
        Assert.Equal(new StreamInfo("h264", 1280, 720, 25), info);
        Assert.True(StreamInfo.TryParse(output, out var outInfo));
        Assert.Equal("rawvideo", outInfo!.Codec);
    }

    private static void AssertPair(IReadOnlyList<string> args, string option, string value)
    {
        var index = args.ToList().IndexOf(option);
        Assert.True(index >= 0, $"{option} bulunamadı");
        Assert.Equal(value, args[index + 1]);
    }
}
