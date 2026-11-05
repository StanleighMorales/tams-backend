using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Data;

namespace Tams.Api.Settings;

/// <summary>Typed read access to effective settings (stored override, else code default).</summary>
public interface ISettings
{
    Task<int> GetIntAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, StoredSetting>> GetOverridesAsync(CancellationToken ct = default);
    void Invalidate();
}

public sealed record StoredSetting(int Value, Guid UpdatedByUserId, DateTimeOffset UpdatedAt);

/// <summary>
/// Caches all overrides for a few seconds. Invalidated on write in this process; a second
/// API instance would see changes after the TTL (single instance today).
/// </summary>
public sealed class SettingsService(IServiceScopeFactory scopes, TimeProvider clock) : ISettings
{
    static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);
    readonly SemaphoreSlim gate = new(1, 1);
    volatile IReadOnlyDictionary<string, StoredSetting>? cache;
    DateTimeOffset expires;

    public async Task<int> GetIntAsync(string key, CancellationToken ct = default)
    {
        if (!SettingDefinitions.ByKey.TryGetValue(key, out var def))
            throw new KeyNotFoundException($"Unknown setting '{key}'.");
        var overrides = await GetOverridesAsync(ct);
        return overrides.TryGetValue(key, out var s) ? s.Value : def.Default;
    }

    public async Task<IReadOnlyDictionary<string, StoredSetting>> GetOverridesAsync(CancellationToken ct = default)
    {
        var snapshot = cache;
        if (snapshot is not null && clock.GetUtcNow() < expires) return snapshot;

        await gate.WaitAsync(ct);
        try
        {
            snapshot = cache;
            if (snapshot is not null && clock.GetUtcNow() < expires) return snapshot;

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
            var rows = await db.SystemSettings.AsNoTracking().ToListAsync(ct);
            var map = new Dictionary<string, StoredSetting>();
            foreach (var r in rows)
            {
                // Retired keys or values that no longer validate fall back to the default.
                if (SettingDefinitions.ByKey.TryGetValue(r.Key, out var def)
                    && JsonSerializer.Deserialize<JsonElement>(r.Value) is { ValueKind: JsonValueKind.Number } n
                    && n.TryGetInt32(out var v) && def.Validate(v) is null)
                    map[r.Key] = new StoredSetting(v, r.UpdatedByUserId, r.UpdatedAt);
            }
            expires = clock.GetUtcNow() + Ttl;
            cache = map;
            return map;
        }
        finally { gate.Release(); }
    }

    public void Invalidate() => cache = null;
}
