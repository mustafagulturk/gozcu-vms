using System.Text;
using Gozcu.LoadTest.Scenarios;

namespace Gozcu.LoadTest.Cli;

internal static class HelpText
{
    public static string Usage() =>
        """
        Gözcü yük testi — çalışan sistemin okuma API'lerini eşzamanlı yük altında ölçer.

        Kullanım:
          dotnet run -c Release --project tools/Gozcu.LoadTest -- [seçenekler]

        Seçenekler:
          --scenario <ad|all>     Senaryo adı, virgülle ayrılmış adlar ya da "all" (varsayılan: mixed).
                                  "all" dışa aktarmayı (export) içermez.
          --duration <süre>       Ölçüm süresi (varsayılan: 30s). Biçim: 500ms, 30s, 2m, 1h.
          --warmup <süre>         Ölçüme alınmayan ısınma süresi (varsayılan: 5s).
          --concurrency <n>       Kapalı modelde işçi sayısı; açık modelde uçuştaki en fazla istek (varsayılan: 16).
          --rate <n>              Açık model: saniyede n istek başlat. Verilmezse kapalı model.
          --timeout <süre>        İstek zaman aşımı (varsayılan: 30s).
          --streaming-url <url>   Yayın servisi (varsayılan: http://localhost:5080).
          --recording-url <url>   Kayıt servisi (varsayılan: http://localhost:5081).
          --analysis-url <url>    Analiz servisi (varsayılan: http://localhost:5082).
          --output <dosya>        Sonuçları JSON olarak yaz.
          --markdown <dosya>      Sonuçları Markdown tablo olarak yaz.
          --list                  Senaryoları listele.
          -h, --help              Bu yardımı göster.

        Örnekler:
          dotnet run -c Release --project tools/Gozcu.LoadTest -- --scenario all --duration 20s
          dotnet run -c Release --project tools/Gozcu.LoadTest -- --scenario timeline-24h --concurrency 32
          dotnet run -c Release --project tools/Gozcu.LoadTest -- --scenario mixed --rate 200 --concurrency 64 --markdown sonuc.md

        """ + Scenarios();

    public static string Scenarios()
    {
        var sb = new StringBuilder("Senaryolar:\n");
        foreach (var s in ScenarioCatalog.All)
        {
            sb.Append("  ").Append(s.Name.PadRight(18)).Append(s.Description);
            if (!s.IncludedInAll)
            {
                sb.Append(" [all'a dahil değil]");
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }
}
