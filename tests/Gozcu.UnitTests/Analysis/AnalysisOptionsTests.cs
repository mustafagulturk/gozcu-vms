using Gozcu.AnalysisService;
using Gozcu.AnalysisService.Pipeline;

namespace Gozcu.UnitTests.Analysis;

public class AnalysisOptionsTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        var options = new AnalysisOptions();

        Assert.Empty(options.Validate());
        Assert.Equal(2, options.FramesPerSecond);
        Assert.Equal("models/yolox_tiny.onnx", options.ModelPath);
        Assert.Equal(0.35f, options.ConfidenceThreshold);
        Assert.Equal(0.45f, options.NmsThreshold);
        Assert.Equal(2, options.MaxConcurrentInferences);
        Assert.Equal(0.5f, options.Events.MinConfidence);
        Assert.Equal(2, options.Events.MinConsecutiveFrames);
        Assert.Equal(TimeSpan.FromSeconds(5), options.Events.EndAfter);
        Assert.Equal(TimeSpan.FromDays(7), options.FrameRetention);
    }

    [Fact]
    public void Invalid_values_are_reported_in_turkish()
    {
        var options = new AnalysisOptions
        {
            FramesPerSecond = 0,
            ModelPath = " ",
            ConfidenceThreshold = 1.5f,
            MaxConcurrentInferences = 0,
            FrameRetention = TimeSpan.Zero,
            Events = { MinConsecutiveFrames = 0, EndAfter = TimeSpan.Zero, MinConfidence = 2 },
        };

        var errors = options.Validate();

        Assert.Equal(8, errors.Count);
        Assert.Contains(errors, e => e.Contains("FramesPerSecond", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("MinConsecutiveFrames en az 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Detection_queue_drops_and_counts_when_full()
    {
        var queue = new DetectionQueue(capacity: 2);
        var output = new AnalysisOutput(null, []);

        Assert.True(queue.TryEnqueue(output));
        Assert.True(queue.TryEnqueue(output));
        Assert.False(queue.TryEnqueue(output));
        Assert.Equal(1, queue.Dropped);
        Assert.Equal(2, queue.Count);
    }
}
