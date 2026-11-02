namespace Gozcu.Contracts.Analysis;

/// <summary>Analiz servisinin olay ürettiği nesne kategorileri. COCO sınıfları bu kategorilere eşlenir.</summary>
public enum ObjectCategory
{
    /// <summary>COCO "person".</summary>
    Person,

    /// <summary>COCO "bicycle", "car", "motorcycle", "bus", "truck".</summary>
    Vehicle,
}
