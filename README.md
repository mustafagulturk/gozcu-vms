# Gözcü VMS – Video Analiz ve Kayıt Sunucusu

[![CI](https://github.com/mustafagulturk/gozcu-vms/actions/workflows/ci.yml/badge.svg)](https://github.com/mustafagulturk/gozcu-vms/actions/workflows/ci.yml)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)
![FFmpeg](https://img.shields.io/badge/FFmpeg-copy%20mode-007808)
![Docker Compose](https://img.shields.io/badge/Docker-Compose-2496ED)

Birden fazla IP kameradan RTSP akışı alan, yeniden kodlamadan kaydeden, görüntüde insan ve araç tespit eden
ve olayları web panelde gösteren mikroservis tabanlı bir video yönetim sistemi (VMS).
Gerçek kamera gerektirmez: video dosyaları MediaMTX ile sahte RTSP kameralar olarak yayınlanır; testler ve CI da bu
sahte kameralarla çalışır.

> **Durum:** 2. aşama tamamlandı (çoklu kamera ve saate göre geri oynatma). Yol haritası aşağıda.

![Dört kameralı canlı izleme](docs/images/asama2-coklu-kamera.png)

![Saate göre geri oynatma](docs/images/asama2-geri-oynatma.png)

## English summary

Gözcü ("watchman") is a microservice-based video management system written in C# / .NET 8. It ingests RTSP streams
from IP cameras, records them to disk in one-minute MP4 segments **without re-encoding** (FFmpeg stream copy), tracks
camera health with automatic reconnection and exponential backoff, and publishes domain events over RabbitMQ with
segment metadata stored in PostgreSQL. Recordings can be played back by wall-clock time in the browser (timeline with gaps,
seamless segment-to-segment playback over HTTP range requests) and exported as clips with the FFmpeg concat demuxer, again
without re-encoding. On startup the recorder reconciles disk and database: segments cut off by a crash are detected with
ffprobe and quarantined, and finished segments missing from the index are recovered. Upcoming phases add YOLO object
detection with ONNX Runtime, event search and a web dashboard. Fake cameras are served by MediaMTX, so the whole system, its integration tests and CI
run without real hardware. `docker compose up --build` starts everything.

## Mimari

```mermaid
flowchart LR
    subgraph Kameralar["Sahte IP kameralar"]
        V[(samples/videos/*.mp4<br/>veya test desenleri)] --> M[MediaMTX · cam1…cam4<br/>RTSP :8554 · HLS :8888]
    end

    M -- "RTSP (TCP)" --> S[Akış servisi<br/>bağlantı · yeniden bağlanma · sağlık]
    M -- "RTSP (TCP)" --> R[Kayıt servisi<br/>-c copy · 1 dk MP4 parçaları · saklama]
    M -- HLS --> B[Tarayıcı<br/>canlı izleme]
    S -- "/api/cameras" --> B
    R -- "geri oynatma · Range · klip" --> B

    R --> D[(Disk<br/>/recordings)]
    R -- "parça metaverisi · uzlaştırma" --> P[(PostgreSQL)]
    S -- kamera kataloğu --> P

    S -- camera.status.changed --> Q{{RabbitMQ<br/>gozcu.events}}
    R -- recording.segment.completed --> Q

    Q -.-> A[Analiz servisi<br/>YOLO + ONNX Runtime]:::planned
    Q -.-> E[API ve olay servisi]:::planned
    E -.-> W[Web panel]:::planned

    classDef planned stroke-dasharray: 5 5,opacity:0.6
```

Kesikli kutular sonraki aşamalarda eklenecek servislerdir.

| Bileşen | Sorumluluk |
|---|---|
| `Gozcu.StreamingService` | Her kamera için akışı çözmeden okuyan bir ffmpeg süreci çalıştırır. Akışın gerçek zamanlı gelip gelmediğini ölçer (`Connecting → Online → Degraded → Offline`), bağlantı koparsa üstel geri çekilmeyle yeniden bağlanır, durum değişikliklerini RabbitMQ'ya yayınlar. Canlı izleme sayfasını ve `/api/cameras` uç noktasını sunar. |
| `Gozcu.RecordingService` | ffmpeg segment muxer ile akışı **yeniden kodlamadan** saat sınırlarına hizalı MP4 parçalarına yazar. Biten her parçayı PostgreSQL'e kaydeder ve olay yayınlar. Kota aşıldığında veya disk dolmak üzereyken en eski parçaları siler. Geri oynatma sayfasını, zaman çizelgesi / parça akışı / klip dışa aktarma API'sini sunar. Açılışta disk ile veritabanını uzlaştırır. |
| `Gozcu.Media` | ffmpeg süreç denetleyicisi (yeniden başlatma, nazik durdurma), komut üreticileri (kayıt, izleme, concat ile klip), ffprobe ile medya doğrulama ve çıktı ayrıştırıcıları. |
| `Gozcu.Infrastructure` | PostgreSQL (Dapper, gömülü SQL betikleriyle şema yönetimi) ve RabbitMQ (topic exchange) erişimi. |
| `Gozcu.Contracts` | Servisler arası olaylar ve kamera tanımları. |

### Tasarım kararları

- **Yeniden kodlama yok.** Kayıt ve izleme `-c copy` ile yapılır; kamera başına CPU kullanımı %1'in altındadır.
  Kod çözme yalnızca 3. aşamadaki analiz servisinde, saniyede birkaç kare için yapılacak.
- **Saate hizalı parçalar.** `-segment_atclocktime` ile her parça dakikanın başında başlar ve dosya adı UTC başlangıç
  zamanını taşır (`cam1_20261028T171500Z.mp4`). Böylece saate göre geri oynatma basit bir aralık sorgusudur ve
  veritabanı olmadan da dosyalar sıralanabilir.
- **Kayıt veritabanını beklemez.** PostgreSQL veya RabbitMQ erişilemezken de diske kayıt sürer; parçalar kuyrukta bekler
  ve bağlantı gelince indekslenir. Saklama politikası kaynak olarak dosya sistemini kullanır, böylece disk yine dolmaz.
- **Nazik durdurma.** Süreçler kapatılırken ffmpeg'e `q` gönderilir; son parçanın MP4 başlığı yazılır ve dosya bozulmaz.
- **Geri oynatma tarayıcıda parça parça.** MP4 parçaları HLS'e çevrilmez; tarayıcı her parçayı HTTP Range ile doğrudan
  açar. İki `<video>` öğesiyle bir sonraki parça önceden yüklenir ve `ended` olayında yer değiştirilir, böylece parça
  geçişleri fark edilmez. Kayıt olmayan aralıklar zaman çizelgesinde boşluk olarak gösterilir ve oynatma sırasında atlanır.
  Duvar saati = parçanın başlangıç zamanı + `currentTime`.
- **Klip de yeniden kodlanmaz.** Dışa aktarma ffmpeg concat demuxer'ına `inpoint`/`outpoint` içeren bir liste verir
  (`-c copy -movflags +faststart`). Bedeli: klip istenen andan önceki anahtar kareden başlar (en fazla bir GOP, ~2 sn).
  Medya dosyası yolları veritabanından gelse bile kayıt kökü dışına çıkamaz (yol geçişi denetimi).
- **Açılış uzlaştırması.** Servis aniden kesilirse yazılmakta olan parçanın `moov` kutusu yazılmaz ve dosya büyük ama
  oynatılamaz kalır; bellekteki indeksleme kuyruğu da kaybolur. Kayıt süreçleri başlamadan önce diskteki dosyaların
  listesi alınır; veritabanında olmayan ya da boyutu farklı olanlar ffprobe ile doğrulanır. Oynatılabilenler indekslenir ve
  olayları yayınlanır, bozuklar `_quarantine/` altına taşınır, dosyası kaybolmuş kayıtlar silindi olarak işaretlenir.
  Sonuç `/api/recordings/reconciliation` adresinden okunur. İşlem idempotenttir; ikinci çalıştırma hiçbir şey değiştirmez.
- **Test edilebilir zaman.** Sağlık durum makinesi ve yeniden bağlanma denetleyicisi .NET 8 `TimeProvider` kullanır;
  birim testleri `FakeTimeProvider` ile gerçek beklemeden çalışır.

## Hızlı başlangıç

Gereksinimler: Docker (Compose v2). Geliştirme için ayrıca .NET 8 SDK ve ffmpeg.

```bash
git clone https://github.com/mustafagulturk/gozcu-vms.git
cd gozcu-vms

# (İsteğe bağlı) Pexels'tan indirdiğin bir trafik videosunu cam1 olarak hazırla.
# Bu adımı atlarsan her kamera (cam1…cam4) kendi test desenini yayınlar.
./scripts/prepare-video.sh ~/Downloads/trafik.mp4 cam1

docker compose up --build
```

| Adres | Ne var |
|---|---|
| <http://localhost:5080> | Canlı izleme sayfası (4 kamera, durum + HLS oynatıcı) |
| <http://localhost:5081> | Geri oynatma sayfası (zaman çizelgesi, saate göre oynatma, klip indirme) |
| <http://localhost:5081/api/playback/cam1/timeline> | Son bir saatin kayıtları, boşlukları ve kapsama aralıkları |
| `http://localhost:5081/api/playback/cam1/export?from=…&to=…` | Yeniden kodlamasız MP4 klip (en fazla 1 saat) |
| <http://localhost:5081/api/recordings/reconciliation> | Açılış uzlaştırması raporu |
| <http://localhost:5080/api/cameras> | Kamera sağlık durumu |
| <http://localhost:5081/api/recordings/status> | Kayıt durumu ve kayıt gecikmesi ölçümleri |
| <http://localhost:5081/api/recordings/segments?cameraId=cam1&from=2026-10-28T10:00:00Z&to=2026-10-28T11:00:00Z> | Saate göre kayıt parçası arama |
| <http://localhost:15672> | RabbitMQ yönetim arayüzü (`gozcu` / `gozcu`) |
| `rtsp://localhost:8554/cam1` | Sahte kamera (VLC ile açılabilir) |

Kayıtlar `recordings` Docker biriminde tutulur. Parça süresi ve saklama sınırları ortam değişkenleriyle değiştirilebilir:

```bash
SEGMENT_SECONDS=10 RETENTION_MAX_BYTES=2147483648 docker compose up --build
```

Uçtan uca doğrulama (CI'daki duman testiyle aynı):

```bash
SEGMENT_SECONDS=10 docker compose up -d --build
./scripts/smoke-test.sh
```

## Geliştirme

```bash
dotnet build
dotnet test tests/Gozcu.UnitTests          # hızlı, harici bağımlılık yok
dotnet test tests/Gozcu.IntegrationTests   # Docker ve ffmpeg gerekir (Testcontainers)
```

Servisleri IDE'den çalıştırmak için altyapıyı Compose ile başlatıp servisleri yerelde çalıştırabilirsin:

```bash
docker compose up -d mediamtx postgres rabbitmq
dotnet run --project src/Gozcu.StreamingService   # http://localhost:5080
dotnet run --project src/Gozcu.RecordingService   # http://localhost:5081
```

### Testler

| Proje | Kapsam |
|---|---|
| `Gozcu.UnitTests` | Yeniden bağlanma politikası, ffmpeg komutları ve çıktı ayrıştırıcıları, süreç denetleyicisi, kamera sağlık durum makinesi, kayıt parçası işleme, saklama planlayıcısı, yapılandırma doğrulaması; geri oynatma zaman çizelgesi ve konum bulma, klip planlayıcı, concat listesi kaçışları, yol geçişi denetimi; uzlaştırma planlayıcısı, karantina ve ffprobe çıktısı |
| `Gozcu.IntegrationTests` | Gerçek MediaMTX konteyneri ve ffmpeg ile: kamera kopup geri geldiğinde otomatik yeniden bağlanma; yeniden kodlamasız, saate hizalı, oynatılabilir parça kaydı. PostgreSQL ile şema betikleri ve saate göre arama; RabbitMQ ile olay yayını ve yönlendirme. Parçalardan yeniden kodlamasız klip üretimi. SIGKILL ile yarıda kesilmiş MP4'ün ffprobe ile tespiti ve PostgreSQL ile uçtan uca uzlaştırma |
| `scripts/smoke-test.sh` | Docker Compose ile tüm sistem: 4 kamera çevrimiçi, HLS yayınları, parçaların diske ve veritabanına yazılması, zaman çizelgesi, HTTP Range ile parça akışı, klip dışa aktarma, kayıt servisini SIGKILL ile öldürüp açılış uzlaştırmasını doğrulama |

## Ölçümler

2. aşama, 4 kamera (2× 1280x720 25 fps, 960x540 15 fps, 640x360 25 fps H.264), Docker üzerinde, 4 vCPU:

| Ölçüm | 1 kamera (1. aşama) | 4 kamera |
|---|---|---|
| Akış servisi CPU / bellek | %0,6 / 87 MiB | %2,8 / 95 MiB |
| Kayıt servisi CPU / bellek | %0,6 / 114 MiB | %2,4 / 114 MiB |
| Kayıt gecikmesi (parça bitişi → veritabanında aranabilir) | ort. 170 ms, en çok 250 ms | ort. 131 ms, en çok 185 ms |
| Kamera kopmasından sonra yeniden bağlanma | 1–2 sn (ilk deneme) | 1–2 sn |
| 1 saatlik zaman çizelgesi sorgusu | – | 12 ms |
| 60 sn'lik klip dışa aktarma (yeniden kodlamasız) | – | 156 ms |
| SIGKILL sonrası açılış uzlaştırması | – | 309 ms (16 dosya; 2 karantina) |

Eşzamanlı kamera sayısı üst sınırı ve tespit FPS ölçümleri 5. aşamada eklenecek. (Sahte kamera tarafındaki MediaMTX CPU
kullanımı test desenlerinin anlık kodlanmasından kaynaklanır; hazırlanmış video dosyasıyla yayın kopyalama modundadır.)

## Yol haritası

- [x] **1. Tek kamera canlı izleme ve kayıt** – RTSP bağlantısı, otomatik yeniden bağlanma, sağlık durumu, yeniden kodlamasız 1 dk parçalar, saklama politikası, Docker Compose, CI
- [x] **2. Çoklu kamera ve geri oynatma** – 4 sahte kamera, saate göre geri oynatma ve zaman çizelgesi, yeniden kodlamasız klip dışa aktarma, açılışta disk/veritabanı uzlaştırması
- [ ] **3. Nesne tespiti** – saniyede birkaç kare ile YOLO (ONNX Runtime), sonuçların metaveri olarak kaydı
- [ ] **4. Olay arama ve web panel** – kamera yönetimi API'si, olay arama, tespit kutuları, zaman çizelgesi
- [ ] **5. Ölçümler, testler ve dokümantasyon** – yük testleri, ölçüm tablosu, demo GIF

## Öğrenme notları

Her aşamanın arkasındaki kavramlar, tasarım kararları ve mülakat soruları:

- [Aşama 2: çoklu kamera ve geri oynatma](docs/asama-2-ogrenme-notu.md)

## Lisans

MIT
