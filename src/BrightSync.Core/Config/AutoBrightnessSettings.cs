namespace BrightSync.Core.Config;

public sealed class AutoBrightnessSettings
{
    public const int CurrentCurveVersion = 1;

    public bool Enabled { get; set; }
    public bool LockWhenManualBrightnessChanges { get; set; }
    public int CurveVersion { get; set; } = CurrentCurveVersion;
    public List<AutoBrightnessControlPoint> Curve { get; set; } = [];

    public static AutoBrightnessSettings CreateDefault()
    {
        return new AutoBrightnessSettings
        {
            Enabled = false,
            LockWhenManualBrightnessChanges = false,
            CurveVersion = CurrentCurveVersion,
            Curve = BuildDefaultCurve()
        };
    }

    public void EnsureDefaults()
    {
        if (CurveVersion <= 0)
            CurveVersion = CurrentCurveVersion;

        if (Curve.Count == 0)
            Curve = BuildDefaultCurve();

        Curve = Curve
            .Select(point => new AutoBrightnessControlPoint
            {
                MinuteOfDay = Math.Clamp(point.MinuteOfDay, 0, 1440),
                Brightness = Math.Clamp(point.Brightness, 0, 100)
            })
            .OrderBy(point => point.MinuteOfDay)
            .ToList();

        var expectedMinutes = GetDefaultPointMinutes();
        var normalized = new List<AutoBrightnessControlPoint>(expectedMinutes.Length);
        foreach (var minute in expectedMinutes)
        {
            var existing = Curve.FirstOrDefault(point => point.MinuteOfDay == minute);
            normalized.Add(existing ?? new AutoBrightnessControlPoint
            {
                MinuteOfDay = minute,
                Brightness = EstimateBrightnessForMinute(minute, TimeZoneInfo.Local)
            });
        }

        normalized[^1].Brightness = normalized[0].Brightness;
        Curve = normalized;
    }

    public static int[] GetDefaultPointMinutes()
    {
        return [0, 180, 360, 540, 720, 900, 1080, 1260, 1440];
    }

    private static List<AutoBrightnessControlPoint> BuildDefaultCurve()
    {
        return GetDefaultPointMinutes()
            .Select(minute => new AutoBrightnessControlPoint
            {
                MinuteOfDay = minute,
                Brightness = EstimateBrightnessForMinute(minute, TimeZoneInfo.Local)
            })
            .ToList();
    }

    private static int EstimateBrightnessForMinute(int minuteOfDay, TimeZoneInfo timeZone)
    {
        if (minuteOfDay >= 1440)
            minuteOfDay = 0;

        var offsetHours = timeZone.BaseUtcOffset.TotalHours;
        var sunriseMinute = (int)Math.Round(Math.Clamp(390 - (offsetHours * 12), 300, 480));
        var sunsetMinute = (int)Math.Round(Math.Clamp(1110 + (offsetHours * 12), 1020, 1260));
        const int nightBrightness = 18;
        const int noonBrightness = 82;

        if (minuteOfDay <= sunriseMinute)
        {
            var progress = sunriseMinute == 0 ? 0 : (double)minuteOfDay / sunriseMinute;
            return (int)Math.Round(Lerp(nightBrightness, 38, Ease(progress)));
        }

        if (minuteOfDay <= 720)
        {
            var progress = (double)(minuteOfDay - sunriseMinute) / Math.Max(1, 720 - sunriseMinute);
            return (int)Math.Round(Lerp(38, noonBrightness, Ease(progress)));
        }

        if (minuteOfDay <= sunsetMinute)
        {
            var progress = (double)(minuteOfDay - 720) / Math.Max(1, sunsetMinute - 720);
            return (int)Math.Round(Lerp(noonBrightness, 40, Ease(progress)));
        }

        var nightProgress = (double)(minuteOfDay - sunsetMinute) / Math.Max(1, 1440 - sunsetMinute);
        return (int)Math.Round(Lerp(40, nightBrightness, Ease(nightProgress)));
    }

    private static double Ease(double value)
    {
        var clamped = Math.Clamp(value, 0, 1);
        return 0.5 - (Math.Cos(clamped * Math.PI) / 2.0);
    }

    private static double Lerp(double from, double to, double amount)
        => from + ((to - from) * amount);
}