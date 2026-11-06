using Tams.Api.Data;
using Tams.Api.Data.Entities;

namespace Tams.Api.Enrollment;

/// <summary>
/// The one place that decides whether a kiosk request really comes from the station it names.
/// PRD 6.1/10: kiosks hold no credentials; the defence is station_code plus an IP allowlist
/// (per-device keys are listed as future work). No shared station-auth component exists yet
/// (TAMS-32 owns stations), so this hook is deliberately small: swap the body when device
/// keys or a station auth scheme land and every kiosk route here follows.
/// </summary>
public static class StationAuth
{
    public static bool IsStationAuthenticated(HttpContext http, Station station)
    {
        if (station.DeletedAt is not null || station.Status != RecordStatus.Active) return false;
        if (station.Type != StationType.CounterKiosk) return false;

        var config = http.RequestServices.GetRequiredService<IConfiguration>();
        // Escape hatch for local development with a simulator that has no fixed address.
        if (config.GetValue<bool>("Kiosk:SkipIpCheck")) return true;

        // No registered address means nothing to compare against: refuse rather than trust.
        if (string.IsNullOrWhiteSpace(station.IpAddress)) return false;
        var remote = http.Connection.RemoteIpAddress;
        var remoteText = remote is null ? null : (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString();
        return string.Equals(remoteText, station.IpAddress, StringComparison.OrdinalIgnoreCase);
    }
}
