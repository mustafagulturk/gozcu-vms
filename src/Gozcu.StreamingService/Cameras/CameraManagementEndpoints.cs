using Gozcu.Contracts.Cameras;
using Gozcu.Contracts.Events;
using Gozcu.Infrastructure.Cameras;
using Gozcu.Infrastructure.Messaging;
using Gozcu.Infrastructure.Persistence;
using Gozcu.Media.Probing;
using Gozcu.StreamingService.Health;
using Microsoft.AspNetCore.Mvc;

namespace Gozcu.StreamingService.Cameras;

/// <summary>
/// Kamera yönetimi API'si. Kaynak veritabanındaki <c>cameras</c> tablosudur; her başarılı yazmadan sonra yerel katalog
/// yenilenir ve diğer servisler için <see cref="CameraCatalogChanged"/> yayınlanır. İyimser eşzamanlılık için
/// <c>ETag</c>/<c>If-Match</c> kamera sürümünü taşır.
/// </summary>
public static class CameraManagementEndpoints
{
    public static IEndpointRouteBuilder MapCameraManagementApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/cameras");

        api.MapGet("/", async (CameraRepository repository, CameraRegistry registry, CancellationToken cancellationToken) =>
        {
            var cameras = await repository.ListAsync(cancellationToken);
            return Results.Ok(cameras.Select(c => CameraView.From(c, registry)));
        });

        api.MapGet("/{id}", async (string id, CameraRepository repository, CameraRegistry registry, HttpContext context, CancellationToken cancellationToken) =>
        {
            var camera = await repository.FindAsync(id, cancellationToken);
            if (camera is null)
            {
                return NotFound(id);
            }

            context.Response.Headers.ETag = CameraEntityTag.Format(camera.Version);
            return Results.Ok(CameraView.From(camera, registry));
        });

        api.MapPost("/", async (
            CreateCameraRequest? request, CameraRepository repository, CameraRegistry registry, ICameraCatalog catalog,
            IEventPublisher publisher, ILoggerFactory loggers, HttpContext context, CancellationToken cancellationToken) =>
        {
            if (request is null)
            {
                return BodyRequired();
            }

            var errors = new Dictionary<string, string[]>();
            AddError(errors, "id", CameraValidation.ValidateId(request.Id));
            AddError(errors, "name", CameraValidation.ValidateName(request.Name));
            _ = CameraValidation.TryParseRtspUrl(request.RtspUrl, out var url, out var urlError);
            AddError(errors, "rtspUrl", urlError ?? (RtspUrlMasking.IsMasked(request.RtspUrl) ? MaskedPasswordError : null));
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, title: "Kamera bilgileri geçersiz");
            }

            var definition = new CameraDefinition
            {
                Id = request.Id!,
                Name = request.Name!.Trim(),
                RtspUrl = url,
                Enabled = request.Enabled ?? true,
            };
            var result = await repository.InsertAsync(definition, cancellationToken);
            if (!result.Succeeded)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Kamera zaten var",
                    detail: $"'{request.Id}' kimliğinde (büyük/küçük harf farkı gözetmeksizin) bir kamera zaten tanımlı.");
            }

            var camera = result.Camera!;
            var logger = loggers.CreateLogger(typeof(CameraManagementEndpoints));
            logger.LogInformation("Kamera eklendi: {CameraId} ({Name}) {Url}", camera.Id, camera.Name, RtspUrlMasking.MaskPassword(camera.RtspUrl));
            await CameraChangeNotifier.NotifyAsync(
                catalog, publisher, new CameraCatalogChanged(camera.Id, CameraChangeKind.Created, camera.Version), logger, cancellationToken);

            context.Response.Headers.ETag = CameraEntityTag.Format(camera.Version);
            return Results.Created($"/api/cameras/{camera.Id}", CameraView.From(camera, registry));
        });

        api.MapPut("/{id}", async (
            string id, UpdateCameraRequest? request, CameraRepository repository, CameraRegistry registry, ICameraCatalog catalog,
            IEventPublisher publisher, ILoggerFactory loggers, HttpContext context, CancellationToken cancellationToken) =>
        {
            if (request is null)
            {
                return BodyRequired();
            }

            if (!CameraEntityTag.TryParseIfMatch(context.Request.Headers.IfMatch, out var expectedVersion))
            {
                return PreconditionFailed(null);
            }

            var errors = new Dictionary<string, string[]>();
            AddError(errors, "name", CameraValidation.ValidateName(request.Name));

            Uri? newUrl = null;
            if (!string.IsNullOrWhiteSpace(request.RtspUrl))
            {
                var requested = request.RtspUrl.Trim();
                if (RtspUrlMasking.IsMasked(requested))
                {
                    // Panel adresi gizlenmiş parolayla geri gönderdi: kayıtlı parola kullanılır.
                    var current = await repository.FindAsync(id, cancellationToken);
                    if (current is null)
                    {
                        return NotFound(id);
                    }

                    requested = RtspUrlMasking.Unmask(requested, current.RtspUrl) ?? requested;
                }

                _ = CameraValidation.TryParseRtspUrl(requested, out newUrl, out var urlError);
                AddError(errors, "rtspUrl", urlError ?? (RtspUrlMasking.IsMasked(requested) ? MaskedPasswordError : null));
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, title: "Kamera bilgileri geçersiz");
            }

            var result = await repository.UpdateAsync(id, request.Name!.Trim(), newUrl, request.Enabled, expectedVersion, cancellationToken);
            switch (result.Status)
            {
                case CameraWriteStatus.NotFound:
                    return NotFound(id);
                case CameraWriteStatus.VersionMismatch:
                    return PreconditionFailed(result.Camera);
            }

            var camera = result.Camera!;
            var logger = loggers.CreateLogger(typeof(CameraManagementEndpoints));
            logger.LogInformation(
                "Kamera güncellendi: {CameraId} ({Name}, etkin: {Enabled}, sürüm {Version}) {Url}",
                camera.Id, camera.Name, camera.Enabled, camera.Version, RtspUrlMasking.MaskPassword(camera.RtspUrl));
            await CameraChangeNotifier.NotifyAsync(
                catalog, publisher, new CameraCatalogChanged(camera.Id, CameraChangeKind.Updated, camera.Version), logger, cancellationToken);

            context.Response.Headers.ETag = CameraEntityTag.Format(camera.Version);
            return Results.Ok(CameraView.From(camera, registry));
        });

        api.MapDelete("/{id}", async (
            string id, CameraRepository repository, ICameraCatalog catalog, IEventPublisher publisher,
            ILoggerFactory loggers, HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!CameraEntityTag.TryParseIfMatch(context.Request.Headers.IfMatch, out var expectedVersion))
            {
                return PreconditionFailed(null);
            }

            var result = await repository.DeleteAsync(id, expectedVersion, cancellationToken);
            switch (result.Status)
            {
                case CameraWriteStatus.NotFound:
                    return NotFound(id);
                case CameraWriteStatus.VersionMismatch:
                    return PreconditionFailed(result.Camera);
            }

            var logger = loggers.CreateLogger(typeof(CameraManagementEndpoints));
            logger.LogInformation("Kamera silindi: {CameraId}. Kayıtları saklama politikasına göre diskte kalır", id);
            await CameraChangeNotifier.NotifyAsync(
                catalog, publisher, new CameraCatalogChanged(result.Camera!.Id, CameraChangeKind.Deleted, null), logger, cancellationToken);
            return Results.NoContent();
        });

        api.MapPost("/probe", async (
            ProbeCameraRequest? request, CameraRepository repository, IStreamProbe probe, CancellationToken cancellationToken) =>
        {
            var requested = request?.RtspUrl?.Trim();
            if (RtspUrlMasking.IsMasked(requested) && !string.IsNullOrWhiteSpace(request?.CameraId))
            {
                var current = await repository.FindAsync(request.CameraId, cancellationToken);
                if (current is not null)
                {
                    requested = RtspUrlMasking.Unmask(requested!, current.RtspUrl) ?? requested;
                }
            }

            _ = CameraValidation.TryParseRtspUrl(requested, out var url, out var urlError);
            urlError ??= RtspUrlMasking.IsMasked(requested) ? MaskedPasswordError : null;
            if (urlError is not null)
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["rtspUrl"] = [urlError] }, title: "Kamera bilgileri geçersiz");
            }

            var result = await probe.ProbeAsync(url!, cancellationToken);
            return Results.Ok(result.Ok
                ? new ProbeCameraResponse(true, result.Codec, result.Width, result.Height, result.FramesPerSecond, null)
                : new ProbeCameraResponse(false, null, null, null, null, RtspUrlMasking.Redact(result.Error ?? "Bilinmeyen hata")));
        });

        return app;
    }

    private const string MaskedPasswordError = "Adres gizlenmiş bir parola (***) içeriyor; gerçek parolayı yazın.";

    private static void AddError(Dictionary<string, string[]> errors, string field, string? message)
    {
        if (message is not null)
        {
            errors[field] = [message];
        }
    }

    private static IResult NotFound(string id) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Kamera bulunamadı", detail: $"'{id}' kimliğinde kamera yok.");

    private static IResult BodyRequired() =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Geçersiz istek", detail: "İstek gövdesi (JSON) gerekli.");

    private static IResult PreconditionFailed(CameraRecord? current)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status412PreconditionFailed,
            Title = "Kamera başka biri tarafından değiştirildi",
            Detail = current is null
                ? "If-Match başlığı okunamadı; \"<sürüm>\" biçiminde olmalı."
                : $"Kameranın güncel sürümü {current.Version}. Sayfayı yenileyip değişikliği tekrar yapın.",
        };
        if (current is not null)
        {
            problem.Extensions["currentVersion"] = current.Version;
        }

        return Results.Problem(problem);
    }
}
