using Gozcu.AnalysisService.Events;
using Gozcu.Vision;

namespace Gozcu.AnalysisService.Pipeline;

/// <summary>
/// Borudan okunmuş, analiz sırasını bekleyen kare.
/// </summary>
/// <param name="Data">Model boyutunda, letterbox uygulanmış BGR24 kare.</param>
/// <param name="CapturedAt">
/// Karenin kameradan sunucuya geliş anı (UTC). ffmpeg'in paket zaman damgalarından hesaplanır (bkz.
/// <see cref="Gozcu.Media.Frames.FrameClock"/>); kod çözme gecikmesini içermez ve kayıt parçalarının başlangıcıyla
/// aynı saattedir. Hesaplanamazsa karenin borudan okunduğu an kullanılır.
/// </param>
/// <param name="Geometry">Kutuları kaynak görüntüye geri eşlemek için letterbox geometrisi.</param>
public sealed record CapturedFrame(byte[] Data, DateTimeOffset CapturedAt, LetterboxGeometry Geometry);

/// <summary>Analiz edilmiş bir karenin sonucu.</summary>
public sealed record AnalyzedFrame(
    string CameraId,
    DateTimeOffset CapturedAt,
    double InferenceMs,
    LetterboxGeometry Geometry,
    IReadOnlyList<Detection> Detections);

/// <summary>
/// Çıkarım döngüsünden veritabanı yazıcısına giden iş. <see cref="Frame"/> yalnızca en az bir tespit varsa doludur
/// (boş kareler saklanmaz); olay geçişleri ayrıca taşınır.
/// </summary>
public sealed record AnalysisOutput(AnalyzedFrame? Frame, IReadOnlyList<DetectionEventTransition> Transitions);
