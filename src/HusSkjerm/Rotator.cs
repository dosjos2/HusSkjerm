using System.Security.Cryptography;

namespace HusSkjerm;

/// <summary>Bytter mellom visningene og sender dem til skjermen.</summary>
public sealed class Rotator(ConfigStore store, SlideRenderer renderer, IDisplay display, ILogger<Rotator> log) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0);
    private volatile string? _force;
    private string? _lastHash;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

    public byte[]? LastFrame { get; private set; }
    public string? CurrentSlideId { get; private set; }
    public DateTimeOffset? LastShown { get; private set; }
    public string? LastError { get; private set; }
    public string DisplayName => display.Name;

    public void ShowNow(string slideId)
    {
        _force = slideId;
        _wake.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var config = store.Current;
            var active = config.Slides.Where(s => s.Enabled).ToList();
            var forced = _force;
            if (active.Count == 0 || (config.Settings.Paused && forced is null))
            {
                await Wait(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            Slide slide;
            if (forced is not null)
            {
                _force = null;
                slide = config.Slides.FirstOrDefault(s => s.Id == forced) ?? active[0];
            }
            else
            {
                var at = active.FindIndex(s => s.Id == CurrentSlideId);
                slide = active[(at + 1) % active.Count];
            }

            var started = DateTime.UtcNow;
            try
            {
                var pixels = renderer.Render(slide, DateTime.Now);
                CurrentSlideId = slide.Id;
                LastFrame = pixels;

                var hash = Convert.ToHexString(SHA1.HashData(pixels));
                if (hash != _lastHash || DateTimeOffset.Now - _lastRefresh > TimeSpan.FromHours(1))
                {
                    await display.ShowAsync(pixels, ct);
                    _lastHash = hash;
                    _lastRefresh = DateTimeOffset.Now;
                    LastShown = DateTimeOffset.Now;
                    log.LogInformation("Viste {Type} ({Id}) på {Display}.", slide.Type, slide.Id, display.Name);
                }
                LastError = null;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                LastError = ex.Message;
                log.LogError(ex, "Kunne ikke vise {Type} ({Id}).", slide.Type, slide.Id);
                await Wait(TimeSpan.FromSeconds(10), ct);
                continue;
            }

            var seconds = slide.Seconds ?? config.Settings.RotationSeconds;
            var remaining = TimeSpan.FromSeconds(seconds) - (DateTime.UtcNow - started);
            if (remaining > TimeSpan.Zero) await Wait(remaining, ct);
        }
    }

    private async Task Wait(TimeSpan time, CancellationToken ct)
    {
        try { await _wake.WaitAsync(time, ct); } catch (OperationCanceledException) { }
    }
}
