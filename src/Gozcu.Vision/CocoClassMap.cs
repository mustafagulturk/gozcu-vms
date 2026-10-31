using Gozcu.Contracts.Analysis;

namespace Gozcu.Vision;

/// <summary>
/// COCO veri kümesinin 80 sınıfı ve bunlardan Gözcü'nün ilgilendiklerinin kategori eşlemesi. YOLOX modelleri
/// COCO üzerinde eğitildiği için çıktıdaki sınıf indeksleri bu sıradadır.
/// </summary>
public static class CocoClassMap
{
    /// <summary>COCO sınıf sayısı; model çıktısındaki sınıf skoru sayısı.</summary>
    public const int ClassCount = 80;

    private static readonly string[] Names =
    [
        "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat", "traffic light",
        "fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat", "dog", "horse", "sheep", "cow",
        "elephant", "bear", "zebra", "giraffe", "backpack", "umbrella", "handbag", "tie", "suitcase", "frisbee",
        "skis", "snowboard", "sports ball", "kite", "baseball bat", "baseball glove", "skateboard", "surfboard",
        "tennis racket", "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple",
        "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair", "couch",
        "potted plant", "bed", "dining table", "toilet", "tv", "laptop", "mouse", "remote", "keyboard", "cell phone",
        "microwave", "oven", "toaster", "sink", "refrigerator", "book", "clock", "vase", "scissors", "teddy bear",
        "hair drier", "toothbrush",
    ];

    /// <summary>COCO sınıf adları (İngilizce), indeks sırasıyla.</summary>
    public static IReadOnlyList<string> Labels => Names;

    /// <summary>
    /// Sınıfın Gözcü kategorisi: insan (0) → <see cref="ObjectCategory.Person"/>; bisiklet (1), otomobil (2),
    /// motosiklet (3), otobüs (5), kamyon (7) → <see cref="ObjectCategory.Vehicle"/>. Diğer sınıflar (ve geçersiz
    /// indeksler) için <c>null</c>; bu tespitler yok sayılır.
    /// </summary>
    public static ObjectCategory? GetCategory(int classIndex) => classIndex switch
    {
        0 => ObjectCategory.Person,
        1 or 2 or 3 or 5 or 7 => ObjectCategory.Vehicle,
        _ => null,
    };

    /// <summary>Sınıf Gözcü'nün izlediği bir kategoriye karşılık geliyorsa etiketini ve kategorisini verir.</summary>
    public static bool TryGetTarget(int classIndex, out string label, out ObjectCategory category)
    {
        if (GetCategory(classIndex) is { } found)
        {
            label = Names[classIndex];
            category = found;
            return true;
        }

        label = string.Empty;
        category = default;
        return false;
    }
}
