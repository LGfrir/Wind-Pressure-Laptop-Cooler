using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartFan.App.Services;

/// <summary>Reads configured Windows PnP nodes without opening a GPU, loading NVAPI, or changing device state.</summary>
public sealed class NvidiaPnpIdentityReader : INvidiaLhmIdentityReader
{
    private static readonly Regex PciId = new(@"^PCI\\VEN_(?<vendor>[0-9A-F]{4})&DEV_(?<device>[0-9A-F]{4})&SUBSYS_(?<subsystem>[0-9A-F]{8})(?:&[^\\]+)?\\[^\\]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex PciLocation = new(@"^PCI (?:(?:segment (?<domain>[0-9]+) )?bus (?<bus>[0-9]+), device (?<device>[0-9]+), function (?<function>[0-9]+))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    public IReadOnlyList<NvidiaLhmPciAdapter> ReadAdapters(IReadOnlyDictionary<string, string> deviceIds)
    {
        var result = new List<NvidiaLhmPciAdapter>();
        if (!OperatingSystem.IsWindows()) return result;
        foreach (var (hardwareId, deviceId) in deviceIds)
        {
            if (!hardwareId.StartsWith("/gpu-nvidia/", StringComparison.Ordinal) || deviceId.Length > 1024) continue;
            try
            {
                // NORMAL (0) only locates a present configured instance; do not locate phantom nodes.
                if (CM_Locate_DevNodeW(out var node, deviceId, 0) != 0) continue;
                if (!ReadDword(node, 0x16, out var bus) || !ReadDword(node, 0x1D, out var address) ||
                    !TryParseNumericIdentity(deviceId, bus, address, out var numeric)) continue;
                var key = new DevPropKey { FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = 15 };
                var buffer = new byte[2048]; var size = (uint)buffer.Length;
                var location = CM_Get_DevNode_PropertyW(node, ref key, out var type, buffer, ref size, 0) == 0 &&
                    type == 0x12 && size is >= 2 and <= 2048 && size % 2 == 0
                    ? Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0') : null;
                if (TryParseIdentity(deviceId, location, out var identity))
                {
                    if (identity!.Bus == numeric!.Bus && identity.Device == numeric.Device && identity.Function == numeric.Function)
                        result.Add(new(hardwareId, identity, DomainKnown: location!.Contains(" segment ", StringComparison.OrdinalIgnoreCase)));
                }
                else result.Add(new(hardwareId, numeric!, DomainKnown: false));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { /* A missing, localized or inaccessible property must leave this GPU independent. */ }
        }
        return result;
    }

    private static bool ReadDword(uint node, uint property, out uint value)
    {
        var size = 4u;
        return CM_Get_DevNode_Registry_PropertyW(node, property, out var type, out value, ref size, 0) == 0 && type == 4 && size == 4;
    }

    /// <summary>CM_DRP_ADDRESS: PCI device in the high word, function in the low word. Segment is not inferred here.</summary>
    public static bool TryParseNumericIdentity(string? deviceId, uint bus, uint address, out NvidiaPciIdentity? identity)
    {
        identity = null;
        if (bus > 255 || address == uint.MaxValue || (address >> 16) > 31 || (address & 0xFFFF) > 7) return false;
        // Reuse strict vendor/device/subsystem validation and bounds, with a temporary single-segment location.
        return TryParseIdentity(deviceId, FormattableString.Invariant($"PCI bus {bus}, device {address >> 16}, function {address & 0xFFFF}"), out identity);
    }

    /// <summary>Parse only documented complete PCI locations. Unknown/localized formats cannot establish identity.</summary>
    public static bool TryParseIdentity(string? deviceId, string? location, out NvidiaPciIdentity? identity)
    {
        identity = null;
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(location) || deviceId.Length > 1024 || location.Length > 1024) return false;
        var id = PciId.Match(deviceId.Trim()); var address = PciLocation.Match(location.Trim());
        if (!id.Success || !address.Success ||
            !uint.TryParse(id.Groups["vendor"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vendor) || vendor != 0x10DE ||
            !uint.TryParse(id.Groups["device"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var deviceIdPart) ||
            !uint.TryParse(id.Groups["subsystem"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var subsystem) ||
            !uint.TryParse(address.Groups["bus"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var bus) || bus > 255 ||
            !uint.TryParse(address.Groups["device"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var device) || device > 31 ||
            !uint.TryParse(address.Groups["function"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var function) || function > 7) return false;
        uint domain = 0;
        if (address.Groups["domain"].Success && (!uint.TryParse(address.Groups["domain"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out domain) || domain > 65535)) return false;
        identity = new(domain, bus, device, function, (deviceIdPart << 16) | vendor, subsystem);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey { public Guid FormatId; public uint PropertyId; }
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CM_Get_DevNode_PropertyW(uint node, ref DevPropKey key, out uint type, [Out] byte[] buffer, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CM_Get_DevNode_Registry_PropertyW(uint node, uint property, out uint type, out uint buffer, ref uint size, uint flags);
}
