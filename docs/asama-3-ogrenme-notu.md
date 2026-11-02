# Aşama 3 öğrenme notu: nesne tespiti

Bu not, 3. aşamada ne yaptığımızı, neden öyle yaptığımızı ve mülakatta bu konularda sorulabilecek soruları özetler.
1. ve 2. aşamaların notları ayrıca duruyor; sonda hepsini birleştiren bir mülakat hazırlık dokümanı yazılacak.

## 1. Kısaca ne yaptık?

| Parça | Ne yapıyor | Neden önemli |
|---|---|---|
| Analiz servisi (`:5082`) | Her kameradan saniyede 2 kare çözer, YOLOX ile insan ve araç arar | İlandaki "video **analiz** sistemleri" kısmı; sistemde videoyu çözen tek yer |
| `Gozcu.Vision` kütüphanesi | Tensör hazırlama, ONNX Runtime çıkarımı, YOLOX çıktısını kutulara çevirme, NMS | Modelden bağımsız, ASP.NET'e bağımlı olmayan, birim testli çekirdek |
| Tespit metaverisi | En az bir nesne olan her kare PostgreSQL'e JSONB olarak, toplu yazılır | 4. aşamada "dün 14:00–15:00 arası cam2'de araç" sorgusu bunun üzerine kurulacak |
| Olaylar | Art arda karelerden "insan göründü / kayboldu" olayları üretir, RabbitMQ'ya yayınlar | Operatör kareyle değil olayla ilgilenir; zaman çizelgesi işaretleri bunlar |
| Analiz sayfası | Son analiz edilen kare + kutular, sayaçlar, son olaylar | Sistemi gözle doğrulamak ve demo |

Uçtan uca doğrulama: `scripts/smoke-test.sh` CI'da Intel örnek videolarını indirir, sistemi Compose ile kaldırır ve
4 kameranın analiz edildiğini, insan ve araç olaylarının oluşup RabbitMQ'ya yayınlandığını sınar.

## 2. Temel kavramlar

### Nesne tespiti ne üretir?
- Sınıflandırma "bu resimde kedi var" der. **Tespit** her nesne için bir **kutu** (x, y, genişlik, yükseklik), bir
  **sınıf** ve bir **güven skoru** üretir.
- Modelimiz COCO veri kümesiyle eğitilmiş: 80 sınıf. Bize gereken 6'sı: `person` → İnsan; `bicycle`, `car`, `motorcycle`,
  `bus`, `truck` → Araç (`CocoClassMap`). Gerisi (köpek, sandalye…) atılır.
- Kutuları **0..1 aralığında normalize** saklıyoruz (kaynak görüntüye göre). Böylece web panel videoyu hangi boyutta
  gösterirse göstersin kutu doğru yere düşer, kamera çözünürlüğü değişse bile eski kayıtlar bozulmaz.

### YOLO ailesi ve YOLOX
- YOLO ("You Only Look Once") tek geçişte (single-stage) tespit yapar: görüntüyü bir ızgaraya böler, her hücre için
  kutu ve sınıf tahmin eder. İki aşamalı yöntemlerden (Faster R-CNN) çok daha hızlıdır, gerçek zamanlı video için standarttır.
- **YOLOX** anchor-free bir YOLO türevidir. 416×416 girişte 3 ölçekte ızgara vardır: adım (stride) 8, 16 ve 32 →
  52², 26² ve 13² hücre = **3549** satır. Her satırda 85 değer: 4 kutu + 1 nesnellik (objectness) + 80 sınıf skoru.
- Modelin çıktısı ham haldedir; kutuya çevirmek bizim işimiz (`YoloxDecoder`):
  - merkez: `cx = (o0 + ızgara_x) × adım`, `cy = (o1 + ızgara_y) × adım`
  - boyut: `w = e^(o2) × adım`, `h = e^(o3) × adım`
  - skor: `nesnellik × sınıf skoru`, en yüksek sınıf seçilir
- Model boyları: **nano** (3,6 MB, 16 ms) · **tiny** (20 MB, 42 ms) · **s** (36 MB, 112 ms). Örnek karede nano insanı
  ve bisikleti kaçırdı; tiny hepsini buldu. Varsayılan **tiny**: doğruluk/hız dengesi.

### Letterbox (en-boy oranını koruyarak sığdırma)
- Model 416×416 kare bekler, kamera 1280×720'dir. Germek nesnelerin şeklini bozar ve doğruluğu düşürür.
- Letterbox: ölçek = min(416/1280, 416/720) = **0,325** → görüntü 416×234 olur, sol üste yerleşir, kalan alan gri (114)
  ile doldurulur. Model eğitimde de böyle gördüğü için doğru sonuç verir.
- Kutuyu geri çevirirken içerik alanına (416×234) bölünür: `x_norm = x_model / 416`, `y_norm = y_model / 234`
  (`LetterboxGeometry`). Tamamen dolguda kalan kutular atılır.
- Bu işi **ffmpeg** yapıyor: `fps=2,scale=416:416:force_original_aspect_ratio=decrease,pad=416:416:0:0:color=0x727272`
  ve `-pix_fmt bgr24 -f rawvideo pipe:1`. Ölçekleme C ile, SIMD ile yapılır; .NET tarafı yalnızca baytları okur.

### Tensör düzeni: HWC → CHW, BGR
- ffmpeg pikselleri satır satır verir: `B G R B G R …` (**HWC**: yükseklik, genişlik, kanal).
- Model **CHW** bekler: önce tüm B düzlemi, sonra G, sonra R. `FrameTensor.FillChw` bu dönüşümü yapar.
- Bu YOLOX sürümü OpenCV ile eğitildiği için **BGR** sırası ve **0..255 ham değer** ister (/255 ya da ortalama/sapma
  normalizasyonu yok). Ön işlemeyi eğitimdekiyle birebir aynı yapmamak, hatasız çalışıp **sessizce kötü sonuç veren**
  en yaygın hatadır; bu yüzden Python referansıyla karşılaştırdık.

### NMS (Non-Maximum Suppression)
- Model aynı nesne için komşu hücrelerden birçok örtüşen kutu üretir.
- NMS: kutuları güvene göre sırala, en iyisini al, onunla **IoU** (kesişim/birleşim) > 0,45 olan aynı sınıftaki kutuları
  at, tekrarla. Sınıf bazında yapıyoruz: bisikletin üstündeki insan, bisiklet kutusunu silmemeli.
- İki eşik var, karıştırılmamalı: **güven eşiği** (0,35: zayıf adayları baştan eler) ve **NMS IoU eşiği** (0,45:
  ne kadar örtüşmeye "aynı nesne" diyeceğimiz).

### ONNX ve ONNX Runtime
- **ONNX**, eğitim çerçevesinden (PyTorch) bağımsız model biçimidir. Model Python'da eğitilir, ONNX'e aktarılır,
  C#/C++/Java'da çalıştırılır. Üretimde Python'a bağımlılık kalmaz.
- **ONNX Runtime** (`Microsoft.ML.OnnxRuntime`) bu modeli çalıştırır. CPU dışında CUDA, TensorRT, OpenVINO gibi
  "execution provider"lar takılabilir; kod değişmeden donanım değişir.
- `InferenceSession` pahalıdır (model yükleme, grafik optimizasyonu): **bir kez** oluşturulur, `Run` iş parçacığı
  güvenlidir ve tüm kameralar paylaşır. `IntraOpNumThreads` tek çıkarımın kaç çekirdek kullanacağını belirler.

### Model lisansı
- Ultralytics YOLOv8/11 **AGPL-3.0**'dır: ağ üzerinden hizmet veren yazılımın tüm kaynağını açmayı gerektirir.
  Kapalı kaynak bir savunma ürününde kabul edilemez (alternatifi ücretli lisans).
- YOLOX **Apache-2.0**'dır: ticari kullanım serbest, yalnızca bildirim korunur. Örnek videolar ve test karesi Intel'in
  **CC BY 4.0** lisanslı videolarıdır; atıf `samples/videos/README.md` ve `tests/.../TestData/README.md` içinde.
- Mülakatta "model seçerken neye baktın?" sorusuna doğruluk ve hızın yanında **lisansı** söylemek fark yaratır.

### Geri basınç (back-pressure): en taze kare
- Kare kaynağı (ffmpeg) sabit hızda üretir. Çıkarım bazen yavaşlar (CPU başka işe gider). Araya sınırsız bir kuyruk
  koyarsak kuyruk büyür ve **gecikme sürekli artar**: 10 dk sonra 3 dk önceki kareyi analiz ediyor oluruz.
- Çözüm: kapasitesi 1 olan, `BoundedChannelFullMode.DropOldest` kipindeki bir `Channel`. Yeni kare gelince bekleyen
  eski kare atılır (ve sayılır). Çıkarım her zaman **en taze** kareyi işler. Canlı analizde eski kare değersizdir.
- ffmpeg'in stdout'unu her zaman okumak da şart: okumayı bırakırsak işletim sistemi borusu (pipe, ~64 KB) dolar,
  ffmpeg yazamaz, RTSP bağlantısı zaman aşımına düşer.
- Ayrıca tüm kameralar için **eşzamanlı çıkarım sınırı** var (`InferenceGate`, SemaphoreSlim, varsayılan 2). Sınır
  olmasa 16 kamera aynı anda 16 çıkarım başlatır, hepsi yavaşlar ve CPU önbelleği çöker.

### Kareden olaya: debounce ve histerezis
- Tek kare gürültülüdür: bir gölge bir kare "insan" görünebilir, gerçek insan bir kare kaçabilir.
- `DetectionEventTracker` kamera × kategori başına küçük bir durum makinesidir:
  - %50 üzeri güvenle **art arda 2 kare** görülünce olay açılır (debounce: tek karelik yanlış pozitif olay olmaz),
  - **5 sn** hiç görülmezse kapanır; bitiş zamanı **son görüldüğü an**dır (histerezis: bir kare kaçınca olay bölünmez),
  - açıkken en fazla nesne sayısı, en yüksek güven ve kare sayısı izlenir.
- Takipçi saf (I/O yok, zaman parametre olarak gelir), bu yüzden birim testleri anında ve kesin çalışır.

### Ne yazılır, ne yayınlanır?
- **Kareler** (`detection_frames`): yalnızca en az bir tespit olan kareler, tespitler JSONB dizisi olarak. Boş kareler
  yazılmaz; 4 kamera × 2 fps her şeyi yazsa günde ~700 bin satır olurdu. Saklama süresi 7 gün, saatlik silinir.
- **Olaylar** (`detection_events`): açılış ve kapanışta yazılır ve RabbitMQ'ya yayınlanır
  (`analysis.detection.started` / `analysis.detection.ended`). Aradaki güncellemeler yalnızca veritabanına ve en fazla
  2 sn'de bir gider; ara güncellemeyle mesaj kuyruğunu doldurmayız.
- **Toplu yazma**: `DetectionWriter` tek tüketicidir, 200 satır ya da 1 sn dolunca tek komutla yazar. Satır başına bir
  `INSERT` yerine toplu yazma bağlantı ve işlem (transaction) yükünü azaltır.
- Veritabanı çökerse analiz durmaz: yazıcı geri çekilmeyle yeniden dener, tampon dolarsa kareleri atar ve sayar.

### JSONB mi, ayrı tablo mu?
- Her tespiti ayrı satır yapmak (kare başına 1–5 satır) sorgu açısından en esnek yoldur ama satır sayısını katlar.
- JSONB ile kare başına bir satır yazılır; PostgreSQL JSONB'yi ikili ve sorgulanabilir saklar
  (`detections @> '[{"category":"Person"}]'`, gerekirse GIN indeksi).
- Asıl arama olay tablosu üzerinden yapılacak (kamera + zaman indeksi). Kare tablosu "o anda kutular neredeydi"
  sorusu için. Ayrıca zamana göre silme için `captured_at` üzerinde **BRIN** indeksi var: zaman sıralı eklenen büyük
  tablolarda B-tree'nin yüzde biri boyutunda.

### Çökme sonrası açık olaylar
- Servis aniden ölürse açık olayların `ended_at`'i NULL kalır, sonsuza kadar "sürüyor" görünür.
- Yazıcı açılışta bunları kapatır: o kategorinin o kameradaki son tespit karesinin zamanıyla (yoksa başlangıçla).
  2. aşamadaki açılış uzlaştırmasının analiz tarafındaki karşılığı.

### Kare zamanı
- Bir karenin zamanı, karenin ffmpeg borusundan **okunduğu an** (UTC, `TimeProvider`). Gerçek çekim anından ağ, kod çözme
  ve tamponlama kadar (yüzlerce ms) geç. Kamera RTCP/NTP zamanı daha doğru olurdu ama ffmpeg'in ham çıktısında yok.
  Olay araması için saniyenin altındaki fark önemsiz; kare hassasiyetinde eşleme gerekirse ilk bakılacak yer burası.

## 3. Koda nereden bakılır?

| Konu | Dosya |
|---|---|
| ffmpeg kare komutu (fps + letterbox + bgr24) | `src/Gozcu.Media/Ffmpeg/FfmpegCommands.cs` → `AnalysisFrames` |
| ffmpeg süreci, stdout'tan tam kare okuma | `src/Gozcu.Media/Frames/FfmpegFrameSource.cs`, `RawFrameReader.cs` |
| Letterbox geometrisi, normalize kutu, IoU | `src/Gozcu.Vision/LetterboxGeometry.cs`, `Detection.cs` |
| HWC → CHW tensör | `src/Gozcu.Vision/FrameTensor.cs` |
| YOLOX çıktısını çözme | `src/Gozcu.Vision/YoloxDecoder.cs` |
| NMS | `src/Gozcu.Vision/NonMaxSuppression.cs` |
| ONNX Runtime oturumu | `src/Gozcu.Vision/OnnxYoloxDetector.cs` |
| Kamera başına hat, DropOldest kanal, yeniden bağlanma | `src/Gozcu.AnalysisService/Pipeline/CameraAnalyzer.cs` |
| Eşzamanlı çıkarım sınırı | `src/Gozcu.AnalysisService/Pipeline/InferenceGate.cs` |
| Kareden olaya durum makinesi | `src/Gozcu.AnalysisService/Events/DetectionEventTracker.cs` |
| Toplu yazma, olay yayını | `src/Gozcu.AnalysisService/Persistence/DetectionWriter.cs` |
| Şema | `src/Gozcu.Infrastructure/Persistence/Migrations/0002_detections.sql` |
| Sorgular | `src/Gozcu.Infrastructure/Persistence/DetectionRepository.cs` |
| API ve kutulu BMP anlık görüntü | `src/Gozcu.AnalysisService/Api/AnalysisEndpoints.cs`, `Snapshots/` |
| Model indirme (SHA-256) | `scripts/download-model.sh`, `deploy/analysis.Dockerfile` |
| Gerçek modelle test | `tests/Gozcu.IntegrationTests/Vision/OnnxYoloxDetectorIntegrationTests.cs` |

## 4. Bilerek verdiğimiz kararlar ve bedelleri

| Karar | Neden | Bedeli / sonraki adım |
|---|---|---|
| Analiz ayrı servis, ayrı ffmpeg bağlantısı | Kayıt asla analiz yüzünden yavaşlamaz; analiz çökerse kayıt sürer | Kamera başına bir RTSP bağlantısı daha (MediaMTX gibi bir yeniden yayıncı arkasında sorun değil) |
| Tüm kareleri çözüp `fps=2` ile seçmek | Basit ve doğru zamanlı | 25 fps'nin tamamı çözülür (kamera başına ≈ %8 CPU). Alternatif: yalnızca anahtar kareleri çözmek (`-skip_frame nokey`), ama bu 0,5 fps'ye iner |
| CPU'da çıkarım | Her makinede çalışır, CI'da da | 4 kamera × 2 fps ≈ 1,5 çekirdek. GPU/OpenVINO execution provider ile 10 kat artar; kod değişmez |
| Kamera başına tek tek çıkarım (batch yok) | Basit; gecikme düşük | Çok kamerada kareleri toplayıp batch 4/8 çalıştırmak verimi artırır |
| Nesne takibi (tracking) yok | Olay için kategori sayısı yetiyor | Aynı kişiyi kareler arası izlemek (ByteTrack/SORT) sayım ve "çizgi geçme" kuralları için gerekir |
| Outbox yok | Aşama 2 ile aynı bilinçli eksik | Yayın başarısız olursa olay veritabanında var ama mesaj kaybolur. Çözüm: aynı transaction'da outbox tablosu |
| Anlık görüntü BMP | Kodlayıcı kütüphanesi gerekmez, 30 satır | 290 KB/kare; yalnızca hata ayıklama sayfası için. Panelde canlı video üzerine kutu çizilecek (4. aşama) |

## 5. Mülakat soruları (kısa yanıtlarıyla)

1. **Modeli neden saniyede 2 karede çalıştırıyorsun, 25'te değil?**
   İnsan ve araç saniyeler mertebesinde hareket eder; olay için 2 fps yeter. 25 fps 12 kat CPU demek. Hız ayarlanabilir
   (`Analysis__FramesPerSecond`).
2. **Çıkarım yavaşlarsa ne olur?**
   Kanal 1 kapasiteli ve DropOldest: eski kare atılır, sayaç artar, gecikme büyümez. Atılan kare sayısı `/status`'ta görünür;
   sürekli artıyorsa fps düşürülür ya da donanım eklenir.
3. **Letterbox nedir, neden gerekli?**
   En-boy oranını koruyarak sığdırıp kalanı gri doldurmak. Germek nesneleri bozar ve model eğitimde letterbox gördüğü
   için doğruluk düşer. Kutu geri çevrilirken dolgu hesaba katılmalı.
4. **NMS ne işe yarar? IoU eşiğini 0,9 yaparsan ne olur? 0,1 yaparsan?**
   Aynı nesnenin örtüşen kutularını teke indirir. 0,9: neredeyse hiç bastırmaz, çift kutular kalır. 0,1: yan yana duran
   iki ayrı insanı da tek kutuya indirir.
5. **Güven eşiği ile olay eşiği neden farklı (0,35 ve 0,5)?**
   Düşük eşik metaveriye zayıf adayları da kaydeder (sonradan arama/ince ayar için), olay ise daha temkinli açılır.
   Olay açmak operatörün dikkatini çekmektir; yanlış alarm pahalıdır.
6. **Tek karelik yanlış tespit alarm üretir mi?**
   Hayır: art arda 2 kare şartı (debounce). Tek karelik kaçırma da olayı bölmez: 5 sn boşluk toleransı (histerezis).
7. **InferenceSession'ı her istekte mi oluşturuyorsun?**
   Hayır, bir kez; `Run` iş parçacığı güvenli. Her istekte oluşturmak model yüklemeyi (yüzlerce ms) her kareye ekler.
8. **ONNX neden? Python'da çalıştırsan olmaz mı?**
   Olur ama ayrı bir Python servisi, ayrı imaj, süreçler arası kare taşıma demek. ONNX ile model C# sürecinde çalışır,
   kare kopyalanmaz; GPU'ya geçmek için yalnızca execution provider değişir.
9. **Hangi modeli neden seçtin?**
   YOLOX-Tiny. Nano örnek karede insanı kaçırdı, S üç kat yavaş. Ultralytics daha popüler ama AGPL; YOLOX Apache-2.0.
10. **Ön işlemeyi yanlış yaparsan (RGB verirsen, /255 bölersen) ne olur?**
    Hata almazsın; model sessizce daha az ya da yanlış tespit yapar. Bu yüzden bilinen bir kare üzerinde referans
    uygulamayla (Python) karşılaştırdık ve entegrasyon testine gerçek kareyle beklenen kutuyu koyduk.
11. **Tespitleri neden JSONB tutuyorsun?**
    Kare başına bir satır; satır sayısı düşük, kare atomik okunur. Arama olay tablosundan yapılır. Gerekirse JSONB'ye
    GIN indeksi eklenir ya da ayrı tabloya geçilir.
12. **Veritabanı 1 dakika erişilemezse?**
    Analiz sürer; yazıcı geri çekilmeyle dener, tampon dolarsa kareleri atar ve sayar. Olay açılış/kapanışları ayrıca
    bekletilir. Kayıt servisi zaten veritabanından bağımsız.
13. **Servis çökerse "sürüyor" kalan olaylar?**
    Açılışta son tespit karesinin zamanıyla kapatılır.
14. **16 kameraya nasıl ölçeklersin?**
    Önce ölç: kamera başına CPU = kod çözme + fps × çıkarım süresi. Sonra: GPU/OpenVINO, kameralar arası batch,
    donanım kod çözücü (VAAPI/NVDEC), birden fazla analiz örneği (kameraları örneklere paylaştırma).
15. **Kare zamanı ne kadar doğru?**
    Okunduğu an; çekimden birkaç yüz ms geç. Olay araması için yeterli; daha doğrusu için RTP zaman damgası gerekir.

## 6. Kendin dene

```bash
./scripts/download-sample-videos.sh          # 4 örnek güvenlik kamerası videosu (insan + araç)
docker compose up --build
# http://localhost:5082 → son analiz edilen kareler ve kutular
curl 'http://localhost:5082/api/analysis/events?category=Vehicle&limit=5'
curl 'http://localhost:5082/api/analysis/cameras/cam3/latest'
```

Deneyler:
- `ANALYSIS_FPS=8 docker compose up -d analysis` ile hızı artır; `/api/analysis/status` içindeki `framesDropped` ve
  `averageInferenceMs` nasıl değişiyor?
- `docker compose build --build-arg MODEL=yolox_nano analysis` ile nano'ya geç; koridor kamerasında (cam3) kaç insan
  kaçıyor?
- `docker compose stop postgres` yap, 30 sn bekle, başlat: analiz durdu mu? `writer.lastError` ne diyor?
- `docker compose kill -s SIGKILL analysis` sonra `up -d analysis`: "sürüyor" görünen olaylar kapandı mı?
- RabbitMQ arayüzünde (<http://localhost:15672>) `gozcu.events` exchange'ine `analysis.#` ile bağlı bir kuyruk oluştur
  ve gelen olay mesajlarını incele.
