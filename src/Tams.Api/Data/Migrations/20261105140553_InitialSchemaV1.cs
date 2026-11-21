using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Tams.Api.Data;

#nullable disable

namespace Tams.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchemaV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:anomaly_status", "OPEN,ACKNOWLEDGED,RESOLVED")
                .Annotation("Npgsql:Enum:anomaly_type", "TRANSIT_TIMEOUT,WRONG_ROOM,UNBORROWED_TAP")
                .Annotation("Npgsql:Enum:approval_decision", "PENDING,APPROVED,DECLINED,EXPIRED")
                .Annotation("Npgsql:Enum:asset_condition", "GOOD,FAIR,DAMAGED")
                .Annotation("Npgsql:Enum:asset_status", "IN_STORAGE,AT_COUNTER,IN_TRANSIT,IN_ROOM_VERIFIED,LOCATION_ANOMALY,OVERDUE,MISSING,MAINTENANCE,RETIRED")
                .Annotation("Npgsql:Enum:asset_type", "BORROWABLE,FIXED")
                .Annotation("Npgsql:Enum:audit_result", "MATCHED,MISSING,MISPLACED,UNKNOWN")
                .Annotation("Npgsql:Enum:audit_source", "MOBILE,EXTERNAL_IMPORT")
                .Annotation("Npgsql:Enum:audit_status", "IN_PROGRESS,COMPLETED,SIGNED_OFF")
                .Annotation("Npgsql:Enum:card_status", "ACTIVE,LOST,REVOKED")
                .Annotation("Npgsql:Enum:custody_state", "AWAITING_ARRIVAL,VERIFIED,ANOMALY,NOT_REQUIRED")
                .Annotation("Npgsql:Enum:device_platform", "ANDROID,IOS,WEB")
                .Annotation("Npgsql:Enum:event_status", "SCHEDULED,ACTIVE,CLOSED,CANCELLED")
                .Annotation("Npgsql:Enum:ledger_event", "CHECKOUT,ROOM_VERIFIED,LATE_ARRIVAL_WARNING,RETURN,OVERDUE,MARKED_MISSING,SETTLED")
                .Annotation("Npgsql:Enum:liability_status", "OUTSTANDING,SETTLED_PAYMENT,SETTLED_REPLACEMENT,WAIVED")
                .Annotation("Npgsql:Enum:loan_status", "ACTIVE,OVERDUE,RETURNED,MISSING")
                .Annotation("Npgsql:Enum:loan_type", "STANDARD,EVENT")
                .Annotation("Npgsql:Enum:media_kind", "CHECKOUT_PHOTO,ASSET_IMAGE,AUDIT_REPORT,AUDIT_IMPORT,QR_SHEET")
                .Annotation("Npgsql:Enum:penalty_reason", "LATE_ARRIVAL_LIMIT,OVERDUE,MISSING")
                .Annotation("Npgsql:Enum:penalty_status", "ACTIVE,CLEARED")
                .Annotation("Npgsql:Enum:record_status", "ACTIVE,INACTIVE")
                .Annotation("Npgsql:Enum:scan_result", "VERIFIED,VERIFIED_LATE,WRONG_ROOM,NOT_BORROWED,UNKNOWN_TAG")
                .Annotation("Npgsql:Enum:session_status", "AWAITING_FINGERPRINT,AWAITING_ASSET,AWAITING_PHOTO,AWAITING_APPROVAL,AWAITING_CONFIRM,COMPLETED,DECLINED,CANCELLED,EXPIRED,FAILED")
                .Annotation("Npgsql:Enum:session_type", "CHECKOUT,RETURN,EVENT_BATCH,EVENT_RETURN,ENROLLMENT")
                .Annotation("Npgsql:Enum:station_health", "ONLINE,WARNING,OFFLINE")
                .Annotation("Npgsql:Enum:station_type", "COUNTER_KIOSK,ROOM_STATION")
                .Annotation("Npgsql:Enum:student_standing", "GOOD,WARNED,BLOCKED")
                .Annotation("Npgsql:Enum:user_role", "ADMIN,TECH_STAFF,CUSTODIAN,INSTRUCTOR")
                .Annotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "asset_categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    is_borrowable = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_categories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "media_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    kind = table.Column<MediaKind>(type: "media_kind", nullable: false),
                    storage_path = table.Column<string>(type: "text", nullable: false),
                    mime_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_files", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rooms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    building = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    floor = table.Column<short>(type: "smallint", nullable: false),
                    is_storage = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<RecordStatus>(type: "record_status", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rooms", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    asset_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<AssetType>(type: "asset_type", nullable: false),
                    brand = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    model = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    serial_no = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    rfid_tag_uid = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    home_room_id = table.Column<Guid>(type: "uuid", nullable: false),
                    current_room_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<AssetStatus>(type: "asset_status", nullable: false),
                    condition = table.Column<AssetCondition>(type: "asset_condition", nullable: false),
                    replacement_cost = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    acquired_at = table.Column<DateOnly>(type: "date", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    image_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assets", x => x.id);
                    table.ForeignKey(
                        name: "fk_assets_category_id",
                        column: x => x.category_id,
                        principalTable: "asset_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_assets_current_room_id",
                        column: x => x.current_room_id,
                        principalTable: "rooms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_assets_home_room_id",
                        column: x => x.home_room_id,
                        principalTable: "rooms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_assets_image_file_id",
                        column: x => x.image_file_id,
                        principalTable: "media_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    station_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    type = table.Column<StationType>(type: "station_type", nullable: false),
                    room_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    firmware_version = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    health = table.Column<StationHealth>(type: "station_health", nullable: false),
                    status = table.Column<RecordStatus>(type: "record_status", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stations", x => x.id);
                    table.ForeignKey(
                        name: "fk_stations_room_id",
                        column: x => x.room_id,
                        principalTable: "rooms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    employee_no = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    first_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    last_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    role = table.Column<UserRole>(type: "user_role", nullable: false),
                    is_event_coordinator = table.Column<bool>(type: "boolean", nullable: false),
                    default_room_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<RecordStatus>(type: "record_status", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email = table.Column<string>(type: "citext", maxLength: 256, nullable: false),
                    normalized_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    security_stamp = table.Column<string>(type: "text", nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true),
                    lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lockout_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    access_failed_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.ForeignKey(
                        name: "fk_users_default_room_id",
                        column: x => x.default_room_id,
                        principalTable: "rooms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "station_heartbeats",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    station_id = table.Column<Guid>(type: "uuid", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    rssi = table.Column<short>(type: "smallint", nullable: false),
                    uptime_s = table.Column<int>(type: "integer", nullable: false),
                    free_heap = table.Column<int>(type: "integer", nullable: false),
                    firmware_version = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_station_heartbeats", x => x.id);
                    table.ForeignKey(
                        name: "fk_station_heartbeats_station_id",
                        column: x => x.station_id,
                        principalTable: "stations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_station_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    before = table.Column<string>(type: "jsonb", nullable: true),
                    after = table.Column<string>(type: "jsonb", nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                    table.ForeignKey(
                        name: "fk_audit_log_actor_station_id",
                        column: x => x.actor_station_id,
                        principalTable: "stations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_audit_log_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "audits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    room_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<AuditSource>(type: "audit_source", nullable: false),
                    status = table.Column<AuditStatus>(type: "audit_status", nullable: false),
                    started_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    signed_off_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    signed_off_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expected_count = table.Column<int>(type: "integer", nullable: false),
                    matched_count = table.Column<int>(type: "integer", nullable: false),
                    missing_count = table.Column<int>(type: "integer", nullable: false),
                    misplaced_count = table.Column<int>(type: "integer", nullable: false),
                    unknown_count = table.Column<int>(type: "integer", nullable: false),
                    import_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    report_file_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audits", x => x.id);
                    table.ForeignKey(
                        name: "fk_audits_import_file_id",
                        column: x => x.import_file_id,
                        principalTable: "media_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_audits_report_file_id",
                        column: x => x.report_file_id,
                        principalTable: "media_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_audits_room_id",
                        column: x => x.room_id,
                        principalTable: "rooms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_audits_signed_off_by_user_id",
                        column: x => x.signed_off_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_audits_started_by_user_id",
                        column: x => x.started_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "auth_cards",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    card_uid = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<CardStatus>(type: "card_status", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_cards", x => x.id);
                    table.ForeignKey(
                        name: "fk_auth_cards_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "device_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<DevicePlatform>(type: "device_platform", nullable: false),
                    token = table.Column<string>(type: "text", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_tokens_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    title = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    venue_text = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    venue_room_id = table.Column<Guid>(type: "uuid", nullable: true),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    coordinator_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<EventStatus>(type: "event_status", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_events", x => x.id);
                    table.CheckConstraint("ck_events_period", "ends_at > starts_at");
                    table.ForeignKey(
                        name: "fk_events_coordinator_user_id",
                        column: x => x.coordinator_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_events_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_events_venue_room_id",
                        column: x => x.venue_room_id,
                        principalTable: "rooms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    program = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    year_level = table.Column<short>(type: "smallint", nullable: false),
                    school_year = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    adviser_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sections", x => x.id);
                    table.ForeignKey(
                        name: "fk_sections_adviser_user_id",
                        column: x => x.adviser_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "system_settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_system_settings", x => x.key);
                    table.ForeignKey(
                        name: "fk_system_settings_updated_by_user_id",
                        column: x => x.updated_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "audit_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    audit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scanned_value = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    result = table.Column<AuditResult>(type: "audit_result", nullable: false),
                    client_scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scanned_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scanned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_audit_items_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_audit_items_audit_id",
                        column: x => x.audit_id,
                        principalTable: "audits",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_audit_items_scanned_by_user_id",
                        column: x => x.scanned_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "students",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    student_no = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    first_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    last_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    section_id = table.Column<Guid>(type: "uuid", nullable: false),
                    standing = table.Column<StudentStanding>(type: "student_standing", nullable: false),
                    active_warning_count = table.Column<short>(type: "smallint", nullable: false),
                    active_mark_count = table.Column<short>(type: "smallint", nullable: false),
                    has_active_hold = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<RecordStatus>(type: "record_status", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_students", x => x.id);
                    table.ForeignKey(
                        name: "fk_students_section_id",
                        column: x => x.section_id,
                        principalTable: "sections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "checkout_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    station_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<SessionType>(type: "session_type", nullable: false),
                    status = table.Column<SessionStatus>(type: "session_status", nullable: false),
                    auth_card_id = table.Column<Guid>(type: "uuid", nullable: true),
                    instructor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    student_id = table.Column<Guid>(type: "uuid", nullable: true),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    photo_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    destination_room_id = table.Column<Guid>(type: "uuid", nullable: true),
                    class_length_minutes = table.Column<short>(type: "smallint", nullable: true),
                    fingerprint_attempts = table.Column<short>(type: "smallint", nullable: false),
                    lcd_lines = table.Column<string>(type: "jsonb", nullable: false),
                    failure_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_checkout_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_checkout_sessions_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_checkout_sessions_auth_card_id",
                        column: x => x.auth_card_id,
                        principalTable: "auth_cards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_checkout_sessions_destination_room_id",
                        column: x => x.destination_room_id,
                        principalTable: "rooms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_checkout_sessions_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_checkout_sessions_instructor_user_id",
                        column: x => x.instructor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_checkout_sessions_photo_file_id",
                        column: x => x.photo_file_id,
                        principalTable: "media_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_checkout_sessions_station_id",
                        column: x => x.station_id,
                        principalTable: "stations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_checkout_sessions_student_id",
                        column: x => x.student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fingerprint_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    finger_index = table.Column<short>(type: "smallint", nullable: false),
                    template = table.Column<byte[]>(type: "bytea", nullable: false),
                    sensor_model = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    quality_score = table.Column<short>(type: "smallint", nullable: true),
                    enrolled_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enrolled_station_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fingerprint_templates", x => x.id);
                    table.CheckConstraint("ck_fingerprint_templates_finger_index", "finger_index BETWEEN 0 AND 9");
                    table.ForeignKey(
                        name: "fk_fingerprint_templates_enrolled_by_user_id",
                        column: x => x.enrolled_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fingerprint_templates_enrolled_station_id",
                        column: x => x.enrolled_station_id,
                        principalTable: "stations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fingerprint_templates_student_id",
                        column: x => x.student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "approvals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instructor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision = table.Column<ApprovalDecision>(type: "approval_decision", nullable: false),
                    destination_room_id = table.Column<Guid>(type: "uuid", nullable: true),
                    class_length_minutes = table.Column<short>(type: "smallint", nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_device = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approvals", x => x.id);
                    table.CheckConstraint("ck_approvals_class_length", "class_length_minutes IS NULL OR class_length_minutes BETWEEN 90 AND 180");
                    table.ForeignKey(
                        name: "fk_approvals_destination_room_id",
                        column: x => x.destination_room_id,
                        principalTable: "rooms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_approvals_instructor_user_id",
                        column: x => x.instructor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_approvals_session_id",
                        column: x => x.session_id,
                        principalTable: "checkout_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "loans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    loan_no = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    type = table.Column<LoanType>(type: "loan_type", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    coordinator_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approval_id = table.Column<Guid>(type: "uuid", nullable: true),
                    checkout_station_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_room_id = table.Column<Guid>(type: "uuid", nullable: true),
                    photo_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<LoanStatus>(type: "loan_status", nullable: false),
                    custody_state = table.Column<CustodyState>(type: "custody_state", nullable: false),
                    checked_out_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    transit_deadline_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    verified_station_id = table.Column<Guid>(type: "uuid", nullable: true),
                    class_length_minutes = table.Column<short>(type: "smallint", nullable: true),
                    class_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    grace_minutes = table.Column<short>(type: "smallint", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    overdue_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    missing_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    returned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    returned_by_student_id = table.Column<Guid>(type: "uuid", nullable: true),
                    return_station_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_loans", x => x.id);
                    table.CheckConstraint("ck_loans_class_length", "class_length_minutes IS NULL OR class_length_minutes BETWEEN 90 AND 180");
                    table.CheckConstraint("ck_loans_grace", "grace_minutes BETWEEN 15 AND 30");
                    table.ForeignKey(
                        name: "fk_loans_approval_id",
                        column: x => x.approval_id,
                        principalTable: "approvals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_loans_approved_by_user_id",
                        column: x => x.approved_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_loans_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_loans_checkout_station_id",
                        column: x => x.checkout_station_id,
                        principalTable: "stations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_loans_coordinator_user_id",
                        column: x => x.coordinator_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_loans_destination_room_id",
                        column: x => x.destination_room_id,
                        principalTable: "rooms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_loans_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_loans_photo_file_id",
                        column: x => x.photo_file_id,
                        principalTable: "media_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_loans_return_station_id",
                        column: x => x.return_station_id,
                        principalTable: "stations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_loans_returned_by_student_id",
                        column: x => x.returned_by_student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_loans_session_id",
                        column: x => x.session_id,
                        principalTable: "checkout_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_loans_student_id",
                        column: x => x.student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_loans_verified_station_id",
                        column: x => x.verified_station_id,
                        principalTable: "stations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "anomalies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    type = table.Column<AnomalyType>(type: "anomaly_type", nullable: false),
                    status = table.Column<AnomalyStatus>(type: "anomaly_status", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    loan_id = table.Column<Guid>(type: "uuid", nullable: true),
                    room_id = table.Column<Guid>(type: "uuid", nullable: true),
                    station_id = table.Column<Guid>(type: "uuid", nullable: true),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    acknowledged_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution_note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_anomalies", x => x.id);
                    table.ForeignKey(
                        name: "fk_anomalies_acknowledged_by_user_id",
                        column: x => x.acknowledged_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_anomalies_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_anomalies_loan_id",
                        column: x => x.loan_id,
                        principalTable: "loans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_anomalies_resolved_by_user_id",
                        column: x => x.resolved_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_anomalies_room_id",
                        column: x => x.room_id,
                        principalTable: "rooms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_anomalies_station_id",
                        column: x => x.station_id,
                        principalTable: "stations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "approval_freezes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    instructor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    loan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<PenaltyReason>(type: "penalty_reason", nullable: false),
                    status = table.Column<PenaltyStatus>(type: "penalty_status", nullable: false),
                    frozen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lifted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lifted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lift_note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approval_freezes", x => x.id);
                    table.ForeignKey(
                        name: "fk_approval_freezes_instructor_user_id",
                        column: x => x.instructor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_approval_freezes_lifted_by_user_id",
                        column: x => x.lifted_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_approval_freezes_loan_id",
                        column: x => x.loan_id,
                        principalTable: "loans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "liabilities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    loan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    status = table.Column<LiabilityStatus>(type: "liability_status", nullable: false),
                    receipt_no = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    settled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_liabilities", x => x.id);
                    table.ForeignKey(
                        name: "fk_liabilities_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_liabilities_loan_id",
                        column: x => x.loan_id,
                        principalTable: "loans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_liabilities_settled_by_user_id",
                        column: x => x.settled_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_liabilities_student_id",
                        column: x => x.student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "non_compliance_marks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    loan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<PenaltyReason>(type: "penalty_reason", nullable: false),
                    status = table.Column<PenaltyStatus>(type: "penalty_status", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cleared_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cleared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    clear_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_non_compliance_marks", x => x.id);
                    table.ForeignKey(
                        name: "fk_non_compliance_marks_cleared_by_user_id",
                        column: x => x.cleared_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_non_compliance_marks_loan_id",
                        column: x => x.loan_id,
                        principalTable: "loans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_non_compliance_marks_student_id",
                        column: x => x.student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    recipient_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    title = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: true),
                    loan_id = table.Column<Guid>(type: "uuid", nullable: true),
                    student_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_notifications_loan_id",
                        column: x => x.loan_id,
                        principalTable: "loans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_notifications_recipient_user_id",
                        column: x => x.recipient_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_notifications_student_id",
                        column: x => x.student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "profile_holds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    loan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<PenaltyStatus>(type: "penalty_status", nullable: false),
                    placed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lifted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lifted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_profile_holds", x => x.id);
                    table.ForeignKey(
                        name: "fk_profile_holds_lifted_by_user_id",
                        column: x => x.lifted_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_profile_holds_loan_id",
                        column: x => x.loan_id,
                        principalTable: "loans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_profile_holds_student_id",
                        column: x => x.student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "room_scans",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    station_id = table.Column<Guid>(type: "uuid", nullable: false),
                    room_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_uid = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    loan_id = table.Column<Guid>(type: "uuid", nullable: true),
                    result = table.Column<ScanResult>(type: "scan_result", nullable: false),
                    scanned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_room_scans", x => x.id);
                    table.ForeignKey(
                        name: "fk_room_scans_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_room_scans_loan_id",
                        column: x => x.loan_id,
                        principalTable: "loans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_room_scans_room_id",
                        column: x => x.room_id,
                        principalTable: "rooms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_room_scans_station_id",
                        column: x => x.station_id,
                        principalTable: "stations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "transaction_ledger",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    @event = table.Column<LedgerEvent>(name: "event", type: "ledger_event", nullable: false),
                    loan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: true),
                    instructor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approval_decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    photo_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    station_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    details = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transaction_ledger", x => x.id);
                    table.ForeignKey(
                        name: "fk_transaction_ledger_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transaction_ledger_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transaction_ledger_instructor_user_id",
                        column: x => x.instructor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transaction_ledger_loan_id",
                        column: x => x.loan_id,
                        principalTable: "loans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transaction_ledger_photo_file_id",
                        column: x => x.photo_file_id,
                        principalTable: "media_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transaction_ledger_station_id",
                        column: x => x.station_id,
                        principalTable: "stations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transaction_ledger_student_id",
                        column: x => x.student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "late_arrival_warnings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    loan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    anomaly_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warning_no = table.Column<short>(type: "smallint", nullable: false),
                    school_term = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    school_year = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    triggered_penalty = table.Column<bool>(type: "boolean", nullable: false),
                    mark_id = table.Column<Guid>(type: "uuid", nullable: true),
                    freeze_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<PenaltyStatus>(type: "penalty_status", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cleared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cleared_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    clear_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_late_arrival_warnings", x => x.id);
                    table.ForeignKey(
                        name: "fk_late_arrival_warnings_anomaly_id",
                        column: x => x.anomaly_id,
                        principalTable: "anomalies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_late_arrival_warnings_cleared_by_user_id",
                        column: x => x.cleared_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_late_arrival_warnings_freeze_id",
                        column: x => x.freeze_id,
                        principalTable: "approval_freezes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_late_arrival_warnings_loan_id",
                        column: x => x.loan_id,
                        principalTable: "loans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_late_arrival_warnings_mark_id",
                        column: x => x.mark_id,
                        principalTable: "non_compliance_marks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_late_arrival_warnings_student_id",
                        column: x => x.student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_anomalies_acknowledged_by_user_id",
                table: "anomalies",
                column: "acknowledged_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_anomalies_asset_id",
                table: "anomalies",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_anomalies_loan_id",
                table: "anomalies",
                column: "loan_id");

            migrationBuilder.CreateIndex(
                name: "ix_anomalies_resolved_by_user_id",
                table: "anomalies",
                column: "resolved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_anomalies_room_id",
                table: "anomalies",
                column: "room_id");

            migrationBuilder.CreateIndex(
                name: "ix_anomalies_station_id",
                table: "anomalies",
                column: "station_id");

            migrationBuilder.CreateIndex(
                name: "ix_approval_freezes_instructor_user_id",
                table: "approval_freezes",
                column: "instructor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_approval_freezes_lifted_by_user_id",
                table: "approval_freezes",
                column: "lifted_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_approval_freezes_loan_id",
                table: "approval_freezes",
                column: "loan_id");

            migrationBuilder.CreateIndex(
                name: "ix_approvals_destination_room_id",
                table: "approvals",
                column: "destination_room_id");

            migrationBuilder.CreateIndex(
                name: "ix_approvals_instructor_user_id",
                table: "approvals",
                column: "instructor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_approvals_session_id_uq",
                table: "approvals",
                column: "session_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_asset_categories_name_uq",
                table: "asset_categories",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_assets_asset_code_uq",
                table: "assets",
                column: "asset_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_assets_category_id",
                table: "assets",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_assets_current_room_id",
                table: "assets",
                column: "current_room_id");

            migrationBuilder.CreateIndex(
                name: "ix_assets_home_room_id",
                table: "assets",
                column: "home_room_id");

            migrationBuilder.CreateIndex(
                name: "ix_assets_image_file_id",
                table: "assets",
                column: "image_file_id");

            migrationBuilder.CreateIndex(
                name: "ix_assets_rfid_tag_uid_uq",
                table: "assets",
                column: "rfid_tag_uid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_items_asset_id",
                table: "audit_items",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_items_audit_id",
                table: "audit_items",
                column: "audit_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_items_client_scan_id_uq",
                table: "audit_items",
                column: "client_scan_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_items_scanned_by_user_id",
                table: "audit_items",
                column: "scanned_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_actor_station_id",
                table: "audit_log",
                column: "actor_station_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_actor_user_id",
                table: "audit_log",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_created_at",
                table: "audit_log",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_entity_type_entity_id",
                table: "audit_log",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audits_import_file_id",
                table: "audits",
                column: "import_file_id");

            migrationBuilder.CreateIndex(
                name: "ix_audits_report_file_id",
                table: "audits",
                column: "report_file_id");

            migrationBuilder.CreateIndex(
                name: "ix_audits_room_id",
                table: "audits",
                column: "room_id");

            migrationBuilder.CreateIndex(
                name: "ix_audits_signed_off_by_user_id",
                table: "audits",
                column: "signed_off_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_audits_started_by_user_id",
                table: "audits",
                column: "started_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_auth_cards_card_uid_uq",
                table: "auth_cards",
                column: "card_uid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_auth_cards_user_id",
                table: "auth_cards",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_checkout_sessions_asset_id",
                table: "checkout_sessions",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_checkout_sessions_auth_card_id",
                table: "checkout_sessions",
                column: "auth_card_id");

            migrationBuilder.CreateIndex(
                name: "ix_checkout_sessions_destination_room_id",
                table: "checkout_sessions",
                column: "destination_room_id");

            migrationBuilder.CreateIndex(
                name: "ix_checkout_sessions_event_id",
                table: "checkout_sessions",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_checkout_sessions_instructor_user_id",
                table: "checkout_sessions",
                column: "instructor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_checkout_sessions_photo_file_id",
                table: "checkout_sessions",
                column: "photo_file_id");

            migrationBuilder.CreateIndex(
                name: "ix_checkout_sessions_station_id",
                table: "checkout_sessions",
                column: "station_id");

            migrationBuilder.CreateIndex(
                name: "ix_checkout_sessions_student_id",
                table: "checkout_sessions",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_tokens_token_uq",
                table: "device_tokens",
                column: "token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_tokens_user_id",
                table: "device_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_events_coordinator_user_id",
                table: "events",
                column: "coordinator_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_events_created_by_user_id",
                table: "events",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_events_venue_room_id",
                table: "events",
                column: "venue_room_id");

            migrationBuilder.CreateIndex(
                name: "ix_fingerprint_templates_enrolled_by_user_id",
                table: "fingerprint_templates",
                column: "enrolled_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_fingerprint_templates_enrolled_station_id",
                table: "fingerprint_templates",
                column: "enrolled_station_id");

            migrationBuilder.CreateIndex(
                name: "ix_fingerprint_templates_student_id_finger_index_uq",
                table: "fingerprint_templates",
                columns: new[] { "student_id", "finger_index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_late_arrival_warnings_anomaly_id",
                table: "late_arrival_warnings",
                column: "anomaly_id");

            migrationBuilder.CreateIndex(
                name: "ix_late_arrival_warnings_cleared_by_user_id",
                table: "late_arrival_warnings",
                column: "cleared_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_late_arrival_warnings_freeze_id",
                table: "late_arrival_warnings",
                column: "freeze_id");

            migrationBuilder.CreateIndex(
                name: "ix_late_arrival_warnings_loan_id_uq",
                table: "late_arrival_warnings",
                column: "loan_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_late_arrival_warnings_mark_id",
                table: "late_arrival_warnings",
                column: "mark_id");

            migrationBuilder.CreateIndex(
                name: "ix_late_arrival_warnings_student_id",
                table: "late_arrival_warnings",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "ix_liabilities_asset_id",
                table: "liabilities",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_liabilities_loan_id_uq",
                table: "liabilities",
                column: "loan_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_liabilities_settled_by_user_id",
                table: "liabilities",
                column: "settled_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_liabilities_student_id",
                table: "liabilities",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "ix_loans_approval_id",
                table: "loans",
                column: "approval_id");

            migrationBuilder.CreateIndex(
                name: "ix_loans_approved_by_user_id",
                table: "loans",
                column: "approved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_loans_checkout_station_id",
                table: "loans",
                column: "checkout_station_id");

            migrationBuilder.CreateIndex(
                name: "ix_loans_coordinator_user_id",
                table: "loans",
                column: "coordinator_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_loans_destination_room_id",
                table: "loans",
                column: "destination_room_id");

            migrationBuilder.CreateIndex(
                name: "ix_loans_event_id",
                table: "loans",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_loans_loan_no_uq",
                table: "loans",
                column: "loan_no",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_loans_photo_file_id",
                table: "loans",
                column: "photo_file_id");

            migrationBuilder.CreateIndex(
                name: "ix_loans_return_station_id",
                table: "loans",
                column: "return_station_id");

            migrationBuilder.CreateIndex(
                name: "ix_loans_returned_by_student_id",
                table: "loans",
                column: "returned_by_student_id");

            migrationBuilder.CreateIndex(
                name: "ix_loans_session_id",
                table: "loans",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_loans_student_id",
                table: "loans",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "ix_loans_verified_station_id",
                table: "loans",
                column: "verified_station_id");

            migrationBuilder.CreateIndex(
                name: "ux_loans_open_asset",
                table: "loans",
                column: "asset_id",
                unique: true,
                filter: "status IN ('ACTIVE','OVERDUE')");

            migrationBuilder.CreateIndex(
                name: "ix_non_compliance_marks_cleared_by_user_id",
                table: "non_compliance_marks",
                column: "cleared_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_non_compliance_marks_loan_id_reason_uq",
                table: "non_compliance_marks",
                columns: new[] { "loan_id", "reason" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_non_compliance_marks_student_id",
                table: "non_compliance_marks",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_loan_id",
                table: "notifications",
                column: "loan_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_recipient_user_id_read_at",
                table: "notifications",
                columns: new[] { "recipient_user_id", "read_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_student_id",
                table: "notifications",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "ix_profile_holds_lifted_by_user_id",
                table: "profile_holds",
                column: "lifted_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_profile_holds_loan_id",
                table: "profile_holds",
                column: "loan_id");

            migrationBuilder.CreateIndex(
                name: "ix_profile_holds_student_id",
                table: "profile_holds",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "ix_room_scans_asset_id",
                table: "room_scans",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_room_scans_loan_id",
                table: "room_scans",
                column: "loan_id");

            migrationBuilder.CreateIndex(
                name: "ix_room_scans_room_id",
                table: "room_scans",
                column: "room_id");

            migrationBuilder.CreateIndex(
                name: "ix_room_scans_station_id_scanned_at",
                table: "room_scans",
                columns: new[] { "station_id", "scanned_at" });

            migrationBuilder.CreateIndex(
                name: "ix_rooms_code_uq",
                table: "rooms",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sections_adviser_user_id",
                table: "sections",
                column: "adviser_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_sections_code_school_year_uq",
                table: "sections",
                columns: new[] { "code", "school_year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_station_heartbeats_station_id_received_at",
                table: "station_heartbeats",
                columns: new[] { "station_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_stations_room_id",
                table: "stations",
                column: "room_id");

            migrationBuilder.CreateIndex(
                name: "ix_stations_station_code_uq",
                table: "stations",
                column: "station_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_students_section_id",
                table: "students",
                column: "section_id");

            migrationBuilder.CreateIndex(
                name: "ix_students_student_no_uq",
                table: "students",
                column: "student_no",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_system_settings_updated_by_user_id",
                table: "system_settings",
                column: "updated_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_transaction_ledger_actor_user_id",
                table: "transaction_ledger",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_transaction_ledger_asset_id",
                table: "transaction_ledger",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_transaction_ledger_instructor_user_id",
                table: "transaction_ledger",
                column: "instructor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_transaction_ledger_loan_id",
                table: "transaction_ledger",
                column: "loan_id");

            migrationBuilder.CreateIndex(
                name: "ix_transaction_ledger_photo_file_id",
                table: "transaction_ledger",
                column: "photo_file_id");

            migrationBuilder.CreateIndex(
                name: "ix_transaction_ledger_station_id",
                table: "transaction_ledger",
                column: "station_id");

            migrationBuilder.CreateIndex(
                name: "ix_transaction_ledger_student_id",
                table: "transaction_ledger",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_default_room_id",
                table: "users",
                column: "default_room_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_email_uq",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_employee_no_uq",
                table: "users",
                column: "employee_no",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_normalized_email",
                table: "users",
                column: "normalized_email");

            migrationBuilder.CreateIndex(
                name: "ix_users_normalized_user_name_uq",
                table: "users",
                column: "normalized_user_name",
                unique: true);

            // PRD 7.3: transaction_ledger is append-only; a trigger rejects UPDATE and DELETE.
            migrationBuilder.Sql(@"
CREATE FUNCTION transaction_ledger_reject_change() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'transaction_ledger is append-only';
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER transaction_ledger_append_only
BEFORE UPDATE OR DELETE ON transaction_ledger
FOR EACH ROW EXECUTE FUNCTION transaction_ledger_reject_change();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER transaction_ledger_append_only ON transaction_ledger; DROP FUNCTION transaction_ledger_reject_change();");

            migrationBuilder.DropTable(
                name: "audit_items");

            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "device_tokens");

            migrationBuilder.DropTable(
                name: "fingerprint_templates");

            migrationBuilder.DropTable(
                name: "late_arrival_warnings");

            migrationBuilder.DropTable(
                name: "liabilities");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "profile_holds");

            migrationBuilder.DropTable(
                name: "room_scans");

            migrationBuilder.DropTable(
                name: "station_heartbeats");

            migrationBuilder.DropTable(
                name: "system_settings");

            migrationBuilder.DropTable(
                name: "transaction_ledger");

            migrationBuilder.DropTable(
                name: "audits");

            migrationBuilder.DropTable(
                name: "anomalies");

            migrationBuilder.DropTable(
                name: "approval_freezes");

            migrationBuilder.DropTable(
                name: "non_compliance_marks");

            migrationBuilder.DropTable(
                name: "loans");

            migrationBuilder.DropTable(
                name: "approvals");

            migrationBuilder.DropTable(
                name: "checkout_sessions");

            migrationBuilder.DropTable(
                name: "assets");

            migrationBuilder.DropTable(
                name: "auth_cards");

            migrationBuilder.DropTable(
                name: "events");

            migrationBuilder.DropTable(
                name: "stations");

            migrationBuilder.DropTable(
                name: "students");

            migrationBuilder.DropTable(
                name: "asset_categories");

            migrationBuilder.DropTable(
                name: "media_files");

            migrationBuilder.DropTable(
                name: "sections");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "rooms");
        }
    }
}
