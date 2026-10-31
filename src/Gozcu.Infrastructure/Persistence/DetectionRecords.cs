using Gozcu.Contracts.Analysis;

namespace Gozcu.Infrastructure.Persistence;

/// <summary>
/// Bir karedeki tek tespit; detection_frames.detections jsonb dizisinin bir elemanı. Kutu kaynak görüntüye göre
/// 0..1 aralığında normalize edilmiştir: <c>{"label":"car","category":"Vehicle","confidence":0.91,"x":0.1,"y":0.2,"w":0.3,"h":0.4}</c>.
/// </summary>
public sealed record StoredDetection(string Label, ObjectCategory Category, float Confidence, float X, float Y, float W, float H);

/// <summary>Veritabanına yazılacak, en az bir tespit içeren analiz edilmiş kare.</summary>
public sealed record DetectionFrameRecord(
    string CameraId,
    DateTimeOffset CapturedAt,
    float InferenceMs,
    IReadOnlyList<StoredDetection> Detections);

/// <summary>Veritabanından okunan analiz edilmiş kare.</summary>
public sealed record DetectionFrame(
    long Id,
    string CameraId,
    DateTimeOffset CapturedAt,
    float InferenceMs,
    IReadOnlyList<StoredDetection> Detections);

/// <summary>Bir kategorinin bir kamerada görünmeye başlayıp kaybolduğu aralık. <see cref="EndedAt"/> null ise sürüyor.</summary>
public sealed record DetectionEventRecord(
    Guid Id,
    string CameraId,
    ObjectCategory Category,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int MaxCount,
    float PeakConfidence,
    int FrameCount);
