using System.Text.Json;
using System.Text.Json.Serialization;

namespace HusSkjerm;

public static class Canvas
{
    public const int ScreenWidth = 800;
    public const int ScreenHeight = 480;
    public const int HeaderHeight = 40;
    public const int PlanWidth = ScreenWidth;
    public const int PlanHeight = ScreenHeight - HeaderHeight;
}

public sealed class Room
{
    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; } = 120;
    public int H { get; set; } = 100;
    /// <summary>Aqara-enhets-id (did) for sensorene som hører til rommet.</summary>
    public List<string> Sensors { get; set; } = [];

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];
}

public sealed class Floor
{
    public string Id { get; set; } = Room.NewId();
    public string Name { get; set; } = "";
    public List<Room> Rooms { get; set; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SlideType { Floor, Weather, Image }

public sealed class Slide
{
    public string Id { get; set; } = Room.NewId();
    public SlideType Type { get; set; }
    public string? FloorId { get; set; }
    public string? Image { get; set; }
    /// <summary>Overstyrer standard visningstid hvis satt.</summary>
    public int? Seconds { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class Settings
{
    public int RotationSeconds { get; set; } = 10;
    public bool Paused { get; set; }
    public string Location { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double AlertLow { get; set; } = 15;
    public double AlertHigh { get; set; } = 26;
    /// <summary>Valgfri Aqara-sensor som brukes som utetemperatur i stedet for værvarselet.</summary>
    public string? OutdoorSensorId { get; set; }
}

public sealed class PanelConfig
{
    public int Version { get; set; } = 1;
    public Settings Settings { get; set; } = new();
    public List<Floor> Floors { get; set; } = [];
    public List<Slide> Slides { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static PanelConfig CreateDefault()
    {
        var first = new Floor { Name = "1. etasje" };
        var basement = new Floor { Name = "Kjeller" };
        return new PanelConfig
        {
            Floors = [first, basement],
            Slides =
            [
                new Slide { Type = SlideType.Floor, FloorId = first.Id },
                new Slide { Type = SlideType.Floor, FloorId = basement.Id },
                new Slide { Type = SlideType.Weather },
            ],
        };
    }

    /// <summary>Rydder og begrenser innkommende konfig slik at tegneren alltid får gyldige verdier.</summary>
    public PanelConfig Normalize(Func<string, bool> imageExists)
    {
        Settings ??= new Settings();
        Settings.RotationSeconds = Math.Clamp(Settings.RotationSeconds, 1, 3600);
        Settings.Location = (Settings.Location ?? "").Trim();
        if (Settings.Latitude is < -90 or > 90) Settings.Latitude = null;
        if (Settings.Longitude is < -180 or > 180) Settings.Longitude = null;
        if (string.IsNullOrWhiteSpace(Settings.OutdoorSensorId)) Settings.OutdoorSensorId = null;

        Floors = (Floors ?? []).Take(10).ToList();
        var seen = new HashSet<string>();
        foreach (var floor in Floors)
        {
            if (string.IsNullOrWhiteSpace(floor.Id) || !seen.Add(floor.Id)) floor.Id = Room.NewId();
            floor.Name = Clip(floor.Name, 30, "Etasje");
            floor.Rooms = (floor.Rooms ?? []).Take(40).ToList();
            foreach (var room in floor.Rooms)
            {
                if (string.IsNullOrWhiteSpace(room.Id) || !seen.Add(room.Id)) room.Id = Room.NewId();
                room.Name = Clip(room.Name, 30, "");
                room.W = Math.Clamp(room.W, 20, Canvas.PlanWidth);
                room.H = Math.Clamp(room.H, 20, Canvas.PlanHeight);
                room.X = Math.Clamp(room.X, 0, Canvas.PlanWidth - room.W);
                room.Y = Math.Clamp(room.Y, 0, Canvas.PlanHeight - room.H);
                room.Sensors = (room.Sensors ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Take(6).ToList();
            }
        }

        var floorIds = Floors.Select(f => f.Id).ToHashSet();
        var slideIds = new HashSet<string>();
        Slides = (Slides ?? []).Take(50).Where(s => s.Type switch
        {
            SlideType.Floor => s.FloorId is not null && floorIds.Contains(s.FloorId),
            SlideType.Image => !string.IsNullOrWhiteSpace(s.Image) && imageExists(s.Image),
            _ => true,
        }).ToList();
        foreach (var slide in Slides)
        {
            if (string.IsNullOrWhiteSpace(slide.Id) || !slideIds.Add(slide.Id)) { slide.Id = Room.NewId(); slideIds.Add(slide.Id); }
            if (slide.Seconds is { } sec) slide.Seconds = Math.Clamp(sec, 1, 3600);
        }
        return this;
    }

    private static string Clip(string? s, int max, string fallback)
    {
        s = (s ?? "").Trim();
        if (s.Length > max) s = s[..max];
        return s.Length == 0 ? fallback : s;
    }
}

public sealed record Reading(double? Temperature, double? Humidity, DateTimeOffset Updated);

public interface IReadings
{
    Reading? Get(string deviceId);
}

public sealed record ForecastPoint(DateTimeOffset Time, double Temperature, string Symbol);

public sealed record WeatherSnapshot(
    double Temperature, double? Wind, double? Humidity, double? Precipitation6h,
    string Symbol, IReadOnlyList<ForecastPoint> Forecast, DateTimeOffset Updated);

public interface IWeather
{
    WeatherSnapshot? Current { get; }
}
