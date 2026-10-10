namespace BrightSync.Core.Monitors;

public readonly record struct MonitorIdentity(
    string ManufacturerName,
    string ModelName,
    string FriendlyName)
{
    public static MonitorIdentity Unknown => new(string.Empty, "Unknown Monitor", "Unknown Monitor");
}

/// <summary>Builds user-facing monitor names from vendor/model fragments (EDID, WMI, PnP IDs).</summary>
public static class MonitorNames
{
    /// <summary>
    /// Decodes a PnP vendor ID (first 3 chars of a hardware ID such as "DEL4141") to a manufacturer name.
    /// The remaining chars are the model code and are kept as-is.
    /// </summary>
    public static MonitorIdentity DecodePnpId(string hwId)
    {
        if (hwId.Length < 3) return new MonitorIdentity(string.Empty, hwId, hwId);

        // 3-char EISA/PnP vendor codes (ISA Plug and Play standard)
        var vendor = hwId[..3].ToUpperInvariant();
        var model = hwId.Length > 3 ? hwId[3..] : string.Empty;

        return BuildIdentity(DecodeManufacturerCode(vendor), model);
    }

    public static MonitorIdentity BuildIdentity(string manufacturer, string model)
    {
        manufacturer = manufacturer.Trim();
        model = model.Trim();

        if (string.IsNullOrWhiteSpace(manufacturer) && string.IsNullOrWhiteSpace(model))
            return MonitorIdentity.Unknown;

        if (string.IsNullOrWhiteSpace(manufacturer))
            return new MonitorIdentity(string.Empty, model, model);

        var cleanedModel = RemoveDuplicatedManufacturerPrefix(model, manufacturer);
        var friendly = string.IsNullOrWhiteSpace(cleanedModel)
            ? manufacturer
            : $"{manufacturer} {cleanedModel}";
        return new MonitorIdentity(manufacturer, cleanedModel, friendly);
    }

    public static string RemoveDuplicatedManufacturerPrefix(string model, string manufacturer)
    {
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(manufacturer))
            return model;

        if (model.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase))
            return model[manufacturer.Length..].TrimStart(' ', '-', '_');

        return model;
    }

    public static string DecodeManufacturerCode(string code)
        => code.Trim().ToUpperInvariant() switch
        {
            "ACR" => "Acer",
            "ACI" => "Asus",
            "APP" => "Apple",
            "DEL" => "Dell",
            "EIZ" => "EIZO",
            "GSM" => "LG",
            "HPN" or "HWP" => "HP",
            "HIC" => "Hisense",
            "HSD" => "HannStar",
            "IBM" => "IBM",
            "LEN" => "Lenovo",
            "MAX" => "Maxdata",
            "MEI" => "Panasonic",
            "MSI" => "MSI",
            "NEC" => "NEC",
            "PHL" => "Philips",
            "SAM" => "Samsung",
            "SHP" => "Sharp",
            "SNY" => "Sony",
            "VSC" => "ViewSonic",
            "BNQ" => "BenQ",
            "AOC" => "AOC",
            _ => code.Trim()
        };
}
