# Sahte kamera videoları

Bu klasördeki `<kamera-id>.mp4` dosyaları MediaMTX tarafından sonsuz döngüde RTSP kamera akışı olarak
yayınlanır (`rtsp://localhost:8554/<kamera-id>`). Dosya yoksa kamera test deseni yayınlar, yani sistem
video olmadan da çalışır.

Videolar boyutları nedeniyle repoya eklenmez. Hazır örnekleri tek komutla indirebilirsiniz:

```bash
./scripts/download-sample-videos.sh
docker compose restart mediamtx
```

| Kamera | Video | İçerik |
|---|---|---|
| cam1 | `car-detection.mp4` | Otopark girişi, araçlar |
| cam2 | `person-bicycle-car-detection.mp4` | Otopark alanı, yaya, bisiklet ve araç |
| cam3 | `people-detection.mp4` | Koridor, yürüyen insanlar |
| cam4 | `one-by-one-person-detection.mp4` | Lobi, tek tek geçen insanlar |

Kaynak: [Intel IoT DevKit sample videos](https://github.com/intel-iot-devkit/sample-videos), lisans
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) (© Intel Corporation).

Kendi videonuzu (ör. Pexels'tan bir trafik videosu) kullanmak için hazırlama betiğine verin:

```bash
./scripts/prepare-video.sh ~/Downloads/trafik.mp4 cam1
docker compose restart mediamtx
```

Betik videoyu gerçek bir IP kameraya benzetir: H.264, 1280x720, 25 fps, 2 saniyede bir anahtar kare, ses yok.
