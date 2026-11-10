using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Tams.Api.Data.Entities;

[Table("rooms")]
public class Room
{
    public Guid Id { get; set; }
    [MaxLength(20)] public string Code { get; set; } = "";
    [MaxLength(120)] public string Name { get; set; } = "";
    [MaxLength(80)] public string Building { get; set; } = "College Building";
    public short Floor { get; set; }
    public bool IsStorage { get; set; }
    public RecordStatus Status { get; set; } = RecordStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

[Table("stations")]
public class Station
{
    public Guid Id { get; set; }
    [MaxLength(30)] public string StationCode { get; set; } = "";
    public StationType Type { get; set; }
    public Guid RoomId { get; set; }
    [MaxLength(80)] public string Name { get; set; } = "";
    [MaxLength(30)] public string? FirmwareVersion { get; set; }
    [MaxLength(45)] public string? IpAddress { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public StationHealth Health { get; set; } = StationHealth.Offline;
    public RecordStatus Status { get; set; } = RecordStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

[Table("station_heartbeats")]
public class StationHeartbeat
{
    public long Id { get; set; }
    public Guid StationId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public short Rssi { get; set; }
    public int UptimeS { get; set; }
    public int FreeHeap { get; set; }
    [MaxLength(30)] public string? FirmwareVersion { get; set; }
}

[Table("asset_categories")]
public class AssetCategory
{
    public Guid Id { get; set; }
    [MaxLength(80)] public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsBorrowable { get; set; }
}

[Table("assets")]
public class Asset
{
    public Guid Id { get; set; }
    [MaxLength(30)] public string AssetCode { get; set; } = "";
    [MaxLength(120)] public string Name { get; set; } = "";
    public Guid CategoryId { get; set; }
    public AssetType Type { get; set; }
    [MaxLength(80)] public string? Brand { get; set; }
    [MaxLength(80)] public string? Model { get; set; }
    [MaxLength(80)] public string? SerialNo { get; set; }
    [MaxLength(20)] public string? RfidTagUid { get; set; }
    public Guid HomeRoomId { get; set; }
    public Guid? CurrentRoomId { get; set; }
    public AssetStatus Status { get; set; } = AssetStatus.InStorage;
    public AssetCondition Condition { get; set; } = AssetCondition.Good;
    [Column(TypeName = "numeric(12,2)")] public decimal ReplacementCost { get; set; }
    public DateOnly? AcquiredAt { get; set; }
    public string? Notes { get; set; }
    // schema-v1-decisions #5
    public Guid? ImageFileId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
