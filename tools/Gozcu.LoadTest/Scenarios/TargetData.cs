namespace Gozcu.LoadTest.Scenarios;

/// <summary>Hangi servise istek atılacağı; temel adres ayarlardan seçilir.</summary>
internal enum ServiceTarget
{
    Streaming,
    Recording,
    Analysis,
}

/// <summary>Bir senaryonun istek üretebilmek için önceden keşfedilmesi gereken veriler.</summary>
[Flags]
internal enum DataNeeds
{
    None = 0,
    Cameras = 1,
    Segments = 2,
}

/// <summary>Zaman çizelgesinden keşfedilen bir kayıt parçası. Boyut, Range isteğiyle ölçülmüşse doludur.</summary>
internal sealed record SegmentInfo(long Id, string CameraId, DateTimeOffset Start, DateTimeOffset End, long? SizeBytes = null);

/// <summary>Test başlamadan önce çalışan sistemden okunan kamera ve parça listesi.</summary>
internal sealed record TargetData(IReadOnlyList<string> CameraIds, IReadOnlyList<SegmentInfo> Segments)
{
    public static readonly TargetData Empty = new([], []);

    /// <summary>Boyutu bilinen parçalar (Range isteği üretmek için gerekli).</summary>
    public IReadOnlyList<SegmentInfo> SizedSegments { get; } = Segments.Where(s => s.SizeBytes > 0).ToList();
}

/// <summary>Üretilmiş tek bir isteğin saf tanımı; HttpRequestMessage'a motor çevirir.</summary>
/// <param name="Label">Raporlardaki satır adı (senaryo adı).</param>
/// <param name="PathAndQuery">Kök göreli yol ve sorgu, ör. "/api/analysis/status".</param>
/// <param name="RangeStart">Verilirse "Range: bytes=RangeStart-RangeEnd" başlığı eklenir.</param>
internal sealed record RequestSpec(string Label, ServiceTarget Target, string PathAndQuery, long? RangeStart = null, long? RangeEnd = null);

/// <summary>Bir isteği üretmek için gerekenler: keşfedilen veri, rastgele kaynak ve "şimdi".</summary>
internal sealed record ScenarioInput(TargetData Data, Random Random, DateTimeOffset Now);
