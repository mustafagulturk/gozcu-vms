using Gozcu.Contracts.Analysis;
using Gozcu.Vision;

namespace Gozcu.AnalysisService.Events;

public enum DetectionEventChange
{
    /// <summary>Kategori art arda yeterli karede görüldü; olay açıldı.</summary>
    Started,

    /// <summary>Açık olay yeni bir karede yeniden görüldü (sayılar güncellendi).</summary>
    Updated,

    /// <summary>Kategori <see cref="DetectionEventOptions.EndAfter"/> boyunca görülmedi ya da analiz durdu; olay kapandı.</summary>
    Ended,
}

/// <summary>Bir olayın anlık durumu.</summary>
/// <param name="LastSeenAt">Kategorinin olay eşiğini geçen bir tespitle son görüldüğü kare zamanı.</param>
/// <param name="EndedAt">Kapandıysa bitiş (son görülme) zamanı.</param>
/// <param name="ObjectCount">Son görüldüğü karedeki nesne sayısı.</param>
/// <param name="Confidence">Son görüldüğü karedeki en yüksek güven.</param>
/// <param name="FrameCount">Kategorinin görüldüğü kare sayısı (olayı açan ardışık kareler dahil).</param>
public sealed record DetectionEventState(
    Guid Id,
    string CameraId,
    ObjectCategory Category,
    DateTimeOffset StartedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? EndedAt,
    int ObjectCount,
    int MaxCount,
    float Confidence,
    float PeakConfidence,
    int FrameCount);

public sealed record DetectionEventTransition(DetectionEventChange Change, DetectionEventState Event);

/// <summary>
/// Bir kameranın kare kare tespitlerinden kategori başına olay (başladı / güncellendi / bitti) üretir.
/// Saf mantıktır: G/Ç yapmaz, saati kendisi okumaz; zaman damgaları karelerden gelir. İş parçacığı güvenli değildir;
/// kameranın çıkarım döngüsü tarafından sırayla çağrılır.
/// </summary>
/// <remarks>
/// Kurallar (kategoriler birbirinden bağımsız):
/// <list type="bullet">
/// <item>Bir karede kategoriye ait ve güveni <see cref="DetectionEventOptions.MinConfidence"/> üstünde en az bir tespit varsa kare "nitelikli"dir.</item>
/// <item>Olay, art arda <see cref="DetectionEventOptions.MinConsecutiveFrames"/> nitelikli kareden sonra açılır; başlangıcı ilk nitelikli karedir.
/// Arada niteliksiz bir kare gelirse sayaç sıfırlanır (tek karelik yanlış alarmlar olay üretmez).</item>
/// <item>Açık olay, son görülmesinden bu yana <see cref="DetectionEventOptions.EndAfter"/> geçtiğinde kapanır; bitiş son görülme anıdır.</item>
/// <item>Kapanan kategori yeniden görülürse yeni kimlikle yeni bir olay başlar.</item>
/// </list>
/// </remarks>
public sealed class DetectionEventTracker
{
    private static readonly ObjectCategory[] Categories = Enum.GetValues<ObjectCategory>();

    private readonly string _cameraId;
    private readonly DetectionEventOptions _options;
    private readonly Func<Guid> _newId;
    private readonly Dictionary<ObjectCategory, CategoryState> _states;

    public DetectionEventTracker(string cameraId, DetectionEventOptions options, Func<Guid>? newId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraId);
        ArgumentNullException.ThrowIfNull(options);
        _cameraId = cameraId;
        _options = options;
        _newId = newId ?? Guid.NewGuid;
        _states = Categories.ToDictionary(c => c, _ => new CategoryState());
    }

    /// <summary>Şu an açık olaylar.</summary>
    public IReadOnlyList<DetectionEventState> OpenEvents =>
        Categories.Select(c => _states[c].Open).Where(o => o is not null).Select(o => ToState(o!, null)).ToList();

    /// <summary>Bir karenin tespitlerini işler ve oluşan geçişleri döner (aynı karede bir kategori için önce Ended, sonra Started gelebilir).</summary>
    public IReadOnlyList<DetectionEventTransition> Process(DateTimeOffset timestamp, IReadOnlyList<Detection> detections)
    {
        ArgumentNullException.ThrowIfNull(detections);
        var transitions = new List<DetectionEventTransition>();
        foreach (var category in Categories)
        {
            var count = 0;
            var best = 0f;
            foreach (var detection in detections)
            {
                if (detection.Category == category && detection.Confidence >= _options.MinConfidence)
                {
                    count++;
                    best = Math.Max(best, detection.Confidence);
                }
            }

            Step(_states[category], category, timestamp, count, best, transitions);
        }

        return transitions;
    }

    /// <summary>
    /// Açık tüm olayları son görüldükleri anla kapatır ve olgunlaşmamış adayları atar.
    /// Analiz durdurulurken ya da akış koptuğunda çağrılır.
    /// </summary>
    public IReadOnlyList<DetectionEventTransition> CloseAll()
    {
        var transitions = new List<DetectionEventTransition>();
        foreach (var category in Categories)
        {
            var state = _states[category];
            state.Pending = null;
            if (state.Open is { } open)
            {
                transitions.Add(new DetectionEventTransition(DetectionEventChange.Ended, ToState(open, open.LastSeenAt)));
                state.Open = null;
            }
        }

        return transitions;
    }

    private void Step(CategoryState state, ObjectCategory category, DateTimeOffset timestamp, int count, float best, List<DetectionEventTransition> transitions)
    {
        // Olay uzun süredir görülmüyorsa kapat; akış takılıp uzun bir aradan sonra gelen nitelikli kare de yeni olay sayılır.
        if (state.Open is { } open && timestamp - open.LastSeenAt >= _options.EndAfter)
        {
            transitions.Add(new DetectionEventTransition(DetectionEventChange.Ended, ToState(open, open.LastSeenAt)));
            state.Open = null;
        }

        // Aday ardışık olmalı: niteliksiz kare ya da uzun bir boşluk sayacı sıfırlar.
        if (state.Pending is { } pending && (count == 0 || timestamp - pending.LastSeenAt >= _options.EndAfter))
        {
            state.Pending = null;
        }

        if (count == 0)
        {
            return;
        }

        if (state.Open is { } current)
        {
            current.See(timestamp, count, best);
            transitions.Add(new DetectionEventTransition(DetectionEventChange.Updated, ToState(current, null)));
            return;
        }

        state.Pending ??= new Track(Guid.Empty, category, timestamp);
        state.Pending.See(timestamp, count, best);
        if (state.Pending.FrameCount >= _options.MinConsecutiveFrames)
        {
            var opened = state.Pending;
            opened.Id = _newId();
            state.Open = opened;
            state.Pending = null;
            transitions.Add(new DetectionEventTransition(DetectionEventChange.Started, ToState(opened, null)));
        }
    }

    private DetectionEventState ToState(Track track, DateTimeOffset? endedAt) => new(
        track.Id,
        _cameraId,
        track.Category,
        track.StartedAt,
        track.LastSeenAt,
        endedAt,
        track.ObjectCount,
        track.MaxCount,
        track.Confidence,
        track.PeakConfidence,
        track.FrameCount);

    private sealed class CategoryState
    {
        /// <summary>Henüz yeterli ardışık karede görülmemiş aday.</summary>
        public Track? Pending { get; set; }

        public Track? Open { get; set; }
    }

    private sealed class Track(Guid id, ObjectCategory category, DateTimeOffset startedAt)
    {
        public Guid Id { get; set; } = id;

        public ObjectCategory Category { get; } = category;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public DateTimeOffset LastSeenAt { get; private set; } = startedAt;

        public int ObjectCount { get; private set; }

        public int MaxCount { get; private set; }

        public float Confidence { get; private set; }

        public float PeakConfidence { get; private set; }

        public int FrameCount { get; private set; }

        public void See(DateTimeOffset timestamp, int count, float confidence)
        {
            LastSeenAt = timestamp;
            ObjectCount = count;
            Confidence = confidence;
            MaxCount = Math.Max(MaxCount, count);
            PeakConfidence = Math.Max(PeakConfidence, confidence);
            FrameCount++;
        }
    }
}
