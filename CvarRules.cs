using System.Globalization;

namespace MgSprays;

internal static class CvarRules
{
    public static string SoundEventForVolume(string baseEvent, int percent)
    {
        if (percent is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        return percent == 100 ? baseEvent : baseEvent + ".Volume" + percent.ToString("D3", CultureInfo.InvariantCulture);
    }

    public static bool InRange(float value, float min, float max) =>
        float.IsFinite(value) && value >= min && value <= max;

    public static (bool Valid, bool Value) ParseBool(string text) =>
        (text is "0" or "1", text == "1");

    public static (bool Valid, float Value) ParseFloat(string text, float min, float max)
    {
        bool parsed = float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value);
        return (parsed && InRange(value, min, max), value);
    }

    public static (bool Valid, int Value) ParseInt(string text, int min, int max)
    {
        bool parsed = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value);
        return (parsed && value >= min && value <= max, value);
    }

    // A lifetime edit changes the deadline, not the original placement time.
    public static double Remaining(double placedAt, double now, float lifetime) =>
        placedAt + lifetime - now;

    public static int CooldownSecondsLeft(double lastUsed, double now, float cooldown) =>
        cooldown <= 0 ? 0 : (int)Math.Ceiling(Math.Max(0, Remaining(lastUsed, now, cooldown)));

    public static (bool Allowed, bool ReplaceOldest) PlanPlacement(
        int ownedCount, int playerLimit, int activeCount, int globalLimit)
    {
        bool globalFull = activeCount >= globalLimit;
        if (globalFull && ownedCount == 0) return (false, false);
        return (true, ownedCount > 0 && (ownedCount >= playerLimit || globalFull));
    }
}
