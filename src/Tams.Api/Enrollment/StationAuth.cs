using System.Security.Cryptography;
using System.Text;
using Tams.Api.Data;
using Tams.Api.Data.Entities;

namespace Tams.Api.Enrollment;

/// <summary>
/// The one place that decides whether a kiosk request really comes from the station it names.
/// PRD 6.2/10.2: kiosks hold no credentials; the defence is station_code plus, optionally, an IP
/// allowlist. Room stations additionally carry a per-station key (TAMS-127, see IsRoomStationAuthenticated).
///
/// 1. The request must carry <c>X-Station-Code</c> equal to the session's station_code (always).
/// 2. With <c>Kiosk:EnforceIp=true</c> the caller's IP must also be stations.ip_address or one of the
///    comma-separated <c>Kiosk:AllowedIps</c>. Off by default: behind Caddy the address is only
///    trustworthy because Program.cs applies X-Forwarded-For, and kiosk addresses may be DHCP.
/// </summary>
public static class StationAuth
{
    public const string StationCodeHeader = "X-Station-Code";

    public static bool IsStationAuthenticated(HttpContext http, Station station)
    {
        if (station.Type != StationType.CounterKiosk) return false;

        var presented = http.Request.Headers[StationCodeHeader].ToString().Trim();
        if (presented.Length == 0
            || !string.Equals(presented, station.StationCode, StringComparison.OrdinalIgnoreCase))
            return false;

        return IsDeviceAllowed(http, station);
    }

    public const string StationKeyHeader = "X-Station-Key";

    /// <summary>A new random secret for a room station (64 hex characters). Shown once; only its hash is stored.</summary>
    public static string NewKey() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public static string HashKey(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>
    /// Room-station scans and heartbeats (TAMS-127, G2): <see cref="IsDeviceAllowed"/> plus the per-station secret in
    /// <c>X-Station-Key</c>, compared as hashes in constant time. A wrong key is always refused. A missing key, or a
    /// station that has no key yet, is accepted only while <c>station_key_required</c> is 0 (the grace period that
    /// lets existing stations keep working until their keys are loaded). Counter kiosks hold no credentials (FR-1.4).
    /// </summary>
    public static bool IsRoomStationAuthenticated(HttpContext http, Station station, bool keyRequired)
    {
        if (!IsDeviceAllowed(http, station)) return false;
        if (station.Type != StationType.RoomStation) return true;

        var presented = http.Request.Headers[StationKeyHeader].ToString().Trim();
        if (presented.Length == 0) return !keyRequired;
        if (station.KeyHash is null) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(HashKey(presented)), Encoding.ASCII.GetBytes(station.KeyHash));
    }

    /// <summary>
    /// Room-station scans and heartbeats name the station in the path (/stations/{code}/...), so only rule 2
    /// applies on top of the station being live and active.
    /// </summary>
    public static bool IsDeviceAllowed(HttpContext http, Station station)
    {
        if (station.DeletedAt is not null || station.Status != RecordStatus.Active) return false;

        var config = http.RequestServices.GetRequiredService<IConfiguration>();
        if (!config.GetValue<bool>("Kiosk:EnforceIp")) return true;

        var remote = http.Connection.RemoteIpAddress;
        var remoteText = remote is null ? null : (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString();
        if (remoteText is null) return false;

        var allowed = (config["Kiosk:AllowedIps"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(station.IpAddress ?? "")
            .Where(ip => ip.Length > 0);
        return allowed.Any(ip => string.Equals(ip, remoteText, StringComparison.OrdinalIgnoreCase));
    }
}
