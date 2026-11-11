using System.Device.Gpio;
using System.Device.Spi;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace HusSkjerm;

public static class Pixel
{
    public const byte Black = 0, White = 1, Red = 2;
}

public sealed class DisplayOptions
{
    /// <summary>Auto (Epd på Linux, File ellers), Epd7in5bV2, File eller None.</summary>
    public string Driver { get; set; } = "Auto";
    public int ResetPin { get; set; } = 17;
    public int DcPin { get; set; } = 25;
    public int BusyPin { get; set; } = 24;
    public int PowerPin { get; set; } = 18;
    public int SpiBus { get; set; }
    public int SpiChipSelect { get; set; }
    /// <summary>Feilsøking: snu sort/hvitt eller rødt hvis fargene kommer ut feil.</summary>
    public bool InvertBlack { get; set; }
    public bool InvertRed { get; set; }
}

public interface IDisplay
{
    string Name { get; }
    Task ShowAsync(byte[] pixels, CancellationToken ct);
}

public static class PixelImage
{
    public static byte[] ToPng(byte[] pixels)
    {
        using var bitmap = new SKBitmap(Canvas.ScreenWidth, Canvas.ScreenHeight, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var colors = new SKColor[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
            colors[i] = pixels[i] switch { Pixel.Black => SKColors.Black, Pixel.Red => new SKColor(220, 0, 0), _ => SKColors.White };
        bitmap.Pixels = colors;
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }
}

/// <summary>Skriver siste bilde til data/current.png. Brukes på PC og som «Forhåndsvis nåværende».</summary>
public sealed class FileDisplay(ConfigStore store) : IDisplay
{
    public string Name => "File";
    public string Path => System.IO.Path.Combine(store.DataDirectory, "current.png");

    public Task ShowAsync(byte[] pixels, CancellationToken ct)
    {
        File.WriteAllBytes(Path, PixelImage.ToPng(pixels));
        return Task.CompletedTask;
    }
}

public sealed class NullDisplay : IDisplay
{
    public string Name => "None";
    public Task ShowAsync(byte[] pixels, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Waveshare 7.5" e-Paper HAT (B) V2, 800x480 sort/hvit/rød over SPI.
/// Kommandosekvensen følger Waveshares epd7in5b_V2-driver. Ikke testet på ekte skjerm.
/// </summary>
public sealed class EpdDisplay(IOptions<DisplayOptions> options, ILogger<EpdDisplay> log) : IDisplay, IDisposable
{
    private readonly DisplayOptions _o = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GpioController? _gpio;
    private SpiDevice? _spi;

    public string Name => "Epd7in5bV2";

    public async Task ShowAsync(byte[] pixels, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await Task.Run(() =>
            {
                var (black, red) = Pack(pixels, _o.InvertBlack, _o.InvertRed);
                Open();
                Init();
                Command(0x10); Data(black);
                Command(0x13); Data(red);
                Command(0x12);
                Thread.Sleep(100);
                WaitBusy();
                Sleep();
            }, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Svart-lag: 1 = hvit, 0 = svart. Rød-lag: 1 = rød. Rød piksel er «hvit» i svart-laget.</summary>
    public static (byte[] Black, byte[] Red) Pack(byte[] pixels, bool invertBlack = false, bool invertRed = false)
    {
        var stride = Canvas.ScreenWidth / 8;
        var black = new byte[stride * Canvas.ScreenHeight];
        var red = new byte[black.Length];
        for (var y = 0; y < Canvas.ScreenHeight; y++)
            for (var x = 0; x < Canvas.ScreenWidth; x++)
            {
                var p = pixels[y * Canvas.ScreenWidth + x];
                var bit = (byte)(0x80 >> (x & 7));
                var i = y * stride + (x >> 3);
                if (p != Pixel.Black) black[i] |= bit;
                if (p == Pixel.Red) red[i] |= bit;
            }
        if (invertBlack) for (var i = 0; i < black.Length; i++) black[i] = (byte)~black[i];
        if (invertRed) for (var i = 0; i < red.Length; i++) red[i] = (byte)~red[i];
        return (black, red);
    }

    private void Open()
    {
        if (_gpio is not null) return;
        _gpio = new GpioController();
        _gpio.OpenPin(_o.ResetPin, PinMode.Output);
        _gpio.OpenPin(_o.DcPin, PinMode.Output);
        _gpio.OpenPin(_o.PowerPin, PinMode.Output);
        _gpio.OpenPin(_o.BusyPin, PinMode.Input);
        _spi = SpiDevice.Create(new SpiConnectionSettings(_o.SpiBus, _o.SpiChipSelect) { ClockFrequency = 4_000_000, Mode = SpiMode.Mode0 });
        log.LogInformation("E-paper-skjerm åpnet (SPI{Bus}.{Cs}).", _o.SpiBus, _o.SpiChipSelect);
    }

    private void Init()
    {
        _gpio!.Write(_o.PowerPin, PinValue.High);
        _gpio.Write(_o.ResetPin, PinValue.High); Thread.Sleep(200);
        _gpio.Write(_o.ResetPin, PinValue.Low); Thread.Sleep(4);
        _gpio.Write(_o.ResetPin, PinValue.High); Thread.Sleep(200);

        Command(0x01); Data(0x07, 0x07, 0x3f, 0x3f);
        Command(0x04); Thread.Sleep(100); WaitBusy();
        Command(0x00); Data(0x0F);
        Command(0x61); Data(0x03, 0x20, 0x01, 0xE0);
        Command(0x15); Data(0x00);
        Command(0x50); Data(0x11, 0x07);
        Command(0x60); Data(0x22);
        Command(0x65); Data(0x00, 0x00, 0x00, 0x00);
    }

    private void Sleep()
    {
        Command(0x02); WaitBusy();
        Command(0x07); Data(0xA5);
        _gpio!.Write(_o.PowerPin, PinValue.Low);
    }

    private void WaitBusy()
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        do
        {
            Command(0x71);
            if (_gpio!.Read(_o.BusyPin) == PinValue.High) return;
            Thread.Sleep(50);
        } while (DateTime.UtcNow < deadline);
        throw new TimeoutException("E-paper-skjermen svarte ikke (BUSY).");
    }

    private void Command(byte command) { _gpio!.Write(_o.DcPin, PinValue.Low); _spi!.WriteByte(command); }
    private void Data(params byte[] data) => Data((ReadOnlySpan<byte>)data);

    private void Data(ReadOnlySpan<byte> data)
    {
        _gpio!.Write(_o.DcPin, PinValue.High);
        for (var i = 0; i < data.Length; i += 4096) _spi!.Write(data.Slice(i, Math.Min(4096, data.Length - i)));
    }

    public void Dispose() { _spi?.Dispose(); _gpio?.Dispose(); }
}
