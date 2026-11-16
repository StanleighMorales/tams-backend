using Microsoft.EntityFrameworkCore.Migrations;
using Tams.Api.Data;

#nullable disable

namespace Tams.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DamageLiabilityReason : Migration
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
                .Annotation("Npgsql:Enum:liability_reason", "MISSING,DAMAGE")
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
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:Enum:anomaly_status", "OPEN,ACKNOWLEDGED,RESOLVED")
                .OldAnnotation("Npgsql:Enum:anomaly_type", "TRANSIT_TIMEOUT,WRONG_ROOM,UNBORROWED_TAP")
                .OldAnnotation("Npgsql:Enum:approval_decision", "PENDING,APPROVED,DECLINED,EXPIRED")
                .OldAnnotation("Npgsql:Enum:asset_condition", "GOOD,FAIR,DAMAGED")
                .OldAnnotation("Npgsql:Enum:asset_status", "IN_STORAGE,AT_COUNTER,IN_TRANSIT,IN_ROOM_VERIFIED,LOCATION_ANOMALY,OVERDUE,MISSING,MAINTENANCE,RETIRED")
                .OldAnnotation("Npgsql:Enum:asset_type", "BORROWABLE,FIXED")
                .OldAnnotation("Npgsql:Enum:audit_result", "MATCHED,MISSING,MISPLACED,UNKNOWN")
                .OldAnnotation("Npgsql:Enum:audit_source", "MOBILE,EXTERNAL_IMPORT")
                .OldAnnotation("Npgsql:Enum:audit_status", "IN_PROGRESS,COMPLETED,SIGNED_OFF")
                .OldAnnotation("Npgsql:Enum:card_status", "ACTIVE,LOST,REVOKED")
                .OldAnnotation("Npgsql:Enum:custody_state", "AWAITING_ARRIVAL,VERIFIED,ANOMALY,NOT_REQUIRED")
                .OldAnnotation("Npgsql:Enum:device_platform", "ANDROID,IOS,WEB")
                .OldAnnotation("Npgsql:Enum:event_status", "SCHEDULED,ACTIVE,CLOSED,CANCELLED")
                .OldAnnotation("Npgsql:Enum:ledger_event", "CHECKOUT,ROOM_VERIFIED,LATE_ARRIVAL_WARNING,RETURN,OVERDUE,MARKED_MISSING,SETTLED")
                .OldAnnotation("Npgsql:Enum:liability_status", "OUTSTANDING,SETTLED_PAYMENT,SETTLED_REPLACEMENT,WAIVED")
                .OldAnnotation("Npgsql:Enum:loan_status", "ACTIVE,OVERDUE,RETURNED,MISSING")
                .OldAnnotation("Npgsql:Enum:loan_type", "STANDARD,EVENT")
                .OldAnnotation("Npgsql:Enum:media_kind", "CHECKOUT_PHOTO,ASSET_IMAGE,AUDIT_REPORT,AUDIT_IMPORT,QR_SHEET")
                .OldAnnotation("Npgsql:Enum:penalty_reason", "LATE_ARRIVAL_LIMIT,OVERDUE,MISSING")
                .OldAnnotation("Npgsql:Enum:penalty_status", "ACTIVE,CLEARED")
                .OldAnnotation("Npgsql:Enum:record_status", "ACTIVE,INACTIVE")
                .OldAnnotation("Npgsql:Enum:scan_result", "VERIFIED,VERIFIED_LATE,WRONG_ROOM,NOT_BORROWED,UNKNOWN_TAG")
                .OldAnnotation("Npgsql:Enum:session_status", "AWAITING_FINGERPRINT,AWAITING_ASSET,AWAITING_PHOTO,AWAITING_APPROVAL,AWAITING_CONFIRM,COMPLETED,DECLINED,CANCELLED,EXPIRED,FAILED")
                .OldAnnotation("Npgsql:Enum:session_type", "CHECKOUT,RETURN,EVENT_BATCH,EVENT_RETURN,ENROLLMENT")
                .OldAnnotation("Npgsql:Enum:station_health", "ONLINE,WARNING,OFFLINE")
                .OldAnnotation("Npgsql:Enum:station_type", "COUNTER_KIOSK,ROOM_STATION")
                .OldAnnotation("Npgsql:Enum:student_standing", "GOOD,WARNED,BLOCKED")
                .OldAnnotation("Npgsql:Enum:user_role", "ADMIN,TECH_STAFF,CUSTODIAN,INSTRUCTOR")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.AddColumn<LiabilityReason>(
                name: "reason",
                table: "liabilities",
                type: "liability_reason",
                nullable: false,
                defaultValueSql: "'MISSING'::liability_reason");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reason",
                table: "liabilities");

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
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:Enum:anomaly_status", "OPEN,ACKNOWLEDGED,RESOLVED")
                .OldAnnotation("Npgsql:Enum:anomaly_type", "TRANSIT_TIMEOUT,WRONG_ROOM,UNBORROWED_TAP")
                .OldAnnotation("Npgsql:Enum:approval_decision", "PENDING,APPROVED,DECLINED,EXPIRED")
                .OldAnnotation("Npgsql:Enum:asset_condition", "GOOD,FAIR,DAMAGED")
                .OldAnnotation("Npgsql:Enum:asset_status", "IN_STORAGE,AT_COUNTER,IN_TRANSIT,IN_ROOM_VERIFIED,LOCATION_ANOMALY,OVERDUE,MISSING,MAINTENANCE,RETIRED")
                .OldAnnotation("Npgsql:Enum:asset_type", "BORROWABLE,FIXED")
                .OldAnnotation("Npgsql:Enum:audit_result", "MATCHED,MISSING,MISPLACED,UNKNOWN")
                .OldAnnotation("Npgsql:Enum:audit_source", "MOBILE,EXTERNAL_IMPORT")
                .OldAnnotation("Npgsql:Enum:audit_status", "IN_PROGRESS,COMPLETED,SIGNED_OFF")
                .OldAnnotation("Npgsql:Enum:card_status", "ACTIVE,LOST,REVOKED")
                .OldAnnotation("Npgsql:Enum:custody_state", "AWAITING_ARRIVAL,VERIFIED,ANOMALY,NOT_REQUIRED")
                .OldAnnotation("Npgsql:Enum:device_platform", "ANDROID,IOS,WEB")
                .OldAnnotation("Npgsql:Enum:event_status", "SCHEDULED,ACTIVE,CLOSED,CANCELLED")
                .OldAnnotation("Npgsql:Enum:ledger_event", "CHECKOUT,ROOM_VERIFIED,LATE_ARRIVAL_WARNING,RETURN,OVERDUE,MARKED_MISSING,SETTLED")
                .OldAnnotation("Npgsql:Enum:liability_reason", "MISSING,DAMAGE")
                .OldAnnotation("Npgsql:Enum:liability_status", "OUTSTANDING,SETTLED_PAYMENT,SETTLED_REPLACEMENT,WAIVED")
                .OldAnnotation("Npgsql:Enum:loan_status", "ACTIVE,OVERDUE,RETURNED,MISSING")
                .OldAnnotation("Npgsql:Enum:loan_type", "STANDARD,EVENT")
                .OldAnnotation("Npgsql:Enum:media_kind", "CHECKOUT_PHOTO,ASSET_IMAGE,AUDIT_REPORT,AUDIT_IMPORT,QR_SHEET")
                .OldAnnotation("Npgsql:Enum:penalty_reason", "LATE_ARRIVAL_LIMIT,OVERDUE,MISSING")
                .OldAnnotation("Npgsql:Enum:penalty_status", "ACTIVE,CLEARED")
                .OldAnnotation("Npgsql:Enum:record_status", "ACTIVE,INACTIVE")
                .OldAnnotation("Npgsql:Enum:scan_result", "VERIFIED,VERIFIED_LATE,WRONG_ROOM,NOT_BORROWED,UNKNOWN_TAG")
                .OldAnnotation("Npgsql:Enum:session_status", "AWAITING_FINGERPRINT,AWAITING_ASSET,AWAITING_PHOTO,AWAITING_APPROVAL,AWAITING_CONFIRM,COMPLETED,DECLINED,CANCELLED,EXPIRED,FAILED")
                .OldAnnotation("Npgsql:Enum:session_type", "CHECKOUT,RETURN,EVENT_BATCH,EVENT_RETURN,ENROLLMENT")
                .OldAnnotation("Npgsql:Enum:station_health", "ONLINE,WARNING,OFFLINE")
                .OldAnnotation("Npgsql:Enum:station_type", "COUNTER_KIOSK,ROOM_STATION")
                .OldAnnotation("Npgsql:Enum:student_standing", "GOOD,WARNED,BLOCKED")
                .OldAnnotation("Npgsql:Enum:user_role", "ADMIN,TECH_STAFF,CUSTODIAN,INSTRUCTOR")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");
        }
    }
}
