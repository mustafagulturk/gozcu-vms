using Gozcu.Contracts.Cameras;

namespace Gozcu.UnitTests.Contracts;

public class CameraOptionsTests
{
    [Fact]
    public void Valid_configuration_has_no_errors()
    {
        var options = new CameraOptions();
        options.Items.Add(new CameraDefinition { Id = "cam1", Name = "A", RtspUrl = new Uri("rtsp://host:8554/cam1") });
        options.Items.Add(new CameraDefinition { Id = "cam_2", Name = "B", RtspUrl = new Uri("rtsps://host/cam2") });

        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Ids_reserved_for_system_directories_are_rejected()
    {
        var options = new CameraOptions();
        options.Items.Add(new CameraDefinition { Id = "_quarantine", RtspUrl = new Uri("rtsp://h/a") });

        Assert.Single(options.Validate());
    }

    [Fact]
    public void Invalid_ids_duplicates_and_urls_are_reported()
    {
        var options = new CameraOptions();
        options.Items.Add(new CameraDefinition { Id = "cam 1", RtspUrl = new Uri("rtsp://h/a") });
        options.Items.Add(new CameraDefinition { Id = "cam2", RtspUrl = new Uri("http://h/b") });
        options.Items.Add(new CameraDefinition { Id = "CAM2", RtspUrl = null });

        var errors = options.Validate();

        Assert.Equal(4, errors.Count);
        Assert.Contains(errors, e => e.Contains("cam 1", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("birden fazla", StringComparison.Ordinal));
    }

    [Fact]
    public void Disabled_cameras_are_excluded()
    {
        var options = new CameraOptions();
        options.Items.Add(new CameraDefinition { Id = "a", Enabled = true });
        options.Items.Add(new CameraDefinition { Id = "b", Enabled = false });

        Assert.Equal(["a"], options.Enabled.Select(c => c.Id));
    }
}
