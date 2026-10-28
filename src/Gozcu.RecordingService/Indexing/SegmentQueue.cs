using System.Threading.Channels;
using Gozcu.RecordingService.Recording;

namespace Gozcu.RecordingService.Indexing;

/// <summary>Kaydedilen parçaları veritabanı indeksleyicisine taşıyan kuyruk.</summary>
public sealed class SegmentQueue
{
    private readonly Channel<CompletedSegment> _channel = Channel.CreateUnbounded<CompletedSegment>(
        new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(CompletedSegment segment) => _channel.Writer.TryWrite(segment);

    public ChannelReader<CompletedSegment> Reader => _channel.Reader;
}
