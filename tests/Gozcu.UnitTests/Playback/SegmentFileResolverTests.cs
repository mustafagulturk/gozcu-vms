using Gozcu.RecordingService.Playback;

namespace Gozcu.UnitTests.Playback;

public sealed class SegmentFileResolverTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gozcu-ut-").FullName;

    [Fact]
    public void Existing_file_inside_root_is_resolved()
    {
        var path = Path.Combine(_root, "cam1", "cam1_20261030T100000Z.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);

        Assert.Equal(path, new SegmentFileResolver(_root).Resolve(path));
    }

    [Fact]
    public void Traversal_outside_root_and_missing_files_are_rejected()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, Path.GetFileName(_root) + "-outside.mp4");
        File.WriteAllBytes(outside, [0]);
        try
        {
            var resolver = new SegmentFileResolver(_root);

            Assert.Null(resolver.Resolve(outside));
            Assert.Null(resolver.Resolve(Path.Combine(_root, "..", Path.GetFileName(outside))));
            Assert.Null(resolver.Resolve(Path.Combine(_root, "cam1", "missing.mp4")));
            Assert.Null(resolver.Resolve(_root));
            Assert.Null(resolver.Resolve(""));
            Assert.Null(resolver.Resolve(null));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Sibling_directory_with_same_prefix_is_rejected()
    {
        var resolver = new SegmentFileResolver(_root);

        Assert.False(resolver.IsInsideRoot(_root + "-evil/cam1/x.mp4", out _));
        Assert.True(resolver.IsInsideRoot(Path.Combine(_root, "cam1", "x.mp4"), out _));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
