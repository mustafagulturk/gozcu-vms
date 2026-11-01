using System.Text;
using Gozcu.AnalysisService.Api;
using Gozcu.Contracts.Analysis;
using Gozcu.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace Gozcu.UnitTests.Analysis;

public class EventSearchParametersTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Cursor_round_trips_with_microsecond_precision_and_is_url_safe()
    {
        var cursor = new EventSearchCursor(new DateTimeOffset(2026, 10, 31, 10, 15, 30, TimeSpan.Zero).AddTicks(1_234_560), Guid.NewGuid());

        var encoded = cursor.Encode();

        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
        Assert.True(EventSearchCursor.TryDecode(encoded, out var decoded));
        Assert.Equal(cursor, decoded);
    }

    [Fact]
    public void Cursor_encoding_matches_documented_format()
    {
        var id = Guid.Parse("0b9c7d0e-1f2a-4b3c-8d4e-5f6a7b8c9d0e");
        var cursor = new EventSearchCursor(DateTimeOffset.FromUnixTimeMilliseconds(1_761_905_730_123).AddTicks(4560), id);

        var raw = Encoding.UTF8.GetString(Convert.FromBase64String(Pad(cursor.Encode().Replace('-', '+').Replace('_', '/'))));

        Assert.Equal("1761905730123456|0b9c7d0e-1f2a-4b3c-8d4e-5f6a7b8c9d0e", raw);
    }

    [Fact]
    public void Cursor_with_non_utc_offset_is_normalised()
    {
        var utc = new DateTimeOffset(2026, 10, 31, 10, 0, 0, TimeSpan.Zero);
        var cursor = new EventSearchCursor(utc.ToOffset(TimeSpan.FromHours(3)), Guid.Empty);

        Assert.True(EventSearchCursor.TryDecode(cursor.Encode(), out var decoded));
        Assert.Equal(utc, decoded.StartedAt);
        Assert.Equal(TimeSpan.Zero, decoded.StartedAt.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("!!!")]
    [InlineData("YWJj")]                                // "abc": ayırıcı yok
    [InlineData("MTIzfG5vdC1hLWd1aWQ")]                 // "123|not-a-guid"
    [InlineData("LTF8MGI5YzdkMGUtMWYyYS00YjNjLThkNGUtNWY2YTdiOGM5ZDBl")]   // negatif zaman
    [InlineData("OTk5OTk5OTk5OTk5OTk5OTk5OTl8MGI5YzdkMGUtMWYyYS00YjNjLThkNGUtNWY2YTdiOGM5ZDBl")] // long taşması
    [InlineData("OTAwMDAwMDAwMDAwMDAwMDAwMHwwYjljN2QwZS0xZjJhLTRiM2MtOGQ0ZS01ZjZhN2I4YzlkMGU")] // DateTimeOffset taşması
    [InlineData("YWJj+/==")]
    public void Malformed_cursor_is_rejected_without_throwing(string value)
    {
        Assert.False(EventSearchCursor.TryDecode(value, out _));
    }

    [Fact]
    public void Defaults_apply_when_no_parameter_is_given()
    {
        Assert.True(EventSearchParameters.TryParse(Query(""), Now, out var query, out var error), error);

        Assert.Empty(query.CameraIds);
        Assert.Null(query.Category);
        Assert.Null(query.From);
        Assert.Null(query.To);
        Assert.Null(query.MinConfidence);
        Assert.Null(query.MinDuration);
        Assert.Null(query.After);
        Assert.Equal(EventSearchParameters.DefaultLimit, query.Limit);
        Assert.Equal(Now, query.Now);
    }

    [Fact]
    public void All_parameters_are_parsed()
    {
        var cursor = new EventSearchCursor(Now.AddHours(-1), Guid.NewGuid());
        var text = "cameraId=cam1&cameraId=cam2,cam3&cameraId=cam1&category=person&from=2026-10-31T10:00:00%2B03:00&to=2026-10-31T12:00:00Z"
            + $"&minConfidence=0.55&minDurationSeconds=2.5&limit=200&cursor={cursor.Encode()}";

        Assert.True(EventSearchParameters.TryParse(Query(text), Now, out var query, out var error), error);

        Assert.Equal(["cam1", "cam2", "cam3"], query.CameraIds);
        Assert.Equal(ObjectCategory.Person, query.Category);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 7, 0, 0, TimeSpan.Zero), query.From);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero), query.To);
        Assert.Equal(0.55f, query.MinConfidence);
        Assert.Equal(TimeSpan.FromSeconds(2.5), query.MinDuration);
        Assert.Equal(200, query.Limit);
        Assert.Equal(cursor, query.After);
    }

    [Fact]
    public void Time_without_offset_is_treated_as_utc()
    {
        Assert.True(EventSearchParameters.TryParse(Query("from=2026-10-31T10:00:00"), Now, out var query, out _));
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 10, 0, 0, TimeSpan.Zero), query.From);
    }

    [Theory]
    [InlineData("category=Cat", "kategori")]
    [InlineData("category=7", "kategori")]
    [InlineData("category=0", "kategori")]
    [InlineData("from=dün", "'from'")]
    [InlineData("to=2026-13-01", "'to'")]
    [InlineData("from=2026-10-31T10:00:00Z&to=2026-10-31T10:00:00Z", "sonra")]
    [InlineData("minConfidence=1.5", "'minConfidence'")]
    [InlineData("minConfidence=-0.1", "'minConfidence'")]
    [InlineData("minConfidence=NaN", "'minConfidence'")]
    [InlineData("minConfidence=0,5", "'minConfidence'")]
    [InlineData("minDurationSeconds=-1", "'minDurationSeconds'")]
    [InlineData("limit=0", "'limit'")]
    [InlineData("limit=201", "'limit'")]
    [InlineData("limit=ten", "'limit'")]
    [InlineData("limit=5&limit=6", "bir kez")]
    [InlineData("cursor=bozuk!", "'cursor'")]
    public void Invalid_parameter_is_rejected_with_turkish_message(string text, string expectedFragment)
    {
        Assert.False(EventSearchParameters.TryParse(Query(text), Now, out _, out var error));
        Assert.Contains(expectedFragment, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Too_many_cameras_are_rejected()
    {
        var text = string.Join('&', Enumerable.Range(0, EventSearchParameters.MaxCameras + 1).Select(i => $"cameraId=c{i}"));
        Assert.False(EventSearchParameters.TryParse(Query(text), Now, out _, out _));
    }

    [Fact]
    public void Frame_range_requires_from_and_to_and_applies_default_limit()
    {
        Assert.False(FrameRangeParameters.TryParse(Query("from=2026-10-31T10:00:00Z"), out _, out var error));
        Assert.Contains("gerekli", error, StringComparison.Ordinal);

        Assert.True(FrameRangeParameters.TryParse(Query("from=2026-10-31T10:00:00Z&to=2026-10-31T10:15:00Z"), out var range, out error), error);
        Assert.Equal(TimeSpan.FromMinutes(15), range.To - range.From);
        Assert.Equal(FrameRangeParameters.DefaultLimit, range.Limit);
    }

    [Theory]
    [InlineData("from=2026-10-31T10:00:00Z&to=2026-10-31T10:15:00.001Z", "15 dakika")]
    [InlineData("from=2026-10-31T10:00:00Z&to=2026-10-31T09:59:00Z", "sonra")]
    [InlineData("from=2026-10-31T10:00:00Z&to=2026-10-31T10:01:00Z&limit=5001", "'limit'")]
    [InlineData("from=x&to=2026-10-31T10:01:00Z", "'from'")]
    public void Invalid_frame_range_is_rejected(string text, string expectedFragment)
    {
        Assert.False(FrameRangeParameters.TryParse(Query(text), out _, out var error));
        Assert.Contains(expectedFragment, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Event_view_reports_duration_so_far_for_ongoing_event()
    {
        var started = Now.AddSeconds(-42.5);
        var ongoing = new DetectionEventRecord(Guid.NewGuid(), "cam1", ObjectCategory.Vehicle, started, null, 2, 0.8f, 10);
        var closed = ongoing with { EndedAt = started.AddSeconds(3) };

        var ongoingView = EventView.From(ongoing, "Otopark", Now);
        var closedView = EventView.From(closed, "Otopark", Now);

        Assert.Equal(42.5, ongoingView.DurationSeconds);
        Assert.Null(ongoingView.EndedAt);
        Assert.Equal("Vehicle", ongoingView.Category);
        Assert.Equal(3, closedView.DurationSeconds);
        Assert.Equal(0, EventView.From(ongoing with { StartedAt = Now.AddSeconds(5) }, "x", Now).DurationSeconds);
    }

    private static QueryCollection Query(string text) => new(QueryHelpers.ParseQuery(text));

    private static string Pad(string base64) => base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
}
