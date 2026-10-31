using Gozcu.Contracts.Analysis;
using Gozcu.Media.Frames;
using Gozcu.Vision;

namespace Gozcu.UnitTests.Analysis;

/// <summary>Her çalıştırma için bir senaryo yürüten sahte kare kaynağı. Senaryolar bitince iptal edilene kadar bekler.</summary>
internal sealed class FakeFrameSource(params Func<IFrameSourceHandler, CancellationToken, Task<int>>[] runs) : IFrameSource
{
    private int _runs;

    public int Runs => Volatile.Read(ref _runs);

    public async Task<int> RunAsync(FrameSourceRequest request, IFrameSourceHandler handler, CancellationToken cancellationToken)
    {
        var run = Interlocked.Increment(ref _runs);
        if (run <= runs.Length)
        {
            return await runs[run - 1](handler, cancellationToken);
        }

        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }
}

/// <summary>
/// Sahte tespit edici: her karenin ilk baytını kaydeder ve verilen sonucu döner. İstenirse ilk çağrıda kapı açılana
/// kadar bekler (yavaş çıkarım benzetimi). Eşzamanlı çağrı sayısının en yükseğini de ölçer.
/// </summary>
internal sealed class FakeDetector : IObjectDetector, IDisposable
{
    public const int Size = 4;

    private readonly Func<byte, IReadOnlyList<Detection>> _result;
    private readonly List<byte> _frames = [];
    private readonly ManualResetEventSlim _gate;
    private readonly TimeSpan _work;
    private int _concurrent;
    private int _maxConcurrent;

    public FakeDetector(Func<byte, IReadOnlyList<Detection>>? result = null, bool blockFirstCall = false, TimeSpan work = default)
    {
        _result = result ?? (_ => []);
        _gate = new ManualResetEventSlim(!blockFirstCall);
        _work = work;
    }

    public DetectorModelInfo Model { get; } = new("sahte", Size, Size);

    public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

    public IReadOnlyList<byte> Frames
    {
        get
        {
            lock (_frames)
            {
                return [.. _frames];
            }
        }
    }

    public IReadOnlyList<Detection> Detect(ReadOnlySpan<byte> bgrFrame, LetterboxGeometry geometry)
    {
        var id = bgrFrame[0];
        var concurrent = Interlocked.Increment(ref _concurrent);
        int max;
        while (concurrent > (max = Volatile.Read(ref _maxConcurrent)) && Interlocked.CompareExchange(ref _maxConcurrent, concurrent, max) != max)
        {
        }

        try
        {
            lock (_frames)
            {
                _frames.Add(id);
            }

            _gate.Wait(TimeSpan.FromSeconds(10));
            if (_work > TimeSpan.Zero)
            {
                Thread.Sleep(_work);
            }

            return _result(id);
        }
        finally
        {
            Interlocked.Decrement(ref _concurrent);
        }
    }

    public void Release() => _gate.Set();

    public Task WaitForCallsAsync(int count) => Until(() => Frames.Count >= count, $"{count} çıkarım çağrısı bekleniyordu");

    public void Dispose() => _gate.Dispose();

    public static byte[] Frame(byte id)
    {
        var frame = new byte[Size * Size * 3];
        frame[0] = id;
        return frame;
    }

    public static Detection Person(float confidence = 0.9f) =>
        new("person", ObjectCategory.Person, confidence, new BoundingBox(0.1f, 0.2f, 0.3f, 0.4f));

    public static async Task Until(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException(because);
    }
}
