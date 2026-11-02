using System.Buffers;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Gozcu.Vision;

/// <summary>
/// ONNX Runtime (CPU) ile YOLOX çıkarımı. Tek bir <see cref="InferenceSession"/> paylaşılır; ONNX Runtime'ın
/// <c>Run</c> çağrısı iş parçacığı güvenli olduğu için <see cref="Detect"/> farklı kameralardan aynı anda çağrılabilir.
/// Giriş tensörü için tampon <see cref="ArrayPool{T}"/>'dan kiralanır.
/// </summary>
public sealed class OnnxYoloxDetector : IObjectDetector, IDisposable
{
    private readonly InferenceSession _session;
    private readonly YoloxDetectorOptions _options;
    private readonly string[] _inputNames;
    private readonly string[] _outputNames;
    private readonly long[] _inputShape;
    private bool _disposed;

    public OnnxYoloxDetector(YoloxDetectorOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();
        _options = options;

        var modelPath = Path.GetFullPath(options.ModelPath);
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException(
                $"Model dosyası bulunamadı: {modelPath}. Modeli scripts/download-model.sh ile indirin.", modelPath);
        }

        using (var sessionOptions = new SessionOptions())
        {
            sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            sessionOptions.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            sessionOptions.IntraOpNumThreads = options.IntraOpThreads;
            sessionOptions.InterOpNumThreads = 1;
            _session = new InferenceSession(modelPath, sessionOptions);
        }

        try
        {
            if (_session.InputMetadata.Count != 1 || _session.OutputMetadata.Count != 1)
            {
                throw new InvalidOperationException(
                    $"YOLOX modelinin tek girişi ve tek çıkışı olmalı; {_session.InputMetadata.Count} giriş, {_session.OutputMetadata.Count} çıkış var: {modelPath}");
            }

            var (inputName, input) = _session.InputMetadata.First();
            var (outputName, output) = _session.OutputMetadata.First();
            if (input.ElementDataType != TensorElementType.Float || output.ElementDataType != TensorElementType.Float)
            {
                throw new InvalidOperationException($"YOLOX modelinin giriş ve çıkışı float32 olmalı: {modelPath}");
            }

            var (width, height) = ValidateShapes(input.Dimensions, output.Dimensions);
            _inputNames = [inputName];
            _outputNames = [outputName];
            _inputShape = [1, 3, height, width];
            Model = new DetectorModelInfo(Path.GetFileNameWithoutExtension(modelPath), width, height);
        }
        catch
        {
            _session.Dispose();
            throw;
        }

        logger.LogInformation(
            "YOLOX modeli yüklendi: {Model} ({Width}x{Height}), güven eşiği {Confidence}, NMS eşiği {Nms}, iş parçacığı {Threads}",
            Model.Name, Model.InputWidth, Model.InputHeight, options.ConfidenceThreshold, options.NmsThreshold,
            options.IntraOpThreads == 0 ? "varsayılan" : options.IntraOpThreads);
    }

    public DetectorModelInfo Model { get; }

    public IReadOnlyList<Detection> Detect(ReadOnlySpan<byte> bgrFrame, LetterboxGeometry geometry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry.ModelWidth != Model.InputWidth || geometry.ModelHeight != Model.InputHeight)
        {
            throw new ArgumentException(
                $"Letterbox geometrisi {geometry.ModelWidth}x{geometry.ModelHeight} model girişi için; model {Model.InputWidth}x{Model.InputHeight} bekliyor.",
                nameof(geometry));
        }

        var length = FrameTensor.Length(Model.InputWidth, Model.InputHeight);
        var buffer = ArrayPool<float>.Shared.Rent(length);
        try
        {
            FrameTensor.FillChw(bgrFrame, Model.InputWidth, Model.InputHeight, buffer);
            using var input = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, buffer.AsMemory(0, length), _inputShape);
            using var runOptions = new RunOptions();
            using var outputs = _session.Run(runOptions, _inputNames, [input], _outputNames);
            var candidates = YoloxDecoder.Decode(
                outputs[0].GetTensorDataAsSpan<float>(), Model.InputWidth, Model.InputHeight, _options.ConfidenceThreshold, geometry);
            return NonMaxSuppression.Apply(candidates, _options.NmsThreshold, _options.MaxDetections);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Dispose();
    }

    /// <summary>
    /// Giriş [1, 3, H, W] ve çıkış [1, N, 85] biçimini doğrular; giriş boyutunu döndürür. Toplu (batch) boyut ve N
    /// dinamik (-1) olabilir, H ve W sabit olmalıdır.
    /// </summary>
    internal static (int Width, int Height) ValidateShapes(IReadOnlyList<int> input, IReadOnlyList<int> output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (input.Count != 4 || input[0] is not (1 or -1) || input[1] != 3 || input[2] <= 0 || input[3] <= 0)
        {
            throw new InvalidOperationException(
                $"Model girişi sabit boyutlu [1, 3, H, W] olmalı; [{string.Join(", ", input)}] bulundu.");
        }

        int height = input[2], width = input[3];
        var anchors = YoloxDecoder.AnchorCount(width, height);
        if (output.Count != 3 || output[0] is not (1 or -1) || output[1] is not -1 && output[1] != anchors
            || output[2] != YoloxDecoder.ValuesPerAnchor)
        {
            throw new InvalidOperationException(
                $"Model çıkışı [1, {anchors}, {YoloxDecoder.ValuesPerAnchor}] olmalı (kod çözümü grafiğe gömülmemiş YOLOX); [{string.Join(", ", output)}] bulundu.");
        }

        return (width, height);
    }
}
