using Gozcu.Media.Frames;

namespace Gozcu.UnitTests.Media;

public class RawFrameReaderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public async Task Reads_whole_frames_from_stream_returning_small_random_chunks(int seed)
    {
        const int frameSize = 4 * 3 * 3;
        var data = Enumerable.Range(0, frameSize * 5).Select(i => (byte)(i % 251)).ToArray();
        var reader = new RawFrameReader(new ChunkedStream(data, new Random(seed)), frameSize);

        var frames = new List<byte[]>();
        var buffer = new byte[frameSize];
        while (await reader.ReadAsync(buffer, CancellationToken.None))
        {
            frames.Add(buffer.ToArray());
        }

        Assert.Equal(5, frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            Assert.Equal(data.AsSpan(i * frameSize, frameSize).ToArray(), frames[i]);
        }
    }

    [Fact]
    public async Task Empty_stream_ends_cleanly()
    {
        var reader = new RawFrameReader(new MemoryStream(), 12);
        Assert.False(await reader.ReadAsync(new byte[12], CancellationToken.None));
    }

    [Fact]
    public async Task Truncated_last_frame_throws_after_complete_frames()
    {
        var reader = new RawFrameReader(new ChunkedStream(new byte[30], new Random(3)), 12);
        var buffer = new byte[12];

        Assert.True(await reader.ReadAsync(buffer, CancellationToken.None));
        Assert.True(await reader.ReadAsync(buffer, CancellationToken.None));
        var error = await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadAsync(buffer, CancellationToken.None).AsTask());
        Assert.Contains("6/12", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Buffer_smaller_than_frame_is_rejected()
    {
        var reader = new RawFrameReader(new MemoryStream(new byte[12]), 12);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadAsync(new byte[11], CancellationToken.None).AsTask());
    }

    /// <summary>Boru gibi davranan akış: her okumada istenenden az, rastgele sayıda bayt döner.</summary>
    private sealed class ChunkedStream(byte[] data, Random random) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var available = data.Length - _position;
            if (available == 0 || buffer.Length == 0)
            {
                return 0;
            }

            var count = Math.Min(Math.Min(available, buffer.Length), random.Next(1, 8));
            data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            return Read(buffer.Span);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
