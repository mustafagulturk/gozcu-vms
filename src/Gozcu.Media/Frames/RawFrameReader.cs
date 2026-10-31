namespace Gozcu.Media.Frames;

/// <summary>
/// Başlıksız ham video akışından (ör. ffmpeg <c>-f rawvideo pipe:1</c>) sabit boyutlu kareleri okur.
/// Boru (pipe) bir okumada kareden az bayt döndürebilir; okuyucu kare tamamlanana kadar okumaya devam eder.
/// </summary>
public sealed class RawFrameReader
{
    private readonly Stream _stream;

    public RawFrameReader(Stream stream, int frameSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameSize);
        _stream = stream;
        FrameSize = frameSize;
    }

    /// <summary>Bir karenin bayt cinsinden boyutu (BGR24 için genişlik × yükseklik × 3).</summary>
    public int FrameSize { get; }

    /// <summary>Bir sonraki kareyi <paramref name="buffer"/>'ın başına okur.</summary>
    /// <returns>Kare tamamen okunduysa <c>true</c>; akış bir kare sınırında temiz biçimde bittiyse <c>false</c>.</returns>
    /// <exception cref="EndOfStreamException">Akış bir karenin ortasında bitti (son kare eksik).</exception>
    public async ValueTask<bool> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(buffer.Length, FrameSize);

        var filled = 0;
        while (filled < FrameSize)
        {
            var read = await _stream.ReadAsync(buffer[filled..FrameSize], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (filled == 0)
                {
                    return false;
                }

                throw new EndOfStreamException($"Akış bir karenin ortasında bitti: {filled}/{FrameSize} bayt okundu.");
            }

            filled += read;
        }

        return true;
    }
}
