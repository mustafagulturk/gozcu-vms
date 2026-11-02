# Gözcü yük testi (`Gozcu.LoadTest`)

Çalışan Gözcü sisteminin **okuma API'lerini** eşzamanlı yük altında ölçen, üçüncü parti bağımlılığı olmayan
HTTP yük üreticisi. Operatör panelinin yaptığı istekleri (kamera durumu, zaman çizelgesi, parça oynatma,
analiz sorguları) taklit eder; gecikme yüzdeliklerini, verimi ve hata dağılımını raporlar.

> Araç sistemi **başlatmaz**. Önce servisleri ayağa kaldırın (`docker compose up -d`) ve birkaç dakika
> kayıt birikmesini bekleyin; parça ve zaman çizelgesi senaryoları son 24 saatteki kayıtları kullanır.

## Hızlı başlangıç

```bash
# Varsayılan: karma (mixed) senaryo, 16 işçi, 5 sn ısınma + 30 sn ölçüm
dotnet run -c Release --project tools/Gozcu.LoadTest

# Tüm senaryolar sırayla (export hariç), her biri 20 sn; sonuçları dosyaya da yaz
dotnet run -c Release --project tools/Gozcu.LoadTest -- --scenario all --duration 20s \
    --output sonuc.json --markdown sonuc.md

# Tek senaryo, daha yüksek eşzamanlılık
dotnet run -c Release --project tools/Gozcu.LoadTest -- --scenario timeline-24h --concurrency 64

# Açık model: saniyede 200 istek, en fazla 64 istek aynı anda uçuşta
dotnet run -c Release --project tools/Gozcu.LoadTest -- --scenario mixed --rate 200 --concurrency 64

# Birden çok senaryo (virgülle) ve ağır dışa aktarma senaryosu
dotnet run -c Release --project tools/Gozcu.LoadTest -- --scenario segment-media,export --concurrency 4

# Servisler başka makinede
dotnet run -c Release --project tools/Gozcu.LoadTest -- --recording-url http://sunucu:5081 \
    --analysis-url http://sunucu:5082 --streaming-url http://sunucu:5080
```

Tüm seçenekler için `--help`, senaryo listesi için `--list`. Ölçümü her zaman `-c Release` ile çalıştırın;
Debug derlemesi yük üreticisini yavaşlatır.

| Seçenek | Varsayılan | Açıklama |
| --- | --- | --- |
| `--scenario` | `mixed` | Senaryo adı, virgülle ayrılmış adlar ya da `all` |
| `--duration` | `30s` | Ölçüm süresi (`500ms`, `30s`, `2m`, `1h`) |
| `--warmup` | `5s` | Ölçüme alınmayan ısınma (JIT, bağlantı havuzu, veritabanı önbelleği) |
| `--concurrency` | `16` | Kapalı modelde işçi sayısı; açık modelde uçuştaki en fazla istek |
| `--rate` | — | Verilirse açık model: saniyede bu kadar istek başlatılır |
| `--timeout` | `30s` | İstek zaman aşımı |
| `--streaming-url` | `http://localhost:5080` | Yayın servisi |
| `--recording-url` | `http://localhost:5081` | Kayıt servisi |
| `--analysis-url` | `http://localhost:5082` | Analiz servisi |
| `--output` | — | JSON sonuç dosyası |
| `--markdown` | — | Markdown tablo dosyası (README'ye ya da PR'a yapıştırmak için) |

## Senaryolar

| Ad | İstek | Not |
| --- | --- | --- |
| `cameras` | `GET /api/cameras` (yayın) | Canlı kamera sağlık listesi |
| `recording-status` | `GET /api/recordings/status` | Kaydedici durumları ve sayaçlar |
| `analysis-status` | `GET /api/analysis/status` | Analiz hattı durumu |
| `timeline-1h` | `GET /api/playback/{kamera}/timeline?from=&to=` | 1 saatlik pencere, son 24 saat içinde rastgele konum |
| `timeline-24h` | `GET /api/playback/{kamera}/timeline?from=&to=` | Son 24 saat (izin verilen en geniş aralık) |
| `locate` | `GET /api/playback/{kamera}/locate?time=` | Bilinen bir parçanın içinden rastgele an |
| `segment-media` | `GET /api/playback/segments/{id}/media` + `Range` | Rastgele konumdan 1 MiB (oynatıcının atlaması gibi) |
| `detections` | `GET /api/analysis/detections?cameraId=&from=&to=&limit=100` | Son bir saat, rastgele kamera |
| `events` | `GET /api/analysis/events?from=&to=&limit=100` | Son bir saat, tüm kameralar |
| `event-search` | `GET /api/analysis/events/search?category=Person&from=&to=&minConfidence=0.5&limit=50` | Olay arama sayfasının sorgusu, son 24 saat |
| `mixed` | yukarıdakilerin ağırlıklı karışımı | Operatör paneli yükü (aşağıda) |
| `export` | `GET /api/playback/{kamera}/export?from=&to=` | 30 sn klip; ffmpeg çalıştırır, **ağırdır**, `all`'a dahil değildir |

Kamera adları kayıt servisinin `GET /api/playback/cameras` yanıtından alınır. Parça kullanan senaryolar için
test başlamadan önce her kameranın son 24 saatlik zaman çizelgesi okunur ve rastgele 64 parçanın dosya boyutu
`Range: bytes=0-0` isteğiyle öğrenilir (yanıttaki `Content-Range` toplamı). Gerekli veri bulunamazsa senaryo
nedeni yazılarak atlanır.

`mixed` ağırlıkları (toplam 100): `cameras` 20, `timeline-1h` 20, `segment-media` 20, `locate` 10,
`analysis-status` 10, `recording-status` 5, `timeline-24h` 5, `detections` 5, `event-search` 5. Karma
koşuda her alt senaryo ayrı satırda, en altta `TOPLAM` satırıyla raporlanır.

## Yük modelleri

**Kapalı model** (varsayılan): `--concurrency` kadar işçi döngüde istek atar; biri yanıtı sonuna kadar okuyunca
sıradakini gönderir. "N eşzamanlı kullanıcıyla sistem saniyede en fazla kaç isteğe yanıt verebiliyor?"
sorusunu yanıtlar. Verim (`İstek/sn`) asıl çıktıdır.

**Açık model** (`--rate N`): istekler yanıtlardan bağımsız olarak saniyede N tane, sabit aralıklarla başlatılır.
"Saniyede N istek gelirken kullanıcılar ne kadar bekliyor?" sorusunu yanıtlar. Gecikme yüzdelikleri asıl
çıktıdır. Aynı anda uçuşta olabilecek istek sayısı `--concurrency` ile sınırlıdır; sınır doluyken zamanı gelen
istekler **düşürülür** ve ayrıca sayılır.

### Koordineli ihmal (coordinated omission)

Kapalı modelde sunucu 2 saniye takılırsa işçiler de bekler ve o 2 saniyede yalnızca N tane yavaş örnek
kaydedilir. Gerçekte kullanıcılar sunucuyu beklemeden istek göndermeye devam ederdi ve o sürede gelen yüzlerce
isteğin hepsi gecikirdi. Yani kapalı model kuyruk gecikmesini (p99, maks) **olduğundan iyi** gösterebilir.

Açık model bunu iki yolla önler:

1. Gecikme isteğin gönderildiği andan değil, **planlandığı andan** ölçülür. İstemci geride kalırsa bekleme süresi
   de gecikmeye eklenir.
2. Uçuştaki sınır dolu olduğu için başlatılamayan istekler sessizce atlanmaz, `Düşürülen` olarak raporlanır.
   Düşürülen sayısı sıfırdan büyükse sistem (ya da verilen `--concurrency`) o hızı kaldıramıyor demektir ve
   yüzdelikler iyimserdir: hızı düşürün ya da `--concurrency`'yi artırın.

## Sonuçları okumak

```
Senaryo: mixed (kapalı model, 16 işçi; ölçüm 30s, ısınma 5s)
Uç nokta          İstek  Hata  İstek/sn  Ort ms  p50 ms  p90 ms  p95 ms  p99 ms  Maks ms    MiB
----------------  -----  ----  --------  ------  ------  ------  ------  ------  -------  -----
cameras            6210     0     207.0    1.92    1.71    2.95    3.48    5.12    18.40    3.1
...
TOPLAM            31044     3    1034.8    7.41    3.02   18.20   24.75   61.31   212.66  6120.4
Hatalar: locate 404×3
```

- **İstek**: ölçüm penceresinde *başlayan* ve tamamlanan istekler (ısınmada başlayanlar sayılmaz). HTTP yanıtı
  alınamayan istekler (bağlantı hatası, zaman aşımı) da sayılır.
- **Hata**: 4xx/5xx yanıtlar ve taşıma hataları. `Hatalar:` satırı dağılımı verir: durum kodu (`404×3`),
  `zaman-aşımı` ya da `hata:ConnectionError` gibi. `206 Partial Content` başarılıdır.
- **İstek/sn**: istek sayısı / ölçüm süresi.
- **Ort ms**: ortalama gecikme. Gecikme dağılımı genellikle sağa çarpıktır; ortalama birkaç yavaş istekle
  kayar. Kararları ortalamaya değil yüzdeliklere göre verin.
- **p50** (medyan): isteklerin yarısı bundan hızlı. "Tipik" kullanıcı deneyimi.
- **p90 / p95**: her 10 / 20 istekten biri bundan yavaş. Bir panel açılışı onlarca istek yaptığından,
  kullanıcıların çoğu her sayfa yüklemesinde en az bir p95 gecikmesine denk gelir.
- **p99**: her 100 istekten biri bundan yavaş. Kuyruk gecikmesi; GC duraklamaları, bağlantı havuzu tükenmesi,
  veritabanı kilitleri, disk G/Ç burada görünür. p99 ile p50 arasındaki büyük fark kararsızlığa işaret eder.
- **Maks**: tek en kötü istek. Tek seferlik olaylardan (ilk bağlantı, JIT) etkilenir; tek başına güvenilir
  değildir ama p99'dan çok uzaksa incelenmeye değer.
- **MiB**: alınan yanıt gövdesi toplamı. `segment-media` için verim × 1 MiB'a yakın olmalı.

Yüzdelikler, HdrHistogram benzeri log-doğrusal bir histogramla hesaplanır: 128 µs'ye kadar değerler tamdır,
üstünde göreli hata en fazla ~%1,6'dır ve verilen değer kovanın üst sınırıdır (yani hafifçe kötümser).
`Maks` her zaman tamdır. Gecikme, istek gönderilmeden hemen önce başlar ve yanıt gövdesinin son baytı
okununca biter (`Stopwatch.GetTimestamp`).

### İpuçları

- Kapalı modelde `--concurrency`'yi kademeli artırın (1, 4, 16, 64). Verim artmayı bırakıp p99 hızla
  yükseldiği nokta sistemin doyma noktasıdır.
- Açık modelde hızı, kapalı modelde bulunan en yüksek verimin %50–80'i civarında seçin; doymaya yaklaştıkça
  kuyruklar ve p99 doğrusal olmayan biçimde büyür.
- Yük üreticisini ölçülen sistemle aynı makinede çalıştırmak CPU'yu paylaştırır; sonuçlar alt sınırdır.
- Karşılaştırma yaparken süre, ısınma, eşzamanlılık ve kayıtlı veri miktarını sabit tutun; JSON çıktısı
  sonradan karşılaştırmak için saklanabilir.

## Kod düzeni

| Dosya | İçerik |
| --- | --- |
| `LoadTestApp.cs` | Giriş noktası (`Main`); üst düzey deyim kullanılmaz, servislerin `Program` sınıflarıyla çakışmasın diye |
| `Cli/` | Elle yazılmış argüman ayrıştırıcı, süre biçimi, Türkçe yardım metni |
| `Metrics/` | Kilitsiz log-doğrusal histogram, uç nokta sayaçları |
| `Scenarios/` | Senaryo kataloğu, ağırlıklı seçici, gerçek rotalara göre istek üretimi |
| `Engine/` | Kapalı/açık model motoru, açık model zamanlayıcısı, hedef veri keşfi |
| `Reporting/` | Konsol tablosu, Markdown ve JSON çıktısı |

Saf mantık (ayrıştırma, histogram, seçim, istek üretimi, biçimlendirme) `tests/Gozcu.UnitTests/LoadTest/`
altında birim testleriyle doğrulanır.
