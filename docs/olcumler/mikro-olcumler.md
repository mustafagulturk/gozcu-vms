# Mikro ölçümler (BenchmarkDotNet, ShortRun)

2026-11-01, 4 vCPU bulut makinesi, .NET 8, `--job short` (3 yineleme; hata payı geniş, eğilim için). Komut: `dotnet run -c Release --project benchmarks/Gozcu.Benchmarks -- --filter '*' --job short`

## Media.ConcatListBenchmarks

| Method | Entries | Mean      | Error      | StdDev    | Gen0    | Gen1    | Gen2    | Allocated |
|------- |-------- |----------:|-----------:|----------:|--------:|--------:|--------:|----------:|
| Build  | 60      |  12.21 μs |   4.014 μs |  0.220 μs |  1.5411 |  0.0610 |       - |   26.1 KB |
| Build  | 1440    | 433.91 μs | 541.362 μs | 29.674 μs | 55.1758 | 55.1758 | 55.1758 | 540.98 KB |

## Media.FfmpegProgressParserBenchmarks

| Method      | Mean     | Error    | StdDev   | Gen0   | Allocated |
|------------ |---------:|---------:|---------:|-------:|----------:|
| ParseBlocks | 842.9 ns | 657.8 ns | 36.06 ns | 0.0598 |   1.01 KB |

## Media.RawFrameReaderBenchmarks

| Method     | ChunkSize | Mean     | Error    | StdDev   | Allocated |
|----------- |---------- |---------:|---------:|---------:|----------:|
| ReadFrames | 0         | 54.68 μs | 87.96 μs | 4.822 μs |       2 B |
| ReadFrames | 65536     | 64.25 μs | 73.82 μs | 4.047 μs |       2 B |

## Media.SegmentFileNameBenchmarks

| Method              | Mean      | Error       | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|-------------------- |----------:|------------:|----------:|------:|--------:|-------:|----------:|------------:|
| Format              | 217.80 ns |   305.31 ns | 16.735 ns |  0.25 |    0.02 | 0.0074 |     128 B |        0.22 |
| TryParseFileName    | 892.21 ns | 1,411.07 ns | 77.346 ns |  1.01 |    0.11 | 0.0334 |     592 B |        1.00 |
| TryParseFullPath    | 833.46 ns |   934.47 ns | 51.221 ns |  0.94 |    0.09 | 0.0381 |     664 B |        1.12 |
| TryParseForeignFile |  73.56 ns |    78.49 ns |  4.302 ns |  0.08 |    0.01 | 0.0023 |      40 B |        0.07 |

## Recording.ClipPlannerBenchmarks

| Method                        | Mean     | Error     | StdDev  | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|------------------------------ |---------:|----------:|--------:|------:|--------:|-------:|-------:|----------:|------------:|
| PlanFiveMinutes               | 114.8 μs |  84.70 μs | 4.64 μs |  1.01 |    0.07 | 2.9297 | 0.2441 |  51.56 KB |        0.93 |
| PlanOneHour                   | 113.7 μs | 142.98 μs | 7.84 μs |  1.00 |    0.08 | 3.1738 | 0.4883 |  55.52 KB |        1.00 |
| PlanAndBuildConcatListOneHour | 128.5 μs |  19.20 μs | 1.05 μs |  1.13 |    0.07 | 5.1270 | 0.4883 |   89.5 KB |        1.61 |

## Recording.PlaybackPlannerBenchmarks

| Method               | Mean     | Error     | StdDev   | Ratio | RatioSD | Gen0    | Gen1   | Allocated | Alloc Ratio |
|--------------------- |---------:|----------:|---------:|------:|--------:|--------:|-------:|----------:|------------:|
| BuildTimelineFullDay | 223.1 μs | 243.90 μs | 13.37 μs |  1.00 |    0.07 | 14.4043 | 3.6621 |  243.8 KB |        1.00 |
| BuildTimelineOneHour | 116.3 μs |  12.38 μs |  0.68 μs |  0.52 |    0.03 |  3.5400 | 0.4883 |   59.8 KB |        0.25 |
| LocateNoon           | 101.3 μs |  37.24 μs |  2.04 μs |  0.46 |    0.03 |  2.9297 | 0.3662 |  51.09 KB |        0.21 |

## Recording.RetentionPlannerBenchmarks

| Method     | Segments | Mean       | Error      | StdDev     | Ratio | RatioSD | Gen0     | Gen1     | Gen2     | Allocated  | Alloc Ratio |
|----------- |--------- |-----------:|-----------:|-----------:|------:|--------:|---------:|---------:|---------:|-----------:|------------:|
| UnderQuota | 10000    |   2.952 ms |   2.705 ms |  0.1483 ms |  1.00 |    0.06 |  46.8750 |  46.8750 |  46.8750 |   352.1 KB |        1.00 |
| OverQuota  | 10000    |   4.063 ms |   7.778 ms |  0.4263 ms |  1.38 |    0.14 |  85.9375 |  85.9375 |  85.9375 |   608.4 KB |        1.73 |
|            |          |            |            |            |       |         |          |          |          |            |             |
| UnderQuota | 100000   |  77.990 ms | 286.716 ms | 15.7159 ms |  1.03 |    0.26 | 142.8571 | 142.8571 | 142.8571 | 3517.45 KB |        1.00 |
| OverQuota  | 100000   | 106.530 ms |  88.895 ms |  4.8727 ms |  1.41 |    0.26 | 166.6667 | 166.6667 | 166.6667 | 5564.69 KB |        1.58 |

## Vision.FrameTensorBenchmarks

| Method  | Size | Mean     | Error    | StdDev   | Allocated |
|-------- |----- |---------:|---------:|---------:|----------:|
| FillChw | 416  | 359.6 μs | 290.8 μs | 15.94 μs |         - |
| FillChw | 640  | 933.5 μs | 816.0 μs | 44.73 μs |       1 B |

## Vision.LetterboxGeometryBenchmarks

| Method           | Mean     | Error    | StdDev   | Gen0   | Allocated |
|----------------- |---------:|---------:|---------:|-------:|----------:|
| ToNormalized     | 18.21 ns | 15.78 ns | 0.865 ns |      - |         - |
| CreateAndMeasure | 29.06 ns | 15.91 ns | 0.872 ns | 0.0018 |      32 B |

## Vision.NonMaxSuppressionBenchmarks

| Method | Boxes | Mean       | Error       | StdDev    | Gen0   | Allocated |
|------- |------ |-----------:|------------:|----------:|-------:|----------:|
| Apply  | 50    |   3.114 μs |   0.7527 μs | 0.0413 μs | 0.0725 |   1.26 KB |
| Apply  | 200   |  23.153 μs |  26.0046 μs | 1.4254 μs | 0.2136 |   4.02 KB |
| Apply  | 1000  | 337.625 μs | 180.7002 μs | 9.9048 μs | 0.9766 |  18.07 KB |

## Vision.OnnxYoloxDetectorBenchmarks

| Method | IntraOpThreads | Mean     | Error     | StdDev   | Allocated |
|------- |--------------- |---------:|----------:|---------:|----------:|
| Detect | 1              | 77.21 ms |  8.865 ms | 0.486 ms |    3.2 KB |
| Detect | 2              | 49.45 ms | 31.329 ms | 1.717 ms |   3.11 KB |
| Detect | 4              | 31.17 ms | 37.405 ms | 2.050 ms |    3.1 KB |

## Vision.YoloxDecoderBenchmarks

| Method | HotAnchors | Mean     | Error     | StdDev   | Gen0   | Allocated |
|------- |----------- |---------:|----------:|---------:|-------:|----------:|
| Decode | 0          | 10.28 μs |  8.597 μs | 0.471 μs |      - |      32 B |
| Decode | 30         | 13.89 μs | 19.421 μs | 1.065 μs | 0.0763 |    1568 B |
| Decode | 300        | 48.05 μs | 19.470 μs | 1.067 μs | 0.7324 |   12904 B |
