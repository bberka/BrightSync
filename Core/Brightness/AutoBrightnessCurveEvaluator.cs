using BrightSync.Core.Config;

namespace BrightSync.Core.Brightness;

public static class AutoBrightnessCurveEvaluator
{
    public static int Evaluate(IReadOnlyList<AutoBrightnessControlPoint> points, TimeSpan timeOfDay)
    {
        if (points.Count == 0)
            return 50;
        if (points.Count == 1)
            return Math.Clamp(points[0].Brightness, 0, 100);

        var minute = NormalizeMinute(timeOfDay.TotalMinutes);
        for (var i = 0; i < points.Count - 1; i++)
        {
            var start = points[i];
            var end = points[i + 1];
            if (minute > end.MinuteOfDay)
                continue;

            return Interpolate(start, end, minute);
        }

        return Math.Clamp(points[^1].Brightness, 0, 100);
    }

    private static int Interpolate(AutoBrightnessControlPoint start, AutoBrightnessControlPoint end, double minute)
    {
        var span = Math.Max(1, end.MinuteOfDay - start.MinuteOfDay);
        var progress = (minute - start.MinuteOfDay) / span;
        var eased = 0.5 - (Math.Cos(Math.Clamp(progress, 0, 1) * Math.PI) / 2.0);
        var brightness = start.Brightness + ((end.Brightness - start.Brightness) * eased);
        return (int)Math.Round(Math.Clamp(brightness, 0, 100));
    }

    private static double NormalizeMinute(double minute)
    {
        var normalized = minute % 1440.0;
        if (normalized < 0)
            normalized += 1440.0;
        return normalized;
    }
}
