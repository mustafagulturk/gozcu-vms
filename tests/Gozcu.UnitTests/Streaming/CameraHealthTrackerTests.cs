using Gozcu.Contracts.Cameras;
using Gozcu.Media.Ffmpeg;
using Gozcu.StreamingService;
using Gozcu.StreamingService.Health;
using Microsoft.Extensions.Time.Testing;

namespace Gozcu.UnitTests.Streaming;

public class CameraHealthTrackerTests
{
    private static readonly CameraDefinition Camera = new() { Id = "cam1", Name = "Test", RtspUrl = new Uri("rtsp://x/cam1") };

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly CameraHealthTracker _tracker;

    public CameraHealthTrackerTests()
    {
        _tracker = new CameraHealthTracker(Camera, new StreamingOptions(), _time);
    }

    [Fact]
    public void Starts_in_connecting_and_goes_online_when_media_flows()
    {
        Assert.Equal(CameraHealthState.Connecting, _tracker.State);

        _tracker.OnConnecting();
        Assert.Null(_tracker.OnProgress(new FfmpegProgress(null, null, false)));
        Assert.Equal(CameraHealthState.Connecting, _tracker.State);

        var transition = Feed(TimeSpan.FromSeconds(0.5));
        Assert.Equal(new CameraStateTransition("cam1", CameraHealthState.Connecting, CameraHealthState.Online, null), transition);

        _time.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Null(Feed(TimeSpan.FromSeconds(1.0)));
        Assert.Equal(1.0, _tracker.Snapshot().IngestRate);
    }

    [Fact]
    public void Becomes_degraded_when_media_stalls_and_recovers_when_it_resumes()
    {
        GoOnline();

        _time.Advance(TimeSpan.FromSeconds(5));
        var stalled = _tracker.Evaluate();
        Assert.Equal(CameraHealthState.Degraded, stalled!.Current);

        // Akış yeniden gerçek zamanlı akmaya başlar.
        for (var i = 0; i < 10; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(0.5));
            _out += TimeSpan.FromSeconds(0.5);
            _tracker.OnProgress(new FfmpegProgress(_out, 1.0, false));
        }

        Assert.Equal(CameraHealthState.Online, _tracker.State);
    }

    [Fact]
    public void Becomes_degraded_when_ingest_rate_is_below_threshold()
    {
        GoOnline();

        // Her 1 sn'de yalnızca 0.3 sn medya geliyor.
        for (var i = 0; i < 10; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            _out += TimeSpan.FromSeconds(0.3);
            _tracker.OnProgress(new FfmpegProgress(_out, null, false));
        }

        Assert.Equal(CameraHealthState.Degraded, _tracker.State);
        Assert.True(_tracker.Snapshot().IngestRate < 0.8);
    }

    [Fact]
    public void Disconnect_goes_offline_with_reason_and_reconnect_is_counted()
    {
        GoOnline();
        _tracker.OnErrorLine("Connection refused");

        var offline = _tracker.OnDisconnected(1, TimeSpan.FromSeconds(2));
        Assert.Equal(CameraHealthState.Offline, offline!.Current);
        Assert.Equal("Connection refused", offline.Reason);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromSeconds(2), _tracker.Snapshot().NextRetryAt);

        // Offline iken periyodik değerlendirme durumu değiştirmez.
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(_tracker.Evaluate());

        _tracker.OnConnecting();
        Assert.Equal(1, _tracker.Snapshot().ReconnectCount);
        Assert.Null(_tracker.Snapshot().NextRetryAt);
    }

    [Fact]
    public void Failed_first_connection_does_not_count_as_reconnect()
    {
        _tracker.OnConnecting();
        _tracker.OnDisconnected(1, TimeSpan.FromSeconds(1));
        _tracker.OnConnecting();

        Assert.Equal(0, _tracker.Snapshot().ReconnectCount);
    }

    [Fact]
    public void Snapshot_contains_hls_url_and_stream_info()
    {
        _tracker.OnStreamInfo(new StreamInfo("h264", 1280, 720, 25));
        var snapshot = _tracker.Snapshot();

        Assert.Equal(new Uri("http://localhost:8888/cam1/index.m3u8"), snapshot.HlsUrl);
        Assert.Equal(1280, snapshot.Stream!.Width);
    }

    private TimeSpan _out = TimeSpan.Zero;

    private CameraStateTransition? Feed(TimeSpan outTime)
    {
        _out = outTime;
        return _tracker.OnProgress(new FfmpegProgress(outTime, 1.0, false));
    }

    private void GoOnline()
    {
        _tracker.OnConnecting();
        Feed(TimeSpan.FromSeconds(0.5));
        _time.Advance(TimeSpan.FromSeconds(0.5));
        Feed(TimeSpan.FromSeconds(1.0));
        Assert.Equal(CameraHealthState.Online, _tracker.State);
    }
}
