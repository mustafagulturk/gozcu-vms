using Gozcu.Contracts.Analysis;
using Gozcu.Vision;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gozcu.UnitTests.Vision;

/// <summary>Model dosyası gerektirmeyen denetimler; gerçek çıkarım entegrasyon testlerindedir.</summary>
public class OnnxYoloxDetectorTests
{
    [Theory]
    [InlineData("1,3,416,416", "1,3549,85", 416, 416)]
    [InlineData("1,3,640,640", "1,8400,85", 640, 640)]
    [InlineData("-1,3,416,416", "-1,-1,85", 416, 416)]
    public void Valid_yolox_shapes_give_input_size(string input, string output, int width, int height)
    {
        Assert.Equal((width, height), OnnxYoloxDetector.ValidateShapes(Shape(input), Shape(output)));
    }

    [Theory]
    [InlineData("1,3,-1,-1", "1,3549,85")]   // dinamik giriş boyutu
    [InlineData("1,416,416,3", "1,3549,85")] // HWC giriş
    [InlineData("1,3,416,416", "1,8400,85")]  // giriş/çıkış uyumsuz
    [InlineData("1,3,416,416", "1,3549,84")]  // YOLOv8 benzeri, nesnellik yok
    [InlineData("1,3,416,416", "3549,85")]
    public void Unexpected_shapes_are_rejected(string input, string output)
    {
        Assert.Throws<InvalidOperationException>(() => OnnxYoloxDetector.ValidateShapes(Shape(input), Shape(output)));
    }

    [Fact]
    public void Missing_model_file_explains_how_to_download_it()
    {
        var options = new YoloxDetectorOptions { ModelPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.onnx") };

        var error = Assert.Throws<FileNotFoundException>(() => new OnnxYoloxDetector(options, NullLogger.Instance));

        Assert.Contains("Model dosyası bulunamadı", error.Message, StringComparison.Ordinal);
        Assert.Contains("scripts/download-model.sh", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_options_are_valid()
    {
        var options = new YoloxDetectorOptions();

        options.Validate();
        Assert.Equal(0.35f, options.ConfidenceThreshold);
        Assert.Equal(0.45f, options.NmsThreshold);
        Assert.Equal(1, options.IntraOpThreads);
    }

    [Theory]
    [InlineData("", 0.35f, 0.45f, 100, 1, "ModelPath")]
    [InlineData("m.onnx", 0f, 0.45f, 100, 1, "ConfidenceThreshold")]
    [InlineData("m.onnx", 1.5f, 0.45f, 100, 1, "ConfidenceThreshold")]
    [InlineData("m.onnx", float.NaN, 0.45f, 100, 1, "ConfidenceThreshold")]
    [InlineData("m.onnx", 0.35f, 0f, 100, 1, "NmsThreshold")]
    [InlineData("m.onnx", 0.35f, 0.45f, 0, 1, "MaxDetections")]
    [InlineData("m.onnx", 0.35f, 0.45f, 100, -1, "IntraOpThreads")]
    public void Invalid_options_are_rejected(string path, float confidence, float nms, int max, int threads, string parameter)
    {
        var options = new YoloxDetectorOptions
        {
            ModelPath = path,
            ConfidenceThreshold = confidence,
            NmsThreshold = nms,
            MaxDetections = max,
            IntraOpThreads = threads,
        };

        var error = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Equal(parameter, error.ParamName);
    }

    [Fact]
    public void Coco_map_keeps_only_people_and_vehicles()
    {
        Assert.Equal(CocoClassMap.ClassCount, CocoClassMap.Labels.Count);
        Assert.True(CocoClassMap.TryGetTarget(0, out var person, out var personCategory));
        Assert.Equal(("person", ObjectCategory.Person), (person, personCategory));

        var vehicles = Enumerable.Range(0, CocoClassMap.ClassCount)
            .Where(i => CocoClassMap.GetCategory(i) == ObjectCategory.Vehicle)
            .Select(i => CocoClassMap.Labels[i]);
        Assert.Equal(["bicycle", "car", "motorcycle", "bus", "truck"], vehicles);

        Assert.False(CocoClassMap.TryGetTarget(16, out _, out _)); // dog
        Assert.Null(CocoClassMap.GetCategory(-1));
        Assert.Null(CocoClassMap.GetCategory(80));
    }

    private static int[] Shape(string dimensions) =>
        dimensions.Split(',').Select(d => int.Parse(d, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
}
