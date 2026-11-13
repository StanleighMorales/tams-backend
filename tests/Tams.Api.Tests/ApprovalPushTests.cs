using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Tams.Api.Live;
using Tams.Api.Push;

namespace Tams.Api.Tests;

public class ApprovalPushTests
{
    static readonly Guid Session = Guid.NewGuid(), User = Guid.NewGuid();

    // Same shape as ApprovalEndpoints.RequestPayloadAsync.
    static object Request() => new
    {
        sessionId = Session,
        student = new { id = Guid.NewGuid(), studentNo = "2021-0001", fullName = "Ana Cruz", sectionCode = "BSIT-3A" },
        asset = new { id = Guid.NewGuid(), assetCode = "LAP-001", name = "Laptop" },
        photoUrl = $"/api/approvals/{Session}/photo",
        roomOptions = new[] { new { id = Guid.NewGuid(), code = "R101", name = "Room 101" } },
        defaultRoomId = (Guid?)null,
        classLengthOptions = new[] { 90, 120, 150, 180 },
        defaultClassLengthMinutes = 120,
        requestedAt = DateTimeOffset.Parse("2026-11-11T08:00:00Z"),
        expiresAt = DateTimeOffset.Parse("2026-11-11T08:02:00Z"),
    };

    [Fact]
    public void Build_carries_the_fr_2_1_fields()
    {
        var p = ApprovalPush.Build(User, Request());
        Assert.Equal(Session, p.SessionId);
        Assert.Equal("Ana Cruz", p.StudentName);
        Assert.Equal($"/api/approvals/{Session}/photo", p.PhotoUrl);
        Assert.Equal("LAP-001", p.AssetCode);
        Assert.Equal([1.5, 2, 2.5, 3], p.ClassLengthHours);
        Assert.Equal([90, 120, 150, 180], p.ClassLengthMinutes);
        Assert.Equal(120, p.DeadlineSeconds);
        Assert.Equal(1, p.RoomOptions.GetArrayLength());

        var json = JsonSerializer.SerializeToElement(p.ToMessage(), JsonSerializerOptions.Web);
        Assert.Equal("approval.requested", json.GetProperty("type").GetString());
        Assert.Equal("Ana Cruz", json.GetProperty("studentName").GetString());
        Assert.Equal(120, json.GetProperty("deadlineSeconds").GetInt32());
    }

    [Fact]
    public void Fcm_request_uses_string_data_and_the_deadline_as_ttl()
    {
        var req = Tams.Api.Push.FcmApprovalPushSender.BuildRequest(ApprovalPush.Build(User, Request()), "tok");
        var m = req["message"]!;
        Assert.Equal("tok", m["token"]!.GetValue<string>());
        Assert.Equal("approval.requested", m["data"]!["type"]!.GetValue<string>());
        Assert.Equal(Session.ToString(), m["data"]!["sessionId"]!.GetValue<string>());
        Assert.Equal("120s", m["android"]!["ttl"]!.GetValue<string>());
        Assert.Contains("Ana Cruz", JsonDocument.Parse(m["data"]!["payload"]!.GetValue<string>()).RootElement.GetProperty("studentName").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}", true)]
    [InlineData(HttpStatusCode.NotFound, "not json", true)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"status":"NOT_FOUND","details":[{"errorCode":"UNREGISTERED"}]}}""", true)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"status":"INVALID_ARGUMENT","message":"The registration token is not a valid FCM registration token"}}""", true)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"status":"INVALID_ARGUMENT","message":"Invalid JSON payload"}}""", false)]
    [InlineData(HttpStatusCode.InternalServerError, "{}", false)]
    public void Invalid_token_detection(HttpStatusCode status, string body, bool expected) =>
        Assert.Equal(expected, FcmApprovalPushSender.IsInvalidToken(status, body));

    [Fact]
    public async Task Fake_sender_emits_to_the_instructors_user_group()
    {
        var hub = new RecordingHub();
        var sender = new FakeApprovalPushSender(hub, NullLogger<FakeApprovalPushSender>.Instance);
        var invalid = await sender.SendAsync(ApprovalPush.Build(User, Request()), [], default);

        Assert.Empty(invalid);
        Assert.Equal(LiveHub.UserGroup(User), hub.Group);
        Assert.Equal("approval.push", hub.Method);
        var json = JsonSerializer.SerializeToElement(hub.Arg, JsonSerializerOptions.Web);
        Assert.Equal(Session, json.GetProperty("sessionId").GetGuid());
    }

    sealed class RecordingHub : IHubContext<LiveHub>
    {
        public string? Group, Method;
        public object? Arg;
        public IHubClients Clients => new C(this);
        public IGroupManager Groups => throw new NotSupportedException();

        sealed class C(RecordingHub o) : IHubClients
        {
            public IClientProxy All => throw new NotSupportedException();
            public IClientProxy AllExcept(IReadOnlyList<string> c) => throw new NotSupportedException();
            public IClientProxy Client(string c) => throw new NotSupportedException();
            public IClientProxy Clients(IReadOnlyList<string> c) => throw new NotSupportedException();
            public IClientProxy Group(string g) { o.Group = g; return new P(o); }
            public IClientProxy GroupExcept(string g, IReadOnlyList<string> c) => throw new NotSupportedException();
            public IClientProxy Groups(IReadOnlyList<string> g) => throw new NotSupportedException();
            public IClientProxy User(string u) => throw new NotSupportedException();
            public IClientProxy Users(IReadOnlyList<string> u) => throw new NotSupportedException();
        }

        sealed class P(RecordingHub o) : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
            {
                (o.Method, o.Arg) = (method, args[0]);
                return Task.CompletedTask;
            }
        }
    }
}
