using Gozcu.Contracts.Analysis;
using Gozcu.Vision;

namespace Gozcu.IntegrationTests.Vision;

/// <summary>
/// Gerçek YOLOX modeliyle uçtan uca tespit: ffmpeg letterbox → BGR24 → ONNX Runtime → kod çözme → NMS.
/// Model dosyası (scripts/download-model.sh) ve ana makinede ffmpeg gerektirir; Docker gerekmez.
/// </summary>
public sealed class OnnxYoloxDetectorIntegrationTests(DetectorFixture fixture) : IClassFixture<DetectorFixture>
{
    // Intel IoT DevKit "person-bicycle-car-detection" videosunun 49. saniyesi (768x432): solda park etmiş bir
    // otomobil, sağ üstte bisikletiyle bir insan. Kaynak ve lisans: TestData/README.md
    private const string Fixture = "TestData/parking-person-bicycle-car.jpg";
    private const int SourceWidth = 768;
    private const int SourceHeight = 432;

    [Fact]
    public void Model_input_size_is_read_from_the_model()
    {
        var model = fixture.Detector.Model;

        Assert.Equal(Path.GetFileNameWithoutExtension(fixture.ModelPath), model.Name);
        Assert.Equal(model.InputWidth, model.InputHeight);
        Assert.True(model.InputWidth is 416 or 640, $"Beklenmeyen giriş boyutu: {model.InputWidth}");
    }

    [Fact]
    public async Task Detects_car_person_and_bicycle_in_parking_lot_frame()
    {
        var (frame, geometry) = await LoadFixtureAsync();

        var detections = fixture.Detector.Detect(frame, geometry);

        var summary = string.Join("; ", detections.Select(d => $"{d.Label} {d.Confidence:F2} {d.Box}"));
        Assert.True(detections.Count >= 3, $"En az 3 tespit bekleniyordu: {summary}");
        Assert.Equal(detections.OrderByDescending(d => d.Confidence), detections);

        // Python başvuru uygulaması: otomobil 0.92, piksel [x=0, y=49, w=135, h=106] → normalize ≈ (0, 0.11, 0.18, 0.25).
        var car = detections.FirstOrDefault(d => d.Label == "car");
        Assert.True(car is not null, $"Otomobil bulunamadı: {summary}");
        Assert.Equal(ObjectCategory.Vehicle, car.Category);
        Assert.True(car.Confidence > 0.8f, $"Otomobil güveni düşük: {car.Confidence}");
        Assert.Equal(0f, car.Box.X, 0.03f);
        Assert.Equal(49f / SourceHeight, car.Box.Y, 0.03f);
        Assert.Equal(135f / SourceWidth, car.Box.Width, 0.03f);
        Assert.Equal(106f / SourceHeight, car.Box.Height, 0.03f);

        // İnsan [683, 31, 29, 40] ve bisiklet [653, 61, 76, 41] sağ üst köşede.
        var person = Assert.Single(detections, d => d.Category == ObjectCategory.Person);
        Assert.Equal("person", person.Label);
        Assert.Equal((683f + 14.5f) / SourceWidth, person.Box.X + (person.Box.Width / 2), 0.03f);
        Assert.Equal((31f + 20f) / SourceHeight, person.Box.Y + (person.Box.Height / 2), 0.03f);

        var bicycle = detections.FirstOrDefault(d => d.Label == "bicycle");
        Assert.True(bicycle is not null, $"Bisiklet bulunamadı: {summary}");
        Assert.Equal(ObjectCategory.Vehicle, bicycle.Category);
        Assert.Equal((653f + 38f) / SourceWidth, bicycle.Box.X + (bicycle.Box.Width / 2), 0.03f);
    }

    [Fact]
    public void Empty_gray_frame_has_no_detections()
    {
        var model = fixture.Detector.Model;
        var frame = new byte[model.InputWidth * model.InputHeight * 3];
        Array.Fill(frame, (byte)114);

        var detections = fixture.Detector.Detect(frame, new LetterboxGeometry(model.InputWidth, model.InputHeight, 1280, 720));

        Assert.Empty(detections);
    }

    [Fact]
    public async Task Concurrent_calls_on_shared_session_give_identical_results()
    {
        var (frame, geometry) = await LoadFixtureAsync();
        var expected = fixture.Detector.Detect(frame, geometry);

        var results = new IReadOnlyList<Detection>[16];
        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            i => results[i] = fixture.Detector.Detect(frame, geometry));

        Assert.All(results, result => Assert.Equal(expected, result));
    }

    [Fact]
    public void Frame_of_wrong_size_is_rejected()
    {
        var model = fixture.Detector.Model;
        var geometry = new LetterboxGeometry(model.InputWidth, model.InputHeight, SourceWidth, SourceHeight);

        Assert.Throws<ArgumentException>(() => fixture.Detector.Detect(new byte[100], geometry));
        Assert.Throws<ArgumentException>(() => fixture.Detector.Detect(
            new byte[model.InputWidth * model.InputHeight * 3], new LetterboxGeometry(model.InputWidth + 32, model.InputHeight + 32, SourceWidth, SourceHeight)));
    }

    private async Task<(byte[] Frame, LetterboxGeometry Geometry)> LoadFixtureAsync()
    {
        var model = fixture.Detector.Model;
        var path = Path.Combine(AppContext.BaseDirectory, Fixture);
        var frame = await DetectorFixture.LetterboxAsync(path, model.InputWidth, model.InputHeight);
        return (frame, new LetterboxGeometry(model.InputWidth, model.InputHeight, SourceWidth, SourceHeight));
    }
}
