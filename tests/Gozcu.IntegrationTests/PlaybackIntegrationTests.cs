using System.Diagnostics;
using Gozcu.Infrastructure.Persistence;
using Gozcu.IntegrationTests.Infrastructure;
using Gozcu.Media.Ffmpeg;
using Gozcu.Media.Processes;
using Gozcu.RecordingService.Playback;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gozcu.IntegrationTests;

/// <summary>Ana makinedeki ffmpeg ile üretilen parçalardan klip dışa aktarma. Veritabanı gerekmez.</summary>
public sealed class PlaybackIntegrationTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 10, 15, 0, TimeSpan.Zero);

    private readonly string _root = Directory.CreateTempSubdirectory("gozcu-it-playback-").FullName;

    [Fact]
    public async Task Exports_clip_spanning_three_segments_without_reencoding()
    {
        // 2 sn'lik, saniyede bir anahtar kareli üç ardışık parça (10:15:00, 10:15:02, 10:15:04).
        var directory = Path.Combine(_root, "cam1");
        Directory.CreateDirectory(directory);
        var segments = new List<RecordingSegment>();
        for (var i = 0; i < 3; i++)
        {
            var start = T0.AddSeconds(i * 2);
            var path = Path.Combine(directory, SegmentFileName.Format("cam1", start));
            await GenerateSegmentAsync(path);
            segments.Add(new RecordingSegment(i + 1, "cam1", path, start, start.AddSeconds(2), new FileInfo(path).Length));
        }

        // İlk parçanın ortasından son parçanın ortasına: 10:15:01 – 10:15:05 (4 sn).
        var from = T0.AddSeconds(1);
        var to = T0.AddSeconds(5);
        var resolver = new SegmentFileResolver(_root);
        var entries = ClipPlanner.Plan(segments, from, to, s => resolver.Resolve(s.FilePath));
        Assert.Equal(3, entries.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), entries[0].InPoint);
        Assert.Equal(TimeSpan.FromSeconds(1), entries[2].OutPoint);

        using var exporter = new ClipExporter(new ProcessRunner(), "ffmpeg", NullLogger<ClipExporter>.Instance);
        string tempDirectory;
        await using (var clip = await exporter.ExportAsync(entries, CancellationToken.None))
        {
            tempDirectory = clip.DirectoryPath;
            Assert.True(clip.Length > 0);
            Assert.False(File.Exists(Path.Combine(tempDirectory, "list.txt")), "liste dosyası silinmeli");

            var probe = await Ffprobe.ProbeAsync(clip.Name);
            Assert.Equal(["h264"], probe.VideoCodecs);

            // -c copy kesimi anahtar kareye yuvarlar: klip en fazla bir GOP (1 sn) uzun olabilir.
            Assert.InRange(probe.DurationSeconds, 3.5, 5.6);
        }

        Assert.False(Directory.Exists(tempDirectory), "geçici dizin akış kapatılınca silinmeli");
    }

    [Fact]
    public async Task Failed_export_cleans_up_and_throws()
    {
        var missing = Path.Combine(_root, "cam1", "yok.mp4");
        using var exporter = new ClipExporter(new ProcessRunner(), "ffmpeg", NullLogger<ClipExporter>.Instance);
        var before = Directory.GetDirectories(Path.GetTempPath(), "gozcu-export-*").Length;

        await Assert.ThrowsAsync<ClipExportException>(() => exporter.ExportAsync([new ConcatEntry(missing)], CancellationToken.None));

        Assert.True(Directory.GetDirectories(Path.GetTempPath(), "gozcu-export-*").Length <= before);
    }

    private static async Task GenerateSegmentAsync(string path)
    {
        var startInfo = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25",
                     "-t", "2", "-c:v", "libx264", "-g", "25", "-pix_fmt", "yuv420p", path,
                 })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"ffmpeg test parçası üretemedi: {error}");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
