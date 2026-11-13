using System.Text.Json;

namespace Tams.Api.Push;

/// <summary>What a phone needs to show the approval request (FR-2.1). Names follow PRD 6.4 approval.requested.</summary>
public sealed record ApprovalPush(
    Guid UserId, Guid SessionId, string StudentName, string PhotoUrl, string AssetCode, string AssetName,
    JsonElement RoomOptions, double[] ClassLengthHours, int[] ClassLengthMinutes, DateTimeOffset ExpiresAt,
    int DeadlineSeconds, JsonElement Request)
{
    public const string Type = "approval.requested";

    /// <summary>Builds the push from the approval.requested payload that the SignalR event and GET /approvals/pending already use.</summary>
    public static ApprovalPush Build(Guid userId, object requestPayload)
    {
        var p = JsonSerializer.SerializeToElement(requestPayload, JsonSerializerOptions.Web);
        var minutes = p.GetProperty("classLengthOptions").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var expires = p.GetProperty("expiresAt").GetDateTimeOffset();
        var requested = p.GetProperty("requestedAt").GetDateTimeOffset();
        return new ApprovalPush(
            userId, p.GetProperty("sessionId").GetGuid(),
            p.GetProperty("student").GetProperty("fullName").GetString() ?? "",
            p.GetProperty("photoUrl").GetString() ?? "",
            p.GetProperty("asset").GetProperty("assetCode").GetString() ?? "",
            p.GetProperty("asset").GetProperty("name").GetString() ?? "",
            p.GetProperty("roomOptions").Clone(), minutes.Select(m => m / 60.0).ToArray(), minutes,
            expires, (int)Math.Round((expires - requested).TotalSeconds), p.Clone());
    }

    public string Title => "Approval request";
    public string Body => $"{StudentName} wants to borrow {AssetName} ({AssetCode})";

    /// <summary>The full message as JSON: the shape the Development fake emits and the FCM "payload" data field carries.</summary>
    public object ToMessage() => new
    {
        type = Type, sessionId = SessionId, studentName = StudentName, photoUrl = PhotoUrl,
        asset = new { assetCode = AssetCode, name = AssetName },
        roomOptions = RoomOptions, classLengthOptionsHours = ClassLengthHours, classLengthOptionsMinutes = ClassLengthMinutes,
        expiresAt = ExpiresAt, deadlineSeconds = DeadlineSeconds, request = Request,
    };
}

/// <summary>Delivers an approval push. Returns the tokens the provider rejected as invalid so the caller can delete them.</summary>
public interface IApprovalPushSender
{
    Task<IReadOnlyList<string>> SendAsync(ApprovalPush push, IReadOnlyList<string> tokens, CancellationToken ct);
}
