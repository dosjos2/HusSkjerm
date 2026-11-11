using SkiaSharp;

namespace HusSkjerm;

public static class Quantizer
{
    /// <summary>Hard terskling for tegninger/tekst: sort, hvit eller rød.</summary>
    public static byte[] Threshold(SKBitmap bitmap)
    {
        var px = bitmap.Pixels;
        var result = new byte[px.Length];
        for (var i = 0; i < px.Length; i++) result[i] = Classify(px[i].Red, px[i].Green, px[i].Blue);
        return result;
    }

    public static byte Classify(int r, int g, int b)
    {
        if (r - Math.Max(g, b) > 80) return Pixel.Red;
        return (r * 299 + g * 587 + b * 114) / 1000 < 128 ? Pixel.Black : Pixel.White;
    }

    /// <summary>Floyd–Steinberg-dithering mot paletten sort/hvit/rød, for bilder og bilder.</summary>
    public static byte[] Dither(SKBitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        var px = bitmap.Pixels;
        var buf = new float[w * h * 3];
        for (var i = 0; i < px.Length; i++) { buf[i * 3] = px[i].Red; buf[i * 3 + 1] = px[i].Green; buf[i * 3 + 2] = px[i].Blue; }
        var result = new byte[w * h];
        ReadOnlySpan<(int R, int G, int B)> palette = [(0, 0, 0), (255, 255, 255), (255, 0, 0)];

        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                float r = Math.Clamp(buf[i], 0, 255), g = Math.Clamp(buf[i + 1], 0, 255), b = Math.Clamp(buf[i + 2], 0, 255);
                var best = 0; var bestDist = float.MaxValue;
                for (var p = 0; p < palette.Length; p++)
                {
                    float dr = r - palette[p].R, dg = g - palette[p].G, db = b - palette[p].B;
                    var d = dr * dr * 0.299f + dg * dg * 0.587f + db * db * 0.114f;
                    if (d < bestDist) { bestDist = d; best = p; }
                }
                result[y * w + x] = (byte)best;
                float er = r - palette[best].R, eg = g - palette[best].G, eb = b - palette[best].B;
                Spread(buf, w, h, x + 1, y, er, eg, eb, 7 / 16f);
                Spread(buf, w, h, x - 1, y + 1, er, eg, eb, 3 / 16f);
                Spread(buf, w, h, x, y + 1, er, eg, eb, 5 / 16f);
                Spread(buf, w, h, x + 1, y + 1, er, eg, eb, 1 / 16f);
            }
        return result;
    }

    private static void Spread(float[] buf, int w, int h, int x, int y, float er, float eg, float eb, float f)
    {
        if (x < 0 || x >= w || y >= h) return;
        var i = (y * w + x) * 3;
        buf[i] += er * f; buf[i + 1] += eg * f; buf[i + 2] += eb * f;
    }
}

public sealed class SlideRenderer(ConfigStore store, IReadings readings, IWeather weather)
{
    private static readonly SKColor Ink = SKColors.Black;
    private static readonly SKColor Accent = new(255, 0, 0);
    private static readonly TimeSpan Stale = TimeSpan.FromHours(6);

    public byte[] Render(Slide slide, DateTime now)
    {
        using var bitmap = new SKBitmap(Canvas.ScreenWidth, Canvas.ScreenHeight, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        var photo = false;
        var config = store.Current;

        switch (slide.Type)
        {
            case SlideType.Floor:
                var floor = config.Floors.FirstOrDefault(f => f.Id == slide.FloorId);
                DrawFloor(canvas, floor, config.Settings, now);
                break;
            case SlideType.Weather:
                DrawWeather(canvas, config, now);
                break;
            case SlideType.Image:
                photo = DrawImage(canvas, slide.Image);
                break;
        }
        return photo ? Quantizer.Dither(bitmap) : Quantizer.Threshold(bitmap);
    }

    // ---------- gulv ----------

    private void DrawFloor(SKCanvas canvas, Floor? floor, Settings settings, DateTime now)
    {
        Header(canvas, floor?.Name ?? "Etasje", now);
        if (floor is null) return;
        canvas.Save();
        canvas.Translate(0, Canvas.HeaderHeight);
        foreach (var room in floor.Rooms)
        {
            var rect = new SKRect(room.X, room.Y, room.X + room.W, room.Y + room.H);
            using (var wall = new SKPaint { Color = Ink, Style = SKPaintStyle.Stroke, StrokeWidth = 4, IsAntialias = true }) canvas.DrawRect(rect, wall);

            if (room.Name.Length > 0)
                Text(canvas, room.Name, rect.Left + 8, rect.Top + 6 + Math.Clamp(room.H * 0.14f, 13, 20), Math.Clamp(room.H * 0.14f, 13, 20), true, Ink, SKTextAlign.Left, room.W - 16);

            var values = room.Sensors.Select(readings.Get).Where(r => r is not null && DateTimeOffset.Now - r.Updated < Stale).ToList();
            var temps = values.Where(r => r!.Temperature.HasValue).Select(r => r!.Temperature!.Value).ToList();
            var cx = rect.MidX; var cy = rect.MidY;
            var size = Math.Clamp(Math.Min(room.W * 0.26f, room.H * 0.36f), 16, 64);
            if (room.Sensors.Count > 0 && temps.Count == 0)
            {
                Text(canvas, "–", cx, cy + size / 3, size, true, Ink, SKTextAlign.Center, room.W - 16);
                continue;
            }
            if (temps.Count == 0) continue;

            var avg = temps.Average();
            var alert = avg < settings.AlertLow || avg > settings.AlertHigh;
            Text(canvas, FormatTemp(avg), cx, cy + size / 3, size, true, alert ? Accent : Ink, SKTextAlign.Center, room.W - 12);
            var hum = values.Where(r => r!.Humidity.HasValue).Select(r => r!.Humidity!.Value).ToList();
            if (hum.Count > 0 && room.H >= 80)
                Text(canvas, $"{hum.Average():0} %", cx, cy + size / 3 + Math.Clamp(size * 0.5f, 14, 22), Math.Clamp(size * 0.4f, 12, 18), false, Ink, SKTextAlign.Center, room.W - 12);
        }
        canvas.Restore();
    }

    // ---------- vær ----------

    private void DrawWeather(SKCanvas canvas, PanelConfig config, DateTime now)
    {
        Header(canvas, "Temperaturer og vær", now);
        var settings = config.Settings;

        // Inne
        Text(canvas, "INNE", 16, 72, 16, true, Ink, SKTextAlign.Left, 300);
        var rows = config.Floors.SelectMany(f => f.Rooms.Where(r => r.Sensors.Count > 0).Select(r => (Floor: f, Room: r))).ToList();
        var y = 104;
        var rowHeight = rows.Count > 11 ? 30 : 34;
        foreach (var (floor, room) in rows.Take(12))
        {
            var temps = room.Sensors.Select(readings.Get).Where(r => r?.Temperature is not null && DateTimeOffset.Now - r.Updated < Stale)
                .Select(r => r!.Temperature!.Value).ToList();
            Text(canvas, string.IsNullOrEmpty(room.Name) ? floor.Name : room.Name, 16, y, 20, false, Ink, SKTextAlign.Left, 250);
            if (temps.Count == 0) Text(canvas, "–", 390, y, 24, true, Ink, SKTextAlign.Right, 100);
            else
            {
                var avg = temps.Average();
                Text(canvas, FormatTemp(avg), 390, y, 24, true, avg < settings.AlertLow || avg > settings.AlertHigh ? Accent : Ink, SKTextAlign.Right, 100);
            }
            using var line = new SKPaint { Color = Ink, StrokeWidth = 1 };
            canvas.DrawLine(16, y + 8, 390, y + 8, line);
            y += rowHeight;
        }
        if (rows.Count == 0) Text(canvas, "Ingen rom har sensorer ennå.", 16, 110, 18, false, Ink, SKTextAlign.Left, 370);

        using (var divider = new SKPaint { Color = Ink, StrokeWidth = 3 }) canvas.DrawLine(410, 56, 410, 470, divider);

        // Ute
        Text(canvas, string.IsNullOrEmpty(settings.Location) ? "UTE" : "UTE – " + settings.Location.ToUpperInvariant(), 430, 72, 16, true, Ink, SKTextAlign.Left, 360);
        var w = weather.Current;
        var outdoorSensor = settings.OutdoorSensorId is { } id ? readings.Get(id) : null;
        double? outTemp = outdoorSensor?.Temperature is { } ot && DateTimeOffset.Now - outdoorSensor.Updated < Stale ? ot : w?.Temperature;

        if (w is null && outTemp is null)
        {
            Text(canvas, "Ingen værdata.", 430, 130, 20, true, Ink, SKTextAlign.Left, 360);
            Text(canvas, "Sett posisjon i kontrollpanelet.", 430, 160, 16, false, Ink, SKTextAlign.Left, 360);
            return;
        }
        if (w is not null) DrawIcon(canvas, w.Symbol, 490, 150, 90);
        if (outTemp is { } t)
            Text(canvas, FormatTemp(t), 790, 170, 72, true, t < 0 ? Ink : Ink, SKTextAlign.Right, 270);
        if (w is not null)
        {
            Text(canvas, Describe(w.Symbol), 430, 222, 22, true, Ink, SKTextAlign.Left, 360);
            var details = new List<string>();
            if (w.Wind is { } wind) details.Add($"Vind {wind:0} m/s");
            if (w.Humidity is { } h) details.Add($"Luftfuktighet {h:0} %");
            if (w.Precipitation6h is { } p) details.Add($"Nedbør neste 6 t: {p:0.#} mm");
            var dy = 252;
            foreach (var d in details) { Text(canvas, d, 430, dy, 18, false, Ink, SKTextAlign.Left, 360); dy += 26; }

            var colW = 360f / 5;
            for (var i = 0; i < w.Forecast.Count; i++)
            {
                var f = w.Forecast[i];
                var cx = 430 + colW * i + colW / 2;
                Text(canvas, f.Time.ToString("HH"), cx, 352, 16, true, Ink, SKTextAlign.Center, colW);
                DrawIcon(canvas, f.Symbol, cx, 400, 38);
                Text(canvas, FormatTemp(f.Temperature), cx, 454, 18, true, f.Temperature <= 0 ? Accent : Ink, SKTextAlign.Center, colW);
            }
            using var line = new SKPaint { Color = Ink, StrokeWidth = 1 };
            canvas.DrawLine(430, 330, 790, 330, line);
        }
    }

    // ---------- bilde ----------

    private bool DrawImage(SKCanvas canvas, string? name)
    {
        if (name is null || !store.ImageExists(name)) { Text(canvas, "Bildet finnes ikke", 400, 240, 24, true, Ink, SKTextAlign.Center, 700); return false; }
        using var source = SKBitmap.Decode(Path.Combine(store.ImagesDirectory, name));
        if (source is null) { Text(canvas, "Kunne ikke lese bildet", 400, 240, 24, true, Ink, SKTextAlign.Center, 700); return false; }

        var scale = Math.Max((float)Canvas.ScreenWidth / source.Width, (float)Canvas.ScreenHeight / source.Height);
        float w = source.Width * scale, h = source.Height * scale;
        var dest = new SKRect((Canvas.ScreenWidth - w) / 2, (Canvas.ScreenHeight - h) / 2, (Canvas.ScreenWidth + w) / 2, (Canvas.ScreenHeight + h) / 2);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        return true;
    }

    // ---------- hjelpere ----------

    private static void Header(SKCanvas canvas, string title, DateTime now)
    {
        Text(canvas, title, 16, 29, 24, true, Ink, SKTextAlign.Left, 560);
        Text(canvas, now.ToString("ddd d. MMM  HH:mm", new System.Globalization.CultureInfo("nb-NO")), 784, 28, 18, false, Ink, SKTextAlign.Right, 300);
        using var line = new SKPaint { Color = Ink, StrokeWidth = 3 };
        canvas.DrawLine(0, Canvas.HeaderHeight - 2, Canvas.ScreenWidth, Canvas.HeaderHeight - 2, line);
    }

    public static string FormatTemp(double value)
    {
        var rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        return rounded.ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("nb-NO")) + "°";
    }

    private static readonly Dictionary<(bool Bold, int Size), SKFont> Fonts = [];
    private static readonly SKTypeface Regular = SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyle.Normal) ?? SKTypeface.Default;
    private static readonly SKTypeface Bold = SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyle.Bold) ?? SKTypeface.Default;

    private static void Text(SKCanvas canvas, string text, float x, float y, float size, bool bold, SKColor color, SKTextAlign align, float maxWidth)
    {
        using var font = new SKFont(bold ? Bold : Regular, size) { Edging = SKFontEdging.Antialias };
        var width = font.MeasureText(text);
        if (width > maxWidth && maxWidth > 0) font.Size = Math.Max(8, size * maxWidth / width);
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawText(text, x, y, align, font, paint);
    }

    public static string Describe(string symbol)
    {
        var s = symbol.Split('_')[0];
        var thunder = s.Contains("thunder") ? " og torden" : "";
        string Core(string x) => x switch
        {
            "clearsky" => "Klarvær", "fair" => "Lettskyet", "partlycloudy" => "Delvis skyet", "cloudy" => "Skyet", "fog" => "Tåke",
            _ when x.Contains("heavyrain") => "Kraftig regn", _ when x.Contains("lightrain") => "Lett regn", _ when x.Contains("rain") => "Regn",
            _ when x.Contains("heavysleet") || x.Contains("sleet") => "Sludd",
            _ when x.Contains("heavysnow") => "Kraftig snø", _ when x.Contains("lightsnow") => "Lett snø", _ when x.Contains("snow") => "Snø",
            _ => "Skyet",
        };
        return Core(s.Replace("showers", "").Replace("andthunder", "").Replace("thunder", "")) + (s.Contains("showers") ? " (byger)" : "") + thunder;
    }

    public static void DrawIcon(SKCanvas canvas, string symbol, float cx, float cy, float size)
    {
        var s = symbol.ToLowerInvariant();
        var night = s.EndsWith("_night");
        var u = size / 100f;
        bool has(string k) => s.Contains(k);

        if (has("clearsky") || has("fair"))
            Sun(canvas, has("fair") || has("clearsky") ? cx - (has("fair") ? 8 * u : 0) : cx, cy - (has("fair") ? 8 * u : 0), has("fair") ? 28 * u : 34 * u, night);
        if (has("partlycloudy")) { Sun(canvas, cx - 18 * u, cy - 18 * u, 24 * u, night); Cloud(canvas, cx + 6 * u, cy + 8 * u, u * 0.85f); }
        else if (has("fair")) Cloud(canvas, cx + 14 * u, cy + 14 * u, u * 0.55f);
        else if (has("cloudy") || has("rain") || has("sleet") || has("snow") || has("thunder")) Cloud(canvas, cx, cy - 6 * u, u);
        if (has("rain") || has("sleet")) Drops(canvas, cx, cy + 30 * u, u);
        if (has("snow") || has("sleet")) Flakes(canvas, cx + (has("sleet") ? 22 * u : 0), cy + 32 * u, u);
        if (has("thunder")) Bolt(canvas, cx, cy + 6 * u, u);
        if (has("fog"))
        {
            using var p = new SKPaint { Color = Ink, StrokeWidth = 5 * u, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
            for (var i = -1; i <= 1; i++) canvas.DrawLine(cx - 34 * u, cy + i * 18 * u, cx + 34 * u, cy + i * 18 * u, p);
        }
    }

    private static void Sun(SKCanvas canvas, float cx, float cy, float r, bool moon)
    {
        using var fill = new SKPaint { Color = moon ? Ink : Accent, IsAntialias = true };
        canvas.DrawCircle(cx, cy, r * 0.6f, fill);
        if (moon)
        {
            using var cut = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawCircle(cx + r * 0.3f, cy - r * 0.2f, r * 0.5f, cut);
            return;
        }
        using var ray = new SKPaint { Color = Accent, StrokeWidth = Math.Max(2, r * 0.14f), StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        for (var i = 0; i < 8; i++)
        {
            var a = i * Math.PI / 4;
            canvas.DrawLine(cx + (float)Math.Cos(a) * r * 0.8f, cy + (float)Math.Sin(a) * r * 0.8f, cx + (float)Math.Cos(a) * r, cy + (float)Math.Sin(a) * r, ray);
        }
    }

    private static void Cloud(SKCanvas canvas, float cx, float cy, float u)
    {
        void Shapes(SKCanvas c, SKPaint paint, float grow)
        {
            c.DrawCircle(cx - 16 * u, cy + 4 * u, 18 * u + grow, paint);
            c.DrawCircle(cx + 6 * u, cy - 8 * u, 24 * u + grow, paint);
            c.DrawCircle(cx + 26 * u, cy + 6 * u, 16 * u + grow, paint);
            c.DrawRoundRect(cx - 34 * u - grow, cy + 2 * u - grow, 78 * u + 2 * grow, 22 * u + 2 * grow, 10 * u, 10 * u, paint);
        }
        using var black = new SKPaint { Color = Ink, IsAntialias = true };
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        Shapes(canvas, black, Math.Max(2, 3 * u));
        Shapes(canvas, white, 0);
    }

    private static void Drops(SKCanvas canvas, float cx, float cy, float u)
    {
        using var p = new SKPaint { Color = Ink, StrokeWidth = 4 * u, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        for (var i = -1; i <= 1; i++) canvas.DrawLine(cx + i * 20 * u, cy, cx + i * 20 * u - 6 * u, cy + 18 * u, p);
    }

    private static void Flakes(SKCanvas canvas, float cx, float cy, float u)
    {
        using var p = new SKPaint { Color = Ink, IsAntialias = true };
        for (var i = -1; i <= 1; i++) canvas.DrawCircle(cx + i * 20 * u, cy + 10 * u, 4 * u, p);
    }

    private static void Bolt(SKCanvas canvas, float cx, float cy, float u)
    {
        using var path = new SKPath();
        path.MoveTo(cx + 6 * u, cy); path.LineTo(cx - 10 * u, cy + 24 * u); path.LineTo(cx, cy + 24 * u);
        path.LineTo(cx - 6 * u, cy + 44 * u); path.LineTo(cx + 16 * u, cy + 16 * u); path.LineTo(cx + 4 * u, cy + 16 * u); path.Close();
        using var p = new SKPaint { Color = Accent, IsAntialias = true };
        canvas.DrawPath(path, p);
    }
}
