using Gozcu.LoadTest.Metrics;

namespace Gozcu.UnitTests.LoadTest;

public class LatencyHistogramTests
{
    [Fact]
    public void Empty_histogram_reports_zeros()
    {
        var h = new LatencyHistogram();

        Assert.Equal(0, h.TotalCount);
        Assert.Equal(0, h.Percentile(99));
        Assert.Equal(0, h.Mean);
        Assert.Equal(0, h.Min);
        Assert.Equal(0, h.Max);
    }

    [Fact]
    public void Small_values_are_exact()
    {
        var h = new LatencyHistogram();
        for (var v = 1; v <= 100; v++)
        {
            h.Record(v);
        }

        Assert.Equal(50, h.Percentile(50));
        Assert.Equal(90, h.Percentile(90));
        Assert.Equal(99, h.Percentile(99));
        Assert.Equal(100, h.Percentile(100));
        Assert.Equal(1, h.Percentile(0));
        Assert.Equal(50.5, h.Mean, 6);
        Assert.Equal(1, h.Min);
        Assert.Equal(100, h.Max);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(90)]
    [InlineData(95)]
    [InlineData(99)]
    [InlineData(99.9)]
    public void Large_values_are_within_relative_precision(double percentile)
    {
        var h = new LatencyHistogram();
        var values = Enumerable.Range(1, 100_000).Select(i => (long)i * 37).ToArray();
        foreach (var v in values)
        {
            h.Record(v);
        }

        var exact = values[(int)Math.Ceiling(percentile / 100 * values.Length) - 1];
        var actual = h.Percentile(percentile);

        Assert.InRange(actual, exact, exact * (1 + (1.0 / LatencyHistogram.HalfSubBucketCount)));
    }

    [Fact]
    public void Max_is_exact_even_for_wide_buckets()
    {
        var h = new LatencyHistogram();
        h.Record(1_000);
        h.Record(1_234_567);

        Assert.Equal(1_234_567, h.Percentile(100));
        Assert.Equal(1_234_567, h.Max);
    }

    [Fact]
    public void Index_and_bounds_round_trip_for_every_bucket()
    {
        for (var i = 0; i < LatencyHistogram.BucketCount; i++)
        {
            var low = LatencyHistogram.LowestValueAt(i);
            var high = LatencyHistogram.HighestValueAt(i);

            Assert.Equal(i, LatencyHistogram.IndexOf(low));
            Assert.Equal(i, LatencyHistogram.IndexOf(high));
            if (i + 1 < LatencyHistogram.BucketCount)
            {
                Assert.Equal(high + 1, LatencyHistogram.LowestValueAt(i + 1));
            }
        }
    }

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        var h = new LatencyHistogram();
        h.Record(-5);
        h.Record(long.MaxValue);

        Assert.Equal(0, h.Min);
        Assert.Equal(LatencyHistogram.MaxTrackableValue, h.Max);
        Assert.Equal(2, h.TotalCount);
    }

    [Fact]
    public async Task Concurrent_recording_loses_no_samples()
    {
        var h = new LatencyHistogram();
        const int workers = 8;
        const int perWorker = 50_000;

        await Task.WhenAll(Enumerable.Range(0, workers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < perWorker; i++)
            {
                h.Record(((w * 7919) + i) % 10_000);
            }
        })));

        Assert.Equal(workers * perWorker, h.TotalCount);
        Assert.Equal(0, h.Min);
        Assert.Equal(9_999, h.Max);
    }

    [Fact]
    public void Add_merges_counts_and_extremes()
    {
        var a = new LatencyHistogram();
        var b = new LatencyHistogram();
        a.Record(10);
        a.Record(20);
        b.Record(5);
        b.Record(1_000);

        a.Add(b);

        Assert.Equal(4, a.TotalCount);
        Assert.Equal(5, a.Min);
        Assert.Equal(1_000, a.Max);
        Assert.Equal(258.75, a.Mean, 6);
        Assert.Equal(10, a.Percentile(50));
    }

    [Fact]
    public void Recorder_counts_statuses_errors_and_transport_failures()
    {
        var r = new EndpointRecorder("locate");
        r.RecordResponse(200, 1_000, 10);
        r.RecordResponse(206, 2_000, 1_048_576);
        r.RecordResponse(404, 500, 0);
        r.RecordFailure(OutcomeClassifier.Timeout);

        Assert.Equal(4, r.Count);
        Assert.Equal(2, r.Errors);
        Assert.Equal(1_048_586, r.Bytes);
        Assert.Equal(3, r.Latency.TotalCount);
        Assert.Equal(1, r.Outcomes["404"]);
        Assert.Equal(1, r.Outcomes[OutcomeClassifier.Timeout]);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(206, true)]
    [InlineData(304, true)]
    [InlineData(400, false)]
    [InlineData(404, false)]
    [InlineData(500, false)]
    [InlineData(503, false)]
    public void Success_is_2xx_and_3xx(int status, bool success)
    {
        Assert.Equal(success, OutcomeClassifier.IsSuccess(status));
    }
}
