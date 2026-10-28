# Sahte kamera videoları

Bu klasördeki `<kamera-id>.mp4` dosyaları MediaMTX tarafından sonsuz döngüde RTSP kamera akışı olarak
yayınlanır (`rtsp://localhost:8554/<kamera-id>`). Dosya yoksa kamera test deseni yayınlar, yani sistem
video olmadan da çalışır.

Videolar boyutları nedeniyle repoya eklenmez. Pexels'tan ücretsiz bir trafik veya yaya videosu indirip
(ör. <https://www.pexels.com/search/videos/traffic/>, <https://www.pexels.com/search/videos/pedestrians/>)
hazırlama betiğine verin:

```bash
./scripts/prepare-video.sh ~/Downloads/trafik.mp4 cam1
docker compose restart mediamtx
```

Betik videoyu gerçek bir IP kameraya benzetir: H.264, 1280x720, 25 fps, 2 saniyede bir anahtar kare, ses yok.
