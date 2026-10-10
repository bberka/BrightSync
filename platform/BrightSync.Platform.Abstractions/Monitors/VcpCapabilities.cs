using System.Globalization;
using System.Text.RegularExpressions;
using Serilog;

namespace BrightSync.Core.Monitors;

/// <summary>Reads one VCP feature from a monitor. Returns false when the monitor did not answer.</summary>
public delegate bool VcpReadFunc(byte vcpCode, out uint currentValue, out uint maxValue);

/// <summary>Parses MCCS capability strings and probes the optional hardware controls of a monitor.</summary>
public static partial class VcpCapabilities
{
    /// <summary>Parses the <c>vcp(...)</c> block of a capabilities string into feature code to allowed values.</summary>
    public static Dictionary<byte, List<uint>> Parse(string capString)
    {
        var result = new Dictionary<byte, List<uint>>();
        try
        {
            var vcpIndex = capString.IndexOf("vcp", StringComparison.OrdinalIgnoreCase);
            if (vcpIndex < 0) return result;

            var openParen = capString.IndexOf('(', vcpIndex);
            if (openParen < 0) return result;

            var parenCount = 1;
            var i = openParen + 1;
            var vcpBlock = "";
            for (; i < capString.Length; i++)
            {
                if (capString[i] == '(') parenCount++;
                else if (capString[i] == ')')
                {
                    parenCount--;
                    if (parenCount == 0)
                    {
                        vcpBlock = capString.Substring(openParen + 1, i - openParen - 1);
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(vcpBlock)) return result;

            var index = 0;
            while (index < vcpBlock.Length)
            {
                while (index < vcpBlock.Length && char.IsWhiteSpace(vcpBlock[index]))
                    index++;

                if (index >= vcpBlock.Length) break;

                var tokenStart = index;
                while (index < vcpBlock.Length && char.IsLetterOrDigit(vcpBlock[index]))
                    index++;

                if (index == tokenStart)
                {
                    index++;
                    continue;
                }

                var token = vcpBlock.Substring(tokenStart, index - tokenStart);
                if (byte.TryParse(token, NumberStyles.HexNumber, null, out var vcpCode))
                {
                    var supportedValues = new List<uint>();
                    if (index < vcpBlock.Length && vcpBlock[index] == '(')
                    {
                        var closeIndex = vcpBlock.IndexOf(')', index);
                        if (closeIndex > index)
                        {
                            var valuesStr = vcpBlock.Substring(index + 1, closeIndex - index - 1);
                            var valTokens = valuesStr.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var valToken in valTokens)
                            {
                                if (uint.TryParse(valToken, NumberStyles.HexNumber, null, out var val))
                                {
                                    supportedValues.Add(val);
                                }
                            }

                            index = closeIndex + 1;
                        }
                    }

                    result[vcpCode] = supportedValues;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to parse capabilities string");
        }

        return result;
    }

    /// <summary>True when the capabilities string lists brightness (0x10) or the legacy 0x02 code.</summary>
    public static bool IndicatesBrightnessSupport(string capabilities)
    {
        var normalized = capabilities.Replace(" ", string.Empty, StringComparison.Ordinal);
        return BrightnessCapabilityRegex().IsMatch(normalized);
    }

    [GeneratedRegex(@"vcp\([^)]*(10|02)", RegexOptions.IgnoreCase)]
    private static partial Regex BrightnessCapabilityRegex();

    /// <summary>
    /// Fills the optional control fields of <paramref name="monitor"/> (contrast, volume, color preset,
    /// RGB gains, input source, sharpness, saturation, gamma, power) using <paramref name="read"/>.
    /// </summary>
    public static void ProbeAdvancedControls(
        DdcMonitor monitor,
        string? capabilitiesString,
        VcpReadFunc read,
        CancellationToken cancellationToken)
    {
        if (monitor.IsInternal)
            return;

        Log.Debug("Probing advanced capabilities for monitor: {Monitor}", monitor.FriendlyName);

        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(capabilitiesString))
        {
            Log.Debug("Capabilities string for {Monitor}: {CapStr}", monitor.FriendlyName, capabilitiesString);
            var parsedCaps = Parse(capabilitiesString);

            if (parsedCaps.TryGetValue(VcpCodes.ColorPreset, out var presets))
                monitor.SupportedPresets = presets;

            if (parsedCaps.TryGetValue(VcpCodes.InputSource, out var inputs))
                monitor.SupportedInputs = inputs;
        }

        if (read(VcpCodes.Contrast, out var contrastVal, out var contrastMax))
        {
            monitor.SupportsContrast = true;
            monitor.CurrentContrast = (int)contrastVal;
            monitor.MaxContrast = (int)contrastMax;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (read(VcpCodes.Volume, out var volVal, out var volMax))
        {
            monitor.SupportsVolume = true;
            monitor.CurrentVolume = (int)volVal;
            monitor.MaxVolume = (int)volMax;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (read(VcpCodes.ColorPreset, out var presetVal, out _))
        {
            monitor.SupportsColorPreset = true;
            monitor.CurrentColorPreset = (int)presetVal;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (read(VcpCodes.RedGain, out var redVal, out var rgbMax) &&
            read(VcpCodes.GreenGain, out var greenVal, out _) &&
            read(VcpCodes.BlueGain, out var blueVal, out _))
        {
            monitor.SupportsRgbGains = true;
            monitor.CurrentRedGain = (int)redVal;
            monitor.CurrentGreenGain = (int)greenVal;
            monitor.CurrentBlueGain = (int)blueVal;
            monitor.MaxRgbGain = (int)rgbMax;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (read(VcpCodes.InputSource, out var inputVal, out _))
        {
            monitor.SupportsInputSource = true;
            monitor.CurrentInputSource = (int)inputVal;
        }

        monitor.RawCapabilitiesString = capabilitiesString ?? string.Empty;

        cancellationToken.ThrowIfCancellationRequested();
        if (read(VcpCodes.Sharpness, out var sharpnessVal, out var sharpnessMax))
        {
            monitor.SupportsSharpness = true;
            monitor.CurrentSharpness = (int)sharpnessVal;
            monitor.MaxSharpness = (int)sharpnessMax;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (read(VcpCodes.Saturation, out var saturationVal, out var saturationMax))
        {
            monitor.SupportsSaturation = true;
            monitor.CurrentSaturation = (int)saturationVal;
            monitor.MaxSaturation = (int)saturationMax;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (read(VcpCodes.Gamma, out var gammaVal, out _))
        {
            monitor.SupportsGamma = true;
            monitor.CurrentGamma = (int)gammaVal;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (read(VcpCodes.PowerControl, out var powerVal, out _))
        {
            monitor.SupportsPowerControl = true;
            monitor.CurrentPowerState = (int)powerVal;
        }
    }
}
