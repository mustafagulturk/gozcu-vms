using Gozcu.Media.Frames;

namespace Gozcu.UnitTests.Media;

public class FrameClockTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

    // ffmpeg 6.1 framecrc çıktısı: anahtar karelerde bayrak alanı yok, diğerlerinde "F=0x0".
    private const string Header = "#tb 0: 1/90000";

    private static string Key(long pts) => $"0,     {pts - 7200},     {pts},     3600,    37813, 0xa2a07035, S=1,       16";

    private static string NonKey(long pts) => $"0,     {pts - 18000},     {pts},     3600,     3999, 0x5c65b015, F=0x0, S=1,       16";

    private static string ShowInfo(double ptsTime) =>
        $"[Parsed_showinfo_3 @ 0x55d1] n:   0 pts:  {ptsTime * 90000:0} pts_time:{ptsTime:0.######} duration:  3600 fmt:bgr24 sar:1/1 s:416x416";

    [Fact]
    public async Task Frame_time_is_arrival_base_plus_pts_not_read_time()
    {
        var clock = new FrameClock();
        Assert.True(clock.TryConsume(Header, T0));
        // PTS 2,0 sn'deki anahtar kare T0+2,0'da geldi → taban T0.
        Assert.True(clock.TryConsume(Key(180_000), T0.AddSeconds(2.0)));
        Assert.True(clock.TryConsume(ShowInfo(2.52), T0.AddSeconds(3.2)));

        // Kare ancak T0+3,3'te okundu (0,78 sn kod çözme gecikmesi); zamanı yine T0+2,52 olmalı.
        var captured = await clock.CaptureTimeAsync(T0.AddSeconds(3.3), TimeProvider.System, CancellationToken.None);

        Assert.Equal(T0.AddSeconds(2.52), captured);
        Assert.Equal(0.78, clock.LastDelay!.Value.TotalSeconds, 3);
    }

    [Fact]
    public async Task Only_key_frames_set_the_base_because_reordered_packets_carry_future_pts()
    {
        var clock = new FrameClock();
        clock.TryConsume(Header, T0);
        clock.TryConsume(Key(90_000), T0.AddSeconds(1.0));
        // B kare yüzünden PTS'i geliş sırasından 0,2 sn ileride olan paket tabanı bozmamalı.
        Assert.True(clock.TryConsume(NonKey(108_000), T0.AddSeconds(1.0)));
        clock.TryConsume(ShowInfo(1.0), T0.AddSeconds(1.5));

        var captured = await clock.CaptureTimeAsync(T0.AddSeconds(1.5), TimeProvider.System, CancellationToken.None);

        Assert.Equal(T0.AddSeconds(1.0), captured);
    }

    [Fact]
    public async Task Smallest_recent_base_wins_so_late_line_delivery_does_not_shift_frames()
    {
        var clock = new FrameClock();
        clock.TryConsume(Header, T0);
        clock.TryConsume(Key(0), T0.AddSeconds(0.30));        // stderr okuyucusu geç kaldı
        clock.TryConsume(Key(180_000), T0.AddSeconds(2.0));   // zamanında
        clock.TryConsume(ShowInfo(2.5), T0.AddSeconds(3.0));

        Assert.Equal(T0.AddSeconds(2.5), await clock.CaptureTimeAsync(T0.AddSeconds(3.0), TimeProvider.System, CancellationToken.None));
    }

    [Fact]
    public async Task Without_packet_timestamps_read_time_is_used()
    {
        var clock = new FrameClock();
        clock.TryConsume(ShowInfo(1.0), T0);

        Assert.Equal(T0.AddSeconds(5), await clock.CaptureTimeAsync(T0.AddSeconds(5), TimeProvider.System, CancellationToken.None));
        Assert.Null(clock.LastDelay);
    }

    [Fact]
    public async Task Missing_pts_line_falls_back_to_read_time_and_its_late_pts_is_skipped_to_keep_pairs_aligned()
    {
        var clock = new FrameClock();
        clock.TryConsume(Header, T0);
        clock.TryConsume(Key(0), T0);

        // 1. kare: PTS satırı bekleme süresi içinde gelmedi → okuma anı.
        var first = await clock.CaptureTimeAsync(T0.AddSeconds(1), TimeProvider.System, CancellationToken.None);
        Assert.Equal(T0.AddSeconds(1), first);

        // Geç gelen 1. karenin PTS'i atılır, 2. karenin PTS'i 2. kareyle eşleşir.
        clock.TryConsume(ShowInfo(0.5), T0.AddSeconds(1.1));
        clock.TryConsume(ShowInfo(1.5), T0.AddSeconds(1.6));
        Assert.Equal(T0.AddSeconds(1.5), await clock.CaptureTimeAsync(T0.AddSeconds(1.7), TimeProvider.System, CancellationToken.None));
    }

    [Fact]
    public async Task Implausible_result_falls_back_to_read_time()
    {
        var clock = new FrameClock();
        clock.TryConsume(Header, T0);
        clock.TryConsume(Key(0), T0);
        clock.TryConsume(ShowInfo(60), T0);   // akış zamanı atladı: hesap okuma anından sonraya düşüyor

        Assert.Equal(T0.AddSeconds(1), await clock.CaptureTimeAsync(T0.AddSeconds(1), TimeProvider.System, CancellationToken.None));
    }

    [Theory]
    [InlineData("[rtsp @ 0x1] method DESCRIBE failed: 404 Not Found")]
    [InlineData("  Stream #0:0: Video: h264 (High), yuv420p(progressive), 1280x720, 25 fps, 25 tbr, 90k tbn")]
    [InlineData("")]
    public void Diagnostic_lines_are_left_to_the_caller(string line)
    {
        Assert.False(new FrameClock().TryConsume(line, T0));
    }

    [Fact]
    public async Task Frame_times_never_go_backwards_when_the_base_improves()
    {
        var clock = new FrameClock();
        clock.TryConsume(Header, T0);
        // Açılışta paketler topluca yazıldı: ilk anahtar kare 0,8 sn geç bildirildi.
        clock.TryConsume(Key(0), T0.AddSeconds(0.8));
        clock.TryConsume(ShowInfo(0.5), T0.AddSeconds(1.4));
        var first = await clock.CaptureTimeAsync(T0.AddSeconds(1.4), TimeProvider.System, CancellationToken.None);
        Assert.Equal(T0.AddSeconds(1.3), first);

        // Sonraki anahtar kare zamanında geldi: taban 0,8 sn erkene kayar, ama zaman geri gitmez.
        clock.TryConsume(Key(180_000), T0.AddSeconds(2.0));
        clock.TryConsume(ShowInfo(1.0), T0.AddSeconds(2.1));
        var second = await clock.CaptureTimeAsync(T0.AddSeconds(2.1), TimeProvider.System, CancellationToken.None);
        Assert.True(second > first);

        clock.TryConsume(ShowInfo(2.5), T0.AddSeconds(3.0));
        Assert.Equal(T0.AddSeconds(2.5), await clock.CaptureTimeAsync(T0.AddSeconds(3.0), TimeProvider.System, CancellationToken.None));
    }
}
