using HusSkjerm;
using SkiaSharp;

namespace HusSkjerm.Tests;

public class CoreTests
{
    [Fact]
    public void Sign_IsDeterministicLowercaseMd5()
    {
        var a = AqaraClient.Sign("tok", "app", "key", "n1", "123", "SECRET");
        var b = AqaraClient.Sign("tok", "app", "key", "n1", "123", "SECRET");
        Assert.Equal(a, b);
        Assert.Matches("^[0-9a-f]{32}$", a);
        Assert.NotEqual(a, AqaraClient.Sign(null, "app", "key", "n1", "123", "SECRET"));
    }

    [Fact]
    public void Sign_MatchesAqaraDocumentationExample()
    {
        var sign = AqaraClient.Sign("532cad73c5493193d63d367016b98b27", "4e693d54d75db580a56d1263",
            "78784564654feda454557", "C6wuzd0Qguxzelhb", "1618914078668", "gU7Qtxi4dWnYAdmudyxni52bWZ58b8uN");
        Assert.Equal("bfd8dd0e7c108353e6740d81e05982d8", sign);
    }

    [Fact]
    public void Normalize_ClampsRoomsAndDropsBrokenSlides()
    {
        var config = new PanelConfig
        {
            Settings = new Settings { RotationSeconds = 0 },
            Floors = [new Floor { Id = "f", Name = " Test ", Rooms = [new Room { X = 790, Y = 430, W = 5000, H = 5 }] }],
            Slides =
            [
                new Slide { Type = SlideType.Floor, FloorId = "f", Seconds = 99999 },
                new Slide { Type = SlideType.Floor, FloorId = "finnesikke" },
                new Slide { Type = SlideType.Image, Image = "borte.png" },
                new Slide { Type = SlideType.Weather },
            ],
        };
        config.Normalize(_ => false);

        Assert.Equal(1, config.Settings.RotationSeconds);
        Assert.Equal("Test", config.Floors[0].Name);
        var r = config.Floors[0].Rooms[0];
        Assert.InRange(r.X + r.W, 0, Canvas.PlanWidth);
        Assert.InRange(r.Y + r.H, 0, Canvas.PlanHeight);
        Assert.Equal(2, config.Slides.Count);
        Assert.Equal(3600, config.Slides[0].Seconds);
    }

    [Fact]
    public void Pack_PutsWhiteAndRedInCorrectLayers()
    {
        var pixels = new byte[Canvas.ScreenWidth * Canvas.ScreenHeight];
        Array.Fill(pixels, Pixel.White);
        pixels[0] = Pixel.Black;
        pixels[1] = Pixel.Red;
        var (black, red) = EpdDisplay.Pack(pixels);

        Assert.Equal(Canvas.ScreenWidth * Canvas.ScreenHeight / 8, black.Length);
        Assert.Equal(0, black[0] & 0x80);
        Assert.Equal(0x40, black[0] & 0x40);
        Assert.Equal(0x40, red[0] & 0x40);
        Assert.Equal(0, red[0] & 0x80);
    }

    [Fact]
    public void Quantizer_ClassifiesColors()
    {
        Assert.Equal(Pixel.Red, Quantizer.Classify(220, 20, 20));
        Assert.Equal(Pixel.Black, Quantizer.Classify(10, 10, 10));
        Assert.Equal(Pixel.White, Quantizer.Classify(250, 250, 250));
    }

    [Fact]
    public void Parse_ReadsCurrentAndForecast()
    {
        var points = string.Join(",", Enumerable.Range(0, 20).Select(i =>
            $"{{\"time\":\"2026-01-01T{i % 24:00}:00:00Z\",\"data\":{{\"instant\":{{\"details\":{{\"air_temperature\":{i},\"wind_speed\":3.5,\"relative_humidity\":70}}}},\"next_1_hours\":{{\"summary\":{{\"symbol_code\":\"cloudy\"}},\"details\":{{\"precipitation_amount\":0.4}}}}}}}}"));
        var snap = WeatherService.Parse($"{{\"properties\":{{\"timeseries\":[{points}]}}}}");

        Assert.Equal(0, snap.Temperature);
        Assert.Equal("cloudy", snap.Symbol);
        Assert.Equal(5, snap.Forecast.Count);
        Assert.Equal(3, snap.Forecast[0].Temperature);
    }

    [Theory]
    [InlineData("hemmelig", true)]
    [InlineData("feil", false)]
    public void AuthHeader_ChecksPassword(string given, bool ok)
    {
        var header = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("bruker:" + given));
        Assert.Equal(ok, AuthHeader.IsValid(header, "hemmelig"));
        Assert.False(AuthHeader.IsValid(null, "hemmelig"));
    }

    [Fact]
    public void Renderer_ShowsAlertTemperatureInRed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigStore(Microsoft.Extensions.Options.Options.Create(new DataOptions { Directory = dir }));
            var config = store.Current;
            config.Floors[0].Rooms = [new Room { Id = "r", Name = "Stue", X = 20, Y = 20, W = 300, H = 200, Sensors = ["s1"] }];
            store.Save(config);
            var renderer = new SlideRenderer(store, new FakeReadings(5), new FakeWeather());

            var slide = store.Current.Slides.First(s => s.Type == SlideType.Floor);
            var pixels = renderer.Render(slide, DateTime.Now);

            Assert.Equal(Canvas.ScreenWidth * Canvas.ScreenHeight, pixels.Length);
            Assert.Contains(Pixel.Red, pixels);
            Assert.Contains(Pixel.Black, pixels);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private sealed class FakeReadings(double temp) : IReadings
    {
        public Reading? Get(string sensorId) => new(temp, 40, DateTimeOffset.Now);
    }

    private sealed class FakeWeather : IWeather
    {
        public WeatherSnapshot? Current => null;
    }
}
