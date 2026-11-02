# Aşama 5 öğrenme notu: ölçümler, yük testleri ve dokümantasyon

Bu not, 5. aşamada ne yaptığımızı, neden öyle yaptığımızı ve mülakatta bu konularda sorulabilecek soruları özetler.
Önceki aşamaların notları ayrıca duruyor; hepsini birleştiren doküman: [mulakat-hazirlik.md](mulakat-hazirlik.md).

## 1. Kısaca ne yaptık?

| Parça | Ne yapıyor | Neden önemli |
|---|---|---|
| Sistem yük testi (`scripts/load-test.sh`) | Kamera sayısını adım adım artırır (4 → 64), her adımda servislerin CPU/belleğini ve işin yetişip yetişmediğini ölçer | "Bu sunucu kaç kamera kaldırır?" sorusunun tahmin değil ölçüm olan cevabı |
| HTTP yük üreticisi (`tools/Gozcu.LoadTest`) | Operatör panelinin yaptığı istekleri eşzamanlı üretir; p50/p95/p99 gecikme, verim, hata dağılımı | API'lerin yük altında nasıl davrandığını gösterir; bağımlılıksız, kendi histogramı |
| Mikro ölçümler (`benchmarks/Gozcu.Benchmarks`) | BenchmarkDotNet ile sıcak yollar: tensör hazırlama, YOLOX kod çözme, NMS, ONNX çıkarımı, ham kare okuma, planlayıcılar | Darboğazın kodda mı, donanımda mı olduğunu ayırır; bellek ayırma (allocation) sayılarını gösterir |
| Demo GIF (`scripts/demo/record-demo.js`) | Playwright ile sayfaları gezer, videoyu ffmpeg ile GIF'e çevirir | README'nin ilk 10 saniyesi; tekrar üretilebilir |
| Ölçüm tablosu ve bu not | README'de tek yerde tüm sayılar, ölçüm yöntemiyle birlikte | Mülakatta "ölçtüm" diyebilmek |

## 2. Temel kavramlar

### Ölçmeden optimize etme
Darboğaz tahmin edilmez, ölçülür. Ölçüm üç katmanda yapıldı; her biri farklı soruyu cevaplar:

| Katman | Soru | Araç |
|---|---|---|
| Mikro ölçüm | Bu fonksiyon kaç µs sürüyor, kaç bayt ayırıyor? | BenchmarkDotNet |
| API yük testi | 64 eşzamanlı kullanıcıda zaman çizelgesi p99 kaç ms? | `Gozcu.LoadTest` |
| Sistem yük testi | Bu makinede kaç kamera kaydedilip analiz edilebilir? | `load-test.sh` + `docker stats` |

Mikro ölçüm "NMS 1000 kutuda 30 µs" der ama sistemin sınırını söylemez: sistem sınırı CPU'nun nerede harcandığına bağlıdır.
Bizde sistem ölçümü bunu net gösterdi: **analiz CPU'sunun yarısına yakını model değil, video kod çözme.**

### Kapasite testi nasıl kurulur?
1. **Değişken tek olsun:** yalnızca kamera sayısı artar; video, fps, makine, süre aynı.
2. **Temiz başlangıç:** her adımda `docker compose down -v` ile boş veritabanı; önceki adımın birikimi ölçümü bozmasın.
3. **Isınma:** ilk 60 sn ölçülmez (JIT, bağlantı kurulumu, ffmpeg başlangıcı, model yükleme).
4. **Pencere sayaçları:** "toplam analiz edilen kare" sayacı pencerenin başında ve sonunda okunur, fark süreye bölünür.
   Ortalama hız servis başladığından beri değil, yalnızca pencerede hesaplanır.
5. **Başarı ölçütü önceden tanımlı:** tüm kameralar çevrimiçi ve kayıtta, beklenen parçaların ≥ %90'ı yazıldı, analiz hızı
   hedefin ≥ %90'ı. "Yavaşladı" gibi öznel bir yargı yok.
6. **Ölçüm aracı ölçüme karışmasın:** sahte kameralar videoyu `-c copy` ile yayınlar (kodlama yok); ölçüm sırasında
   derleme veya başka iş çalıştırılmaz. İlk 4 kamera ölçümünü arka planda derleme çalışırken aldığımız için makine CPU'su
   %70 çıktı; tekrarlayınca %47. Bu yüzden sonuç tablosunda "makine CPU" ile servislerin toplamını yan yana koyuyoruz.

### Yüzdelikler (p50, p95, p99) ve ortalamanın yalanı
- Ortalama, kuyruk gecikmesini gizler: 99 istek 10 ms, 1 istek 2 sn ise ortalama 30 ms'dir ama her 100 kullanıcıdan biri
  2 sn bekler. Operatör paneli sayfa başına onlarca istek yaptığı için p99'a takılma olasılığı yüksektir.
- **p50** tipik deneyim, **p95/p99** kuyruk, **max** tek seferlik aksaklık (GC, bağlantı kurma).
- Yüzdelik hesaplamak için tüm örnekleri saklamak yerine **log-doğrusal histogram** kullandık (HdrHistogram fikri):
  her ikinin kuvveti aralığı 64 kovaya bölünür, göreli hata en çok ~%1,6, bellek sabit, kayıt kilitsiz (`Interlocked`).

### Kapalı model, açık model ve koordineli ihmal (coordinated omission)
- **Kapalı model:** N işçi, biri bitince diğerini gönderir. Sunucu yavaşlarsa istek hızı da düşer; yük üreticisi
  sunucuya "nazikçe" uyar ve gecikme olduğundan iyi görünür. Kapasite (en çok req/s) ölçmek için uygundur.
- **Açık model (`--rate`):** istekler sabit hızla, sunucunun cevabını beklemeden gelir (gerçek kullanıcılar gibi).
  Gecikme isteğin **planlanan** zamanından ölçülür; böylece sunucu takıldığında sıraya giren isteklerin bekleme süresi
  de sayılır. Planlanan zamandan ölçmezsen takılma anındaki gecikmeler örneklemden düşer: buna **koordineli ihmal** denir.
- Uçuşta en çok `--concurrency` istek olabilir; başlatılamayan istekler "atılan" olarak sayılır, sessizce kaybolmaz.

### Yük testinin bulduğu hata: bağlantı havuzu tükenmesi
Açık modelde 800 istek/sn'de akış servisi bir kez HTTP 500 döndü. Günlükte: `Npgsql.PostgresException 53300: sorry, too
many clients already`.
- Npgsql her bağlantı dizesi için bir **bağlantı havuzu** tutar; varsayılan üst sınır **100**. Üç servis (akış, kayıt,
  analiz) aynı veritabanına bağlanıyor: kâğıt üzerinde 300 bağlantı açılabilir.
- PostgreSQL'in `max_connections` değeri varsayılan **100**. Her bağlantı PostgreSQL'de ayrı bir süreçtir (birkaç MB bellek),
  bu yüzden sınır düşüktür.
- Düşük yükte hata görünmez: havuz ancak eşzamanlı sorgu sayısı arttıkça büyür. Yük testi olmasa sahada, en yoğun anda çıkardı.
- Düzeltme: bağlantı dizesine `Maximum Pool Size=25` (3 × 25 = 75 < 100, yönetim araçlarına yer kalır). Tekrarında hata yok,
  en yüksek eşzamanlı bağlantı 70. Havuz dolarsa istek hata almaz, boş bağlantı için sırada **bekler** (zaman aşımına kadar);
  geri basınç veritabanına değil uygulamaya taşınır.
- Daha büyük ölçekte: PgBouncer gibi bir bağlantı havuzlayıcı (transaction pooling) ya da okuma kopyaları.

Açık model sonuçları da "dirsek noktasını" gösterdi: 200 → 600 istek/sn arası p99 22 → 97 ms ile yumuşak artıyor; 800'de
sistem doyuyor (gerçekleşen 731/sn), p99 356 ms'ye sıçrıyor ve uçuştaki 64 istek sınırı dolduğu için 2079 istek
başlatılamıyor. Kapasite planlamasında "en çok kaç istek" değil, "p99 hedefini bozmadan kaç istek" sorulur: burada ~600.

### BenchmarkDotNet neden? `Stopwatch` ile ölçsek olmaz mı?
- JIT ısınması, katmanlı derleme (tiered compilation), GC, CPU frekans değişimi tek `Stopwatch` ölçümünü anlamsız kılar.
  BenchmarkDotNet ayrı süreçte, ısınma ve çok tekrar yapıp istatistik verir (ortalama, hata payı, standart sapma).
- `[MemoryDiagnoser]` işlem başına ayrılan baytı ve Gen0 toplama sayısını verir. Kare başına çalışan kodda ayırma
  sıfıra yakın olmalı; aksi halde GC duraklamaları çıkarım süresine eklenir.
- `[Params]` ile girdi boyutu değiştirilir (NMS'de 50/200/1000 kutu); karmaşıklığın beklenen gibi büyüyüp büyümediği görülür.

### Darboğaz analizi: kamera başına maliyet modeli
Sistem ölçümünden çıkan basit model (4 vCPU, 1280×720 25 fps H.264):

| Bileşen | Kamera başına CPU | Not |
|---|---|---|
| Kayıt (`-c copy`) | ≈ %0,7 | 64 kamerada %46 |
| Canlı izleme durumu (`-c copy -f null`) | ≈ %0,7 | 64 kamerada %48 |
| Sahte kamera (MediaMTX, `-c copy`) | ≈ %1,7 | Gerçek kurulumda kameranın kendisi; sunucuda yok |
| Analiz: kod çözme (tüm 25 kare) | ≈ %8 | fps'ten bağımsız! |
| Analiz: çıkarım | fps × çıkarım süresi | 2 fps × 42 ms ≈ %8,4 tek başına; yükte çıkarım 75–130 ms'ye uzar |

Sonuç: kayıt tarafı kamera sayısıyla doğrusal ve ucuzdur; 4 vCPU 64 kamerayı makinenin %43'ü ile kaydeder. Sınırı analiz
belirler. Fps'i yarıya indirmek (2 → 1) 16 kamerayı sığdırdı ama analiz CPU'su neredeyse aynı kaldı (%304 → %302), çünkü
kod çözme fps'ten bağımsız. Bir sonraki iyileştirmenin nereye yapılacağını bu gösterir: donanım kod çözücü (VAAPI/NVDEC/
Quick Sync), alt akış (kameranın 640×360 ikinci yayını) veya yalnızca anahtar kareleri çözmek.

### Geri basınç sayesinde zarif bozulma
16 kamera 2 fps'te analiz hedefin yarısına (15,7 fps) düştü ama sistem çökmedi: kanal kapasitesi 1 ve DropOldest olduğu
için eski kareler atıldı, her kamera hâlâ saniyede ~1 kare analiz edildi, bellek sınırlı kaldı, kayıt hiç etkilenmedi
(192/197 parça). Kuyruk olsaydı gecikme sınırsız büyür ve sonunda bellek biterdi. Mülakatta "aşırı yükte ne olur?"
sorusunun cevabı bu tablodur.

### Kayıt gecikmesi neden kamera sayısından bağımsız?
Parça bitişinden veritabanında aranabilir olmasına kadar geçen süre 4 kamerada da 64 kamerada da ort. ~0,5 sn, en çok
~1 sn. Gecikmenin çoğu ffmpeg tarafında: parça yalnızca anahtar karede kapatılabilir (örnek videolarda GOP 2 sn) ve
bitiş zamanı "dosya adındaki başlangıç + medya süresi" ile hesaplandığı için kapanışın beklenmesi gecikmeye eklenir.
Veritabanı ve indeksleyici darboğaz değil (PostgreSQL CPU'su %1). Gecikmenin yükle artmaması, ölçeklenmenin sağlıklı
olduğunun işareti; artsaydı ilk bakılacak yer bağlantı havuzu ve indeksleme kuyruğu olurdu.

### Demo GIF nasıl küçük kalır?
Playwright 1280×720 WebM kaydeder; ffmpeg iki geçişte GIF yapar: önce videonun renklerinden 256 renklik ortak palet
(`palettegen`), sonra bu paletle kodlama (`paletteuse`). 8 fps ve 960 px genişlik dosyayı birkaç MB'ta tutar. GIF'i
betikle üretmek, arayüz değişince "yeniden çek" işini tek komuta indirir.

## 3. Koda nereden bakılır?

| Dosya | Ne var |
|---|---|
| `scripts/load-test.sh` | N kameralık MediaMTX yapılandırması ve compose override üretimi, örnekleme, başarı ölçütü, rapor |
| `tools/Gozcu.LoadTest/Engine/LoadEngine.cs` | Kapalı ve açık model, koordineli ihmal açıklaması |
| `tools/Gozcu.LoadTest/Metrics/LatencyHistogram.cs` | Kilitsiz log-doğrusal histogram, yüzdelik hesabı |
| `tools/Gozcu.LoadTest/Scenarios/` | Gerçek uç noktalara karşılık gelen senaryolar, ağırlıklı seçim |
| `benchmarks/Gozcu.Benchmarks/Vision/` | Tensör, kod çözme, NMS, ONNX çıkarımı (1/2/4 iş parçacığı) |
| `benchmarks/Gozcu.Benchmarks/Recording/` | 1440 parçalık günde zaman çizelgesi, klip planı; 100 bin parçada saklama planı |
| `scripts/demo/record-demo.js` | Playwright gezintisi + ffmpeg palet ile GIF |
| `docs/olcumler/` | Ham ölçüm sonuçları (JSONL ve Markdown) |
| `tests/Gozcu.UnitTests/LoadTest/` | Histogram, argüman ayrıştırma, senaryo seçimi ve rapor biçimi testleri |

## 4. Bilerek verdiğimiz kararlar ve bedelleri

| Karar | Neden | Bedeli / sonraki adım |
|---|---|---|
| Yük üreticisini kendimiz yazdık (k6/NBomber yok) | Bağımlılık yok, CI'da ve her makinede `dotnet run`; mülakatta histogram ve koordineli ihmali anlatabilmek | Dağıtık yük (birden çok makine) yok. Gerçek kapasite testinde k6/NBomber/Gatling tercih edilir |
| Sistem testi bash + `docker stats` | Her şey Compose'da; ek ajan (Prometheus) gerekmez | 5 sn örnekleme kaba; uzun süreli izleme için OpenTelemetry metrikleri + Prometheus/Grafana |
| Sahte kameralar aynı makinede | Tek komutla tekrar üretilebilir | MediaMTX CPU'su sonuçlara karışır; tabloda ayrı sütun olarak gösterildi |
| Sınır ölçütü "hedefin %90'ı" | Basit ve önceden tanımlı | Gerçek sistemde SLO ile tanımlanır (ör. "kamera başına ≥ 1,8 fps, p99 analiz gecikmesi < 2 sn") |
| Yük testleri CI'da çalışmıyor | Paylaşımlı CI makinelerinde sayılar gürültülü; süre uzun | Mikro ölçümler için gece çalışan ayrı iş ve gerileme eşiği eklenebilir |
| 10 sn'lik parçalarla yük testi | Kısa pencerede daha çok parça = indeksleyiciye daha çok yük, daha çok örnek | Gerçek kullanımda 60 sn; dosya sayısı 6 kat az |

## 5. Mülakat soruları (kısa yanıtlarıyla)

1. **Sistemin kaç kamera kaldırdığını nasıl buldun?**
   Kamera sayısını 4'ten 64'e çıkaran bir yük testi yazdım. Her adımda temiz başlatma, 60 sn ısınma, 120 sn ölçüm;
   başarı ölçütü önceden tanımlı. 4 vCPU'da tam sistem 2 fps'te 8 kamera, 1 fps'te 16 kamera; yalnızca kayıt 64 kamerada
   makinenin %43'ü.
2. **Darboğaz neydi?**
   Analiz servisi. Kamera başına ~%8 CPU sadece videoyu çözmek için gidiyor (25 karenin hepsi çözülüyor, sonra 2'si seçiliyor);
   üstüne çıkarım. Fps'i düşürmek kod çözmeyi azaltmıyor. Çözüm donanım kod çözücü, alt akış ya da GPU çıkarımı.
3. **Yük altında çıkarım neden 42 ms'den 128 ms'ye çıktı?**
   CPU paylaşımı: ONNX Runtime iş parçacıkları kod çözen ffmpeg süreçleri ve diğer servislerle aynı 4 çekirdeği paylaşıyor.
   Mikro ölçüm tek başına süreyi, sistem testi gerçek süreyi verir; ikisi farklı sorular.
4. **Aşırı yükte sistem ne yapıyor?**
   Zarif bozulma: analiz eski kareleri atıyor (DropOldest), her kamera yine ~1 fps analiz ediliyor, bellek sınırlı, kayıt
   etkilenmiyor. Atılan kare oranı `/api/analysis/status`'ta; alarm buna kurulabilir.
5. **p99 neden önemli? Ortalama yetmez mi?**
   Ortalama kuyruğu gizler. Panel sayfa başına çok istek yaptığı için kullanıcıların çoğu en az bir kez p99'a denk gelir.
6. **Koordineli ihmal nedir?**
   Yük üreticisi sunucu yavaşlayınca istek göndermeyi de yavaşlatırsa takılma anındaki gecikmeler ölçülmez. Çözüm: açık
   model ve gecikmeyi planlanan gönderim zamanından ölçmek.
7. **Yüzdelikleri nasıl hesapladın?**
   Log-doğrusal histogram: sabit bellek, kilitsiz kayıt, ~%1,6 göreli hata. Milyonlarca örneği saklayıp sıralamak gerekmez.
8. **Mikro ölçümde neye bakıyorsun?**
   Ortalama süre, hata payı ve işlem başına ayrılan bellek. Kare başına çalışan kodda ayırmanın sıfıra yakın olması GC
   duraklamalarını önler. Örnek: 416×416 tensör doldurma ve kutu normalizasyonu 0 B, ham kare okuma 2 B (kare başına).
9. **Neden ONNX çıkarımını 1/2/4 iş parçacığıyla ölçtün?**
   Tek kare için çok iş parçacığı gecikmeyi düşürür ama çok kamerada toplam verimi düşürebilir: iş parçacıkları
   birbirinin ve kod çözmenin önüne geçer. Bu yüzden servis "kamera başına az iş parçacığı + sınırlı eşzamanlı çıkarım"
   (InferenceGate) ile çalışıyor.
10. **100 kameraya nasıl çıkarsın?**
    Kayıt: tek sunucu yüzlerce kamera, sınır disk ve ağ (4 Mbps × 100 = 400 Mbps, günde ~4 TB). Analiz: kameraları
    birden çok analiz örneğine paylaştırmak (yatay ölçekleme), GPU, donanım kod çözme, batch çıkarım. Hepsi ölçümle doğrulanır.
11. **Yük testi sana ne buldu?**
    Bağlantı havuzu tükenmesi: Npgsql'in varsayılan havuzu (100) × 3 servis, PostgreSQL'in `max_connections`'ını (100)
    aşabiliyordu. 800 istek/sn'de "too many clients" ile 500 döndü. `Maximum Pool Size=25` ile düzelttim, tekrar ölçtüm:
    hata yok, en çok 70 bağlantı. Büyük ölçekte PgBouncer.
12. **Yük testleri neden CI'da yok?**
    Paylaşımlı CI makinelerinde sayılar gürültülü ve test uzun. CI'da doğruluk (birim, entegrasyon, duman) testleri var;
    performans ayrı, sabit donanımda ve elle/gece çalıştırılır.
13. **Bu ölçümlere ne kadar güvenirsin?**
    Tek makine, tek tekrar, sahte kameralar aynı makinede. Eğilim güvenilir (doğrusal kayıt, analiz darboğazı),
    mutlak sayılar donanıma bağlı. Bu yüzden betik ve ham sonuçlar repoda: başka makinede tek komutla tekrarlanır.

## 6. Kendin dene

```bash
./scripts/download-sample-videos.sh && docker compose build
./scripts/load-test.sh                                   # 4 8 12 16 kamera, tam sistem
CAMERAS="16 32" SERVICES="streaming recording" ./scripts/load-test.sh
ANALYSIS_FPS=1 CAMERAS=16 ./scripts/load-test.sh

docker compose up -d   # birkaç dakika kayıt birikmesini bekle
dotnet run -c Release --project tools/Gozcu.LoadTest -- --scenario all --duration 20s
dotnet run -c Release --project tools/Gozcu.LoadTest -- --scenario mixed --rate 200 --concurrency 64
dotnet run -c Release --project benchmarks/Gozcu.Benchmarks -- --filter '*NonMaxSuppression*'
```

Deneyler:
- `ANALYSIS_MAX_CONCURRENCY=1` ve `=4` ile 12 kamerayı tekrar ölç: analiz hızı nasıl değişiyor?
- Yük testi `--rate` ile açık modelde çalışırken `docker compose pause postgres` yap, 3 sn sonra `unpause`: p99 ve max
  nasıl değişti? Aynısını kapalı modelde yap; farkı koordineli ihmalle açıkla.
- `OnnxYoloxDetectorBenchmarks` sonuçlarına bak: 4 iş parçacığı tek karede kaç kat hızlı? Neden 4 kat değil?
