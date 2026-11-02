namespace Gozcu.Benchmarks.Support;

/// <summary>
/// Bellekteki baytları en fazla <c>chunkSize</c> baytlık parçalar hâlinde veren salt okunur akış. Linux'ta bir
/// borudan (pipe) tek okumada en fazla boru tamponu kadar (varsayılan 64 KiB) veri gelir; bu sınıf ffmpeg'in
/// stdout borusunu taklit ederek okuyucunun bir kareyi birden çok okumayla birleştirme yolunu ölçtürür.
/// </summary>
internal sealed class ChunkedReadStream(byte[] data, int chunkSize) : Stream
{
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => data.Length;

    public override long Position
    {
        get => _position;
        set => _position = checked((int)value);
    }

    /// <summary>Akışı başa sarar; her ölçüm yinelemesinden önce çağrılır.</summary>
    public void Rewind() => _position = 0;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var count = Math.Min(Math.Min(buffer.Length, chunkSize), data.Length - _position);
        data.AsSpan(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
