using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HusSkjerm;
using Microsoft.Extensions.Options;
using SkiaSharp;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true);
builder.Configuration.AddEnvironmentVariables("HUSSKJERM_");

builder.Services.Configure<DataOptions>(builder.Configuration.GetSection("Data"));
builder.Services.Configure<AqaraOptions>(builder.Configuration.GetSection("Aqara"));
builder.Services.Configure<WeatherOptions>(builder.Configuration.GetSection("Weather"));
builder.Services.Configure<DisplayOptions>(builder.Configuration.GetSection("Display"));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddSingleton<ConfigStore>();
// Typede HttpClient-registreringer er transient; tjenestene må være singletons.
builder.Services.AddHttpClient("husskjerm", c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<AqaraClient>(sp, sp.GetRequiredService<IHttpClientFactory>().CreateClient("husskjerm")));
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<WeatherService>(sp, sp.GetRequiredService<IHttpClientFactory>().CreateClient("husskjerm")));
builder.Services.AddSingleton<SensorService>();
builder.Services.AddSingleton<IReadings>(sp => sp.GetRequiredService<SensorService>());
builder.Services.AddSingleton<IWeather>(sp => sp.GetRequiredService<WeatherService>());
builder.Services.AddSingleton<SlideRenderer>();
builder.Services.AddSingleton<FileDisplay>();
builder.Services.AddSingleton<IDisplay>(sp =>
{
    var driver = sp.GetRequiredService<IOptions<DisplayOptions>>().Value.Driver;
    if (driver.Equals("Auto", StringComparison.OrdinalIgnoreCase)) driver = OperatingSystem.IsLinux() ? "Epd7in5bV2" : "File";
    return driver.ToLowerInvariant() switch
    {
        "epd7in5bv2" => ActivatorUtilities.CreateInstance<EpdDisplay>(sp),
        "none" => new NullDisplay(),
        _ => sp.GetRequiredService<FileDisplay>(),
    };
});
builder.Services.AddSingleton<Rotator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SensorService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<WeatherService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<Rotator>());

var app = builder.Build();

var password = app.Configuration["Panel:Password"];
if (!string.IsNullOrEmpty(password))
{
    app.Use(async (ctx, next) =>
    {
        if (AuthHeader.IsValid(ctx.Request.Headers.Authorization, password)) { await next(); return; }
        ctx.Response.StatusCode = 401;
        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"HusSkjerm\", charset=\"UTF-8\"";
    });
}

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/config", (ConfigStore store) => store.Current);
api.MapPut("/config", (PanelConfig config, ConfigStore store) => store.Save(config));

api.MapGet("/status", (AqaraClient aqara, SensorService sensors, WeatherService weather, Rotator rotator) => new
{
    display = rotator.DisplayName,
    currentSlideId = rotator.CurrentSlideId,
    lastShown = rotator.LastShown,
    displayError = rotator.LastError,
    aqara = new { aqara.Configured, aqara.HasToken, lastRefresh = sensors.LastRefresh, error = sensors.LastError },
    weather = new { available = weather.Current is not null, error = weather.LastError },
});

api.MapGet("/readings", (SensorService sensors) => sensors.Snapshot());
api.MapGet("/aqara/devices", (AqaraClient aqara) => aqara.CachedDevices());

api.MapPost("/aqara/code", async (AqaraClient aqara, CancellationToken ct) =>
    await Guard(() => aqara.RequestAuthCodeAsync(ct)));

api.MapPost("/aqara/token", async (CodeRequest request, AqaraClient aqara, CancellationToken ct) =>
    await Guard(() => aqara.ExchangeCodeAsync(request.Code ?? "", ct)));

api.MapPost("/aqara/sync", async (AqaraClient aqara, SensorService sensors, CancellationToken ct) =>
{
    try
    {
        var devices = await aqara.SyncDevicesAsync(ct);
        await sensors.RefreshAsync(ct);
        return Results.Ok(devices);
    }
    catch (Exception ex) when (ex is AqaraException or HttpRequestException) { return Results.BadRequest(new { error = ex.Message }); }
});

api.MapPost("/sensors/refresh", async (SensorService sensors, CancellationToken ct) => { await sensors.RefreshAsync(ct); return Results.Ok(); });
api.MapPost("/weather/refresh", async (WeatherService weather, CancellationToken ct) => { await weather.RefreshAsync(ct); return Results.Ok(); });

api.MapGet("/preview/{id}.png", (string id, ConfigStore store, SlideRenderer renderer) =>
{
    var slide = store.Current.Slides.FirstOrDefault(s => s.Id == id);
    return slide is null ? Results.NotFound() : Results.File(PixelImage.ToPng(renderer.Render(slide, DateTime.Now)), "image/png");
});

api.MapGet("/current.png", (Rotator rotator, ConfigStore store, SlideRenderer renderer) =>
{
    var slide = store.Current.Slides.FirstOrDefault(s => s.Id == rotator.CurrentSlideId);
    if (slide is not null) return Results.File(PixelImage.ToPng(renderer.Render(slide, DateTime.Now)), "image/png");
    return rotator.LastFrame is { } frame ? Results.File(PixelImage.ToPng(frame), "image/png") : Results.NotFound();
});

api.MapPost("/show/{id}", (string id, ConfigStore store, Rotator rotator) =>
{
    if (store.Current.Slides.All(s => s.Id != id)) return Results.NotFound();
    rotator.ShowNow(id);
    return Results.Accepted();
});

api.MapGet("/images", (ConfigStore store) =>
    Directory.EnumerateFiles(store.ImagesDirectory).Select(Path.GetFileName).Order().ToList());

api.MapGet("/images/{name}", (string name, ConfigStore store) =>
    store.ImageExists(name) ? Results.File(Path.Combine(store.ImagesDirectory, name), ContentType(name)) : Results.NotFound());

api.MapPost("/images", async (HttpRequest request, ConfigStore store) =>
{
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "Forventet skjemaopplasting." });
    var form = await request.ReadFormAsync();
    var saved = new List<string>();
    foreach (var file in form.Files)
    {
        if (file.Length is 0 or > 15 * 1024 * 1024) return Results.BadRequest(new { error = $"{file.FileName}: tom eller større enn 15 MB." });
        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer);
        buffer.Position = 0;
        using var codec = SKCodec.Create(buffer);
        var ext = codec?.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => ".png",
            SKEncodedImageFormat.Jpeg => ".jpg",
            SKEncodedImageFormat.Gif => ".gif",
            SKEncodedImageFormat.Bmp => ".bmp",
            SKEncodedImageFormat.Webp => ".webp",
            _ => null,
        };
        if (ext is null) return Results.BadRequest(new { error = $"{file.FileName}: ikke et støttet bilde (png, jpg, gif, bmp, webp)." });
        var name = Guid.NewGuid().ToString("N") + ext;
        await File.WriteAllBytesAsync(Path.Combine(store.ImagesDirectory, name), buffer.ToArray());
        saved.Add(name);
    }
    return Results.Ok(saved);
});

api.MapDelete("/images/{name}", (string name, ConfigStore store) =>
{
    if (!store.ImageExists(name)) return Results.NotFound();
    File.Delete(Path.Combine(store.ImagesDirectory, name));
    var config = store.Current;
    config.Slides.RemoveAll(s => s.Type == SlideType.Image && s.Image == name);
    store.Save(config);
    return Results.NoContent();
});

app.Run();

static async Task<IResult> Guard(Func<Task> action)
{
    try { await action(); return Results.Ok(); }
    catch (Exception ex) when (ex is AqaraException or HttpRequestException) { return Results.BadRequest(new { error = ex.Message }); }
}

static string ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
{
    ".png" => "image/png",
    ".jpg" => "image/jpeg",
    ".gif" => "image/gif",
    ".bmp" => "image/bmp",
    ".webp" => "image/webp",
    _ => "application/octet-stream",
};

public sealed record CodeRequest(string? Code);

public static class AuthHeader
{
    public static bool IsValid(string? header, string password)
    {
        if (string.IsNullOrEmpty(header) || !AuthenticationHeaderValue.TryParse(header, out var value)
            || !"Basic".Equals(value.Scheme, StringComparison.OrdinalIgnoreCase) || value.Parameter is null) return false;
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(value.Parameter));
            var given = text[(text.IndexOf(':') + 1)..];
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(password)));
        }
        catch (FormatException) { return false; }
    }
}
