using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace HusSkjerm;

public sealed class AqaraOptions
{
    public string AppId { get; set; } = "";
    public string KeyId { get; set; } = "";
    public string AppKey { get; set; } = "";
    /// <summary>E-post eller telefon for Aqara-kontoen.</summary>
    public string Account { get; set; } = "";
    public int AccountType { get; set; }
    /// <summary>open-ger.aqara.com (Europa), open-cn, open-usa, open-kr, open-ru, open-sg.</summary>
    public string Domain { get; set; } = "open-ger.aqara.com";
    /// <summary>Valgfritt: bruk en ferdig access token i stedet for innlogging via panelet.</summary>
    public string AccessToken { get; set; } = "";
    public string TemperatureResource { get; set; } = "0.1.85";
    public string HumidityResource { get; set; } = "0.2.85";
    public int RefreshMinutes { get; set; } = 5;
    /// <summary>Falske enheter og verdier – for å teste uten Aqara-konto.</summary>
    public bool Mock { get; set; }
}

public sealed record AqaraDevice(string Did, string Name, string Model);

public sealed class AqaraException(string message) : Exception(message);

public sealed class AqaraToken
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class AqaraClient
{
    private readonly HttpClient _http;
    private readonly AqaraOptions _options;
    private readonly string _tokenFile;
    private readonly string _devicesFile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AqaraToken? _token;
    private List<AqaraDevice>? _devices;

    public AqaraClient(HttpClient http, IOptions<AqaraOptions> options, ConfigStore store)
    {
        _http = http;
        _options = options.Value;
        _tokenFile = Path.Combine(store.DataDirectory, "aqara-token.json");
        _devicesFile = Path.Combine(store.DataDirectory, "devices.json");
    }

    public bool Mock => _options.Mock;
    public bool Configured => Mock || (!string.IsNullOrWhiteSpace(_options.AppId) && !string.IsNullOrWhiteSpace(_options.KeyId)
                                       && !string.IsNullOrWhiteSpace(_options.AppKey));
    public bool HasToken => Mock || !string.IsNullOrWhiteSpace(_options.AccessToken) || LoadToken() is not null;

    public static string Sign(string? accessToken, string appId, string keyId, string nonce, string time, string appKey)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(accessToken)) sb.Append("Accesstoken=").Append(accessToken).Append('&');
        sb.Append("Appid=").Append(appId).Append("&Keyid=").Append(keyId).Append("&Nonce=").Append(nonce).Append("&Time=").Append(time).Append(appKey);
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString().ToLowerInvariant()))).ToLowerInvariant();
    }

    public async Task RequestAuthCodeAsync(CancellationToken ct)
    {
        if (Mock) return;
        await CallAsync("config.auth.getAuthCode", new
        {
            account = _options.Account,
            accountType = _options.AccountType,
            accessTokenValidity = "30d",
        }, withToken: false, ct);
    }

    public async Task ExchangeCodeAsync(string code, CancellationToken ct)
    {
        if (Mock) return;
        var result = await CallAsync("config.auth.getToken", new
        {
            authCode = code.Trim(),
            account = _options.Account,
            accountType = _options.AccountType,
        }, withToken: false, ct);
        SaveToken(ParseToken(result));
    }

    public async Task<IReadOnlyList<AqaraDevice>> SyncDevicesAsync(CancellationToken ct)
    {
        if (Mock) return _devices = MockDevices;
        var all = new List<AqaraDevice>();
        for (var page = 1; page <= 20; page++)
        {
            var result = await CallAsync("query.device.info", new { pageNum = page, pageSize = 50 }, withToken: true, ct);
            var data = result?["data"]?.AsArray() ?? [];
            foreach (var d in data)
            {
                var did = d?["did"]?.GetValue<string>();
                if (string.IsNullOrEmpty(did)) continue;
                all.Add(new AqaraDevice(did, d?["deviceName"]?.GetValue<string>() ?? did, d?["model"]?.GetValue<string>() ?? ""));
            }
            if (data.Count < 50) break;
        }
        _devices = all;
        File.WriteAllText(_devicesFile, JsonSerializer.Serialize(all, PanelConfig.Json));
        return all;
    }

    public IReadOnlyList<AqaraDevice> CachedDevices()
    {
        if (_devices is not null) return _devices;
        if (Mock) return _devices = MockDevices;
        try { if (File.Exists(_devicesFile)) return _devices = JsonSerializer.Deserialize<List<AqaraDevice>>(File.ReadAllText(_devicesFile), PanelConfig.Json) ?? []; }
        catch (JsonException) { }
        return _devices = [];
    }

    public async Task<Dictionary<string, Reading>> ReadAsync(IReadOnlyCollection<string> dids, CancellationToken ct)
    {
        var map = new Dictionary<string, Reading>();
        if (dids.Count == 0) return map;
        var now = DateTimeOffset.Now;
        if (Mock)
        {
            foreach (var did in dids)
            {
                var seed = Math.Abs(did.GetHashCode()) % 100;
                map[did] = new Reading(Math.Round(17 + seed % 9 + (seed % 7) / 10.0, 1), 35 + seed % 30, now);
            }
            return map;
        }

        var resources = new[] { _options.TemperatureResource, _options.HumidityResource };
        var result = await CallAsync("query.resource.value", new
        {
            resources = dids.Select(d => new { subjectId = d, resourceIds = resources }).ToArray(),
        }, withToken: true, ct);

        var temps = new Dictionary<string, (double? T, double? H, long Ts)>();
        foreach (var item in result?.AsArray() ?? [])
        {
            var did = item?["subjectId"]?.GetValue<string>();
            var rid = item?["resourceId"]?.GetValue<string>();
            if (did is null || rid is null || !double.TryParse(item?["value"]?.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var raw)) continue;
            long.TryParse(item?["timeStamp"]?.ToString(), out var ts);
            temps.TryGetValue(did, out var cur);
            if (rid == _options.TemperatureResource) cur.T = raw / 100.0;
            else if (rid == _options.HumidityResource) cur.H = raw / 100.0;
            cur.Ts = Math.Max(cur.Ts, ts);
            temps[did] = cur;
        }
        foreach (var (did, v) in temps)
            map[did] = new Reading(v.T, v.H, v.Ts > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(v.Ts) : now);
        return map;
    }

    private static readonly List<AqaraDevice> MockDevices =
    [
        new("lumi.mock.stue", "Temperatursensor stue", "lumi.weather.v1"),
        new("lumi.mock.kjokken", "Temperatursensor kjøkken", "lumi.weather.v1"),
        new("lumi.mock.soverom", "Temperatursensor soverom", "lumi.weather.v1"),
        new("lumi.mock.kjeller", "Temperatursensor kjeller", "lumi.weather.v1"),
        new("lumi.mock.bad", "Temperatursensor bad", "lumi.weather.v1"),
    ];

    private static AqaraToken ParseToken(JsonNode? result) => new()
    {
        AccessToken = result?["accessToken"]?.GetValue<string>() ?? throw new AqaraException("Svaret manglet accessToken."),
        RefreshToken = result["refreshToken"]?.GetValue<string>() ?? "",
        ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(int.TryParse(result["expiresIn"]?.ToString(), out var s) ? s : 86400),
    };

    private AqaraToken? LoadToken()
    {
        if (_token is not null) return _token;
        try { if (File.Exists(_tokenFile)) return _token = JsonSerializer.Deserialize<AqaraToken>(File.ReadAllText(_tokenFile)); }
        catch (JsonException) { }
        return null;
    }

    private void SaveToken(AqaraToken token)
    {
        _token = token;
        File.WriteAllText(_tokenFile, JsonSerializer.Serialize(token));
    }

    private async Task<string?> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_options.AccessToken)) return _options.AccessToken;
        var token = LoadToken() ?? throw new AqaraException("Ikke logget inn mot Aqara. Be om kode og skriv den inn under «Aqara» i panelet.");
        if (!forceRefresh && token.ExpiresAt > DateTimeOffset.UtcNow.AddHours(1)) return token.AccessToken;
        if (string.IsNullOrEmpty(token.RefreshToken)) return token.AccessToken;

        var result = await SendAsync("config.auth.refreshToken", new { refreshToken = token.RefreshToken }, null, ct);
        var fresh = ParseToken(result);
        if (string.IsNullOrEmpty(fresh.RefreshToken)) fresh.RefreshToken = token.RefreshToken;
        SaveToken(fresh);
        return fresh.AccessToken;
    }

    private async Task<JsonNode?> CallAsync(string intent, object data, bool withToken, CancellationToken ct)
    {
        if (!Configured) throw new AqaraException("Aqara er ikke konfigurert (Aqara:AppId, KeyId og AppKey i appsettings.json).");
        await _gate.WaitAsync(ct);
        try
        {
            var token = withToken ? await GetAccessTokenAsync(false, ct) : null;
            return await SendAsync(intent, data, token, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<JsonNode?> SendAsync(string intent, object data, string? token, CancellationToken ct)
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var nonce = Guid.NewGuid().ToString("N");
        var appId = _options.AppId.Trim();
        var keyId = _options.KeyId.Trim();
        var appKey = _options.AppKey.Trim();
        token = token?.Trim();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{_options.Domain}/v3.0/open/api")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { intent, data }), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Appid", appId);
        request.Headers.TryAddWithoutValidation("Keyid", keyId);
        request.Headers.TryAddWithoutValidation("Nonce", nonce);
        request.Headers.TryAddWithoutValidation("Time", time);
        request.Headers.TryAddWithoutValidation("Sign", Sign(token, appId, keyId, nonce, time, appKey));
        request.Headers.TryAddWithoutValidation("Lang", "en");
        if (!string.IsNullOrEmpty(token)) request.Headers.TryAddWithoutValidation("Accesstoken", token);

        using var response = await _http.SendAsync(request, ct);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        var code = body?["code"]?.GetValue<int>() ?? -1;
        if (code != 0)
            throw new AqaraException($"Aqara svarte {code}: {body?["message"]?.GetValue<string>() ?? response.ReasonPhrase}");
        return body?["result"];
    }
}

public sealed class SensorService(AqaraClient aqara, ConfigStore store, IOptions<AqaraOptions> options, ILogger<SensorService> log) : BackgroundService, IReadings
{
    private readonly Dictionary<string, Reading> _readings = [];
    private readonly object _lock = new();
    public string? LastError { get; private set; }
    public DateTimeOffset? LastRefresh { get; private set; }

    public Reading? Get(string deviceId)
    {
        lock (_lock) return _readings.GetValueOrDefault(deviceId);
    }

    public IReadOnlyDictionary<string, Reading> Snapshot()
    {
        lock (_lock) return new Dictionary<string, Reading>(_readings);
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        var config = store.Current;
        var dids = config.Floors.SelectMany(f => f.Rooms).SelectMany(r => r.Sensors).ToHashSet();
        if (config.Settings.OutdoorSensorId is { } outdoor) dids.Add(outdoor);
        if (dids.Count == 0) return;
        try
        {
            var fresh = await aqara.ReadAsync(dids, ct);
            lock (_lock) foreach (var (k, v) in fresh) _readings[k] = v;
            LastError = null;
            LastRefresh = DateTimeOffset.Now;
        }
        catch (Exception ex) when (ex is AqaraException or HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested) return;
            LastError = ex.Message;
            log.LogWarning("Kunne ikke hente Aqara-data: {Message}", ex.Message);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (aqara.Configured && aqara.HasToken) await RefreshAsync(ct);
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, options.Value.RefreshMinutes)), ct); } catch (OperationCanceledException) { }
        }
    }
}
