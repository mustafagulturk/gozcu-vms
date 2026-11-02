namespace Gozcu.LoadTest.Scenarios;

/// <summary>Bir yük senaryosu: her çağrıda bir istek tanımı üretir.</summary>
internal sealed class Scenario
{
    private readonly Func<ScenarioInput, RequestSpec> _next;

    public Scenario(string name, string description, DataNeeds needs, bool includedInAll, IReadOnlyList<string> labels, Func<ScenarioInput, RequestSpec> next)
    {
        Name = name;
        Description = description;
        Needs = needs;
        IncludedInAll = includedInAll;
        Labels = labels;
        _next = next;
    }

    public string Name { get; }

    public string Description { get; }

    public DataNeeds Needs { get; }

    /// <summary>"--scenario all" ile çalışır mı? Ağır senaryolar (dışa aktarma) dışarıda bırakılır.</summary>
    public bool IncludedInAll { get; }

    /// <summary>Raporda görünecek satırlar (karma senaryoda alt senaryolar).</summary>
    public IReadOnlyList<string> Labels { get; }

    public RequestSpec Next(ScenarioInput input) => _next(input);

    /// <summary>Senaryo verilen veriyle çalışabilir mi; çalışamıyorsa nedeni.</summary>
    public string? CannotRunReason(TargetData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (Needs.HasFlag(DataNeeds.Cameras) && data.CameraIds.Count == 0)
        {
            return "kayıt servisinden kamera listesi alınamadı (/api/playback/cameras boş ya da erişilemiyor)";
        }

        if (Needs.HasFlag(DataNeeds.Segments) && data.Segments.Count == 0)
        {
            return "son 24 saatte kayıt parçası bulunamadı";
        }

        if (Labels.Contains(ScenarioCatalog.SegmentMediaName, StringComparer.Ordinal) && data.SizedSegments.Count == 0)
        {
            return "hiçbir parçanın boyutu ölçülemedi (Range isteği başarısız)";
        }

        return null;
    }
}

/// <summary>Tanımlı senaryolar ve karma senaryonun ağırlıkları.</summary>
internal static class ScenarioCatalog
{
    public const string CamerasName = "cameras";
    public const string RecordingStatusName = "recording-status";
    public const string AnalysisStatusName = "analysis-status";
    public const string Timeline1hName = "timeline-1h";
    public const string Timeline24hName = "timeline-24h";
    public const string LocateName = "locate";
    public const string SegmentMediaName = "segment-media";
    public const string DetectionsName = "detections";
    public const string EventsName = "events";
    public const string EventSearchName = "event-search";
    public const string MixedName = "mixed";
    public const string ExportName = "export";

    /// <summary>
    /// Operatör panelini taklit eden karma yük ağırlıkları (toplam 100). Panel kamera durumunu ve zaman çizelgesini
    /// sık yeniler; oynatma sırasında parça medyası ve konum bulma gelir; analiz sorguları daha seyrektir.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, double Weight)> MixedWeights =
    [
        (CamerasName, 20),
        (Timeline1hName, 20),
        (SegmentMediaName, 20),
        (LocateName, 10),
        (AnalysisStatusName, 10),
        (RecordingStatusName, 5),
        (Timeline24hName, 5),
        (DetectionsName, 5),
        (EventSearchName, 5),
    ];

    public static readonly IReadOnlyList<Scenario> All = Build();

    public static IReadOnlyList<string> Names => All.Select(s => s.Name).ToList();

    /// <summary>"--scenario all" ile çalışacaklar: tekil senaryolar ve en sonda karma; dışa aktarma hariç.</summary>
    public static IReadOnlyList<string> AllNames => All.Where(s => s.IncludedInAll).Select(s => s.Name).ToList();

    public static Scenario Get(string name) =>
        All.FirstOrDefault(s => s.Name == name) ?? throw new ArgumentException($"Bilinmeyen senaryo '{name}'.", nameof(name));

    private static List<Scenario> Build()
    {
        var singles = new List<Scenario>
        {
            Single(CamerasName, "Canlı kamera durum listesi — GET /api/cameras (yayın servisi)", DataNeeds.None,
                _ => RequestFactory.CameraStatus(CamerasName)),
            Single(RecordingStatusName, "Kaydedici durumu — GET /api/recordings/status", DataNeeds.None,
                _ => RequestFactory.RecordingStatus(RecordingStatusName)),
            Single(AnalysisStatusName, "Analiz durumu — GET /api/analysis/status", DataNeeds.None,
                _ => RequestFactory.AnalysisStatus(AnalysisStatusName)),
            Single(Timeline1hName, "1 saatlik zaman çizelgesi — GET /api/playback/{kamera}/timeline?from&to", DataNeeds.Cameras,
                i => RequestFactory.Timeline(Timeline1hName, i, TimeSpan.FromHours(1))),
            Single(Timeline24hName, "24 saatlik zaman çizelgesi — GET /api/playback/{kamera}/timeline?from&to", DataNeeds.Cameras,
                i => RequestFactory.Timeline(Timeline24hName, i, TimeSpan.FromHours(24))),
            Single(LocateName, "Ana göre parça bulma — GET /api/playback/{kamera}/locate?time", DataNeeds.Cameras,
                i => RequestFactory.Locate(LocateName, i)),
            Single(SegmentMediaName, "Parça medyası, rastgele konumdan 1 MiB Range — GET /api/playback/segments/{id}/media", DataNeeds.Segments,
                i => RequestFactory.SegmentMedia(SegmentMediaName, i)),
            Single(DetectionsName, "Son bir saatin tespitleri — GET /api/analysis/detections?cameraId&from&to&limit", DataNeeds.Cameras,
                i => RequestFactory.Detections(DetectionsName, i)),
            Single(EventsName, "Son bir saatin olayları — GET /api/analysis/events?from&to&limit", DataNeeds.None,
                i => RequestFactory.Events(EventsName, i)),
            Single(EventSearchName, "Olay arama, son 24 saat, insan, en az 0,5 güven — GET /api/analysis/events/search", DataNeeds.None,
                i => RequestFactory.EventSearch(EventSearchName, i)),
        };

        var byName = singles.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var selector = new WeightedSelector<Scenario>(MixedWeights.Select(w => (byName[w.Name], w.Weight)));
        var mixed = new Scenario(
            MixedName,
            "Operatör paneli karışımı: " + string.Join(", ", MixedWeights.Select(w => $"{w.Name} %{w.Weight:0}")),
            selector.Items.Aggregate(DataNeeds.None, (acc, s) => acc | s.Needs),
            includedInAll: true,
            selector.Items.Select(s => s.Name).ToList(),
            i => selector.Next(i.Random).Next(i));

        var export = new Scenario(
            ExportName,
            "30 sn klip dışa aktarma (ağır; ffmpeg birleştirme) — GET /api/playback/{kamera}/export?from&to",
            DataNeeds.Segments,
            includedInAll: false,
            [ExportName],
            i => RequestFactory.Export(ExportName, i));

        return [.. singles, mixed, export];
    }

    private static Scenario Single(string name, string description, DataNeeds needs, Func<ScenarioInput, RequestSpec> next) =>
        new(name, description, needs, includedInAll: true, [name], next);
}
