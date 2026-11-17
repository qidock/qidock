using System.Runtime.InteropServices;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;

namespace QiDock.Services.Widgets;

/// <summary>
/// The Wi-Fi networks in range and joining one of them, through the Windows Wi-Fi adapter. Newer Windows versions
/// only let programs list networks while location access is allowed for desktop apps; when it is not, the caller
/// falls back to Windows' own network list.
/// </summary>
public sealed class WifiNetworks
{
    public enum Scan { Ok, Denied, NoAdapter, Failed }

    public enum Join { Connected, NeedsPassword, WrongPassword, NotInRange, Unsupported, Failed }

    public sealed record Network(string Ssid, int Bars, bool Secured, bool Connected, WiFiAvailableNetwork? Source);

    private WiFiAdapter? _adapter;

    public async Task<(Scan Result, IReadOnlyList<Network> Networks)> ScanAsync()
    {
        try
        {
            if (await WiFiAdapter.RequestAccessAsync() != WiFiAccessStatus.Allowed) return (Scan.Denied, []);
            _adapter ??= (await WiFiAdapter.FindAllAdaptersAsync()).FirstOrDefault();
            if (_adapter is null) return (Scan.NoAdapter, []);
            try { await _adapter.ScanAsync(); }
            catch (UnauthorizedAccessException) { return (Scan.Denied, []); }
            var connected = await ConnectedSsidAsync();
            var networks = _adapter.NetworkReport.AvailableNetworks
                .Where(n => !string.IsNullOrWhiteSpace(n.Ssid))
                .GroupBy(n => n.Ssid)
                .Select(same => same.OrderByDescending(n => n.NetworkRssiInDecibelMilliwatts).First())
                .Select(n => new Network(n.Ssid, Math.Clamp((int)n.SignalBars, 0, 4), IsSecured(n), n.Ssid == connected, n))
                .OrderByDescending(n => n.Connected)
                .ThenByDescending(n => n.Bars)
                .ThenBy(n => n.Ssid, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            return (Scan.Ok, networks);
        }
        catch (Exception ex)
        {
            // WinRT can fail in many ways here (cancelled, timed out, adapter gone); the list must not stay stuck.
            return (ex is UnauthorizedAccessException ? Scan.Denied : Scan.Failed, []);
        }
    }

    /// <summary>
    /// Joins <paramref name="network"/>. Without a password Windows uses the saved one (or none for an open network);
    /// a secured network it has never joined answers <see cref="Join.NeedsPassword"/>.
    /// </summary>
    public async Task<Join> ConnectAsync(Network network, string? password)
    {
        if (_adapter is null || network.Source is null) return Join.Failed;
        try
        {
            var result = password is null
                ? await _adapter.ConnectAsync(network.Source, WiFiReconnectionKind.Automatic)
                : await _adapter.ConnectAsync(network.Source, WiFiReconnectionKind.Automatic, new PasswordCredential { Password = password });
            return result.ConnectionStatus switch
            {
                WiFiConnectionStatus.Success => Join.Connected,
                WiFiConnectionStatus.InvalidCredential => password is null && network.Secured ? Join.NeedsPassword : Join.WrongPassword,
                WiFiConnectionStatus.NetworkNotAvailable => Join.NotInRange,
                WiFiConnectionStatus.UnsupportedAuthenticationProtocol => Join.Unsupported,
                _ => password is null && network.Secured ? Join.NeedsPassword : Join.Failed
            };
        }
        catch (Exception)
        {
            return Join.Failed; // whatever went wrong, the network list must stay usable
        }
    }

    public void Disconnect()
    {
        try { _adapter?.Disconnect(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
    }

    private async Task<string?> ConnectedSsidAsync()
    {
        try
        {
            var profile = _adapter is null ? null : await _adapter.NetworkAdapter.GetConnectedProfileAsync();
            return profile is { IsWlanConnectionProfile: true } ? profile.WlanConnectionProfileDetails?.GetConnectedSsid() : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return RadioControls.IsOnWifi() ? RadioControls.CurrentNetworkName() : null;
        }
    }

    private static bool IsSecured(WiFiAvailableNetwork network) =>
        network.SecuritySettings?.NetworkAuthenticationType is not (null or NetworkAuthenticationType.Open80211 or NetworkAuthenticationType.None);
}
