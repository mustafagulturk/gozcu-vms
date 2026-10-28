using Gozcu.RecordingService.Recording;
using Microsoft.Extensions.Time.Testing;

namespace Gozcu.UnitTests.Recording;

public sealed class CameraRecorderTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("gozcu-recorder-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 17, 16, 1, TimeSpan.Zero));
    private readonly List<CompletedSegment> _segments = [];
    private readonly CameraRecorder _recorder;

    public CameraRecorderTests()
    {
        _recorder = new CameraRecorder("cam1", _directory, TimeSpan.FromSeconds(60), _segments.Add, _time);
    }

    [Fact]
    public void Completed_segment_line_produces_segment_with_time_from_file_name()
    {
        WriteFile("cam1_20261028T171500Z.mp4", 4096);
        _recorder.OnStarted();

        _recorder.OnStandardOutput("cam1_20261028T171500Z.mp4,0.000000,60.040000");

        var segment = Assert.Single(_segments);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 17, 15, 0, TimeSpan.Zero), segment.StartedAt);
        Assert.Equal(60.04, (segment.EndedAt - segment.StartedAt).TotalSeconds, 3);
        Assert.Equal(4096, segment.SizeBytes);
        Assert.Equal(Path.Combine(_directory, "cam1_20261028T171500Z.mp4"), segment.FilePath);
        Assert.Equal(1, _recorder.Status().SegmentsWritten);
    }

    [Fact]
    public void Truncated_segment_is_deleted_instead_of_indexed()
    {
        var path = WriteFile("cam1_20261028T171500Z.mp4", 48);
        _recorder.OnStandardOutput("cam1_20261028T171500Z.mp4,0.000000,0.000000");

        Assert.Empty(_segments);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Truncated_leftovers_from_previous_run_are_removed_on_start()
    {
        var empty = WriteFile("cam1_20261028T171400Z.mp4", 0);
        var valid = WriteFile("cam1_20261028T171300Z.mp4", 4096);

        _recorder.OnStarted();

        Assert.False(File.Exists(empty));
        Assert.True(File.Exists(valid));
    }

    [Fact]
    public void Status_tracks_restarts_and_errors()
    {
        _recorder.OnStarted();
        _recorder.OnStandardError("Connection to tcp://mediamtx:8554 failed: Connection refused");
        _recorder.OnExited(1, null, TimeSpan.FromSeconds(2));

        var status = _recorder.Status();
        Assert.Equal(RecorderState.Retrying, status.State);
        Assert.Contains("Connection refused", status.LastError, StringComparison.Ordinal);

        _recorder.OnStarted();
        Assert.Equal(1, _recorder.Status().RestartCount);
        Assert.Equal(RecorderState.Recording, _recorder.Status().State);
    }

    [Fact]
    public void Non_segment_output_is_ignored()
    {
        _recorder.OnStandardOutput("some random line");
        Assert.Empty(_segments);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string WriteFile(string name, int size)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }
}
