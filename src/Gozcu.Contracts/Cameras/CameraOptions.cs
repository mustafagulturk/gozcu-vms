namespace Gozcu.Contracts.Cameras;

/// <summary>
/// "Cameras" yapılandırma bölümü. 4. aşamadan itibaren yalnızca akış servisi okur: veritabanındaki kamera tablosu
/// boşsa ilk açılışta bu liste tohum olarak yazılır. Sonrasında kameralar yönetim API'siyle değiştirilir.
/// </summary>
public sealed class CameraOptions
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
            var idError = CameraValidation.ValidateId(camera.Id);
            if (idError is not null)
            {
                errors.Add(idError);
            }
            else if (!seen.Add(camera.Id))
            {
                errors.Add($"Kamera kimliği '{camera.Id}' birden fazla kez tanımlanmış.");
            }

            if (!CameraValidation.IsValidRtspUrl(camera.RtspUrl))
            {
                errors.Add($"'{camera.Id}' kamerası için geçerli bir rtsp:// adresi gerekli.");
            }
        }

        return errors;
    }
}
