namespace Gozcu.Vision;

/// <summary>YOLOX ONNX tespit edicisinin ayarları.</summary>
public sealed class YoloxDetectorOptions
{
    /// <summary>ONNX model dosyasının yolu. <c>scripts/download-model.sh</c> ile indirilir.</summary>
    public string ModelPath { get; set; } = "models/yolox_tiny.onnx";

    /// <summary>Nesnellik × sınıf skoru için alt sınır (0..1).</summary>
    public float ConfidenceThreshold { get; set; } = 0.35f;

    /// <summary>Aynı sınıftan iki kutunun aynı nesne sayılacağı IoU eşiği (0..1).</summary>
    public float NmsThreshold { get; set; } = 0.45f;

    /// <summary>Bir karede döndürülecek en fazla tespit sayısı.</summary>
    public int MaxDetections { get; set; } = 100;

    /// <summary>
    /// Tek bir çıkarımın kullanacağı iş parçacığı sayısı. Varsayılan 1: birden çok kamera aynı oturumu paralel
    /// çağırdığında çekirdekler kameralar arasında paylaşılır. 0, ONNX Runtime'ın varsayılanı (tüm fiziksel çekirdekler) demektir.
    /// </summary>
    public int IntraOpThreads { get; set; } = 1;

    /// <summary>Geçersiz bir ayar varsa Türkçe açıklamalı <see cref="ArgumentException"/> fırlatır.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ModelPath))
        {
            throw new ArgumentException("Model dosyası yolu (ModelPath) boş olamaz.", nameof(ModelPath));
        }

        if (!(ConfidenceThreshold is > 0f and <= 1f))
        {
            throw new ArgumentException($"Güven eşiği (ConfidenceThreshold) (0, 1] aralığında olmalı; {ConfidenceThreshold} verildi.", nameof(ConfidenceThreshold));
        }

        if (!(NmsThreshold is > 0f and <= 1f))
        {
            throw new ArgumentException($"NMS eşiği (NmsThreshold) (0, 1] aralığında olmalı; {NmsThreshold} verildi.", nameof(NmsThreshold));
        }

        if (MaxDetections < 1)
        {
            throw new ArgumentException($"En fazla tespit sayısı (MaxDetections) en az 1 olmalı; {MaxDetections} verildi.", nameof(MaxDetections));
        }

        if (IntraOpThreads < 0)
        {
            throw new ArgumentException($"İş parçacığı sayısı (IntraOpThreads) negatif olamaz; {IntraOpThreads} verildi.", nameof(IntraOpThreads));
        }
    }
}
