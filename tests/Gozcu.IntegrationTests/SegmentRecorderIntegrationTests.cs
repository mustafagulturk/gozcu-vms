using Gozcu.IntegrationTests.Infrastructure;
using Gozcu.Media.Ffmpeg;
using Gozcu.Media.Processes;
using Gozcu.RecordingService.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gozcu.IntegrationTests;

[Collection(MediaMtxCollection.Name)]
public sealed class SegmentRecorderIntegrationTests(MediaMtxFixture mediaMtx) : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("gozcu-it-").FullName;

    [Fact]
    public async Task Records_playable_fixed_length_segments_without_reencoding()
    {
        await using var publisher = mediaMtx.CreatePublisher("record-test");
        publisher.Start();

        var segments = new List<CompletedSegment>();
        var recorder = new CameraRecorder("record-test", _directory, TimeSpan.FromSeconds(2), s => { lock (segments) { segments.Add(s); } }, TimeProvider.System);
        var spec = new ProcessSpec(
            "ffmpeg",
            FfmpegCommands.SegmentRecorder(mediaMtx.CameraUrl("record-test"), _directory, "record-test", TimeSpan.FromSeconds(2)))
        {
            Environment = FfmpegCommands.UtcEnvironment,
            GracefulStopInput = "q",
        };
        var supervisor = new ProcessSupervisor(
            new ProcessRunner(), new ReconnectPolicy { InitialDelay = TimeSpan.FromMilliseconds(500) }, TimeProvider.System, NullLogger.Instance);
        using var cts = new CancellationTokenSource();

        var run = supervisor.RunAsync(() => spec, recorder, cts.Token);
        await Eventually.TrueAsync(() => { lock (segments) { return segments.Count >= 3; } }, TimeSpan.FromSeconds(40), "en az 3 parça yazılmalı");

        // Nazik durdurma son (yarım) parçayı da geçerli bir MP4 olarak kapatmalı.
        cts.Cancel();
        await run;

        List<CompletedSegment> written;
        lock (segments)
        {
            written = [.. segments];
        }

        var before = DateTimeOffset.UtcNow;
        foreach (var segment in written)
        {
            Assert.True(File.Exists(segment.FilePath));
            Assert.True(segment.StartedAt <= before);
            var probe = await Ffprobe.ProbeAsync(segment.FilePath);
            Assert.Equal(["h264"], probe.VideoCodecs);
        }

        // İlk ve son parça hariç parçalar saat sınırına hizalı ve ~2 sn uzunluğundadır.
        foreach (var segment in written.Skip(1).SkipLast(1))
        {
            var probe = await Ffprobe.ProbeAsync(segment.FilePath);
            Assert.InRange(probe.DurationSeconds, 1.5, 2.5);
            Assert.Equal(0, segment.StartedAt.Second % 2);
        }

        // Dosya adları zamana göre sıralı ve benzersizdir.
        var names = written.Select(s => Path.GetFileName(s.FilePath)).ToList();
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
