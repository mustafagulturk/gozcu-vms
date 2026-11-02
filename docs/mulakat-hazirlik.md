# Gözcü VMS · Mülakat hazırlık dokümanı

Beş aşamanın notlarını tek yerde toplar. Hedef: "video ve analiz kayıt sistemleri" pozisyonunda (C#/.NET, video akışı,
görüntü işleme, mikroservis) bu projeyi anlatırken hiçbir soruda takılmamak.

Nasıl çalışmalı:
1. Önce **2 dakikalık anlatımı** sesli söyle, sonra **ezberlenecek sayılar** tablosunu öğren.
2. **Konu haritası**ndaki her başlığı kendi cümlenle bir dakikada anlatabiliyor musun? Anlatamadığın başlıkta ilgili aşama
   notunu aç (linkler başlıklarda).
3. **En olası sorular**ı cevapları kapatıp yanıtla. **Zor sorular ve zayıf yönler** bölümünü atlama: mülakatçılar en çok
   "neyi yapmadın, neden?" sorusuyla derinliği ölçer.
4. Mülakattan önceki gün **demo kontrol listesi**ni uygula.

Aşama notları: [1](https://claude.ai/artifact/LoG6yrZkMxFNWFRKW8FKgr) (Claude Doc) ·
[2](asama-2-ogrenme-notu.md) · [3](asama-3-ogrenme-notu.md) · [4](asama-4-ogrenme-notu.md) · [5](asama-5-ogrenme-notu.md)

---

## 1. Projeyi anlatmak

### 2 dakikada (Türkçe)
> Gözcü, C# / .NET 8 ile yazdığım mikroservis tabanlı bir video yönetim sistemi. IP kameralardan RTSP ile görüntü alıyor,
> **yeniden kodlamadan** dakika başına hizalı MP4 parçalar halinde kaydediyor; bu sayede kamera başına %1'in altında CPU
> harcıyor. Kamera koparsa üstel geri çekilmeyle yeniden bağlanıyor. Kayıtlar tarayıcıda saate göre oynatılıyor ve yeniden
> kodlamadan klip olarak dışa aktarılıyor. Ayrı bir analiz servisi sistemde videoyu çözen tek yer: kamera başına saniyede
> 2 kare alıp YOLOX modelini ONNX Runtime ile süreç içinde çalıştırıyor, insan ve araç olayları üretiyor. Servisler
> PostgreSQL ve RabbitMQ ile haberleşiyor; YARP ağ geçidinin arkasında tek bir web paneli var: canlı izleme, olay arama,
> tespit kutularıyla geri oynatma, kamera yönetimi. Gerçek kamera yerine MediaMTX ile sahte kameralar kurdum; birim,
> entegrasyon (Testcontainers) ve uçtan uca duman testleri CI'da bunlarla koşuyor. Son aşamada kapasiteyi ölçtüm:
> 4 çekirdekte tam sistem 8 kamera, yalnızca kayıt 64 kamera kaldırıyor; darboğaz analizdeki video kod çözme. Yük testi
> bir de bağlantı havuzu hatası buldu, düzelttim.

### 30 saniyede (English)
> Gözcü is a .NET 8 microservice video management system. It ingests RTSP camera streams and records them as
> clock-aligned MP4 segments with FFmpeg stream copy, so recording costs under 1% CPU per camera. A separate analysis
> service is the only component that decodes video: it samples 2 fps per camera and runs YOLOX through ONNX Runtime
> to produce person and vehicle events. PostgreSQL, RabbitMQ, a YARP gateway and a single web dashboard tie it together.
> Everything runs in Docker Compose against fake MediaMTX cameras, with unit, Testcontainers integration and smoke tests
> in CI. I load-tested it: on 4 vCPUs the full system handles 8 cameras, recording alone 64; the bottleneck is decoding.

### Tek bakışta mimari

```
Kamera (RTSP) ──► MediaMTX ──┬──► Akış servisi   : -c copy -f null, sağlık durumu, kamera API'si ──┐
                             ├──► Kayıt servisi  : -c copy segment, 1 dk MP4, saklama, geri oynatma ─┤──► PostgreSQL
                             ├──► Analiz servisi : fps=2 çöz → YOLOX (ONNX) → tespit + olay ─────────┘    RabbitMQ
                             └──► HLS ───────────► Tarayıcı ◄── YARP ağ geçidi (tek panel, :5000)
```

---

## 2. Ezberlenecek sayılar

| Konu | Sayı | Nereden |
|---|---|---|
| Kayıt CPU'su | kamera başına ≈ %0,7 (`-c copy`) | 5. aşama yük testi |
| Analiz CPU'su | kamera başına ≈ %8 kod çözme + fps × çıkarım | 5. aşama |
| Kapasite, 4 vCPU | tam sistem 2 fps: **8** kamera · 1 fps: **16** · yalnız kayıt + canlı: **64** (makinenin %43'ü) | 5. aşama |
| Çıkarım (YOLOX-Tiny 416) | tek başına 42 ms; 1/2/4 iş parçacığı 77/49/31 ms; yük altında 75–130 ms | 3 ve 5 |
| Model boyları | nano 16 ms · tiny 42 ms · s 112 ms | 3. aşama |
| Kayıt gecikmesi | parça bitişi → aranabilir: ort. ~0,5 sn, en çok ~1 sn, yükle artmıyor | 5. aşama |
| Yeniden bağlanma | 1–2 sn; geri çekilme 1, 2, 4… en çok 30 sn + jitter | 1. aşama |
| Parça | 60 sn, dakika başına hizalı, ad UTC (`cam1_20261028T171500Z.mp4`) | 1. aşama |
| GOP | 2 sn (25 fps → 50 kare); klip en çok bir GOP erken başlar | 1 ve 2 |
| Klip dışa aktarma | 60 sn klip 156 ms (yeniden kodlamasız) | 2. aşama |
| Açılış uzlaştırması | 309 ms (16 dosya, 2 karantina) | 2. aşama |
| API | karma panel yükü 600 istek/sn'de p99 97 ms; 800'de doyuyor (731/sn, p99 356 ms) | 5. aşama |
| Bağlantı havuzu | Npgsql varsayılan 100 × 3 servis > PG `max_connections` 100 → `Maximum Pool Size=25`, en çok 70 bağlantı | 5. aşama |
| YOLOX çıktısı | 3549 satır × 85 (4 kutu + 1 nesnellik + 80 sınıf); adımlar 8/16/32 | 3. aşama |
| Bant genişliği | 720p H.264 ≈ 2–4 Mbps; 100 kamera ≈ 400 Mbps, günde ~4 TB | genel |

---

## 3. Konu haritası

### Video temelleri ([1](https://claude.ai/artifact/LoG6yrZkMxFNWFRKW8FKgr), [2](asama-2-ogrenme-notu.md))
- **Codec ve kapsayıcı:** H.264 "nasıl sıkıştırıldı", MP4/MPEG-TS "nasıl paketlendi". Ham 720p ≈ 70 MB/sn, H.264 ≈ 0,5 MB/sn.
- **I/P/B kareler, GOP:** yalnızca anahtar karede kesilir/başlatılır. Klip hassasiyeti ve parça sınırları bundan.
- **`-c copy` vs yeniden kodlama:** paketleri çözmeden taşımak neredeyse bedava; yeniden kodlama kamera başına bir çekirdek.
- **MP4 `moov`/`mdat`:** içindekiler listesi sonda; süreç ölürse dosya oynatılamaz. Nazik durdurma (`q`), açılış
  uzlaştırması, alternatif fMP4/MPEG-TS. `+faststart` → `moov` başa.
- **RTSP/RTP (TCP), HLS, WebRTC:** RTSP kameradan almak, HLS tarayıcıya (2–6 sn gecikme), WebRTC < 1 sn.
- **HTTP Range / 206:** tarayıcı büyük MP4'ü parça parça ister; geri oynatma bunun üzerine.
- **Concat demuxer:** `inpoint`/`outpoint` ile yeniden kodlamasız klip.

### Görüntü işleme ([3](asama-3-ogrenme-notu.md))
- **Tespit çıktısı:** kutu + sınıf + güven; kutular 0..1 normalize saklanır.
- **YOLO/YOLOX:** tek geçiş, anchor-free; ham çıktıyı kutuya çevirmek (`YoloxDecoder`) bizim işimiz.
- **Letterbox:** oranı koruyarak sığdır, kalanı gri doldur; geri dönüşte dolgu hesaba katılır.
- **Ön işleme:** BGR, 0–255 (bölme yok), HWC → CHW. Yanlış yaparsan hata almazsın, sessizce kötü sonuç alırsın.
- **NMS ve IoU eşiği**, güven eşiği (0,35) ile olay eşiği (0,5) farkı.
- **ONNX Runtime:** model C# sürecinde; `InferenceSession` bir kez oluşturulur, `Run` iş parçacığı güvenli; GPU için
  yalnızca execution provider değişir.
- **Geri basınç:** kapasite 1, DropOldest kanal → her zaman en taze kare, sınırlı bellek, zarif bozulma.
- **Kareden olaya:** debounce (art arda 2 kare) ve histerezis (5 sn boşluk toleransı).
- **Lisans:** Ultralytics AGPL, YOLOX Apache-2.0 → ticari üründe önemli.

### Dağıtık sistem ve mesajlaşma ([1](https://claude.ai/artifact/LoG6yrZkMxFNWFRKW8FKgr), [2](asama-2-ogrenme-notu.md), [4](asama-4-ogrenme-notu.md))
- **RabbitMQ topic exchange** (`gozcu.events`, `camera.#`, `analysis.#`): gevşek bağlılık, yeni tüketici eklemek kolay.
- **En az bir kez teslim + idempotent tüketici;** tam güvence için **transactional outbox** (yapılmadı, biliyorum).
- **Event notification vs event-carried state transfer:** katalog değişince sinyal, veri veritabanından okunur.
- **Sinyal + periyodik yenileme:** mesaj kaybolsa da 30 sn'de yakalanır.
- **Uzlaştırma döngüsü (`CameraWorkerHost`):** istenen durum (katalog) ile çalışan durumu (ffmpeg süreçleri) karşılaştır;
  Kubernetes kontrolcüleriyle aynı fikir.
- **Kayıt veritabanını beklemez:** en kötü hata kayıt kaybıdır; indeksleme kuyrukta bekler.
- **API ağ geçidi (YARP):** tek köken, CORS yok, kimlik doğrulama için tek yer.

### Veritabanı ([1](https://claude.ai/artifact/LoG6yrZkMxFNWFRKW8FKgr), [3](asama-3-ogrenme-notu.md), [4](asama-4-ogrenme-notu.md), [5](asama-5-ogrenme-notu.md))
- Dapper + gömülü SQL betikleri, `schema_migrations`, eşzamanlı açılış için **advisory lock**.
- `(camera_id, started_at)` indeksi; aralık kesişimi: `started_at < to AND (ended_at IS NULL OR ended_at >= from)`.
- Tespit kareleri **JSONB**; arama olay tablosundan.
- **Keyset (imleç) sayfalama** vs OFFSET; opak base64 imleç.
- **İyimser eşzamanlılık:** sürüm sütunu, ETag/If-Match, 412 vs 409.
- **Bağlantı havuzu:** Npgsql varsayılan 100, PostgreSQL `max_connections` 100; yük testinde tükendi.

### .NET ([1](https://claude.ai/artifact/LoG6yrZkMxFNWFRKW8FKgr), [3](asama-3-ogrenme-notu.md), [5](asama-5-ogrenme-notu.md))
- `BackgroundService`, `IHostedService`, sağlık denetimleri (`/health/live`, `/health/ready`).
- `System.Threading.Channels` (sınırlı kanal, `BoundedChannelFullMode.DropOldest`).
- `TimeProvider` + `FakeTimeProvider` ile zamana bağlı kodu beklemeden test etmek.
- ffmpeg'i ayrı süreç olarak yönetmek: `ProcessSupervisor`, stdout'tan ham kare okuma (`RawFrameReader`), nazik durdurma.
- `SemaphoreSlim` ile eşzamanlılık sınırı (çıkarım, klip dışa aktarma, küçük resim).
- `TreatWarningsAsErrors`, analizörler, merkezi paket yönetimi.
- BenchmarkDotNet, `[MemoryDiagnoser]`, kare başına yolda sıfıra yakın ayırma.

### Test ve kalite ([1](https://claude.ai/artifact/LoG6yrZkMxFNWFRKW8FKgr), [5](asama-5-ogrenme-notu.md))
- Üç katman: birim (saf planlayıcılar: saklama, uzlaştırma, klip, zaman çizelgesi), entegrasyon (Testcontainers ile
  gerçek MediaMTX, PostgreSQL, RabbitMQ; gerçek modelle bilinen kare), duman testi (Compose ile tüm sistem, CI'da).
- "Saf fonksiyon planlayıcı + ayrı yürütücü" deseni: kuralları diske dokunmadan test etmek.
- Performans: mikro ölçüm, API yük testi (kapalı/açık model, koordineli ihmal, p99), sistem kapasite testi.

### Güvenlik ([2](asama-2-ogrenme-notu.md), [4](asama-4-ogrenme-notu.md))
- Yol geçişi (path traversal) denetimi: kanonik yol kayıt kökünde mi, ayırıcıyla biten önekle.
- RTSP parolası: yanıtta, günlükte, ffmpeg hata metninde maskeli; düzenlemede maskeli değer gelirse eski parola korunur.
- Kaynak sınırları: eşzamanlı klip/küçük resim, en çok 1 saat klip, istemci iptalinde ffmpeg öldürülür.
- Eksik: kimlik doğrulama (ağ geçidinde OIDC/JWT + roller), veritabanında parolanın şifrelenmesi.

---

## 4. Tasarım kararları ve bedelleri (en önemli 12)

| # | Karar | Neden | Bedeli |
|---|---|---|---|
| 1 | Her yerde `-c copy` | CPU; 64 kamera tek makinede | Çözünürlük/bitrate değiştirilemez |
| 2 | Saate hizalı 1 dk parçalar, adı UTC | Saate göre arama basit, dosyalar DB'siz sıralanır | Çok dosya (kamera başına günde 1440) |
| 3 | ffmpeg ayrı süreç | Çökme izolasyonu, kamera başına yeniden başlatma | Süreç yönetimi, stdout ayrıştırma |
| 4 | Kayıt DB'yi beklemez | Kayıt kaybı en kötü hata | Bellekteki kuyruk çökmede kaybolur → açılış uzlaştırması |
| 5 | Düz MP4 (fMP4 değil) | Her oynatıcıda, concat'ta sorunsuz | Çökmede en çok 1 dk kayıp |
| 6 | Geri oynatmada HLS yok, Range + çift `<video>` | Ek dönüştürme yok | İstemci mantığı karmaşık |
| 7 | Analiz ayrı servis, ayrı RTSP bağlantısı | Kayıt asla analizden etkilenmez | Kamera başına ek bağlantı |
| 8 | Tüm kareleri çöz, 2 fps seç | Basit, doğru zamanlı | Kod çözme fps'ten bağımsız ≈ %8/kamera (darboğaz) |
| 9 | ONNX Runtime süreç içinde, CPU | Her yerde çalışır, kare kopyalanmaz | GPU'suz kapasite düşük |
| 10 | DropOldest geri basınç | Gecikme ve bellek sınırlı | Aşırı yükte kare atılır (ölçülüyor) |
| 11 | Veritabanı kamera kataloğunun kaynağı, RabbitMQ yalnızca sinyal | Sıra/kayıp sorunu yok | Değişiklik en geç 30 sn'de yayılır (sinyal kaybolursa) |
| 12 | Outbox yok | Kapsam | Yayın başarısızsa olay mesajı kaybolabilir |

---

## 5. En olası sorular (kısa yanıtlarıyla)

**Video ve kayıt**
1. **Neden yeniden kodlamıyorsun?** Kopyalama kamera başına %0,7 CPU, yeniden kodlama ≈ 1 çekirdek. Ölçtüm: 64 kamera
   makinenin %43'ü. Kod çözen tek yer analiz.
2. **Kayıt dosyası nasıl bozulur, nasıl önledin?** `moov` sonda. Kapanışta ffmpeg'e `q`; çökmede açılış uzlaştırması
   ffprobe ile bulur, karantinaya alır; bitmiş ama indekslenmemiş parçaları kurtarır. Kalıcı çözüm fMP4.
3. **Klip neden kare hassasiyetinde değil?** Kesim anahtar karede başlar; en çok bir GOP (2 sn) erken. Kare hassasiyeti
   için baştaki GOP'u yeniden kodlamak (smart cut).
4. **Kamera koparsa?** ffmpeg 5 sn veri alamazsa çıkar, durum Çevrimdışı, olay yayınlanır, 1-2-4…30 sn + jitter ile
   yeniden denenir. Entegrasyon testinde yayıncıyı durdurup başlatarak sınandı.
5. **Disk dolarsa?** Saklama 30 sn'de bir kota ve boş alanı denetler, en eskiden siler, her kameranın en yeni parçasına
   dokunmaz. Kaynak dosya sistemi; DB düşse de disk dolmaz.
6. **Tarayıcı 1 GB'lık MP4'te nasıl ileri sarar?** HTTP Range, 206; `faststart` ile tek istek.

**Analiz**
7. **Neden 2 fps?** İnsan/araç olayları için yeterli; 25 fps 12 kat çıkarım. Ama kod çözme fps'ten bağımsız, bunu ölçerek
   gördüm.
8. **Çıkarım yetişmezse?** DropOldest: eski kare atılır, sayaç artar, gecikme büyümez. 16 kamerada %51 atıldı ama her
   kamera ~1 fps analiz edildi, kayıt etkilenmedi.
9. **Letterbox, NMS, IoU eşiği?** Bkz. konu haritası; IoU 0,9 çift kutu bırakır, 0,1 yan yana iki insanı birleştirir.
10. **Ön işlemenin doğru olduğunu nasıl bildin?** Bilinen karede Python referansıyla karşılaştırdım; entegrasyon testi
    gerçek modelle beklenen kutuları arıyor.
11. **Hangi model, neden?** YOLOX-Tiny: nano insanı kaçırdı, S üç kat yavaş; Ultralytics AGPL olduğu için seçilmedi.
12. **GPU olsaydı ne değişirdi?** Execution provider (CUDA/TensorRT/OpenVINO) değişir, kod değişmez. Ama darboğazın yarısı
    kod çözme; NVDEC/VAAPI ile donanım kod çözme de gerekir.

**Mimari ve dağıtık sistem**
13. **Neden mikroservis?** Hata izolasyonu (analiz çökerse kayıt sürer), bağımsız ölçekleme (analiz yatay, kayıt dikey),
    farklı kaynak profilleri. Bedeli: işletim karmaşıklığı, ağ, tutarlılık.
14. **Neden RabbitMQ, neden HTTP değil?** Gevşek bağlılık, alıcı kapalıyken mesaj bekler, yeni tüketici mevcut servise
    dokunmadan eklenir.
15. **Aynı mesaj iki kez gelirse? Kaybolursa?** Tüketici idempotent (kimlikle tekilleştirme). Kayıp için outbox gerekir;
    kamera kataloğunda periyodik yenileme bunu telafi ediyor.
16. **Çalışırken kamera eklenince ne olur?** API veritabanına yazar, sinyal yayınlar; her servisin uzlaştırma döngüsü
    yeni ffmpeg işini başlatır, eskisini bitmesini bekleyerek durdurur. Aynı kamera için iki süreç olmaz.
17. **Neden API ağ geçidi?** Tek köken, CORS yok, tek giriş noktası; kimlik doğrulama, TLS, hız sınırı tek yerde.

**Veritabanı ve API**
18. **OFFSET neden kötü, keyset'in bedeli?** Derin sayfada atlananlar da okunur ve sayfalar kayar; keyset kaymaz ama
    rastgele sayfaya atlanamaz.
19. **409 ve 412 farkı?** 409 kaynağın durumuyla çelişki (aynı kimlik); 412 `If-Match` ön koşulu tutmadı.
20. **"10:00–11:00 arası olaylar" sorgusu?** Kesişim: `started_at < to AND (ended_at IS NULL OR ended_at >= from)`.
21. **Tespitler neden JSONB?** Kare başına bir satır, kare atomik okunur; arama olay tablosundan. Gerekirse GIN ya da ayrı tablo.

**Performans ve test**
22. **Kaç kamera kaldırıyor, nasıl buldun?** Kamera sayısını artıran yük testi; temiz başlangıç, ısınma, önceden tanımlı
    ölçüt. Tam sistem 8 (2 fps) / 16 (1 fps), yalnızca kayıt 64.
23. **Darboğaz?** Analizde kod çözme. Fps'i düşürmek kod çözmeyi azaltmıyor; donanım kod çözücü veya alt akış gerekir.
24. **p99 neden önemli, koordineli ihmal nedir?** Ortalama kuyruğu gizler; kapalı model sunucu yavaşlayınca yükü de
    azaltır ve takılmaları ölçmez. Açık model + planlanan zamandan ölçüm.
25. **Yük testi ne buldu?** Bağlantı havuzu tükenmesi: 3 × 100 > 100. `Maximum Pool Size=25` ile düzeldi, en çok 70 bağlantı.
26. **Test stratejin?** Saf planlayıcılar birimle, dış sistemler Testcontainers'la gerçek konteynerlere karşı, tüm sistem
    Compose duman testiyle CI'da. Zaman `TimeProvider` ile soyut.
27. **Zamana bağlı kodu nasıl test ettin?** `FakeTimeProvider` ile zamanı ileri sarıyorum, gerçek bekleme yok.

**Ölçekleme**
28. **100 kameraya nasıl çıkarsın?** Kayıt: tek sunucu yeter, sınır disk ve ağ (400 Mbps, günde ~4 TB). Analiz: kameraları
    birden çok analiz örneğine paylaştır, GPU + donanım kod çözme, batch çıkarım. Hepsini yük testiyle doğrula.
29. **1000 kameraya?** Kayıt sunucuları kamera gruplarına bölünür (sharding), merkezi katalog; depolama için nesne
    deposu veya dağıtık dosya sistemi; RabbitMQ kümesi; analiz GPU havuzu ve iş kuyruğu; PgBouncer.
30. **Saatler neden UTC?** Yaz saati geçişinde aynı saat iki kez yaşanır; UTC sakla, ekranda yerel saate çevir.

---

## 6. Zor sorular ve zayıf yönler (dürüst cevaplar)

Mülakatçı eksikleri bulur; sen önce söylersen artı puandır. Her eksik için "ne olurdu, nasıl çözerdim" hazır olsun.

| Eksik | Riski | Çözümü |
|---|---|---|
| Transactional outbox yok | DB'ye yazıldı ama olay mesajı kayboldu | Aynı transaction'da outbox tablosu + yayınlayıcı; ya da CDC (Debezium) |
| Kimlik doğrulama yok | Herkes kamera silebilir | Ağ geçidinde OIDC/JWT, rol tabanlı yetki; iç ağda mTLS |
| RTSP parolası DB'de düz | DB sızarsa kamera parolaları gider | ASP.NET Data Protection veya kasa (Vault) |
| Tek düğüm, yüksek erişilebilirlik yok | Sunucu giderse kayıt durur | Kamera başına birincil/yedek kaydedici, paylaşımlı depolama, sağlık tabanlı devralma |
| fMP4 değil | Çökmede ≤ 1 dk kayıp | `-movflags frag_keyframe+empty_moov` veya MPEG-TS |
| Nesne takibi yok | Sayım, "çizgi geçme" kuralları yok | ByteTrack/SORT |
| Kayıt ve analiz zaman damgası farklı süreçlerden | Kutular kayıtta birkaç yüz ms kayabilir | RTP zaman damgası / ortak PTS |
| Metrikler JSON uç noktalarında | Uzun süreli izleme, alarm yok | OpenTelemetry metrikleri + Prometheus/Grafana |
| Yük testleri tek makine, tek tekrar | Mutlak sayılar donanıma bağlı | Sabit donanımda tekrar, gece koşan performans işi, gerileme eşiği |

Sık gelen "zor" sorular:
- **"Bunu neden kendin yazdın, hazır VMS (Milestone, Frigate) varken?"** Öğrenmek ve uçtan uca sahiplenmek için; sahada
  çoğunlukla hazır ürün entegre edilir. Hangi parçaları hazır alırdım: medya sunucusu (MediaMTX zaten hazır), model.
- **"ffmpeg'i süreç olarak çalıştırmak yerine kütüphane (FFmpeg.AutoGen) kullanmaz mıydın?"** Süreç izolasyonu ve
  basitlik için süreç seçtim; bedeli süreç başına bellek ve stdout ayrıştırma. Bellek içi kare paylaşımı gerekirse kütüphane.
- **"Neden Kubernetes değil?"** Kapsam ve tek komutla çalışma. Uzlaştırma döngüsü zaten aynı fikir; geçiş için servisler
  durumsuz (kayıt hariç: disk), sağlık uç noktaları hazır.

---

## 7. Hikâyeler (STAR: durum, görev, eylem, sonuç)

1. **Yük testinin bulduğu bağlantı havuzu hatası.** Durum: 800 istek/sn'de nadir 500. Eylem: günlükte `53300 too many
   clients`; Npgsql havuzu 100 × 3 servis > PG 100 hesabı; `Maximum Pool Size=25`; aynı testi tekrarlayıp bağlantı sayısını
   `pg_stat_activity` ile izledim. Sonuç: hata yok, en çok 70 bağlantı. Ders: düşük yükte görünmeyen hata, ancak ölçümle çıkar.
2. **Çökme sonrası bozuk MP4'ler.** Durum: SIGKILL sonrası oynatılamayan dosyalar ve kaybolan indeks kuyruğu. Eylem:
   açılışta disk/DB uzlaştırması, snapshot'ı kayıttan önce alma (yeni açılan dosyayı bozuk sanmamak için), karantina,
   idempotentlik testi. Sonuç: 309 ms'de toparlanma, ikinci çalışma hiçbir şey değiştirmiyor.
3. **Model seçimi ve lisans.** Durum: en popüler seçenek Ultralytics. Eylem: lisansı okudum (AGPL), YOLOX'a (Apache-2.0)
   geçtim, nano/tiny/s'yi aynı karede ölçtüm. Sonuç: tiny; ticari kullanımda risk yok.
4. **Sessiz ön işleme hataları.** Durum: yanlış renk sırası veya normalizasyon hata vermez, sadece kötü sonuç verir.
   Eylem: Python referansıyla aynı karede karşılaştırma, gerçek modelle entegrasyon testi. Sonuç: doğruluk gerilemesi CI'da yakalanır.
5. **Darboğazı ölçerek bulmak.** Durum: "16 kamera sığmıyor, fps'i düşürelim." Eylem: 1 fps ile ölçtüm; analiz CPU'su
   değişmedi. Sonuç: darboğaz kod çözme; doğru iyileştirme donanım kod çözücü / alt akış.

---

## 8. Demo kontrol listesi (mülakattan önceki gün)

```bash
git clone https://github.com/mustafagulturk/gozcu-vms.git && cd gozcu-vms
./scripts/download-sample-videos.sh
docker compose up --build -d          # 3-4 dk bekle: kayıt ve olay birikir
open http://localhost:5000            # Canlı → Olaylar → bir olaya tıkla (kutulu oynatma) → Kameralar
```
- [ ] Panelde 4 kamera çevrimiçi, olay listesinde küçük resimler var.
- [ ] Bir olaya tıklayınca kayıt kutularla oynuyor.
- [ ] `docker compose restart mediamtx` → kameralar Çevrimdışı → birkaç saniyede Çevrimiçi.
- [ ] Kameralar sayfasından `cam5` ekle (`rtsp://mediamtx:8554/cam1`): yeniden başlatma olmadan izleniyor ve kaydediliyor.
- [ ] `dotnet run -c Release --project tools/Gozcu.LoadTest -- --scenario mixed --duration 15s` canlı göster, p99'u yorumla.
- [ ] README'deki kapasite tablosunu ve demo GIF'i ekran paylaşımında aç.

## 9. İlan ile eşleştirme

| İlanda aranan | Projede nerede |
|---|---|
| C# / .NET | Tüm servisler .NET 8; hosted services, Channels, TimeProvider, minimal API, YARP |
| Video akışı, codec, RTSP | MediaMTX, ffmpeg `-c copy`, segment muxer, HLS, Range ile oynatma, concat klip |
| Video kayıt sistemleri | Saate hizalı parçalar, saklama politikası, açılış uzlaştırması, klip dışa aktarma |
| Görüntü analizi | YOLOX + ONNX Runtime, letterbox, NMS, olay üretimi, geri basınç |
| Veritabanı | PostgreSQL, Dapper, JSONB, keyset sayfalama, advisory lock, iyimser eşzamanlılık |
| Mesajlaşma, dağıtık sistem | RabbitMQ topic exchange, idempotent tüketici, uzlaştırma döngüsü |
| Linux, Docker | Docker Compose, çok aşamalı Dockerfile, sağlık denetimleri |
| Test, CI | xUnit, Testcontainers, duman testi, GitHub Actions |
| Performans | BenchmarkDotNet, HTTP yük üreticisi, kapasite testi, bulunan ve düzeltilen hata |
