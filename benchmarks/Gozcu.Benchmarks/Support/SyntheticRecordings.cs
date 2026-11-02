using Gozcu.Infrastructure.Persistence;
using Gozcu.Media.Ffmpeg;

namespace Gozcu.Benchmarks.Support;

/// <summary>Geri oynatma ve klip planlayıcı ölçümleri için gerçekçi, tekrarlanabilir kayıt parçası listeleri.</summary>
internal static class SyntheticRecordings
{
    /// <summary>Ölçümlerin kullandığı günün başlangıcı (UTC).</summary>
    public static readonly DateTimeOffset DayStart = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Bir kameranın 24 saatlik, 1 dakikalık parçalardan oluşan kaydı (1440 parça), veritabanının döndürdüğü gibi
    /// başlangıca göre sıralı. ffmpeg parçaları anahtar karede böldüğünden bitişler birkaç yüz milisaniye oynar;
    /// her 97. parçada kamera bağlantısı kopmuş gibi parça erken biter, böylece zaman çizelgesinde boşluklar oluşur.
    /// </summary>
    public static RecordingSegment[] OneDayOfMinuteSegments(string cameraId = "cam1")
    {
        const int count = 24 * 60;
        var random = new Random(42);
        var segments = new RecordingSegment[count];
        for (var i = 0; i < count; i++)
        {
            var start = DayStart.AddMinutes(i);
            var duration = i % 97 == 50
                ? TimeSpan.FromSeconds(20)
                : TimeSpan.FromSeconds(60) - TimeSpan.FromMilliseconds(random.Next(0, 400));
            var fileName = SegmentFileName.Format(cameraId, start);
            segments[i] = new RecordingSegment(
                i + 1, cameraId, $"{cameraId}/2026-10-30/{fileName}", start, start + duration, 7_500_000 + random.Next(0, 500_000));
        }

        return segments;
    }
}
