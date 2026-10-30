namespace Gozcu.Media.Ffmpeg;

/// <summary>Kayıt parçalarından klip üreten ffmpeg komutları.</summary>
public static class ClipExportCommands
{
    /// <summary>
    /// Concat listesindeki parçaları yeniden kodlamadan (-c copy) tek bir MP4'e birleştirir.
    /// Kopyalama modunda kesim yalnızca anahtar karelerde yapılabildiğinden ffmpeg, listedeki ilk
    /// "inpoint"ten önceki anahtar kareden başlar: klip istenen başlangıçtan en fazla bir GOP
    /// (anahtar kare aralığı, tipik olarak 1–2 sn) önce başlayabilir. Bitiş de benzer şekilde
    /// paket sınırına yuvarlanır. -movflags +faststart, tarayıcıda indirme bitmeden oynatmayı sağlar.
    /// </summary>
    public static IReadOnlyList<string> Concat(string listPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        return
        [
            "-hide_banner",
            "-nostats",
            "-nostdin",
            "-loglevel", "error",
            "-f", "concat",
            "-safe", "0",
            "-i", listPath,
            "-map", "0:v:0",
            "-c", "copy",
            "-movflags", "+faststart",
            "-y",
            outputPath,
        ];
    }
}
