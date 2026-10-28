using Gozcu.Media.Ffmpeg;

namespace Gozcu.UnitTests.Media;

public class FfmpegParsersTests
{
    [Fact]
    public void Progress_parser_emits_report_on_progress_line()
    {
        var parser = new FfmpegProgressParser();
        string[] lines = ["bitrate=N/A", "total_size=N/A", "out_time_us=2280000", "speed=1.01x"];
        foreach (var line in lines)
        {
            Assert.False(parser.TryAccept(line, out _));
        }

        Assert.True(parser.TryAccept("progress=continue", out var progress));
        Assert.Equal(TimeSpan.FromMilliseconds(2280), progress!.OutTime);
        Assert.Equal(1.01, progress.Speed);
        Assert.False(progress.IsFinal);
    }

    [Fact]
    public void Progress_parser_handles_unavailable_values_and_resets_between_blocks()
    {
        var parser = new FfmpegProgressParser();
        parser.TryAccept("out_time_us=1000000", out _);
        parser.TryAccept("progress=continue", out _);

        parser.TryAccept("out_time_us=N/A", out _);
        parser.TryAccept("speed=N/A", out _);
        Assert.True(parser.TryAccept("progress=end", out var progress));
        Assert.Null(progress!.OutTime);
        Assert.Null(progress.Speed);
        Assert.True(progress.IsFinal);
    }

    [Theory]
    [InlineData("  Stream #0:0: Video: h264 (High), yuv420p(progressive), 1280x720 [SAR 1:1 DAR 16:9], 25 fps, 25 tbr, 90k tbn", "h264", 1280, 720, 25.0)]
    [InlineData("  Stream #0:0[0x1](und): Video: hevc (Main), yuv420p(tv), 1920x1080, 29.97 fps, 90k tbn", "hevc", 1920, 1080, 29.97)]
    public void Stream_info_is_parsed_from_ffmpeg_input_line(string line, string codec, int width, int height, double fps)
    {
        Assert.True(StreamInfo.TryParse(line, out var info));
        Assert.Equal(new StreamInfo(codec, width, height, fps), info);
    }

    [Fact]
    public void Audio_stream_lines_are_ignored()
    {
        Assert.False(StreamInfo.TryParse("  Stream #0:1: Audio: aac (LC), 48000 Hz, stereo, fltp", out _));
    }

    [Theory]
    [InlineData("cam1_20261028T171500Z.mp4,0.000000,60.000000", "cam1_20261028T171500Z.mp4", 60.0)]
    [InlineData("cam1_20261028T171600Z.mp4,60.000000,120.040000", "cam1_20261028T171600Z.mp4", 60.04)]
    [InlineData("\"weird,name.mp4\",1.5,3.0", "weird,name.mp4", 1.5)]
    public void Segment_list_csv_line_is_parsed(string line, string fileName, double durationSeconds)
    {
        Assert.True(SegmentListEntry.TryParse(line, out var entry));
        Assert.Equal(fileName, entry!.FileName);
        Assert.Equal(durationSeconds, entry.Duration.TotalSeconds, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a csv line")]
    [InlineData("file.mp4,abc,def")]
    public void Invalid_segment_list_lines_are_rejected(string line)
    {
        Assert.False(SegmentListEntry.TryParse(line, out _));
    }

    [Fact]
    public void Segment_file_name_round_trips()
    {
        var start = new DateTimeOffset(2026, 10, 28, 17, 15, 0, TimeSpan.Zero);
        var name = SegmentFileName.Format("cam-1", start);

        Assert.Equal("cam-1_20261028T171500Z.mp4", name);
        Assert.True(SegmentFileName.TryParse("/recordings/cam-1/" + name, out var camera, out var parsed));
        Assert.Equal("cam-1", camera);
        Assert.Equal(start, parsed);
    }

    [Fact]
    public void Segment_file_name_with_local_offset_is_stored_as_utc()
    {
        var local = new DateTimeOffset(2026, 10, 28, 20, 15, 0, TimeSpan.FromHours(3));
        Assert.Equal("cam1_20261028T171500Z.mp4", SegmentFileName.Format("cam1", local));
    }

    [Theory]
    [InlineData("cam1_20261028T171500.mp4")]
    [InlineData("cam1_20261399T171500Z.mp4")]
    [InlineData("notes.txt")]
    public void Unknown_files_are_not_treated_as_segments(string fileName)
    {
        Assert.False(SegmentFileName.TryParse(fileName, out _, out _));
    }
}
