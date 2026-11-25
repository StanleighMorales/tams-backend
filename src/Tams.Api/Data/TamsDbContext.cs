using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Tams.Api.Data.Entities;

namespace Tams.Api.Data;

// Schema v1 (PRD 7.3 plus schema-v1-decisions.md). Entities carry scalar FK properties only;
// foreign keys are wired by name in OnModelCreating, so there are no navigation properties.
public class TamsDbContext(DbContextOptions<TamsDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuthCard> AuthCards => Set<AuthCard>();
    public DbSet<Section> Sections => Set<Section>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<FingerprintTemplate> FingerprintTemplates => Set<FingerprintTemplate>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<Station> Stations => Set<Station>();
    public DbSet<StationHeartbeat> StationHeartbeats => Set<StationHeartbeat>();
    public DbSet<AssetCategory> AssetCategories => Set<AssetCategory>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<CheckoutSession> CheckoutSessions => Set<CheckoutSession>();
    public DbSet<CheckoutSessionItem> CheckoutSessionItems => Set<CheckoutSessionItem>();
    public DbSet<Approval> Approvals => Set<Approval>();
    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<RoomScan> RoomScans => Set<RoomScan>();
    public DbSet<Anomaly> Anomalies => Set<Anomaly>();
    public DbSet<TransactionLedger> TransactionLedger => Set<TransactionLedger>();
    public DbSet<LateArrivalWarning> LateArrivalWarnings => Set<LateArrivalWarning>();
    public DbSet<NonComplianceMark> NonComplianceMarks => Set<NonComplianceMark>();
    public DbSet<ProfileHold> ProfileHolds => Set<ProfileHold>();
    public DbSet<ApprovalFreeze> ApprovalFreezes => Set<ApprovalFreeze>();
    public DbSet<Liability> Liabilities => Set<Liability>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Audit> Audits => Set<Audit>();
    public DbSet<AuditItem> AuditItems => Set<AuditItem>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    // Suffix of an FK property name (HomeRoomId -> "Room") to the table it points at.
    static readonly (string Suffix, Type Target)[] FkTargets =
    [
        ("User", typeof(User)), ("Student", typeof(Student)), ("Section", typeof(Section)),
        ("Room", typeof(Room)), ("Station", typeof(Station)), ("Category", typeof(AssetCategory)),
        ("Asset", typeof(Asset)), ("Session", typeof(CheckoutSession)), ("Approval", typeof(Approval)),
        ("Loan", typeof(Loan)), ("Event", typeof(Event)), ("Audit", typeof(Audit)),
        ("Anomaly", typeof(Anomaly)), ("File", typeof(MediaFile)), ("Card", typeof(AuthCard)),
        ("Mark", typeof(NonComplianceMark)), ("Freeze", typeof(ApprovalFreeze)),
    ];

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("citext");
        PgEnums.CreateAll(b);

        ConfigureUsers(b);
        ConfigureIndexes(b);
        ConfigureChecks(b);
        ConfigureDefaults(b);
        WireForeignKeys(b);
        ApplyConventions(b);
    }

    const string Live = "deleted_at IS NULL";

    static void ConfigureUsers(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            // Login is email only (decision #3); Identity's own columns we do not use are dropped.
            e.Property(u => u.Email).HasColumnType("citext").HasMaxLength(256).IsRequired();
            e.Property(u => u.UserName).HasMaxLength(256);
            e.Property(u => u.NormalizedUserName).HasMaxLength(256);
            e.Property(u => u.NormalizedEmail).HasMaxLength(256);
            e.Property(u => u.ConcurrencyStamp).IsConcurrencyToken();
            e.Ignore(u => u.EmailConfirmed);
            e.Ignore(u => u.PhoneNumber);
            e.Ignore(u => u.PhoneNumberConfirmed);
            e.Ignore(u => u.TwoFactorEnabled);
            // Partial: a soft-deleted account must not block reuse of its email (PRD 7.3, decision #7).
            e.HasIndex(u => u.Email).IsUnique().HasFilter(Live);
            e.HasIndex(u => u.NormalizedUserName).IsUnique().HasFilter(Live);
            e.HasIndex(u => u.NormalizedEmail);
        });
    }

    static void ConfigureIndexes(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(x => x.EmployeeNo).IsUnique().HasFilter(Live);
        b.Entity<RefreshToken>().HasIndex(x => x.TokenHash).IsUnique();
        b.Entity<RefreshToken>().HasIndex(x => x.UserId);
        b.Entity<DeviceToken>().HasIndex(x => x.Token).IsUnique();
        // Unique among active cards only: revoked and lost rows stay as history, so their UID can be issued again.
        b.Entity<AuthCard>().HasIndex(x => x.CardUid).IsUnique().HasFilter("status = 'ACTIVE'");
        b.Entity<Section>().HasIndex(x => new { x.Code, x.SchoolYear }).IsUnique();
        b.Entity<Student>().HasIndex(x => x.StudentNo).IsUnique().HasFilter(Live);
        b.Entity<FingerprintTemplate>().HasIndex(x => new { x.StudentId, x.FingerIndex }).IsUnique();
        b.Entity<Room>().HasIndex(x => x.Code).IsUnique().HasFilter(Live);
        b.Entity<Station>().HasIndex(x => x.StationCode).IsUnique().HasFilter(Live);
        b.Entity<StationHeartbeat>().HasIndex(x => new { x.StationId, x.ReceivedAt });
        b.Entity<AssetCategory>().HasIndex(x => x.Name).IsUnique();
        b.Entity<Asset>().HasIndex(x => x.AssetCode).IsUnique().HasFilter(Live);
        b.Entity<Asset>().HasIndex(x => x.RfidTagUid).IsUnique().HasFilter(Live); // null allowed, many nulls fine
        b.Entity<Approval>().HasIndex(x => x.SessionId).IsUnique();
        b.Entity<CheckoutSessionItem>().HasIndex(x => new { x.SessionId, x.AssetId }).IsUnique();
        b.Entity<Loan>().HasIndex(x => x.LoanNo).IsUnique();
        // An asset can have only one open loan (PRD 7.3).
        b.Entity<Loan>().HasIndex(x => x.AssetId).IsUnique().HasDatabaseName("ux_loans_open_asset")
            .HasFilter("status IN ('ACTIVE','OVERDUE')");
        // One open enrollment session per student, and one open session of any type per station.
        const string openStatuses = "status IN ('AWAITING_FINGERPRINT','AWAITING_ASSET','AWAITING_PHOTO','AWAITING_APPROVAL','AWAITING_CONFIRM')";
        b.Entity<CheckoutSession>().HasIndex(x => x.StudentId, "ux_checkout_sessions_open_enrollment_student").IsUnique()
            .HasFilter($"type = 'ENROLLMENT' AND {openStatuses}");
        b.Entity<CheckoutSession>().HasIndex(x => x.StationId, "ux_checkout_sessions_open_station").IsUnique()
            .HasFilter(openStatuses);
        // Two HasIndex calls on one column merge unless the index is named, and EF drops the conventional FK index when it is not declared; declare them so closed-session lookups stay indexed.
        b.Entity<CheckoutSession>().HasIndex(x => x.StudentId).HasDatabaseName("ix_checkout_sessions_student_id");
        b.Entity<CheckoutSession>().HasIndex(x => x.StationId).HasDatabaseName("ix_checkout_sessions_station_id");
        b.Entity<RoomScan>().HasIndex(x => new { x.StationId, x.ScannedAt });
        b.Entity<LateArrivalWarning>().HasIndex(x => x.LoanId).IsUnique();
        b.Entity<NonComplianceMark>().HasIndex(x => new { x.LoanId, x.Reason }).IsUnique();
        b.Entity<Liability>().HasIndex(x => new { x.LoanId, x.Reason }).IsUnique();   // one MISSING and one DAMAGE per loan
        b.Entity<AuditItem>().HasIndex(x => x.ClientScanId).IsUnique();
        b.Entity<AuditLogEntry>().HasIndex(x => new { x.EntityType, x.EntityId });
        b.Entity<AuditLogEntry>().HasIndex(x => x.CreatedAt);
        b.Entity<Notification>().HasIndex(x => new { x.RecipientUserId, x.ReadAt });
    }

    static void ConfigureChecks(ModelBuilder b)
    {
        b.Entity<FingerprintTemplate>().ToTable(t => t.HasCheckConstraint("ck_fingerprint_templates_finger_index", "finger_index BETWEEN 0 AND 9"));
        b.Entity<Approval>().ToTable(t => t.HasCheckConstraint("ck_approvals_class_length", "class_length_minutes IS NULL OR class_length_minutes BETWEEN 90 AND 180"));
        b.Entity<Loan>().ToTable(t =>
        {
            t.HasCheckConstraint("ck_loans_class_length", "class_length_minutes IS NULL OR class_length_minutes BETWEEN 90 AND 180");
            t.HasCheckConstraint("ck_loans_grace", "grace_minutes BETWEEN 15 AND 30");
        });
        b.Entity<Liability>().ToTable(t => t.HasCheckConstraint("ck_liabilities_one_liable",
            "num_nonnulls(student_id, liable_user_id, liable_name) = 1"));
        b.Entity<Event>().ToTable(t => t.HasCheckConstraint("ck_events_period", "ends_at > starts_at"));
        // PRD 7.3: loans.photo_file_id is not null for STANDARD loans; BORROWABLE assets need a tag.
        b.Entity<Loan>().ToTable(t => t.HasCheckConstraint("ck_loans_standard_photo", "type <> 'STANDARD' OR photo_file_id IS NOT NULL OR photo_purged_at IS NOT NULL"));
        b.Entity<Asset>().ToTable(t => t.HasCheckConstraint("ck_assets_borrowable_tag", "type <> 'BORROWABLE' OR rfid_tag_uid IS NOT NULL"));
        b.Entity<CheckoutSession>().ToTable(t => t.HasCheckConstraint("ck_checkout_sessions_finger_index", "finger_index IS NULL OR finger_index BETWEEN 0 AND 9"));
    }

    // PRD 7.3 column defaults, so rows inserted outside EF (seed scripts, psql) behave the same.
    static void ConfigureDefaults(ModelBuilder b)
    {
        // HasSentinel: the value that means "not set, let the database default apply". It equals the
        // default itself, except stations.health where Online (the CLR default) is a real value.
        b.Entity<User>().Property(x => x.IsEventCoordinator).HasDefaultValueSql("false").HasSentinel(false);
        b.Entity<Student>(e =>
        {
            e.Property(x => x.Standing).HasDefaultValueSql("'GOOD'::student_standing").HasSentinel(StudentStanding.Good);
            e.Property(x => x.ActiveWarningCount).HasDefaultValueSql("0");
            e.Property(x => x.ActiveMarkCount).HasDefaultValueSql("0");
            e.Property(x => x.HasActiveHold).HasDefaultValueSql("false").HasSentinel(false);
        });
        b.Entity<Liability>().Property(x => x.Reason).HasDefaultValueSql("'MISSING'::liability_reason").HasSentinel(LiabilityReason.Missing);
        b.Entity<FingerprintTemplate>().Property(x => x.TemplateFormat).HasDefaultValueSql("'RAW_V1'");
        b.Entity<Station>().Property(x => x.Health).HasDefaultValueSql("'OFFLINE'::station_health").HasSentinel(StationHealth.Offline);
        b.Entity<Asset>(e =>
        {
            e.Property(x => x.Status).HasDefaultValueSql("'IN_STORAGE'::asset_status").HasSentinel(AssetStatus.InStorage);
            e.Property(x => x.Condition).HasDefaultValueSql("'GOOD'::asset_condition").HasSentinel(AssetCondition.Good);
        });
        b.Entity<Room>().Property(x => x.Building).HasDefaultValueSql("'College Building'");
        b.Entity<CheckoutSession>(e =>
        {
            e.Property(x => x.FingerprintAttempts).HasDefaultValueSql("0");
            e.Property(x => x.LcdLines).HasDefaultValueSql("'[]'::jsonb");
        });
        b.Entity<RefreshToken>().HasOne<RefreshToken>().WithMany().HasForeignKey(x => x.ReplacedById).OnDelete(DeleteBehavior.SetNull);
    }

    // Every property named <Something>Id whose suffix is in FkTargets becomes an FK. Restrict by
    // default so soft-deleted history is never cascaded away; children owned by one parent cascade.
    static void WireForeignKeys(ModelBuilder b)
    {
        var cascade = new HashSet<(Type, string)>
        {
            (typeof(DeviceToken), nameof(DeviceToken.UserId)),
            (typeof(FingerprintTemplate), nameof(FingerprintTemplate.StudentId)),
            (typeof(StationHeartbeat), nameof(StationHeartbeat.StationId)),
            (typeof(AuditItem), nameof(AuditItem.AuditId)),
        };

        foreach (var entity in b.Model.GetEntityTypes().ToList())
        {
            var clr = entity.ClrType;
            foreach (var prop in clr.GetProperties().Where(p => p.Name.EndsWith("Id") && p.Name != "Id"))
            {
                var stem = prop.Name[..^2];
                var target = FkTargets.OrderByDescending(f => f.Suffix.Length)
                    .FirstOrDefault(f => stem.EndsWith(f.Suffix)).Target;
                if (target is null) continue;

                b.Entity(clr).HasOne(target).WithMany().HasForeignKey(prop.Name)
                    .OnDelete(cascade.Contains((clr, prop.Name)) ? DeleteBehavior.Cascade : DeleteBehavior.Restrict);
            }
        }
    }

    // snake_case columns and keys, uuid and now() defaults.
    static void ApplyConventions(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            var table = entity.GetTableName()!;
            foreach (var prop in entity.GetProperties())
            {
                prop.SetColumnName(PgEnums.ToSnake(prop.Name));

                if (prop.Name == "Id" && prop.ClrType == typeof(Guid))
                    prop.SetDefaultValueSql("gen_random_uuid()");
                else if (prop.Name == "Id" && prop.ClrType == typeof(long))
                    prop.ValueGenerated = ValueGenerated.OnAdd;
                else if (prop.Name is "CreatedAt" or "UpdatedAt" && prop.ClrType == typeof(DateTimeOffset))
                    prop.SetDefaultValueSql("now()");
            }

            entity.FindPrimaryKey()!.SetName($"pk_{table}");
            foreach (var fk in entity.GetForeignKeys())
                fk.SetConstraintName($"fk_{table}_{PgEnums.ToSnake(fk.Properties[0].Name)}");
            foreach (var ix in entity.GetIndexes().Where(i => i.GetDatabaseName() is { } n && n.StartsWith("IX_")))
                ix.SetDatabaseName($"ix_{table}_{string.Join('_', ix.Properties.Select(p => PgEnums.ToSnake(p.Name)))}"
                    + (ix.IsUnique ? "_uq" : ""));
        }
    }
}
