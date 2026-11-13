using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tams.Api.Push;

/// <summary>
/// FCM HTTP v1 sender. Authenticates with a Google service account (JWT bearer grant, scope firebase.messaging)
/// and sends one message per token. Credentials come from Push:Fcm:CredentialsJson (the raw JSON, for env/secret use)
/// or Push:Fcm:CredentialsPath; nothing is committed.
/// </summary>
public sealed class FcmApprovalPushSender(HttpClient http, FcmCredentials creds, ILogger<FcmApprovalPushSender> log) : IApprovalPushSender
{
    string? accessToken;
    DateTimeOffset accessExpires;
    readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IReadOnlyList<string>> SendAsync(ApprovalPush push, IReadOnlyList<string> tokens, CancellationToken ct)
    {
        if (tokens.Count == 0) return [];
        var bearer = await AccessTokenAsync(ct);
        var results = await Task.WhenAll(tokens.Select(t => SendOneAsync(push, t, bearer, ct)));
        return tokens.Where((_, i) => results[i]).ToList();
    }

    /// <summary>True when FCM says the token is dead (UNREGISTERED / not found).</summary>
    async Task<bool> SendOneAsync(ApprovalPush push, string token, string bearer, CancellationToken ct)
    {
        var body = BuildRequest(push, token);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{creds.ProjectId}/messages:send")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        try
        {
            using var res = await http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode) return false;
            var text = await res.Content.ReadAsStringAsync(ct);
            log.LogWarning("FCM rejected a push for session {SessionId}: {Status} {Body}", push.SessionId, (int)res.StatusCode, text);
            return IsInvalidToken(res.StatusCode, text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning(ex, "FCM request failed for session {SessionId}.", push.SessionId);
            return false;
        }
    }

    public static JsonObject BuildRequest(ApprovalPush push, string token) => new()
    {
        ["message"] = new JsonObject
        {
            ["token"] = token,
            ["notification"] = new JsonObject { ["title"] = push.Title, ["body"] = push.Body },
            // FCM data values must be strings.
            ["data"] = new JsonObject
            {
                ["type"] = ApprovalPush.Type,
                ["sessionId"] = push.SessionId.ToString(),
                ["payload"] = JsonSerializer.Serialize(push.ToMessage(), JsonSerializerOptions.Web),
            },
            // The request is useless after the approval deadline, so do not queue it past that.
            ["android"] = new JsonObject { ["priority"] = "HIGH", ["ttl"] = $"{Math.Max(push.DeadlineSeconds, 1)}s" },
            ["apns"] = new JsonObject
            {
                ["headers"] = new JsonObject
                {
                    ["apns-priority"] = "10",
                    ["apns-expiration"] = push.ExpiresAt.ToUnixTimeSeconds().ToString(),
                },
            },
        },
    };

    public static bool IsInvalidToken(HttpStatusCode status, string body)
    {
        if (status == HttpStatusCode.NotFound) return true;
        try
        {
            var err = JsonNode.Parse(body)?["error"];
            if (err?["details"] is JsonArray details && details.Any(d => d?["errorCode"]?.GetValue<string>() == "UNREGISTERED")) return true;
            return status == HttpStatusCode.BadRequest && err?["status"]?.GetValue<string>() == "INVALID_ARGUMENT"
                && (err["message"]?.GetValue<string>() ?? "").Contains("registration token", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }

    async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (accessToken is not null && accessExpires > DateTimeOffset.UtcNow.AddMinutes(1)) return accessToken;
            var now = DateTimeOffset.UtcNow;
            var assertion = SignJwt(creds, now);
            using var res = await http.PostAsync(creds.TokenUri, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = assertion,
            }), ct);
            res.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))!;
            accessToken = json["access_token"]!.GetValue<string>();
            accessExpires = now.AddSeconds(json["expires_in"]?.GetValue<int>() ?? 3000);
            return accessToken;
        }
        finally { gate.Release(); }
    }

    static string SignJwt(FcmCredentials c, DateTimeOffset now)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = c.ClientEmail, scope = "https://www.googleapis.com/auth/firebase.messaging", aud = c.TokenUri,
            iat = now.ToUnixTimeSeconds(), exp = now.AddHours(1).ToUnixTimeSeconds(),
        }));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(c.PrivateKey);
        var sig = rsa.SignData(Encoding.UTF8.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{claims}.{B64(sig)}";
    }
}

public sealed record FcmCredentials(string ProjectId, string ClientEmail, string PrivateKey, string TokenUri)
{
    /// <summary>Null when nothing is configured or the JSON is unusable (push then stays disabled).</summary>
    public static FcmCredentials? Load(IConfiguration cfg, ILogger log)
    {
        var json = cfg["Push:Fcm:CredentialsJson"];
        var path = cfg["Push:Fcm:CredentialsPath"];
        try
        {
            if (string.IsNullOrWhiteSpace(json) && !string.IsNullOrWhiteSpace(path)) json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;
            var n = JsonNode.Parse(json)!;
            var project = cfg["Push:Fcm:ProjectId"] ?? n["project_id"]?.GetValue<string>();
            var email = n["client_email"]?.GetValue<string>();
            var key = n["private_key"]?.GetValue<string>();
            if (project is null || email is null || key is null) throw new InvalidOperationException("project_id, client_email or private_key missing");
            return new FcmCredentials(project, email, key, n["token_uri"]?.GetValue<string>() ?? "https://oauth2.googleapis.com/token");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or NullReferenceException)
        {
            log.LogError(ex, "Push:Fcm credentials could not be read; FCM push is disabled.");
            return null;
        }
    }
}
