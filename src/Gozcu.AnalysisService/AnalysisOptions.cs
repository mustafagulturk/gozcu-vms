namespace Gozcu.AnalysisService;

/// <summary>"Analysis" yapılandırma bölümü.</summary>
public sealed class AnalysisOptions
{
    public const string SectionName = "Analysis";

    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Kamera başına saniyede analiz edilecek kare sayısı. ffmpeg akışı bu hıza seyreltir.</summary>
    public double FramesPerSecond { get; set; } = 2;

    /// <summary>YOLOX ONNX model dosyası.</summary>
    public string ModelPath { get; set; } = "models/yolox_tiny.onnx";

    /// <summary>Bu güvenin altındaki kutular tespit edici tarafından atılır.</summary>
    public float ConfidenceThreshold { get; set; } = 0.35f;

    /// <summary>Örtüşme bastırma (NMS) için IoU eşiği.</summary>
    public float NmsThreshold { get; set; } = 0.45f;

    /// <summary>Bir karede döndürülecek en fazla tespit.</summary>
    public int MaxDetections { get; set; } = 100;

    /// <summary>
    /// Tüm kameralar için aynı anda çalışabilecek en fazla çıkarım (inference). Çıkarım CPU'ya bağlıdır;
    /// bu sınır kameralar arttıkça çekirdeklerin aşırı paylaşılmasını önler.
    /// </summary>
    public int MaxConcurrentInferences { get; set; } = 2;

    /// <summary>Tek bir çıkarımın kullanacağı ONNX Runtime iş parçacığı sayısı. 0: ONNX Runtime varsayılanı.</summary>
    public int IntraOpThreads { get; set; } = 2;

    public TimeSpan SocketTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan ReconnectInitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Tespit karelerinin veritabanında tutulacağı süre. Olaylar silinmez.</summary>
    public TimeSpan FrameRetention { get; set; } = TimeSpan.FromDays(7);

    public DetectionEventOptions Events { get; set; } = new();

    /// <summary>Ayarları doğrular; hatalı her değer için bir mesaj döner.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (FramesPerSecond is <= 0 or > 30)
        {
            errors.Add($"Analysis:FramesPerSecond 0'dan büyük ve en fazla 30 olmalı (şu an {FramesPerSecond}).");
        }

        if (string.IsNullOrWhiteSpace(ModelPath))
        {
            errors.Add("Analysis:ModelPath boş olamaz.");
        }

        if (string.IsNullOrWhiteSpace(FfmpegPath))
        {
            errors.Add("Analysis:FfmpegPath boş olamaz.");
        }

        if (ConfidenceThreshold is <= 0 or >= 1)
        {
            errors.Add($"Analysis:ConfidenceThreshold 0 ile 1 arasında olmalı (şu an {ConfidenceThreshold}).");
        }

        if (NmsThreshold is <= 0 or >= 1)
        {
            errors.Add($"Analysis:NmsThreshold 0 ile 1 arasında olmalı (şu an {NmsThreshold}).");
        }

        if (MaxDetections < 1)
        {
            errors.Add("Analysis:MaxDetections en az 1 olmalı.");
        }

        if (MaxConcurrentInferences < 1)
        {
            errors.Add("Analysis:MaxConcurrentInferences en az 1 olmalı.");
        }

        if (IntraOpThreads < 0)
        {
            errors.Add("Analysis:IntraOpThreads negatif olamaz.");
        }

        if (SocketTimeout <= TimeSpan.Zero)
        {
            errors.Add("Analysis:SocketTimeout pozitif olmalı.");
        }

        if (ReconnectInitialDelay <= TimeSpan.Zero || ReconnectMaxDelay < ReconnectInitialDelay)
        {
            errors.Add("Analysis:ReconnectInitialDelay pozitif ve ReconnectMaxDelay'den büyük olmamalı.");
        }

        if (FrameRetention <= TimeSpan.Zero)
        {
            errors.Add("Analysis:FrameRetention pozitif olmalı.");
        }

        errors.AddRange(Events.Validate());
        return errors;
    }
}

/// <summary>"Analysis:Events" bölümü: karelerdeki tespitlerden olay (başladı/bitti) üretme eşikleri.</summary>
public sealed class DetectionEventOptions
{
    /// <summary>Olaya sayılacak tespitin en düşük güveni. Tespit edicinin eşiğinden yüksek tutulur.</summary>
    public float MinConfidence { get; set; } = 0.5f;

    /// <summary>Olayın açılması için kategorinin art arda görülmesi gereken kare sayısı (tek karelik yanlış alarmlara karşı).</summary>
    public int MinConsecutiveFrames { get; set; } = 2;

    /// <summary>Kategori bu süre boyunca görülmezse olay kapanır; bitiş zamanı son görüldüğü andır.</summary>
    public TimeSpan EndAfter { get; set; } = TimeSpan.FromSeconds(5);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (MinConfidence is < 0 or > 1)
        {
            errors.Add($"Analysis:Events:MinConfidence 0 ile 1 arasında olmalı (şu an {MinConfidence}).");
        }

        if (MinConsecutiveFrames < 1)
        {
            errors.Add("Analysis:Events:MinConsecutiveFrames en az 1 olmalı.");
        }

        if (EndAfter <= TimeSpan.Zero)
        {
            errors.Add("Analysis:Events:EndAfter pozitif olmalı.");
        }

        return errors;
    }
}
