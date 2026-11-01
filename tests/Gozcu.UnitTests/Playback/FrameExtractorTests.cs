using Gozcu.Media.Ffmpeg;
using Gozcu.Media.Processes;
using Gozcu.RecordingService.Playback;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gozcu.UnitTests.Playback;

public class FrameExtractorTests
{
    [Fact]
    public void Jpeg_command_seeks_before_input_scales_and_writes_single_frame_to_stdout()
    {
        var args = FfmpegCommands.JpegFrame("/recordings/cam1/cam1_20261030T101500Z.mp4", TimeSpan.FromSeconds(12.3456), 320);

        // -ss girişten önce: hızlı (anahtar kareye atlayan) ve kare doğruluğunda konumlama.
        Assert.True(args.ToList().IndexOf("-ss") < args.ToList().IndexOf("-i"));
        AssertPair(args, "-ss", "12.346");
        AssertPair(args, "-i", "/recordings/cam1/cam1_20261030T101500Z.mp4");
        AssertPair(args, "-frames:v", "1");
        AssertPair(args, "-vf", "scale=320:-2");
        AssertPair(args, "-q:v", "5");
        AssertPair(args, "-c:v", "mjpeg");
        AssertPair(args, "-f", "image2pipe");
        Assert.Equal("pipe:1", args[^1]);
        Assert.Contains("-nostdin", args);
    }

    [Fact]
    public void Jpeg_command_validates_arguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FfmpegCommands.JpegFrame("a.mp4", TimeSpan.FromSeconds(-1), 320));
        Assert.Throws<ArgumentOutOfRangeException>(() => FfmpegCommands.JpegFrame("a.mp4", TimeSpan.Zero, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => FfmpegCommands.JpegFrame("a.mp4", TimeSpan.Zero, 5000));
        Assert.Throws<ArgumentException>(() => FfmpegCommands.JpegFrame(" ", TimeSpan.Zero, 320));
        AssertPair(FfmpegCommands.JpegFrame("a.mp4", TimeSpan.Zero, 64), "-ss", "0");
    }

    [Theory]
    [InlineData("2026-10-30T10:15:00Z", null, 320)]
    [InlineData("2026-10-30T10:15:00Z", "", 320)]
    [InlineData("2026-10-30T13:15:00+03:00", "64", 64)]
    [InlineData("2026-10-30T10:15:00", "1280", 1280)]
    public void Frame_parameters_are_parsed(string time, string? width, int expectedWidth)
    {
        Assert.True(FrameEndpoints.TryParse(time, width, out var at, out var frameWidth, out var error), error);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 10, 15, 0, TimeSpan.Zero), at);
        Assert.Equal(expectedWidth, frameWidth);
    }

    [Theory]
    [InlineData(null, null, "'time'")]
    [InlineData("dün", null, "'time'")]
    [InlineData("2026-10-30T10:15:00Z", "63", "'width'")]
    [InlineData("2026-10-30T10:15:00Z", "1281", "'width'")]
    [InlineData("2026-10-30T10:15:00Z", "geniş", "'width'")]
    public void Invalid_frame_parameters_are_rejected(string? time, string? width, string expectedFragment)
    {
        Assert.False(FrameEndpoints.TryParse(time, width, out _, out _, out var error));
        Assert.Contains(expectedFragment, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_output_past_end_of_file_is_retried_one_second_earlier()
    {
        var reader = new FakeReader(spec => Offset(spec) >= 59.5 ? Output(0, []) : Output(0, [0xFF, 0xD8]));
        using var extractor = new FrameExtractor(reader, "ffmpeg", NullLogger<FrameExtractor>.Instance);

        var jpeg = await extractor.ExtractJpegAsync("/r/a.mp4", TimeSpan.FromSeconds(59.8), 320, CancellationToken.None);

        Assert.Equal([0xFF, 0xD8], jpeg);
        Assert.Equal([59.8, 58.8], reader.Offsets);
    }

    [Fact]
    public async Task No_frame_returns_null_and_ffmpeg_failure_throws()
    {
        using var empty = new FrameExtractor(new FakeReader(_ => Output(0, [])), "ffmpeg", NullLogger<FrameExtractor>.Instance);
        Assert.Null(await empty.ExtractJpegAsync("/r/a.mp4", TimeSpan.Zero, 320, CancellationToken.None));

        using var failing = new FrameExtractor(new FakeReader(_ => Output(1, [])), "ffmpeg", NullLogger<FrameExtractor>.Instance);
        await Assert.ThrowsAsync<FrameExtractionException>(
            () => failing.ExtractJpegAsync("/r/a.mp4", TimeSpan.FromSeconds(3), 320, CancellationToken.None));
    }

    [Fact]
    public async Task At_most_two_ffmpeg_processes_run_concurrently()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new FakeReader(_ => Output(0, [1]), release.Task);
        using var extractor = new FrameExtractor(reader, "ffmpeg", NullLogger<FrameExtractor>.Instance);

        var requests = Enumerable.Range(0, 5)
            .Select(i => extractor.ExtractJpegAsync($"/r/{i}.mp4", TimeSpan.Zero, 320, CancellationToken.None))
            .ToList();
        await Task.Delay(200);
        Assert.Equal(FrameExtractor.MaxConcurrentExtractions, reader.Started);

        release.SetResult();
        await Task.WhenAll(requests);
        Assert.Equal(5, reader.Started);
        Assert.Equal(FrameExtractor.MaxConcurrentExtractions, reader.MaxActive);
    }

    private static ProcessOutput Output(int exitCode, byte[] data) => new(exitCode, data, ["hata"]);

    private static double Offset(ProcessSpec spec)
    {
        var args = spec.Arguments.ToList();
        return double.Parse(args[args.IndexOf("-ss") + 1], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AssertPair(IReadOnlyList<string> args, string key, string value)
    {
        var index = args.ToList().IndexOf(key);
        Assert.True(index >= 0, $"{key} bulunamadı");
        Assert.Equal(value, args[index + 1]);
    }

    private sealed class FakeReader(Func<ProcessSpec, ProcessOutput> respond, Task? gate = null) : IProcessOutputReader
    {
        private int _active;
        private int _started;
        private int _maxActive;

        public List<double> Offsets { get; } = [];

        public int Started => Volatile.Read(ref _started);

        public int MaxActive => Volatile.Read(ref _maxActive);

        public async Task<ProcessOutput> ReadAllAsync(ProcessSpec spec, int maxBytes, CancellationToken cancellationToken)
        {
            lock (Offsets)
            {
                Offsets.Add(Offset(spec));
            }

            Interlocked.Increment(ref _started);
            var active = Interlocked.Increment(ref _active);
            InterlockedMax(ref _maxActive, active);
            try
            {
                if (gate is not null)
                {
                    await gate.WaitAsync(cancellationToken);
                }

                return respond(spec);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }
}
