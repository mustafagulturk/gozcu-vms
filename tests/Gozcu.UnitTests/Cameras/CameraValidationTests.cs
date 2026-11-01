using Gozcu.Contracts.Cameras;
using Gozcu.Media.Probing;
using Gozcu.StreamingService.Cameras;

namespace Gozcu.UnitTests.Cameras;

public sealed class CameraValidationTests
{
    [Theory]
    [InlineData("cam1")]
    [InlineData("Giris-Kapisi_2")]
    public void Valid_ids_pass(string id) => Assert.Null(CameraValidation.ValidateId(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cam 1")]
    [InlineData("../etc")]
    [InlineData("kamera/1")]
    [InlineData("_quarantine")]
    [InlineData("ç")]
    public void Invalid_ids_are_rejected(string? id) => Assert.NotNull(CameraValidation.ValidateId(id));

    [Fact]
    public void Too_long_id_is_rejected() => Assert.NotNull(CameraValidation.ValidateId(new string('a', 65)));

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Blank_name_is_rejected(string? name) => Assert.NotNull(CameraValidation.ValidateName(name));

    [Fact]
    public void Name_length_is_limited()
    {
        Assert.Null(CameraValidation.ValidateName(new string('a', CameraValidation.MaxNameLength)));
        Assert.NotNull(CameraValidation.ValidateName(new string('a', CameraValidation.MaxNameLength + 1)));
    }

    [Theory]
    [InlineData("rtsp://192.168.1.10:554/stream1")]
    [InlineData("rtsps://admin:p%40ss@kamera.local/h264")]
    [InlineData("  rtsp://h/a  ")]
    public void Rtsp_urls_are_accepted(string url)
    {
        Assert.True(CameraValidation.TryParseRtspUrl(url, out var parsed, out var error));
        Assert.Null(error);
        Assert.NotNull(parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://h/a")]
    [InlineData("rtsp:///yol")]
    [InlineData("kamera")]
    [InlineData("file:///etc/passwd")]
    public void Non_rtsp_urls_are_rejected(string? url)
    {
        Assert.False(CameraValidation.TryParseRtspUrl(url, out var parsed, out var error));
        Assert.Null(parsed);
        Assert.NotNull(error);
    }
}

public sealed class RtspUrlMaskingTests
{
    [Theory]
    [InlineData("rtsp://admin:gizli@10.0.0.5:554/stream", "rtsp://admin:***@10.0.0.5:554/stream")]
    [InlineData("rtsp://admin:g@zli@host/a", "rtsp://admin:***@host/a")]
    [InlineData("rtsp://admin:gizli@host", "rtsp://admin:***@host")]
    [InlineData("rtsp://admin@host/a", "rtsp://admin@host/a")]
    [InlineData("rtsp://host:8554/a@b", "rtsp://host:8554/a@b")]
    [InlineData("rtsp://host:8554/cam1", "rtsp://host:8554/cam1")]
    public void Password_is_masked(string url, string expected) => Assert.Equal(expected, RtspUrlMasking.MaskPassword(url));

    [Fact]
    public void Ffmpeg_error_lines_are_redacted()
    {
        var line = "[in#0 @ 0x1] Error opening input rtsp://admin:S3cret!@10.0.0.5/live: Server returned 401 Unauthorized";

        var redacted = RtspUrlMasking.Redact(line);

        Assert.DoesNotContain("S3cret", redacted, StringComparison.Ordinal);
        Assert.Contains("rtsp://admin:***@10.0.0.5/live", redacted, StringComparison.Ordinal);
        Assert.Equal("Connection refused", RtspUrlMasking.Redact("Connection refused"));
    }

    [Fact]
    public void Masked_password_is_restored_from_original()
    {
        const string original = "rtsp://admin:gizli@eski-host/a";

        Assert.Equal("rtsp://admin:gizli@yeni-host/b", RtspUrlMasking.Unmask("rtsp://admin:***@yeni-host/b", original));
        Assert.Equal("rtsp://x:y@h/a", RtspUrlMasking.Unmask("rtsp://x:y@h/a", original));
        Assert.Null(RtspUrlMasking.Unmask("rtsp://admin:***@h/a", "rtsp://h/a"));
    }
}

public sealed class CameraEntityTagTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("*", null)]
    [InlineData("\"7\"", 7)]
    [InlineData("W/\"7\"", 7)]
    [InlineData(" \"12\" ", 12)]
    public void If_match_is_parsed(string? header, int? expected)
    {
        Assert.True(CameraEntityTag.TryParseIfMatch(header, out var version));
        Assert.Equal(expected, version);
    }

    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"1\", \"2\"")]
    public void Unreadable_if_match_fails(string header) => Assert.False(CameraEntityTag.TryParseIfMatch(header, out _));

    [Fact]
    public void Etag_is_quoted_version() => Assert.Equal("\"3\"", CameraEntityTag.Format(3));
}

public sealed class FfprobeStreamProbeTests
{
    [Fact]
    public void Video_stream_properties_are_parsed()
    {
        const string json = """
            { "streams": [ { "codec_name": "h264", "width": 1920, "height": 1080, "r_frame_rate": "25/1", "avg_frame_rate": "30000/1001" } ] }
            """;

        var result = FfprobeStreamProbe.Parse(json);

        Assert.Equal(new StreamProbeResult(true, "h264", 1920, 1080, 29.97, null), result);
    }

    [Fact]
    public void Unknown_average_rate_falls_back_to_real_rate()
    {
        var result = FfprobeStreamProbe.Parse("""{ "streams": [ { "codec_name": "hevc", "width": 640, "height": 360, "avg_frame_rate": "0/0", "r_frame_rate": "15/1" } ] }""");

        Assert.True(result.Ok);
        Assert.Equal(15, result.FramesPerSecond);
    }

    [Fact]
    public void Missing_video_stream_fails()
    {
        Assert.False(FfprobeStreamProbe.Parse("""{ "streams": [] }""").Ok);
        Assert.False(FfprobeStreamProbe.Parse("bozuk").Ok);
    }

    [Fact]
    public void Arguments_use_tcp_and_socket_timeout()
    {
        var args = FfprobeStreamProbe.Arguments(new Uri("rtsp://h/a"), TimeSpan.FromSeconds(7));

        Assert.Equal("tcp", args[args.ToList().IndexOf("-rtsp_transport") + 1]);
        Assert.Equal("7000000", args[args.ToList().IndexOf("-timeout") + 1]);
        Assert.Equal("rtsp://h/a", args[^1]);
    }
}
