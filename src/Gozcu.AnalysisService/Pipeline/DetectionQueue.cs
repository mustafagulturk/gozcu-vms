using System.Threading.Channels;

namespace Gozcu.AnalysisService.Pipeline;

/// <summary>
/// Çıkarım döngülerinden tek veritabanı yazıcısına (<see cref="Persistence.DetectionWriter"/>) giden sınırlı kuyruk.
/// Yazıcı geride kalırsa analiz beklemez: kuyruk doluysa iş atılır ve sayılır.
/// </summary>
public sealed class DetectionQueue
{
    /// <summary>
    /// Kare başına en fazla bir iş üretilir (4 kamera × 2 fps = 8 iş/sn); yazıcı normalde her şeyi hemen belleğe alır.
    /// Bu sınır yalnızca yazıcı uzun süre takılırsa (ör. veritabanı bağlantısı açılırken) belleği korur.
    /// </summary>
    public const int DefaultCapacity = 2000;

    private readonly Channel<AnalysisOutput> _channel;
    private long _dropped;

    public DetectionQueue(int capacity = DefaultCapacity)
    {
        _channel = Channel.CreateBounded<AnalysisOutput>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public ChannelReader<AnalysisOutput> Reader => _channel.Reader;

    /// <summary>Kuyruk dolu olduğu için atılan iş sayısı.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public int Count => _channel.Reader.Count;

    /// <summary>İşi bekletmeden kuyruğa koyar; kuyruk doluysa atar ve <c>false</c> döner.</summary>
    public bool TryEnqueue(AnalysisOutput output)
    {
        if (_channel.Writer.TryWrite(output))
        {
            return true;
        }

        Interlocked.Increment(ref _dropped);
        return false;
    }
}
