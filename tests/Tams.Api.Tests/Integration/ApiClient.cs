using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Tams.Api.Tests.Integration;

/// <summary>Thin wrapper so tests read as "send this body, expect that status".</summary>
public sealed class ApiClient(HttpClient http)
{
    public Task<HttpResponseMessage> GetAsync(string url) => http.GetAsync(url);
    public Task<HttpResponseMessage> DeleteAsync(string url) => http.DeleteAsync(url);
    public Task<HttpResponseMessage> PostEmptyAsync(string url) => http.PostAsync(url, null);

    public Task<HttpResponseMessage> PostRawAsync(string url, string json) =>
        http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

    public Task<HttpResponseMessage> PutRawAsync(string url, string json) =>
        http.PutAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

    public Task<HttpResponseMessage> PostFileAsync(string url, string fileName, byte[] bytes, string contentType, string field = "file")
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return http.PostAsync(url, new MultipartFormDataContent { { file, field, fileName } });
    }

    /// <summary>Send with extra headers (kiosk routes need X-Station-Code).</summary>
    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? json = null, params (string Name, string Value)[] headers)
    {
        var req = new HttpRequestMessage(method, url);
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        foreach (var (n, v) in headers) req.Headers.Add(n, v);
        return http.SendAsync(req);
    }
}

public static class Http
{
    public static async Task<JsonElement> Json(this HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Assert the status; the body goes in the failure message so a red test explains itself.</summary>
    public static async Task<HttpResponseMessage> Expect(this Task<HttpResponseMessage> task, int status, string? errorCode = null)
    {
        var r = await task;
        var body = await r.Content.ReadAsStringAsync();
        Assert.True((int)r.StatusCode == status,
            $"{r.RequestMessage?.Method} {r.RequestMessage?.RequestUri?.PathAndQuery}: expected {status} but got {(int)r.StatusCode}. Body: {body}");
        if (errorCode is not null) Assert.Contains(errorCode, body);
        return r;
    }

    /// <summary>Assert the status and return the parsed body.</summary>
    public static async Task<JsonElement> Ok(this Task<HttpResponseMessage> task, int status = 200, string? errorCode = null) =>
        await (await task.Expect(status, errorCode)).Json();

    public static Guid Id(this JsonElement e) => e.GetProperty("id").GetGuid();
}
