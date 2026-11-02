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
        AssertPair(args, "-vf", @"select=isnan(prev_selected_t)+gte(t-prev_selected_t\,0.475),scale=416:416:force_original_aspect_ratio=decrease,pad=416:416:0:0:color=0x727272,showinfo");
        AssertPair(args, "-fps_mode", "passthrough");
        AssertPair(args, "-pix_fmt", "bgr24");
        Assert.Equal("rawvideo", args[^2]);
        Assert.Equal("pipe:1", args[^1]);
        Assert.Contains("-an", args);
        Assert.Contains("-nostats", args);
        Assert.Contains("-nostdin", args);
    }

    [Fact]
    public void Packet_timestamps_go_to_stderr_without_decoding_and_decoding_uses_slice_threads()
    {
        var list = FfmpegCommands.AnalysisFrames(Camera, 2, 416, 416).ToList();

        // Zaman damgası çıkışı kopyalar (kod çözmez), kare çıkışından önce gelir ve stderr'e yazar.
        var copy = list.IndexOf("copy");
        Assert.True(copy > list.IndexOf("-i"));
        Assert.Equal("framecrc", list[list.IndexOf("-f", copy) + 1]);
        Assert.Equal("pipe:2", list[list.IndexOf("-f", copy) + 2]);
        Assert.True(copy < list.IndexOf("-vf"));

        // Kare tabanlı çok iş parçacıklı çözme gecikme ekler; dilim tabanlısı eklemez. Giriş seçeneğidir.
        Assert.True(list.IndexOf("-thread_type") < list.IndexOf("-i"));
        Assert.Equal("slice", list[list.IndexOf("-thread_type") + 1]);
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

        AssertPair(args, "-vf", @"select=isnan(prev_selected_t)+gte(t-prev_selected_t\,1.9),scale=640:384:force_original_aspect_ratio=decrease,pad=640:384:0:0:color=0x727272,showinfo");
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
