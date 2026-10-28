using System.Text.RegularExpressions;

namespace Gozcu.Contracts.Cameras;

/// <summary>"Cameras" yapılandırma bölümü.</summary>
public sealed partial class CameraOptions
{
    public const string SectionName = "Cameras";

    public IList<CameraDefinition> Items { get; } = new List<CameraDefinition>();

    public IEnumerable<CameraDefinition> Enabled => Items.Where(c => c.Enabled);

    /// <summary>Kamera listesini doğrular; hatalı her kayıt için bir mesaj döner.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var camera in Items)
        {
            if (!IdPattern().IsMatch(camera.Id))
            {
                errors.Add($"Geçersiz kamera kimliği '{camera.Id}'. Yalnızca harf, rakam, '-' ve '_' kullanılabilir.");
            }
            else if (!seen.Add(camera.Id))
            {
                errors.Add($"Kamera kimliği '{camera.Id}' birden fazla kez tanımlanmış.");
            }

            if (camera.RtspUrl is null || camera.RtspUrl.Scheme is not ("rtsp" or "rtsps"))
            {
                errors.Add($"'{camera.Id}' kamerası için geçerli bir rtsp:// adresi gerekli.");
            }
        }

        return errors;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdPattern();
}
