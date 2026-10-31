# Nesne tespit modelleri

Analiz servisi insan ve araç tespiti için [YOLOX](https://github.com/Megvii-BaseDetection/YOLOX) (Megvii)
modellerini ONNX Runtime ile CPU'da çalıştırır. Model dosyaları büyük olduğu için depoya eklenmez
(`models/.gitignore`); indirmek için:

```bash
./scripts/download-model.sh              # varsayılan: yolox_tiny
./scripts/download-model.sh yolox_s      # veya yolox_nano
```

Betik dosyayı YOLOX'un `0.1.1rc0` GitHub sürümünden indirir ve SHA-256 özetini doğrular; dosya zaten doğru
özetle mevcutsa yeniden indirmez. Hedef klasör `MODELS_DIR` ile değiştirilebilir (Docker imajında
`MODELS_DIR=/models`). Testler ve servis başka bir modeli `GOZCU_MODEL_PATH` / ayarlardaki `ModelPath` ile kullanabilir.

## Lisans

YOLOX kodu ve önceden eğitilmiş ağırlıkları **Apache-2.0** lisanslıdır (© Megvii Inc.). Apache-2.0, projenin MIT
lisansıyla uyumludur ve ticari kullanıma (savunma sektörü dahil) izin verir; yalnızca lisans ve telif bildiriminin
korunmasını ister.

### Neden Ultralytics YOLOv8 değil?

YOLOv8/YOLO11 (Ultralytics) daha yaygın olsa da hem kodu hem ağırlıkları **AGPL-3.0** lisanslıdır. AGPL, modeli
ağ üzerinden hizmet veren bir yazılımda kullananın tüm kaynak kodunu aynı lisansla açmasını gerektirir; bu, MIT
lisanslı bir proje ve kapalı kaynak ticari/savunma kurulumları için kabul edilemez (alternatifi ücretli Ultralytics
kurumsal lisansıdır). YOLOX benzer doğrulukta ve izin verici lisanslıdır.

## Modeller

| Model | Giriş | Dosya | COCO mAP | Ortalama çıkarım (1 iş parçacığı) | Ortalama çıkarım (4 çekirdek) |
|---|---|---|---|---|---|
| `yolox_nano` | 416x416 | 3,7 MB | 25,8 | ~27 ms | ~16 ms |
| **`yolox_tiny`** (varsayılan) | 416x416 | 20 MB | 32,8 | ~88 ms | ~42 ms |
| `yolox_s` | 640x640 | 36 MB | 40,5 | ~291 ms | ~112 ms |

Süreler, entegrasyon testindeki otopark karesinde (768x432 → letterbox) 4 vCPU'lu bir geliştirme makinesinde
ONNX Runtime 1.31 (CPU) ile ölçülmüştür; ön/son işleme dahildir.

### Hız / doğruluk dengesi

Test karesinde (solda park etmiş otomobil, sağ üstte bisikletli küçük bir insan):

- **yolox_nano:** yalnızca otomobili bulur (0,91); uzaktaki insanı ve bisikleti kaçırır.
- **yolox_tiny:** otomobil 0,92, insan 0,65, bisiklet 0,65. Kamera başına saniyede birkaç kare analiz için tek
  çekirdekte yeterince hızlı olduğundan **varsayılandır**.
- **yolox_s:** otomobil 0,94, bisiklet 0,89, insan 0,73; daha güvenilir ama ~3,3 kat yavaş. Güçlü donanımda veya
  az kamerada tercih edilebilir.

## Girdi / çıktı biçimi

Bu `0.1.1rc0` dışa aktarımları:

- **Giriş** `images` [1, 3, H, W] float32: BGR kanal sırası, 0..255 ham değer (normalizasyon yok), CHW. Kare sol
  üst köşeye letterbox edilir, kalan alan 114 gri ile doldurulur (ffmpeg:
  `scale=W:H:force_original_aspect_ratio=decrease,pad=W:H:0:0:color=0x727272`).
- **Çıkış** `output` [1, N, 85]: ızgara kod çözümü grafiğe gömülü değildir; adım 8, 16, 32 ızgaraları üzerinde
  `Gozcu.Vision.YoloxDecoder` tarafından çözülür (416 giriş için N = 3549, 640 için 8400).
