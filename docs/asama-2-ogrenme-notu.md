# Aşama 2 öğrenme notu: çoklu kamera ve geri oynatma

Bu not, 2. aşamada ne yaptığımızı, neden öyle yaptığımızı ve mülakatta bu konularda sorulabilecek soruları özetler.
1. aşamanın notu ayrıca duruyor; sonda hepsini birleştiren bir mülakat hazırlık dokümanı yazılacak.

## 1. Kısaca ne yaptık?

| Parça | Ne yapıyor | Neden önemli |
|---|---|---|
| 4 sahte kamera | MediaMTX `cam1…cam4` path'lerini farklı desen, çözünürlük ve fps ile yayınlıyor | Gerçek VMS'te kameralar farklı tiplerdedir; çoklu kamera kodu ancak birden fazla kamerayla sınanır |
| Canlı izleme ızgarası | 2×2 HLS oynatıcı, "4/4 kamera çevrimiçi" özeti, her kameradan geri oynatmaya bağlantı | Operatörün ilk gördüğü ekran |
| Saate göre geri oynatma | Zaman çizelgesi (kayıt var / boşluk), tıklayıp o ana gitme, parçadan parçaya kesintisiz oynatma, ±10 sn, 1×/2×/4× | İlandaki "kayıt sistemleri" işinin özü: "Dün 14:32'de ne oldu?" sorusunu yanıtlamak |
| Klip dışa aktarma | Seçilen aralığı **yeniden kodlamadan** tek MP4 olarak indirir | Delil/olay paylaşımı; CPU harcamadan saniyeler içinde |
| Açılış uzlaştırması | Çökme sonrası bozuk MP4'leri karantinaya alır, indekslenmemiş parçaları kurtarır, kaybolan dosyaların kayıtlarını kapatır | Elektrik kesintisinde bile disk ve veritabanı tutarlı kalır |

Uçtan uca doğrulama: `scripts/smoke-test.sh` 4 kamerayı, HLS'i, kaydı, zaman çizelgesini, HTTP Range'i, klip üretimini
ve kayıt servisini **SIGKILL ile öldürüp** uzlaştırmanın bozuk dosyaları karantinaya aldığını CI'da sınar.

## 2. Temel kavramlar

### MP4'ün yapısı: `moov` ve `mdat`
- MP4 kutulardan (box/atom) oluşur. `mdat` ham video verisidir, `moov` ise "içindekiler"dir: hangi kare hangi baytta,
  süre, kodek bilgisi.
- ffmpeg normal MP4 yazarken `mdat`'ı akıtır, `moov`'u **en sonda** yazar. Süreç aniden ölürse (SIGKILL, elektrik)
  `moov` hiç yazılmaz: dosya büyüktür ama oynatılamaz. ffprobe "moov atom not found" der.
- `-movflags +faststart` `moov`'u dosyanın başına taşır; tarayıcı dosyanın sonunu beklemeden oynatmaya başlar.
  Klip dışa aktarmada bunu kullanıyoruz.
- Alternatif: **fragmented MP4** (`frag_keyframe+empty_moov`). Her birkaç saniyede bir küçük `moof+mdat` yazılır; çökmede
  son fragmana kadar her şey kurtulur. Bu aşamada seçmedik (bkz. 4. bölüm).

### Anahtar kare (keyframe, I-frame) ve GOP
- H.264'te yalnızca anahtar kareler tek başına çözülebilir; aradaki kareler öncekilere göre farktır.
  GOP = iki anahtar kare arasındaki kare grubu. Sahte kameralarımızda GOP 2 sn (`-g = 2 × fps`).
- `-c copy` ile kesim yalnızca anahtar karelerde temiz olur. Bu yüzden klip, istenen andan **önceki** anahtar kareden
  başlayabilir (en fazla ~1 GOP). Kare hassasiyetinde kesim için yeniden kodlamak gerekir; bu CPU ve kalite bedelidir.
- Kayıt parçaları da anahtar karede başlar; segment muxer parçayı bir sonraki anahtar karede böler.

### HTTP Range istekleri (206 Partial Content)
- Tarayıcı `<video>` ile büyük bir dosyayı baştan sona indirmez; `Range: bytes=…` ile ihtiyaç duyduğu parçayı ister
  (ör. önce `moov`, sonra ileri sardığı yer). Sunucu `206 Partial Content` döner.
- ASP.NET Core'da `Results.File(..., enableRangeProcessing: true)` bunu sağlar. Duman testi 206 döndüğünü doğrular.

### Neden HLS değil de doğrudan MP4?
- Canlı izlemede HLS kullanıyoruz (MediaMTX üretiyor). Geri oynatmada parçalarımız düz MP4; HLS ise MPEG-TS veya
  fragmented MP4 parçaları ister. Her parçayı HLS'e çevirmek ek iş ve ek disk demekti.
- Bunun yerine tarayıcı parçaları sırayla oynatır. **Çift tamponlama (double buffering):** iki `<video>` öğesi var; biri
  oynarken diğeri bir sonraki parçayı önceden yükler, `ended` olayında yer değiştirirler. Geçiş fark edilmez.
- Duvar saati hesabı: `parça.başlangıç + video.currentTime`. Kayıtta `-reset_timestamps 1` kullandığımız için her parça
  0'dan başlar; parça başlangıcı da dosya adındaki UTC zamanıdır.

### Zaman çizelgesi, boşluklar ve tolerans
- `PlaybackPlanner.BuildTimeline` parçaları sıralar; iki parça arasında **1,5 sn'den** fazla boşluk varsa "kayıt yok"
  aralığı üretir. Tolerans gerekir, çünkü parça sınırları anahtar karelere denk gelir ve birkaç yüz ms oynar.
- Boşluk nedenleri: kamera kopması, servis yeniden başlaması, saklama politikasının sildiği eski kayıtlar.
- `Locate(t)`: t bir parçanın içindeyse o parça ve ofset; boşluktaysa bir sonraki parçanın başı.

### Concat demuxer ile yeniden kodlamasız klip
- ffmpeg'e şöyle bir liste verilir:
  ```
  ffconcat version 1.0
  file '/recordings/cam1/cam1_20261030T101500Z.mp4'
  inpoint 12.5
  file '/recordings/cam1/cam1_20261030T101600Z.mp4'
  outpoint 40
  ```
  ve `ffmpeg -f concat -safe 0 -i liste.txt -map 0:v:0 -c copy -movflags +faststart klip.mp4` çalıştırılır.
- 60 sn'lik klip 156 ms'de hazırlanıyor, çünkü hiçbir kare çözülmüyor; yalnızca baytlar kopyalanıyor.
- Dosya adındaki `'` karakteri `'\''` olarak kaçışlanıyor; satır sonu içeren yollar reddediliyor. Yoksa listeye
  yeni satır enjekte edilebilirdi (komut enjeksiyonunun küçük kardeşi).
- Boşluklar klipten çıkar: 110 sn'lik aralıkta 30 sn boşluk varsa klip ~80 sn olur.

### Yol geçişi (path traversal) denetimi
- Medya uç noktası dosya yolunu veritabanından alıyor. Yine de `SegmentFileResolver`, yolun `Path.GetFullPath`
  ile kayıt kökünün **içinde** kaldığını kontrol ediyor. Kök `/recordings` iken `/recordings-evil` gibi kardeş dizinleri
  de reddetmek için karşılaştırma ayırıcıyla biten önekle yapılıyor.
- "Veritabanına güveniyorum" demek yetmez; derinlemesine savunma (defense in depth).

### Açılış uzlaştırması (reconciliation)
Sorun: kayıt servisinin iki "gerçeklik kaynağı" var, disk ve veritabanı. Çökme bunları ayırır:
1. Yazılmakta olan parça `moov`'suz kalır (bozuk).
2. Bitmiş ama bellekteki kuyrukta bekleyen parçalar veritabanına hiç yazılmaz (indekste yok).
3. Dosyası elle ya da başka bir yolla silinmiş kayıtlar veritabanında kalır (hayalet kayıt).

Çözüm, `StartupReconciliationService`:
- **Anlık görüntü (snapshot) kayıt başlamadan alınır.** Hosted service'ler kayıt sırasına göre başlar;
  uzlaştırma `RecorderService`'ten önce kaydedildi. Snapshot sonra alınsaydı, yeni sürecin o an yazdığı (henüz `moov`'u
  olmayan) dosya da bozuk görünür ve yanlışlıkla karantinaya alınırdı. Bu aşamanın en önemli doğruluk noktası budur.
- Veritabanını beklemek kaydı bloke etmez: snapshot senkron, geri kalanı arka planda.
- Yalnızca veritabanında olmayan ya da boyutu farklı dosyalar ffprobe ile denetlenir. Boyutu tutan kayıt, ffmpeg'in
  düzgün kapattığı parçadır. 10 kamera × 30 gün = ~432 bin dosyayı her açılışta probe etmek saatler sürerdi.
- Kararı saf bir fonksiyon verir (`ReconciliationPlanner.Plan`): <1 KB → sil; oynatılabilir → indeksle ve olay yayınla;
  oynatılamaz → `_quarantine/` altına taşı; dosyası olmayan eski kayıt → silindi işaretle; probe başarısız → dokunma.
- **Karantina silmekten iyidir:** geri alınabilir, incelenebilir (untrunc gibi araçlarla kurtarma denenebilir).
  Saklama politikası karantinayı da tarar, yani disk kotasına sayılır ve zamanla silinir.
- **İdempotent:** ikinci çalıştırma hiçbir şey değiştirmez (testte doğrulanıyor). Yarıda kesilen uzlaştırma tekrar
  çalıştırılabilir.
- Her işlemden önce dosyanın boyutu ve değişiklik zamanı snapshot'takiyle karşılaştırılır (yarış durumuna karşı).
- Rapor: `GET /api/recordings/reconciliation`. Duman testinde SIGKILL sonrası örnek:
  `scanned 16, alreadyIndexed 12, quarantined 2, removedTiny 2, totalMs 309`.

### En az bir kez teslim ve idempotent tüketici
- Kurtarılan parçalar için `RecordingSegmentCompleted` olayı yayınlanıyor; aksi halde 3. aşamadaki analiz servisi o
  parçaları hiç görmezdi. Aynı parça için olay iki kez gelebilir (yeniden indeksleme, broker yeniden teslimi). Bu yüzden
  tüketiciler `SegmentId` ile tekilleştirme (dedupe) yapmalı.
- Kalan açık: veritabanına yazılıp olayı yayınlanamadan çöken parça yeniden yayınlanmaz. Kesin çözüm **transactional
  outbox**: olay, kayıtla aynı transaction'da bir `outbox` tablosuna yazılır, ayrı bir işçi onu yayınlar.
- Kurtarılan parçalar `SegmentQueue` üzerinden geçmiyor, çünkü kayıt gecikmesi ölçümü (`şimdi − parça bitişi`) saatlerce
  sapardı ve README'deki ölçüm tablosunu bozardı.

### Sınırlı paralellik
- ffprobe çağrıları `Parallel.ForEachAsync` ile en fazla 4 paralel; dışa aktarma `SemaphoreSlim` ile en fazla 2 eşzamanlı.
  Sınırsız paralellik, disk ve CPU'yu kilitleyip canlı kaydı aksatabilirdi. Kayıt her zaman önceliklidir.

### Zaman dilimleri
- Sunucu tarafında her şey UTC (dosya adları, veritabanı `timestamptz`, API). Tarayıcı yerel saati gösterir ama API'ye
  UTC gönderir. Sorgu dizgisinde `+03:00` yazarsanız `+` boşluğa dönüşür; duman testinde bu hatayı yaşadık ve zamanı
  `Z` biçimine çevirdik.

## 3. Koda nereden bakılır?

| Konu | Dosya |
|---|---|
| Sahte kameralar | `deploy/mediamtx/mediamtx.yml`, `deploy/mediamtx/fake-camera.sh`, `docker-compose.yml` (`x-cameras`) |
| Canlı izleme ızgarası | `src/Gozcu.StreamingService/wwwroot/index.html` |
| Geri oynatma API'si | `src/Gozcu.RecordingService/Playback/PlaybackEndpoints.cs` |
| Zaman çizelgesi ve konum bulma | `src/Gozcu.RecordingService/Playback/PlaybackPlanner.cs` |
| Klip planlama ve üretimi | `Playback/ClipPlanner.cs`, `Playback/ClipExporter.cs`, `src/Gozcu.Media/Ffmpeg/ConcatList.cs`, `ClipExportCommands.cs` |
| Yol geçişi denetimi | `src/Gozcu.RecordingService/Playback/SegmentFileResolver.cs` |
| Geri oynatma sayfası | `src/Gozcu.RecordingService/wwwroot/index.html` |
| Uzlaştırma | `src/Gozcu.RecordingService/Reconciliation/StartupReconciliationService.cs`, `ReconciliationPlanner.cs`, `ReconciliationReport.cs` |
| ffprobe ile doğrulama | `src/Gozcu.Media/Probing/FfprobeMediaProbe.cs` |
| Servis kayıt sırası | `src/Gozcu.RecordingService/Program.cs` (`AddStartupReconciliation` → `RecorderService`) |
| Testler | `tests/Gozcu.UnitTests/Playback/`, `tests/Gozcu.UnitTests/Reconciliation/`, `tests/Gozcu.IntegrationTests/PlaybackIntegrationTests.cs`, `ReconciliationIntegrationTests.cs`, `scripts/smoke-test.sh` |

## 4. Bilerek verdiğimiz kararlar ve bedelleri

| Karar | Kazanç | Bedel |
|---|---|---|
| Düz MP4 parçalar (fMP4 değil) | Her oynatıcıda ve concat'ta sorunsuz, ileri sarma kolay | Çökmede yazılan parça (en fazla 1 dk) kaybolur |
| Geri oynatmada HLS yerine doğrudan MP4 + çift tampon | Ek dönüştürme yok, Range ile sunucu yükü düşük | İstemci mantığı daha karmaşık |
| `-c copy` klip | Neredeyse sıfır CPU, kalite kaybı yok | Başlangıç anahtar kareye yuvarlanır |
| Klip önce geçici diske yazılır | `+faststart` ve doğru `Content-Length` | 1 saatlik klip için geçici alanda o kadar yer gerekir |
| Yalnızca şüpheli dosyaları probe etmek | Açılış birkaç yüz ms | Veritabanı kapalıyken bozuk dosya diskte bekler (zaten oynatılamaz) |
| Karantina (silme değil) | Geri alınabilir, incelenebilir | Disk kotasından yer yer |
| En yeni parça bir parça gecikmeli görünür | Yalnızca bitmiş, tutarlı dosyalar sunulur | "Şimdi" düğmesi 2 dk geriden başlar |

## 5. Mülakat soruları (kısa yanıtlarıyla)

1. **Kayıt sunucusu elektrik kesintisinde ne kaybeder, nasıl toparlanır?**
   Yazılmakta olan parçanın `moov`'u olmadığı için en fazla bir parça (60 sn). Açılışta ffprobe ile bulunup karantinaya
   alınır; bitmiş ama indekslenmemiş parçalar kurtarılır, olayları yayınlanır. fMP4 ile kayıp birkaç saniyeye inebilirdi.
2. **Uzlaştırmanın snapshot'ını neden kayıt başlamadan alıyorsun?** Yeni süreç dosya açtığında o dosyanın da `moov`'u
   henüz yoktur; sonradan alınan snapshot onu bozuk sanıp karantinaya alırdı.
3. **Yeniden kodlamadan klip keserken hassasiyet neden kare düzeyinde değil?** P/B kareler bağımsız çözülemez; kesim
   anahtar karede başlar. Kare hassasiyeti için baştaki GOP'u yeniden kodlamak (smart cut) ya da tamamını kodlamak gerekir.
4. **Tarayıcı 1 GB'lık bir MP4'ü nasıl ileri sarar?** HTTP Range ile; sunucu 206 döner. `moov` sondaysa tarayıcı önce
   sonu ister; `faststart` bunu tek isteğe indirir.
5. **Neden geri oynatmada HLS kullanmadın?** Parçalar düz MP4; HLS TS veya fMP4 ister. Çift tamponlu `<video>` ile ek
   dönüştürmeden kesintisiz oynatma sağlandı. Çok sayıda eşzamanlı izleyici veya uyarlanabilir bitrate gerekirse HLS/DASH'e geçilir.
6. **Zaman çizelgesinde boşluğu nasıl tespit ediyorsun, neden tolerans var?** Ardışık parçalar arasında 1,5 sn'den büyük
   fark boşluktur; parça sınırları anahtar karelere denk geldiği için milisaniyelik kaymalar normaldir.
7. **Veritabanından gelen dosya yolunu sunarken hangi güvenlik kontrolünü yaparsın?** Kanonik yolun kayıt kökünde
   kaldığını, ayırıcıyla biten önekle doğrularım (path traversal ve kardeş dizin tuzağı).
8. **Aynı olay iki kez gelirse ne olur?** RabbitMQ en az bir kez teslim eder; tüketici `SegmentId` ile tekilleştirir.
   Kesin "en az bir kez yayınlama" için transactional outbox kullanılır.
9. **Uzlaştırma idempotent mi? Nasıl test ettin?** Evet; Testcontainers ile gerçek PostgreSQL'e karşı iki kez çalıştırıp
   ikinci çalışmanın hiçbir şey değiştirmediğini ve olay yayınlamadığını doğruladık.
10. **400 bin kayıt dosyası varken açılış neden saatler sürmüyor?** Boyutu veritabanıyla tutan dosyalar probe edilmiyor;
    yalnızca şüpheliler (genelde kamera başına bir dosya) sınırlı paralellikle denetleniyor.
11. **4 kamerada CPU neden %3'ün altında?** Hiçbir yerde kod çözme yok: izleme, kayıt ve klip `-c copy`. Kod çözme
    yalnızca 3. aşamadaki analiz servisinde, saniyede birkaç kare için yapılacak.
12. **Saklama politikası ile karantina nasıl etkileşir?** Karantina da tarandığı için kotaya sayılır ve yaşa göre silinir;
    her kameranın en yeni parçası (yazılıyor olabilir) korunur.
13. **Klip dışa aktarma kaynakları nasıl korur?** En fazla 2 eşzamanlı dışa aktarma, en fazla 1 saat; istemci iptal
    ederse ffmpeg öldürülür ve geçici dizin silinir.

## 6. Kendin dene

```bash
SEGMENT_SECONDS=10 docker compose up -d --build
open http://localhost:5080            # 4 kamera
open http://localhost:5081            # geri oynatma
docker compose kill -s SIGKILL recording && docker compose up -d recording
curl -s http://localhost:5081/api/recordings/reconciliation   # quarantined > 0 görmelisin
./scripts/smoke-test.sh
```
