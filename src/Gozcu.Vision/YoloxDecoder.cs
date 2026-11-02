namespace Gozcu.Vision;

/// <summary>
/// YOLOX ham çıktısının ([1, N, 85]) kod çözümü. 0.1.1rc0 sürümüyle yayınlanan ONNX dosyalarında ızgara ofsetleri
/// ve adımlar grafiğe gömülü değildir; burada uygulanır. Her satır: ofset x, ofset y, log genişlik, log yükseklik,
/// nesnellik ve 80 sınıf skoru (sigmoid uygulanmış).
/// </summary>
public static class YoloxDecoder
{
    /// <summary>Satır başına değer sayısı: 4 kutu + 1 nesnellik + 80 sınıf.</summary>
    public const int ValuesPerAnchor = 5 + CocoClassMap.ClassCount;

    /// <summary>Özellik piramidi adımları; çıktı satırları bu sırayla (önce 8, sonra 16, sonra 32) dizilir.</summary>
    private static readonly int[] Strides = [8, 16, 32];

    /// <summary>Verilen giriş boyutu için çıktıdaki satır (çapa) sayısı; 416×416 için 3549, 640×640 için 8400.</summary>
    public static int AnchorCount(int inputWidth, int inputHeight)
    {
        var count = 0;
        foreach (var stride in Strides)
        {
            count += inputWidth / stride * (inputHeight / stride);
        }

        return count;
    }

    /// <summary>
    /// Ham çıktıdan, güven eşiğini geçen ve izlenen sınıflara (insan, araç) ait aday tespitleri çıkarır. NMS uygulanmaz.
    /// </summary>
    /// <param name="output">Modelin düzleştirilmiş çıktısı; uzunluğu <c>AnchorCount × 85</c> olmalı.</param>
    /// <param name="inputWidth">Model giriş genişliği (piksel).</param>
    /// <param name="inputHeight">Model giriş yüksekliği (piksel).</param>
    /// <param name="confidenceThreshold">Nesnellik × sınıf skoru için alt sınır.</param>
    /// <param name="geometry">Kutuları kaynak kareye göre normalize etmek için letterbox geometrisi.</param>
    public static List<Detection> Decode(
        ReadOnlySpan<float> output, int inputWidth, int inputHeight, float confidenceThreshold, LetterboxGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var anchors = AnchorCount(inputWidth, inputHeight);
        if (output.Length != anchors * ValuesPerAnchor)
        {
            throw new ArgumentException(
                $"Model çıktısı {inputWidth}x{inputHeight} giriş için {anchors}x{ValuesPerAnchor} = {anchors * ValuesPerAnchor} değer içermeli; {output.Length} değer geldi.",
                nameof(output));
        }

        var candidates = new List<Detection>();
        var row = 0;
        foreach (var stride in Strides)
        {
            var gridWidth = inputWidth / stride;
            var gridHeight = inputHeight / stride;
            for (var gridY = 0; gridY < gridHeight; gridY++)
            {
                for (var gridX = 0; gridX < gridWidth; gridX++, row++)
                {
                    var values = output.Slice(row * ValuesPerAnchor, ValuesPerAnchor);

                    // Sınıf skoru en fazla 1 olduğundan nesnellik eşiğin altındaysa skor da altındadır.
                    var objectness = values[4];
                    if (objectness < confidenceThreshold)
                    {
                        continue;
                    }

                    var classScores = values[5..];
                    var bestClass = 0;
                    var bestScore = classScores[0];
                    for (var c = 1; c < classScores.Length; c++)
                    {
                        if (classScores[c] > bestScore)
                        {
                            bestScore = classScores[c];
                            bestClass = c;
                        }
                    }

                    var confidence = objectness * bestScore;
                    if (confidence < confidenceThreshold || !CocoClassMap.TryGetTarget(bestClass, out var label, out var category))
                    {
                        continue;
                    }

                    var centerX = (values[0] + gridX) * stride;
                    var centerY = (values[1] + gridY) * stride;
                    var width = MathF.Exp(values[2]) * stride;
                    var height = MathF.Exp(values[3]) * stride;
                    var box = geometry.ToNormalized(centerX - (width / 2), centerY - (height / 2), width, height);
                    if (box.Width <= 0 || box.Height <= 0)
                    {
                        // Kutu tamamen dolgu alanında; kaynak karede karşılığı yok.
                        continue;
                    }

                    candidates.Add(new Detection(label, category, confidence, box));
                }
            }
        }

        return candidates;
    }
}
