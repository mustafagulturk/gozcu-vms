using System.Globalization;

namespace Gozcu.Media.Ffmpeg;

/// <summary>
/// ffmpeg'in "anahtar=değer" biçimindeki -progress satırlarını toplar ve her
/// "progress=..." satırında tamamlanmış bir <see cref="FfmpegProgress"/> üretir.
/// İş parçacığı güvenli değildir; tek bir sürecin stdout'u için kullanılır.
/// </summary>
public sealed class FfmpegProgressParser
{
    private TimeSpan? _outTime;
    private double? _speed;

    public bool TryAccept(string line, out FfmpegProgress? progress)
    {
        progress = null;
        var separator = line.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();
        switch (key)
        {
            case "out_time_us":
                _outTime = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0
                    ? TimeSpan.FromMicroseconds(us)
                    : null;
                return false;
            case "speed":
                _speed = double.TryParse(value.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed)
                    ? speed
                    : null;
                return false;
            case "progress":
                progress = new FfmpegProgress(_outTime, _speed, value == "end");
                _outTime = null;
                _speed = null;
                return true;
            default:
                return false;
        }
    }
}
