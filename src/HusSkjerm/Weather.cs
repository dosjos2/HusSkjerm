using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HusSkjerm;

public sealed class WeatherOptions
{
    /// <summary>MET Norway krever en identifiserende User-Agent, gjerne med e-post/URL.</summary>
    public string UserAgent { get; set; } = "HusSkjerm/1.0 (privat hjemmeprosjekt)";
    public int RefreshMinutes { get; set; } = 30;
    public bool Mock { get; set; }
}

/// <summary>Værdata fra MET Norway (yr.no) Locationforecast. Ingen API-nøkkel nødvendig.</summary>
public sealed class WeatherService(HttpClient http, ConfigStore store, IOptions<WeatherOptions> options, ILogger<WeatherService> log)
    : BackgroundService, IWeather
{
    private volatile WeatherSnapshot? _current;
    public WeatherSnapshot? Current => _current;
    public string? LastError { get; private set; }

    public async Task RefreshAsync(CancellationToken ct)
    {
        var s = store.Current.Settings;
        if (options.Value.Mock)
        {
            var now = DateTimeOffset.Now;
            _current = new WeatherSnapshot(7.4, 4.2, 81, 1.2, "lightrain",
                Enumerable.Range(1, 5).Select(i => new ForecastPoint(now.AddHours(i * 3), 7 - i * 0.8, i % 2 == 0 ? "cloudy" : "partlycloudy_day")).ToList(), now);
            return;
        }
        if (s.Latitude is null || s.Longitude is null) { _current = null; return; }
        try
        {
            var url = string.Create(CultureInfo.InvariantCulture,
                $"https://api.met.no/weatherapi/locationforecast/2.0/compact?lat={s.Latitude:0.####}&lon={s.Longitude:0.####}");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(options.Value.UserAgent);
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            _current = Parse(await response.Content.ReadAsStringAsync(ct));
            LastError = null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) return;
            LastError = ex.Message;
            log.LogWarning("Kunne ikke hente vær: {Message}", ex.Message);
        }
    }

    public static WeatherSnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var series = doc.RootElement.GetProperty("properties").GetProperty("timeseries");
        var points = series.EnumerateArray().ToList();
        var first = points[0].GetProperty("data");
        var details = first.GetProperty("instant").GetProperty("details");

        double? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : null;
        static string? Symbol(JsonElement data)
        {
            foreach (var key in new[] { "next_1_hours", "next_6_hours", "next_12_hours" })
                if (data.TryGetProperty(key, out var n) && n.TryGetProperty("summary", out var s) && s.TryGetProperty("symbol_code", out var c))
                    return c.GetString();
            return null;
        }

        double? precip = first.TryGetProperty("next_6_hours", out var n6) && n6.TryGetProperty("details", out var d6)
            ? Num(d6, "precipitation_amount") : null;

        var forecast = new List<ForecastPoint>();
        for (var i = 3; i < points.Count && forecast.Count < 5; i += 3)
        {
            var data = points[i].GetProperty("data");
            var t = Num(data.GetProperty("instant").GetProperty("details"), "air_temperature");
            if (t is null) continue;
            forecast.Add(new ForecastPoint(points[i].GetProperty("time").GetDateTimeOffset().ToLocalTime(), t.Value, Symbol(data) ?? "cloudy"));
        }

        return new WeatherSnapshot(
            Num(details, "air_temperature") ?? throw new InvalidOperationException("Mangler temperatur"),
            Num(details, "wind_speed"), Num(details, "relative_humidity"), precip,
            Symbol(first) ?? "cloudy", forecast, DateTimeOffset.Now);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RefreshAsync(ct);
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(5, options.Value.RefreshMinutes)), ct); } catch (OperationCanceledException) { }
        }
    }
}
