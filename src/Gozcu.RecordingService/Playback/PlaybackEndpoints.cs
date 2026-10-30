namespace Gozcu.RecordingService.Playback;

public static class PlaybackEndpoints
{
    public static IServiceCollection AddPlayback(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapPlaybackApi(this IEndpointRouteBuilder app) => app;
}
