using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace QiDock.Services.Widgets;

/// <param name="Kind">"mouse", "keyboard", "headphones" or "other", guessed from the name.</param>
public sealed record DeviceBattery(string Name, int Percent, string Kind);

/// <summary>
/// The battery level Windows keeps for connected Bluetooth devices (mice, keyboards, headphones), the same number the
/// Bluetooth settings page shows. Read on request only: two short device queries, no background work.
/// </summary>
public static partial class BluetoothBatteries
{
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string InstanceKey = "System.Devices.DeviceInstanceId";
    private const string LastConnectedKey = "{2BD67D8B-8BEB-48D5-87E0-6CDA3428040A} 11";

    /// <summary>A Bluetooth mouse drops its link while it sleeps; one used in the last hours still counts.</summary>
    private static readonly TimeSpan Recent = TimeSpan.FromHours(12);
    private const string BluetoothClass = "System.Devices.ClassGuid:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\"";

    public static async Task<IReadOnlyList<DeviceBattery>> ReadAsync()
    {
#if DEBUG
        if (Promo.On) return Promo.Batteries;
#endif
        // Windows also keeps the last level of devices paired long ago; only those connected now (or lately) are shown.
        var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selector in new[]
                 {
                     BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
                     BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected)
                 })
        {
            foreach (var device in await DeviceInformation.FindAllAsync(selector))
                if (AddressOfId(device.Id) is { } address) connected.Add(address);
        }
        var found = new Dictionary<string, DeviceBattery>(StringComparer.OrdinalIgnoreCase);
        var nodes = await DeviceInformation.FindAllAsync(BluetoothClass, [BatteryKey, InstanceKey, LastConnectedKey], DeviceInformationKind.Device);
        foreach (var node in nodes)
        {
            if (!node.Properties.TryGetValue(BatteryKey, out var level) || level is not byte percent) continue;
            if (!node.Properties.TryGetValue(InstanceKey, out var instance) || instance is not string id) continue;
            if (AddressOfInstance(id) is not { } address || found.ContainsKey(address)) continue;
            var recent = node.Properties.TryGetValue(LastConnectedKey, out var last) && last is DateTimeOffset when && DateTimeOffset.Now - when < Recent;
            if (!connected.Contains(address) && !recent) continue;
            var name = string.IsNullOrWhiteSpace(node.Name) ? "Bluetooth" : node.Name.Trim();
            found[address] = new DeviceBattery(name, Math.Clamp((int)percent, 0, 100), KindOf(name, id));
        }
        return found.Values.OrderBy(d => d.Kind switch { "mouse" => 0, "keyboard" => 1, "headphones" => 2, _ => 3 }).ThenBy(d => d.Name).ToList();
    }

    /// <summary>"BluetoothLE#BluetoothLE00:11:…-ed:de:c1:01:98:3d" → "EDDEC101983D".</summary>
    private static string? AddressOfId(string id)
    {
        var dash = id.LastIndexOf('-');
        if (dash < 0) return null;
        var address = id[(dash + 1)..].Replace(":", "");
        return address.Length == 12 ? address.ToUpperInvariant() : null;
    }

    /// <summary>"BTHLE\DEV_EDDEC101983D\…" or "BTHENUM\{…}_…\7&amp;…&amp;0&amp;E65D27240913_C00000000" → the address.</summary>
    private static string? AddressOfInstance(string instance) =>
        AddressPattern().Match(instance) is { Success: true } match ? match.Groups[1].Value.ToUpperInvariant() : null;

    [GeneratedRegex(@"(?:DEV_|&)([0-9A-Fa-f]{12})(?:\\|_|$)")]
    private static partial Regex AddressPattern();

    private static string KindOf(string name, string instance)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("mouse") || lower.Contains("fare") || lower.Contains("mx master") || lower.Contains("mx anywhere")) return "mouse";
        if (lower.Contains("key") || lower.Contains("klavye")) return "keyboard";
        if (lower.Contains("bud") || lower.Contains("pod") || lower.Contains("phone") || lower.Contains("kulaklık") || lower.Contains("headset")
            || lower.Contains("wh-") || lower.Contains("wf-") || lower.Contains("jbl") || lower.Contains("beats")) return "headphones";
        // Classic Bluetooth devices that report a level are almost always headsets (the hands-free profile carries it).
        return instance.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase) ? "headphones" : "other";
    }
}
