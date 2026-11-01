# Mikro ölçümler (BenchmarkDotNet)

`Gozcu.Benchmarks`, sıcak yolları (her karede, her HTTP isteğinde ya da her saklama turunda çalışan kodu)
[BenchmarkDotNet](https://benchmarkdotnet.org/) ile ölçer. Her sınıf tek bir bileşeni ölçer ve neden önemli
olduğunu XML açıklamasında anlatır. Tüm sınıflarda `[MemoryDiagnoser]` açıktır.

| Klasör | Sınıf | Ne ölçer |
|---|---|---|
| `Vision/` | `FrameTensorBenchmarks` | BGR24 kare → CHW float tensör (416 ve 640) |
| | `YoloxDecoderBenchmarks` | 3549×85 ham YOLOX çıktısının kod çözümü (0/30/300 aday) |
| | `NonMaxSuppressionBenchmarks` | 50/200/1000 aday kutuda NMS |
| | `LetterboxGeometryBenchmarks` | Kutuyu kaynak kareye göre normalize etme |
| | `OnnxYoloxDetectorBenchmarks` | `yolox_tiny.onnx` ile uçtan uca tespit, 1/2/4 iş parçacığı |
| `Media/` | `RawFrameReaderBenchmarks` | ffmpeg borusundan ham kare okuma (tam kare / 64 KiB parçalar) |
| | `FfmpegProgressParserBenchmarks` | `-progress` satırlarını ayrıştırma |
| | `SegmentFileNameBenchmarks` | Parça dosya adı üretme/ayrıştırma |
| | `ConcatListBenchmarks` | ffmpeg concat listesi üretme (60 / 1440 girdi) |
| `Recording/` | `PlaybackPlannerBenchmarks` | 1440 parçalık bir günde zaman çizelgesi ve konum bulma |
| | `ClipPlannerBenchmarks` | 1440 parçalık günden 5 dk / 1 saat klip planı |
| | `RetentionPlannerBenchmarks` | 10.000 / 100.000 parçada saklama planı |

## Çalıştırma

Ölçümler mutlaka **Release** yapılandırmasında çalıştırılmalıdır (Debug derlemede BenchmarkDotNet uyarır ve
sonuçlar anlamsızdır):

```bash
# Hepsi (birkaç dakika sürer)
dotnet run -c Release --project benchmarks/Gozcu.Benchmarks -- --filter '*'

# Yalnızca bir sınıf ya da bir grup
dotnet run -c Release --project benchmarks/Gozcu.Benchmarks -- --filter '*NonMaxSuppression*'
dotnet run -c Release --project benchmarks/Gozcu.Benchmarks -- --filter 'Gozcu.Benchmarks.Recording.*'

# Hızlı duman testi: her ölçüm bir kez çalışır, sayılar anlamsızdır ama kodun çalıştığı doğrulanır
dotnet run -c Release --project benchmarks/Gozcu.Benchmarks -- --filter '*' --job Dry

# Ölçüm listesini görmek için
dotnet run -c Release --project benchmarks/Gozcu.Benchmarks -- --list flat
```

Sonuçlar konsolun yanında `BenchmarkDotNet.Artifacts/results/` altına Markdown, CSV ve HTML olarak yazılır
(klasör `.gitignore`'dadır).

`OnnxYoloxDetectorBenchmarks` için model dosyası gerekir: önce `GOZCU_MODEL_PATH` ortam değişkenine bakılır,
yoksa çalışma klasöründen yukarı doğru `models/yolox_tiny.onnx` aranır. Model yoksa yalnızca bu sınıfın
ölçümleri açıklayıcı bir hatayla başarısız olur; model `./scripts/download-model.sh` ile indirilir. ffmpeg
kuruluysa çıkarım, entegrasyon testlerindeki otopark görüntüsü üzerinde yapılır; değilse rastgele bir kare
kullanılır (bu durumda tespit sayısı ve kod çözme maliyeti daha düşük olur).

Güvenilir sonuç için makinede başka yük olmamalı, dizüstü bilgisayar prizde olmalıdır. Aynı makinede yapılan iki
ölçüm arasındaki farka bakın; farklı makinelerin mutlak sayıları karşılaştırılamaz.

## Sonuçları okuma

Örnek çıktı (sayılar yalnızca biçimi göstermek içindir):

```
| Method | Boxes | Mean      | Error    | StdDev   | Gen0   | Allocated |
|------- |------ |----------:|---------:|---------:|-------:|----------:|
| Apply  | 50    |   2.10 us | 0.010 us | 0.009 us | 0.1564 |   1.98 KB |
| Apply  | 1000  | 120.40 us | 0.500 us | 0.470 us | 3.0000 |  18.79 KB |
```

- **Mean**: bir işlemin ortalama süresi (`ns` < `us` < `ms`). `OperationsPerInvoke` kullanan ölçümlerde
  (ör. `RawFrameReaderBenchmarks`, `FfmpegProgressParserBenchmarks`, `LetterboxGeometryBenchmarks.ToNormalized`)
  süre *kare / blok / kutu başınadır*. Sınıf açıklamasında hangisinin geçerli olduğu yazar.
- **Error / StdDev**: ölçümün güven aralığının yarısı ve standart sapma. Mean'e göre büyükse (ör. %10'dan
  fazla) makine gürültülüdür; ölçümü tekrarlayın. İki sonuç arasındaki fark Error'dan küçükse fark yok sayılır.
- **Ratio**: `Baseline = true` işaretli ölçüme oran (ör. 1,42 = %42 daha yavaş).
- **Allocated**: bir işlemin yönetilen yığında (heap) ayırdığı bellek. Sıcak yolda bu değer düşük ve sabit
  olmalıdır; her ayırma çöp toplayıcıya (GC) iş çıkarır ve kameralar arttıkça duraksamalara yol açar.
  `-` ya da `0 B` hiç ayırma olmadığı demektir. Girdiyle doğrusal büyüyen Allocated (ör. NMS'de kutu sayısı)
  beklenir; sabit girdide büyüyorsa sızıntı ya da gereksiz kopya vardır.
- **Gen0/Gen1/Gen2**: 1000 işlem başına tetiklenen çöp toplama sayısı. Gen1/Gen2'nin sıfırdan büyük olması
  uzun ömürlü ya da büyük (85 KB üstü) nesneler ayrıldığını gösterir.
- `Dry` işinde sayılar tek, ısınmamış (JIT dahil) bir çalıştırmadan gelir; yalnızca kodun çalıştığını
  doğrulamak içindir, performans yorumu yapılmaz.

Kare başına bütçe için kaba bir ölçü: `OnnxYoloxDetectorBenchmarks.Detect` Mean'i 40 ms ise tek iş parçacığı
saniyede ~25 kare işler; analiz servisi kamera başına saniyede birkaç kare örneklediğinden çekirdek başına
kaç kamera analiz edilebileceği buradan hesaplanır. Ön işleme (`FrameTensor`), kod çözme ve NMS toplamı
çıkarımın yanında küçük kalmalıdır.
