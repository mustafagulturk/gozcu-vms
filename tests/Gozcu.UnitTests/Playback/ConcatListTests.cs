using Gozcu.Media.Ffmpeg;

namespace Gozcu.UnitTests.Playback;

public class ConcatListTests
{
    [Fact]
    public void Builds_list_with_inpoint_and_outpoint()
    {
        var list = ConcatList.Build(
        [
            new ConcatEntry("/rec/cam1/a.mp4", TimeSpan.FromSeconds(12.5)),
            new ConcatEntry("/rec/cam1/b.mp4"),
            new ConcatEntry("/rec/cam1/c.mp4", null, TimeSpan.FromMilliseconds(30_250)),
        ]);

        Assert.Equal(
            "ffconcat version 1.0\n" +
            "file '/rec/cam1/a.mp4'\ninpoint 12.5\n" +
            "file '/rec/cam1/b.mp4'\n" +
            "file '/rec/cam1/c.mp4'\noutpoint 30.25\n",
            list);
    }

    [Theory]
    [InlineData("/rec/a.mp4", "'/rec/a.mp4'")]
    [InlineData("/rec/it's.mp4", @"'/rec/it'\''s.mp4'")]
    [InlineData("/rec/a b,c.mp4", "'/rec/a b,c.mp4'")]
    [InlineData(@"C:\rec\a.mp4", @"'C:\rec\a.mp4'")]
    public void Paths_are_single_quoted_and_quotes_escaped(string path, string expected)
    {
        Assert.Equal(expected, ConcatList.Quote(path));
    }

    [Theory]
    [InlineData("/rec/a.mp4\nfile '/etc/passwd'")]
    [InlineData("/rec/a.mp4\r")]
    [InlineData("")]
    public void Paths_that_could_inject_directives_are_rejected(string path)
    {
        Assert.ThrowsAny<ArgumentException>(() => ConcatList.Quote(path));
    }

    [Fact]
    public void Empty_list_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => ConcatList.Build([]));
    }

    [Fact]
    public void Export_command_copies_without_reencoding()
    {
        var args = ClipExportCommands.Concat("/tmp/x/list.txt", "/tmp/x/clip.mp4");

        AssertPair(args, "-f", "concat");
        AssertPair(args, "-safe", "0");
        AssertPair(args, "-i", "/tmp/x/list.txt");
        AssertPair(args, "-map", "0:v:0");
        AssertPair(args, "-c", "copy");
        AssertPair(args, "-movflags", "+faststart");
        Assert.Equal("/tmp/x/clip.mp4", args[^1]);
        Assert.DoesNotContain("libx264", args);
    }

    private static void AssertPair(IReadOnlyList<string> args, string flag, string value)
    {
        var index = args.ToList().IndexOf(flag);
        Assert.True(index >= 0, $"{flag} bulunamadı");
        Assert.Equal(value, args[index + 1]);
    }
}
