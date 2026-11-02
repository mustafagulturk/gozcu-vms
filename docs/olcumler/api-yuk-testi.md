# API yük testi sonuçları

2026-11-02, 4 vCPU bulut makinesi. Sistem `docker compose up` ile 4 kamerayla çalışıyor (analiz dahil, ~%160 CPU),
yük üreticisi aynı makinede. ~15 dakikalık kayıt birikmiş. Komutlar:

```bash
dotnet run -c Release --project tools/Gozcu.LoadTest -- --scenario all --duration 20s --warmup 3s --concurrency 16
dotnet run -c Release --project tools/Gozcu.LoadTest -- --scenario mixed --rate <200|400|600|800> --duration 30s --concurrency 64
```

## Açık model: karma panel yükü, artan istek hızı

| Hedef istek/sn | Gerçekleşen | Hata | p50 ms | p95 ms | p99 ms | Maks ms | Düşürülen |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 200 | 200.0 | 0 | 6.14 | 14.08 | 22.27 | 122.86 | 0 |
| 400 | 400.0 | 0 | 7.29 | 24.57 | 55.30 | 198.42 | 0 |
| 600 | 599.9 | 0 | 9.09 | 45.57 | 97.28 | 235.97 | 4 |
| 800 | 730.7 | 0 | 20.73 | 198.66 | 356.35 | 862.24 | 2079 |

800 istek/sn ilk denemede 1 adet HTTP 500 vardı: PostgreSQL `53300: too many clients already`. Npgsql'in varsayılan havuz
sınırı (100) üç servisle PostgreSQL'in `max_connections` (100) değerini aşabiliyordu. `Maximum Pool Size=25` ile tekrarlandı:
hata yok, en yüksek eşzamanlı bağlantı 70. Tablodaki 800 satırı düzeltme sonrasıdır.

## Kapalı model: senaryo başına

### Kapalı model, 16 işçi: senaryo: cameras (kapalı model, 16 işçi; ölçüm 20s, ısınma 3s)

| Uç nokta | İstek | Hata | İstek/sn | Ort ms | p50 ms | p90 ms | p95 ms | p99 ms | Maks ms | MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| cameras | 47419 | 0 | 2370.9 | 6.75 | 4.93 | 11.65 | 16.00 | 36.86 | 574.99 | 111.6 |

### Kapalı model, 16 işçi: senaryo: recording-status (kapalı model, 16 işçi; ölçüm 20s, ısınma 3s)

| Uç nokta | İstek | Hata | İstek/sn | Ort ms | p50 ms | p90 ms | p95 ms | p99 ms | Maks ms | MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| recording-status | 182825 | 0 | 9141.2 | 1.75 | 1.36 | 2.91 | 4.06 | 8.45 | 153.78 | 169.5 |

### Kapalı model, 16 işçi: senaryo: analysis-status (kapalı model, 16 işçi; ölçüm 20s, ısınma 3s)

| Uç nokta | İstek | Hata | İstek/sn | Ort ms | p50 ms | p90 ms | p95 ms | p99 ms | Maks ms | MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| analysis-status | 109662 | 0 | 5483.1 | 2.92 | 2.17 | 5.63 | 7.36 | 12.16 | 98.71 | 291.1 |

### Kapalı model, 16 işçi: senaryo: timeline-1h (kapalı model, 16 işçi; ölçüm 20s, ısınma 3s)

| Uç nokta | İstek | Hata | İstek/sn | Ort ms | p50 ms | p90 ms | p95 ms | p99 ms | Maks ms | MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| timeline-1h | 37562 | 0 | 1878.1 | 8.52 | 5.31 | 14.46 | 21.25 | 67.58 | 633.03 | 8.5 |

### Kapalı model, 16 işçi: senaryo: timeline-24h (kapalı model, 16 işçi; ölçüm 20s, ısınma 3s)

| Uç nokta | İstek | Hata | İstek/sn | Ort ms | p50 ms | p90 ms | p95 ms | p99 ms | Maks ms | MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| timeline-24h | 37229 | 0 | 1861.5 | 8.59 | 5.76 | 14.46 | 20.73 | 60.41 | 361.47 | 48.4 |

### Kapalı model, 16 işçi: senaryo: locate (kapalı model, 16 işçi; ölçüm 20s, ısınma 3s)

| Uç nokta | İstek | Hata | İstek/sn | Ort ms | p50 ms | p90 ms | p95 ms | p99 ms | Maks ms | MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| locate | 37637 | 0 | 1881.8 | 8.50 | 5.57 | 14.59 | 20.73 | 54.78 | 568.11 | 7.8 |

### Kapalı model, 16 işçi: senaryo: segment-media (kapalı model, 16 işçi; ölçüm 20s, ısınma 3s)

| Uç nokta | İstek | Hata | İstek/sn | Ort ms | p50 ms | p90 ms | p95 ms | p99 ms | Maks ms | MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| segment-media | 7010 | 0 | 350.5 | 45.65 | 39.94 | 75.78 | 91.14 | 153.60 | 449.23 | 7010.0 |

### Kapalı model, 16 işçi: senaryo: detections (kapalı model, 16 işçi; ölçüm 20s, ısınma 3s)

| Uç nokta | İstek | Hata | İstek/sn | Ort ms | p50 ms | p90 ms | p95 ms | p99 ms | Maks ms | MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| detections | 17862 | 0 | 893.1 | 17.91 | 15.87 | 28.67 | 34.30 | 52.22 | 213.25 | 457.1 |

### Kapalı model, 16 işçi: senaryo: events (kapalı model, 16 işçi; ölçüm 20s, ısınma 3s)

| Uç nokta | İstek | Hata | İstek/sn | Ort ms | p50 ms | p90 ms | p95 ms | p99 ms | Maks ms | MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| events | 27586 | 0 | 1379.3 | 11.60 | 9.98 | 18.94 | 23.55 | 39.94 | 136.13 | 607.9 |

### Kapalı model, 16 işçi: senaryo: event-search (kapalı model, 16 işçi; ölçüm 20s, ısınma 3s)

| Uç nokta | İstek | Hata | İstek/sn | Ort ms | p50 ms | p90 ms | p95 ms | p99 ms | Maks ms | MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| event-search | 28236 | 0 | 1411.8 | 11.33 | 9.86 | 18.43 | 22.53 | 35.33 | 128.87 | 378.6 |

### Kapalı model, 16 işçi: senaryo: mixed (kapalı model, 16 işçi; ölçüm 20s, ısınma 3s)

| Uç nokta | İstek | Hata | İstek/sn | Ort ms | p50 ms | p90 ms | p95 ms | p99 ms | Maks ms | MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| cameras | 3718 | 0 | 185.9 | 10.64 | 4.74 | 23.81 | 37.38 | 94.21 | 322.39 | 8.8 |
| timeline-1h | 3698 | 0 | 184.9 | 11.21 | 7.10 | 22.27 | 34.30 | 74.75 | 310.85 | 0.8 |
| segment-media | 3585 | 0 | 179.2 | 27.94 | 22.78 | 53.76 | 65.53 | 99.33 | 300.81 | 3585.0 |
| locate | 1773 | 0 | 88.7 | 11.00 | 7.36 | 22.02 | 32.00 | 67.58 | 157.14 | 0.4 |
| analysis-status | 1843 | 0 | 92.2 | 16.52 | 9.73 | 40.45 | 55.30 | 94.21 | 162.43 | 4.9 |
| recording-status | 905 | 0 | 45.2 | 3.30 | 2.00 | 7.10 | 10.37 | 20.73 | 26.66 | 0.8 |
| timeline-24h | 879 | 0 | 44.0 | 11.12 | 7.29 | 23.04 | 32.00 | 84.99 | 180.79 | 1.5 |
| detections | 882 | 0 | 44.1 | 46.19 | 35.33 | 92.16 | 116.73 | 188.41 | 324.63 | 23.4 |
| event-search | 939 | 0 | 47.0 | 37.49 | 28.41 | 75.78 | 100.35 | 163.84 | 327.90 | 12.6 |
| **TOPLAM** | 18222 | 0 | 911.1 | 17.55 | 9.60 | 43.01 | 58.88 | 108.54 | 327.90 | 3638.2 |
