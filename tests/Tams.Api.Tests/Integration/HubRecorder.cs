using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Tams.Api.Live;

namespace Tams.Api.Tests.Integration;

/// <summary>
/// Stands in for the SignalR hub in the test API and records every group send, so a test can ask who was told what.
/// No test connects a real hub client; this is the place to look once one needs to.
/// </summary>
public sealed class HubRecorder : IHubContext<LiveHub>
{
    readonly ConcurrentQueue<(string Group, string Method, string Json)> sent = [];

    public void Clear() => sent.Clear();

    /// <summary>The groups an event reached, for events whose payload mentions the given text (an id, a loan number).</summary>
    public IReadOnlyList<string> GroupsReceiving(string method, string payloadContains) =>
        sent.Where(s => s.Method == method && s.Json.Contains(payloadContains, StringComparison.OrdinalIgnoreCase)).Select(s => s.Group).ToList();

    public IHubClients Clients => new C(this);
    public IGroupManager Groups => throw new NotSupportedException();

    sealed class C(HubRecorder o) : IHubClients
    {
        public IClientProxy All => throw new NotSupportedException();
        public IClientProxy AllExcept(IReadOnlyList<string> c) => throw new NotSupportedException();
        public IClientProxy Client(string c) => throw new NotSupportedException();
        public IClientProxy Clients(IReadOnlyList<string> c) => throw new NotSupportedException();
        public IClientProxy Group(string g) => new P(o, g);
        public IClientProxy GroupExcept(string g, IReadOnlyList<string> c) => throw new NotSupportedException();
        public IClientProxy Groups(IReadOnlyList<string> g) => throw new NotSupportedException();
        public IClientProxy User(string u) => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> u) => throw new NotSupportedException();
    }

    sealed class P(HubRecorder o, string group) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
        {
            o.sent.Enqueue((group, method, JsonSerializer.Serialize(args.FirstOrDefault(), JsonSerializerOptions.Web)));
            return Task.CompletedTask;
        }
    }
}
