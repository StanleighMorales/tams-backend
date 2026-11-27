using Microsoft.EntityFrameworkCore;
using Tams.Api.Data.Entities;
using Tams.Api.Qr;

namespace Tams.Api.Data;

/// <summary>
/// Development only, and only when both Seed:Demo and Seed:Scenarios are true: tops the demo data up with enough rows to
/// test every flow twice (pending approvals, overdue, missing, damaged, anomalies, audits, events and so on). Runs after
/// DevSeed, does nothing once asset LT-003 exists. Kept apart from DevSeed so the integration tests, which turn Seed:Demo
/// on, keep seeing the small demo set. Borrowable tags are obviously fake (FEED...), so they never clash with real cards.
/// </summary>
public static class DevSeedScenarios
{
    public static async Task SeedScenarioDataAsync(this IServiceProvider services)
    {
        var cfg = services.GetRequiredService<IConfiguration>();
        if (!services.GetRequiredService<IHostEnvironment>().IsDevelopment()
            || !cfg.GetValue<bool>("Seed:Demo") || !cfg.GetValue<bool>("Seed:Scenarios")) return;

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
        if (await db.Assets.AnyAsync(a => a.AssetCode == "LT-003")) return;
        var store = await db.Rooms.FirstOrDefaultAsync(r => r.Code == "STORE-1");
        if (store is null) return; // the Seed:Demo base has not run

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset Ago(double hours) => now.AddHours(-hours);

        // ---------------------------------------------------------------- people and places (existing demo rows)
        User U(string email) => db.Users.First(u => u.Email == email);
        var admin = await db.Users.FirstAsync(u => u.Role == UserRole.Admin);
        var tech = U("tech@tams.test");
        var custodian = U("custodian@tams.test");
        var ins1 = U("instructor@tams.test");
        var ins2 = U("instructor2@tams.test");
        var ins3 = U("instructor3@tams.test");
        ins3.IsEventCoordinator = true; // second event coordinator

        var lab1 = await db.Rooms.FirstAsync(r => r.Code == "LAB-101");
        var lab2 = await db.Rooms.FirstAsync(r => r.Code == "LAB-102");
        var kiosk1 = await db.Stations.FirstAsync(s => s.StationCode == "KIOSK-1");
        var reader101 = await db.Stations.FirstAsync(s => s.StationCode == "ROOM-101");
        var laptops = await db.AssetCategories.FirstAsync(c => c.Name == "Laptops");
        var desktops = await db.AssetCategories.FirstAsync(c => c.Name == "Desktop PCs");
        var bscs = await db.Sections.FirstAsync(s => s.Code == "BSCS-3A");
        var bsit = await db.Sections.FirstAsync(s => s.Code == "BSIT-1B");

        // ---------------------------------------------------------------- rooms and stations
        Room NewRoom(string code, string name, short floor, bool storage) => db.Rooms.Add(new Room
        {
            Id = Guid.NewGuid(), Code = code, Name = name, Building = "Main", Floor = floor, IsStorage = storage,
            CreatedAt = now, UpdatedAt = now,
        }).Entity;
        var store2 = NewRoom("STORE-2", "AV Storage", 1, true);
        var avr = NewRoom("AVR-1", "AV Room", 1, false);
        var lab3 = NewRoom("LAB-103", "Computer Lab 3", 2, false);
        var lab201 = NewRoom("LAB-201", "Networking Lab", 2, false);
        var lect = NewRoom("LECT-101", "Lecture Hall 1", 1, false);

        // Health stays at its default (offline): the sweeper recomputes it from real heartbeats, and a seeded heartbeat time
        // would make it raise "station went offline" alerts.
        Station NewStation(string code, StationType type, Room room, string name) => db.Stations.Add(new Station
        {
            Id = Guid.NewGuid(), StationCode = code, Type = type, RoomId = room.Id, Name = name, CreatedAt = now, UpdatedAt = now,
        }).Entity;
        // Open checkout sessions are one per station, so the seeded pending approvals get their own counters
        // and KIOSK-1 stays free for the simulator.
        var kiosk2 = NewStation("KIOSK-2", StationType.CounterKiosk, store, "Counter Kiosk 2");
        var kiosk3 = NewStation("KIOSK-3", StationType.CounterKiosk, store2, "AV Counter Kiosk");
        var reader102 = NewStation("ROOM-102", StationType.RoomStation, lab2, "Lab 2 Reader");
        var reader103 = NewStation("ROOM-103", StationType.RoomStation, lab3, "Lab 3 Reader");
        NewStation("ROOM-201", StationType.RoomStation, lab201, "Networking Lab Reader");
        var readerAvr = NewStation("ROOM-AVR", StationType.RoomStation, avr, "AV Room Reader");

        // ---------------------------------------------------------------- categories
        AssetCategory Cat(string name, bool borrowable) => db.AssetCategories.Add(new AssetCategory
        {
            Id = Guid.NewGuid(), Name = name, Description = name, IsBorrowable = borrowable,
        }).Entity;
        var projectors = Cat("Projectors", true);
        var cords = Cat("Extension Cords", true);
        var cameras = Cat("Cameras", true);
        var speakers = Cat("Speakers and AV", true);
        var monitors = Cat("Monitors", false);
        var network = Cat("Network Gear", false);

        // ---------------------------------------------------------------- assets
        var X = new Dictionary<string, Asset>();
        var tag = 0;
        Asset A(string code, string name, AssetCategory cat, AssetType type, string brand, string model, Room home, decimal cost,
            AssetStatus status = AssetStatus.InStorage, AssetCondition cond = AssetCondition.Good, Room? at = null)
        {
            var a = new Asset
            {
                Id = Guid.NewGuid(), AssetCode = code, Name = name, CategoryId = cat.Id, Type = type, Brand = brand, Model = model,
                SerialNo = "SN-" + code, RfidTagUid = type == AssetType.Borrowable ? "FEED" + (++tag).ToString("X8") : null,
                HomeRoomId = home.Id, CurrentRoomId = (at ?? home).Id, Status = status, Condition = cond, ReplacementCost = cost,
                AcquiredAt = new DateOnly(2026, 6, 15), CreatedAt = now, UpdatedAt = now,
            };
            db.Assets.Add(a);
            X[code] = a;
            return a;
        }

        (string Brand, string Model)[] laptopModels = [("Dell", "Latitude 5440"), ("Lenovo", "ThinkPad E14"), ("HP", "ProBook 450"), ("Acer", "Aspire 5")];
        for (var i = 3; i <= 14; i++)
        {
            var (brand, model) = laptopModels[i % laptopModels.Length];
            A($"LT-{i:D3}", $"{brand} {model} #{i}", laptops, AssetType.Borrowable, brand, model, store, 40000m);
        }
        for (var i = 1; i <= 5; i++) A($"PRJ-{i:D3}", $"Projector #{i}", projectors, AssetType.Borrowable, i % 2 == 0 ? "BenQ" : "Epson", i % 2 == 0 ? "MS550" : "EB-X49", store2, 28000m);
        for (var i = 1; i <= 4; i++) A($"EC-{i:D3}", $"Extension Cord #{i}", cords, AssetType.Borrowable, "Omni", "6-gang 5m", store, 1200m);
        for (var i = 1; i <= 3; i++) A($"CAM-{i:D3}", $"Camera #{i}", cameras, AssetType.Borrowable, "Canon", "EOS M50", store2, 35000m);
        for (var i = 1; i <= 4; i++) A($"SPK-{i:D3}", $"Speaker #{i}", speakers, AssetType.Borrowable, "JBL", "PartyBox 110", avr, 9500m);
        for (var i = 2; i <= 7; i++) A($"PC-101-{i:D2}", $"Lab 1 Desktop {i:D2}", desktops, AssetType.Fixed, "HP", "ProDesk 400", lab1, 32000m);
        for (var i = 1; i <= 3; i++) A($"PC-102-{i:D2}", $"Lab 2 Desktop {i:D2}", desktops, AssetType.Fixed, "HP", "ProDesk 400", lab2, 32000m);
        for (var i = 1; i <= 3; i++) A($"PC-103-{i:D2}", $"Lab 3 Desktop {i:D2}", desktops, AssetType.Fixed, "HP", "ProDesk 400", lab3, 32000m);
        A("MON-001", "Monitor 24in #1", monitors, AssetType.Fixed, "Dell", "P2422H", lab1, 8500m, at: lab2);   // misplaced: turns up in lab 2
        A("MON-002", "Monitor 24in #2", monitors, AssetType.Fixed, "Dell", "P2422H", lab1, 8500m, at: lab3);   // misplaced: turns up in lab 3
        A("MON-003", "Monitor 24in #3", monitors, AssetType.Fixed, "Dell", "P2422H", lab2, 8500m);
        A("MON-004", "Monitor 24in #4", monitors, AssetType.Fixed, "Dell", "P2422H", lab3, 8500m);
        for (var i = 1; i <= 3; i++) A($"NET-{i:D3}", $"Network Switch #{i}", network, AssetType.Fixed, "TP-Link", "TL-SG1024", lab201, 15000m);

        void Set(string code, AssetStatus status, AssetCondition? cond = null)
        {
            X[code].Status = status;
            if (cond is { } c) X[code].Condition = c;
        }
        Set("LT-012", AssetStatus.Maintenance, AssetCondition.Damaged);
        Set("CAM-001", AssetStatus.Maintenance, AssetCondition.Damaged);
        Set("EC-003", AssetStatus.Retired);
        Set("EC-004", AssetStatus.Retired);
        Set("PC-101-06", AssetStatus.Retired);
        Set("MON-004", AssetStatus.Retired);
        Set("LT-014", AssetStatus.InStorage, AssetCondition.Fair);
        Set("CAM-003", AssetStatus.InStorage, AssetCondition.Fair);
        Set("PC-102-02", AssetStatus.InStorage, AssetCondition.Fair);
        Set("NET-002", AssetStatus.InStorage, AssetCondition.Fair);

        // ---------------------------------------------------------------- sections and students
        Section Sec(string code, string program, short year) => db.Sections.Add(new Section
        {
            Id = Guid.NewGuid(), Code = code, Program = program, YearLevel = year, SchoolYear = "2026-2027",
            AdviserUserId = year % 2 == 0 ? ins2.Id : ins3.Id,
        }).Entity;
        Section[] sections = [bscs, bsit, Sec("BSIT-2A", "BSIT", 2), Sec("BSCS-1A", "BSCS", 1), Sec("BSIT-3C", "BSIT", 3), Sec("BSIS-2A", "BSIS", 2)];

        string[] first = ["Liam", "Noah", "Ella", "Mia", "Luis", "Carlo", "Bea", "Jose", "Rina", "Paolo", "Kyla", "Marco",
            "Dani", "Nico", "Tina", "Ruel", "Cathy", "Migo", "Ivy", "Enzo", "Hana", "Gio", "Lara", "Sam"];
        string[] last = ["Aquino", "Bautista", "Castro", "Domingo", "Escobar", "Flores", "Gonzales", "Herrera", "Ignacio", "Javier", "Kho", "Lopez",
            "Mendoza", "Navarro", "Ocampo", "Pascual", "Quiambao", "Ramos", "Soriano", "Tolentino", "Uy", "Valdez", "Wong", "Yap"];
        var S = new List<Student>();
        for (var i = 0; i < first.Length; i++)
        {
            var st = new Student
            {
                Id = Guid.NewGuid(), StudentNo = $"2025-{101 + i:D4}", FirstName = first[i], LastName = last[i],
                SectionId = sections[i % sections.Length].Id, CreatedAt = now, UpdatedAt = now,
            };
            S.Add(st);
            db.Students.Add(st);
        }
        void Standing(Student s, StudentStanding st, short warnings = 0, short marks = 0, bool hold = false)
        {
            s.Standing = st; s.ActiveWarningCount = warnings; s.ActiveMarkCount = marks; s.HasActiveHold = hold;
        }

        await db.SaveChangesAsync(); // places, people and assets first; everything below hangs off them

        // ---------------------------------------------------------------- shared helpers for the transaction data
        var loanSeq = 0;
        Loan? NoLoan = null;

        void Led(LedgerEvent e, Loan l, DateTimeOffset at, Guid? station = null, Guid? actor = null, string? details = null) =>
            db.TransactionLedger.Add(new TransactionLedger
            {
                Event = e, LoanId = l.Id, AssetId = l.AssetId, StudentId = l.StudentId,
                InstructorUserId = l.ApprovedByUserId ?? l.CoordinatorUserId, StationId = station, ActorUserId = actor,
                OccurredAt = at, Details = details,
            });

        void Scan(Station reader, Room room, Asset a, Loan? l, ScanResult r, DateTimeOffset at) => db.RoomScans.Add(new RoomScan
        {
            StationId = reader.Id, RoomId = room.Id, TagUid = a.RfidTagUid ?? "", AssetId = a.Id, LoanId = l?.Id, Result = r, ScannedAt = at,
        });

        // A completed standard checkout: session, approval and loan, with the checkout ledger row.
        Loan Lend(Asset a, Student s, User approver, Room dest, double hoursAgo, int classMin = 90, short grace = 15)
        {
            var at = Ago(hoursAgo);
            var sid = Guid.NewGuid();
            var aid = Guid.NewGuid();
            db.CheckoutSessions.Add(new CheckoutSession
            {
                Id = sid, StationId = kiosk1.Id, Type = SessionType.Checkout, Status = SessionStatus.Completed,
                InstructorUserId = approver.Id, StudentId = s.Id, AssetId = a.Id, DestinationRoomId = dest.Id,
                ClassLengthMinutes = (short)classMin, ExpiresAt = at.AddMinutes(5), CreatedAt = at.AddMinutes(-2), CompletedAt = at,
            });
            db.Approvals.Add(new Approval
            {
                Id = aid, SessionId = sid, InstructorUserId = approver.Id, Decision = ApprovalDecision.Approved,
                DestinationRoomId = dest.Id, ClassLengthMinutes = (short)classMin, RequestedAt = at.AddMinutes(-1),
                DecidedAt = at.AddSeconds(-30), DecidedDevice = "seed",
            });
            var ends = at.AddMinutes(classMin);
            var l = new Loan
            {
                Id = Guid.NewGuid(), LoanNo = $"L-{now.Year}-{++loanSeq:D6}", Type = LoanType.Standard, AssetId = a.Id, StudentId = s.Id,
                ApprovedByUserId = approver.Id, SessionId = sid, ApprovalId = aid, CheckoutStationId = kiosk1.Id, DestinationRoomId = dest.Id,
                PhotoPurgedAt = at.AddMinutes(1), // no photo file is seeded; this satisfies ck_loans_standard_photo
                Status = LoanStatus.Active, CustodyState = CustodyState.AwaitingArrival, CheckedOutAt = at,
                TransitDeadlineAt = at.AddMinutes(15), ClassLengthMinutes = (short)classMin, ClassEndsAt = ends,
                GraceMinutes = grace, DueAt = ends.AddMinutes(grace),
            };
            db.Loans.Add(l);
            Led(LedgerEvent.Checkout, l, at, kiosk1.Id);
            a.Status = AssetStatus.InTransit; a.CurrentRoomId = null; // the default is "still on its way"
            return l;
        }

        void Verify(Loan l, Asset a, Station reader, Room room, double minutesAfter, bool late = false)
        {
            l.CustodyState = CustodyState.Verified;
            l.VerifiedAt = l.CheckedOutAt.AddMinutes(minutesAfter);
            l.VerifiedStationId = reader.Id;
            a.Status = AssetStatus.InRoomVerified; a.CurrentRoomId = room.Id;
            Led(late ? LedgerEvent.LateArrivalWarning : LedgerEvent.RoomVerified, l, l.VerifiedAt.Value, reader.Id);
            Scan(reader, room, a, l, late ? ScanResult.VerifiedLate : ScanResult.Verified, l.VerifiedAt.Value);
        }

        void GiveBack(Loan l, Asset a, double hoursAfter)
        {
            l.Status = LoanStatus.Returned;
            l.ReturnedAt = l.CheckedOutAt.AddHours(hoursAfter);
            l.ReturnedByStudentId = l.StudentId; l.ReturnStationId = kiosk1.Id;
            a.Status = AssetStatus.InStorage; a.CurrentRoomId = a.HomeRoomId;
            Led(LedgerEvent.Return, l, l.ReturnedAt.Value, kiosk1.Id);
        }

        void GoOverdue(Loan l, Asset a)
        {
            l.Status = LoanStatus.Overdue; l.OverdueAt = l.DueAt;
            a.Status = AssetStatus.Overdue;
            Led(LedgerEvent.Overdue, l, l.DueAt);
        }

        void GoMissing(Loan l, Asset a)
        {
            GoOverdue(l, a);
            l.Status = LoanStatus.Missing; l.MissingAt = l.DueAt.AddHours(24);
            a.Status = AssetStatus.Missing; a.CurrentRoomId = null;
            Led(LedgerEvent.MarkedMissing, l, l.MissingAt.Value);
        }

        Anomaly Anom(AnomalyType t, AnomalyStatus st, Asset a, Loan? l, Room? room, Station? station, double hoursAgo)
        {
            var an = new Anomaly
            {
                Id = Guid.NewGuid(), Type = t, Status = st, AssetId = a.Id, LoanId = l?.Id, RoomId = room?.Id, StationId = station?.Id, DetectedAt = Ago(hoursAgo),
            };
            if (st != AnomalyStatus.Open) { an.AcknowledgedByUserId = tech.Id; an.AcknowledgedAt = an.DetectedAt.AddMinutes(10); }
            if (st == AnomalyStatus.Resolved) { an.ResolvedByUserId = tech.Id; an.ResolvedAt = an.DetectedAt.AddMinutes(40); an.ResolutionNote = "Checked with the instructor; the item was where it should be."; }
            db.Anomalies.Add(an);
            return an;
        }

        NonComplianceMark Mark(Loan l, PenaltyReason r, bool cleared) => db.NonComplianceMarks.Add(new NonComplianceMark
        {
            Id = Guid.NewGuid(), StudentId = l.StudentId!.Value, LoanId = l.Id, Reason = r,
            Status = cleared ? PenaltyStatus.Cleared : PenaltyStatus.Active, IssuedAt = l.DueAt.AddHours(1),
            ClearedByUserId = cleared ? custodian.Id : null, ClearedAt = cleared ? now.AddDays(-1) : null, ClearReason = cleared ? "Settled at the counter." : null,
        }).Entity;

        void Hold(Loan l, bool lifted) => db.ProfileHolds.Add(new ProfileHold
        {
            Id = Guid.NewGuid(), StudentId = l.StudentId!.Value, LoanId = l.Id, Status = lifted ? PenaltyStatus.Cleared : PenaltyStatus.Active,
            PlacedAt = l.DueAt.AddHours(1), LiftedByUserId = lifted ? custodian.Id : null, LiftedAt = lifted ? now.AddDays(-1) : null,
            Reason = lifted ? "Liability settled." : "Unsettled liability.",
        });

        void Freeze(Loan l, User instructor, PenaltyReason r, bool lifted) => db.ApprovalFreezes.Add(new ApprovalFreeze
        {
            Id = Guid.NewGuid(), InstructorUserId = instructor.Id, LoanId = l.Id, Reason = r, Status = lifted ? PenaltyStatus.Cleared : PenaltyStatus.Active,
            FrozenAt = l.DueAt.AddHours(1), LiftedByUserId = lifted ? custodian.Id : null, LiftedAt = lifted ? now.AddDays(-1) : null,
            LiftNote = lifted ? "Liability settled." : null,
        });

        var receiptSeq = 100;
        void Liab(Loan l, Asset a, LiabilityReason reason, decimal amount, LiabilityStatus st, Guid? liableUser = null, string? liableName = null, string? note = null)
        {
            var settled = st != LiabilityStatus.Outstanding;
            db.Liabilities.Add(new Liability
            {
                Id = Guid.NewGuid(), StudentId = liableUser is null && liableName is null ? l.StudentId : null, LiableUserId = liableUser, LiableName = liableName,
                LoanId = l.Id, AssetId = a.Id, Amount = amount, Reason = reason, Status = st,
                ReceiptNo = st == LiabilityStatus.SettledPayment ? $"OR-{now.Year}-{++receiptSeq:D4}" : null,
                SettledByUserId = settled ? custodian.Id : null, SettledAt = settled ? now.AddDays(-1) : null, Notes = note,
            });
            if (settled) Led(LedgerEvent.Settled, l, now.AddDays(-1), actor: custodian.Id);
        }

        void Note(User to, string type, string title, string body, Loan? l, bool read) => db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(), RecipientUserId = to.Id, Type = type, Title = title, Body = body, LoanId = l?.Id, StudentId = l?.StudentId,
            CreatedAt = now.AddMinutes(-30), DeliveredAt = now.AddMinutes(-30), ReadAt = read ? now.AddMinutes(-10) : null,
        });

        // ---------------------------------------------------------------- open loans
        // In transit (2): left the counter, not yet seen by the room reader.
        // The transit window is stretched to 12 hours so the sweeper does not turn them into anomalies mid-test.
        var l1 = Lend(X["LT-005"], S[0], ins1, lab1, 0.1); l1.TransitDeadlineAt = now.AddHours(12);
        var l2 = Lend(X["LT-006"], S[1], ins2, lab2, 0.15); l2.TransitDeadlineAt = now.AddHours(12);

        // In room, verified (2).
        var l3 = Lend(X["LT-007"], S[2], ins1, lab1, 0.7); Verify(l3, X["LT-007"], reader101, lab1, 8);
        var l4 = Lend(X["LT-008"], S[3], ins3, lab2, 0.8); Verify(l4, X["LT-008"], reader102, lab2, 9);

        // Location anomaly (2): one in the wrong room, one that never arrived.
        var l5 = Lend(X["PRJ-001"], S[4], ins2, lab1, 1.0);
        l5.CustodyState = CustodyState.Anomaly; X["PRJ-001"].Status = AssetStatus.LocationAnomaly; X["PRJ-001"].CurrentRoomId = lab3.Id;
        Scan(reader103, lab3, X["PRJ-001"], l5, ScanResult.WrongRoom, l5.CheckedOutAt.AddMinutes(12));
        Anom(AnomalyType.WrongRoom, AnomalyStatus.Open, X["PRJ-001"], l5, lab3, reader103, 0.8);
        var l6 = Lend(X["PRJ-002"], S[5], ins1, lab2, 1.2);
        l6.CustodyState = CustodyState.Anomaly; X["PRJ-002"].Status = AssetStatus.LocationAnomaly;
        Anom(AnomalyType.TransitTimeout, AnomalyStatus.Open, X["PRJ-002"], l6, lab2, reader102, 1.0);

        // Overdue (2): each carries an acknowledged transit-timeout from before it was found.
        var l7 = Lend(X["LT-009"], S[6], ins2, lab1, 5);
        Verify(l7, X["LT-009"], reader101, lab1, 12); GoOverdue(l7, X["LT-009"]);
        Anom(AnomalyType.TransitTimeout, AnomalyStatus.Acknowledged, X["LT-009"], l7, lab1, reader101, 4.8);
        Mark(l7, PenaltyReason.Overdue, false); Freeze(l7, ins2, PenaltyReason.Overdue, false); Standing(S[6], StudentStanding.Warned, marks: 1);
        var l8 = Lend(X["LT-010"], S[7], ins3, lab2, 6);
        Verify(l8, X["LT-010"], reader102, lab2, 11); GoOverdue(l8, X["LT-010"]);
        Anom(AnomalyType.TransitTimeout, AnomalyStatus.Acknowledged, X["LT-010"], l8, lab2, reader102, 5.8);
        Mark(l8, PenaltyReason.Overdue, false); Freeze(l8, ins3, PenaltyReason.Overdue, false); Standing(S[7], StudentStanding.Warned, marks: 1);

        // ---------------------------------------------------------------- missing, with liabilities
        var l9 = Lend(X["LT-011"], S[8], ins3, lab1, 72); Verify(l9, X["LT-011"], reader101, lab1, 9); GoMissing(l9, X["LT-011"]);
        Mark(l9, PenaltyReason.Missing, false); Hold(l9, false); Freeze(l9, ins3, PenaltyReason.Missing, false);
        Liab(l9, X["LT-011"], LiabilityReason.Missing, X["LT-011"].ReplacementCost, LiabilityStatus.Outstanding);
        Standing(S[8], StudentStanding.Blocked, marks: 1, hold: true);
        var l10 = Lend(X["PRJ-003"], S[9], ins2, lab2, 80); Verify(l10, X["PRJ-003"], reader102, lab2, 11); GoMissing(l10, X["PRJ-003"]);
        Mark(l10, PenaltyReason.Missing, false); Hold(l10, false); Freeze(l10, ins2, PenaltyReason.Missing, false);
        Liab(l10, X["PRJ-003"], LiabilityReason.Missing, X["PRJ-003"].ReplacementCost, LiabilityStatus.Outstanding);
        Standing(S[9], StudentStanding.Blocked, marks: 1, hold: true);

        // Missing, then settled (2): one paid, one replaced. Penalties are cleared and the asset is retired.
        var l11 = Lend(X["EC-003"], S[10], ins1, lab1, 240); GoMissing(l11, X["EC-003"]);
        Mark(l11, PenaltyReason.Missing, true); Hold(l11, true); Freeze(l11, ins1, PenaltyReason.Missing, true);
        Liab(l11, X["EC-003"], LiabilityReason.Missing, 1200m, LiabilityStatus.SettledPayment);
        X["EC-003"].Status = AssetStatus.Retired;
        var l12 = Lend(X["EC-004"], S[11], ins1, lab2, 260); GoMissing(l12, X["EC-004"]);
        Mark(l12, PenaltyReason.Missing, true); Hold(l12, true); Freeze(l12, ins1, PenaltyReason.Missing, true);
        Liab(l12, X["EC-004"], LiabilityReason.Missing, 1200m, LiabilityStatus.SettledReplacement, note: "Student brought a replacement cord.");
        X["EC-004"].Status = AssetStatus.Retired;

        // ---------------------------------------------------------------- returned loans
        Loan Returned(string code, int student, User approver, Room dest, double daysAgo, double hoursOut = 1.5)
        {
            var l = Lend(X[code], S[student], approver, dest, daysAgo * 24);
            Verify(l, X[code], dest == lab1 ? reader101 : reader102, dest, 7);
            GiveBack(l, X[code], hoursOut);
            return l;
        }
        Returned("LT-013", 12, ins1, lab1, 2);
        Returned("PRJ-004", 13, ins2, lab2, 2.5);
        Returned("EC-001", 14, ins3, lab1, 3);
        Returned("CAM-002", 15, ins1, lab2, 3.5);

        // Late arrival warnings (2): one still active, one cleared by staff.
        var l17 = Lend(X["PRJ-005"], S[16], ins2, lab1, 100);
        Verify(l17, X["PRJ-005"], reader101, lab1, 22, late: true); GiveBack(l17, X["PRJ-005"], 2);
        var an17 = Anom(AnomalyType.TransitTimeout, AnomalyStatus.Resolved, X["PRJ-005"], l17, lab1, reader101, 99.8);
        db.LateArrivalWarnings.Add(new LateArrivalWarning
        {
            Id = Guid.NewGuid(), StudentId = S[16].Id, LoanId = l17.Id, AnomalyId = an17.Id, WarningNo = 1, SchoolTerm = "2026-1T", SchoolYear = "2026-2027",
            IssuedAt = l17.VerifiedAt!.Value,
        });
        Standing(S[16], StudentStanding.Warned, warnings: 1);
        var l18 = Lend(X["LT-014"], S[17], ins3, lab2, 120);
        Verify(l18, X["LT-014"], reader102, lab2, 24, late: true); GiveBack(l18, X["LT-014"], 2);
        Scan(reader103, lab3, X["LT-014"], l18, ScanResult.WrongRoom, l18.CheckedOutAt.AddMinutes(14));
        var an18 = Anom(AnomalyType.WrongRoom, AnomalyStatus.Resolved, X["LT-014"], l18, lab3, reader103, 119.8);
        db.LateArrivalWarnings.Add(new LateArrivalWarning
        {
            Id = Guid.NewGuid(), StudentId = S[17].Id, LoanId = l18.Id, AnomalyId = an18.Id, WarningNo = 1, SchoolTerm = "2026-1T", SchoolYear = "2026-2027",
            Status = PenaltyStatus.Cleared, IssuedAt = l18.VerifiedAt!.Value, ClearedAt = now.AddDays(-1), ClearedByUserId = tech.Id, ClearReason = "Student had a note from the instructor.",
        });

        // Returned damaged (4): outstanding, paid, replaced, waived.
        var l19 = Returned("LT-012", 18, ins1, lab1, 4);
        Liab(l19, X["LT-012"], LiabilityReason.Damage, 12000m, LiabilityStatus.Outstanding, note: "Cracked screen.");
        Hold(l19, false); Standing(S[18], StudentStanding.Blocked, hold: true);
        var l20 = Returned("CAM-001", 19, ins2, lab2, 5);
        Liab(l20, X["CAM-001"], LiabilityReason.Damage, 6000m, LiabilityStatus.SettledPayment, note: "Lens repair.");
        var l21 = Returned("CAM-003", 20, ins3, lab1, 6);
        Liab(l21, X["CAM-003"], LiabilityReason.Damage, 2500m, LiabilityStatus.SettledReplacement, note: "Student replaced the strap and cap.");
        var l22 = Returned("EC-002", 21, ins1, lab2, 7);
        Liab(l22, X["EC-002"], LiabilityReason.Missing, 1200m, LiabilityStatus.Waived, note: "Found in the lab the next day.");
        var l23 = Returned("PRJ-004", 22, ins2, lab1, 8);
        Liab(l23, X["PRJ-004"], LiabilityReason.Damage, 3000m, LiabilityStatus.Waived, note: "Accidental; waived by the head.");
        // Returned loans leave the asset in its after-state, so restore the ones that are still being repaired.
        Set("LT-012", AssetStatus.Maintenance, AssetCondition.Damaged);
        Set("CAM-001", AssetStatus.Maintenance, AssetCondition.Damaged);
        Set("LT-014", AssetStatus.InStorage, AssetCondition.Fair);
        Set("CAM-003", AssetStatus.InStorage, AssetCondition.Fair);

        // Cancelled (2): lent by mistake and voided.
        foreach (var (code, student, approver, dest) in new[] { ("LT-013", 23, ins1, lab1), ("PRJ-005", 0, ins2, lab2) })
        {
            var l = Lend(X[code], S[student], approver, dest, 30);
            l.Status = LoanStatus.Cancelled; l.Notes = "Voided: wrong asset scanned.";
            X[code].Status = AssetStatus.InStorage; X[code].CurrentRoomId = X[code].HomeRoomId;
            Led(LedgerEvent.Voided, l, l.CheckedOutAt.AddMinutes(5), kiosk1.Id, tech.Id, "{\"reason\":\"Wrong asset scanned\"}");
        }

        // ---------------------------------------------------------------- unborrowed taps and unknown tags
        Scan(reader101, lab1, X["EC-001"], NoLoan, ScanResult.NotBorrowed, Ago(2));
        Anom(AnomalyType.UnborrowedTap, AnomalyStatus.Open, X["EC-001"], NoLoan, lab1, reader101, 2);
        Scan(reader102, lab2, X["EC-002"], NoLoan, ScanResult.NotBorrowed, Ago(3));
        Anom(AnomalyType.UnborrowedTap, AnomalyStatus.Open, X["EC-002"], NoLoan, lab2, reader102, 3);
        foreach (var uid in new[] { "FEEDFFFF0001", "FEEDFFFF0002" })
            db.RoomScans.Add(new RoomScan { StationId = reader101.Id, RoomId = lab1.Id, TagUid = uid, Result = ScanResult.UnknownTag, ScannedAt = Ago(4) });

        // ---------------------------------------------------------------- pending approvals and the other session outcomes
        void Pending(Station kiosk, Asset a, Student s, User ins, Room dest)
        {
            var sid = Guid.NewGuid();
            db.CheckoutSessions.Add(new CheckoutSession
            {
                Id = sid, StationId = kiosk.Id, Type = SessionType.Checkout, Status = SessionStatus.AwaitingApproval, InstructorUserId = ins.Id,
                StudentId = s.Id, AssetId = a.Id, DestinationRoomId = dest.Id, ClassLengthMinutes = 120, ExpiresAt = now.AddDays(2), CreatedAt = now.AddMinutes(-2),
            });
            db.CheckoutSessionItems.Add(new CheckoutSessionItem { Id = Guid.NewGuid(), SessionId = sid, AssetId = a.Id, CreatedAt = now.AddMinutes(-2) });
            db.Approvals.Add(new Approval
            {
                Id = Guid.NewGuid(), SessionId = sid, InstructorUserId = ins.Id, DestinationRoomId = dest.Id, ClassLengthMinutes = 120, RequestedAt = now.AddMinutes(-1),
            });
            a.Status = AssetStatus.AtCounter; a.CurrentRoomId = a.HomeRoomId;
            Note(ins, "approval.requested", "Approval requested", $"{s.FirstName} {s.LastName} wants {a.Name} for {dest.Code}.", NoLoan, false);
        }
        Pending(kiosk2, X["LT-003"], S[2], ins1, lab1);
        Pending(kiosk3, X["LT-004"], S[3], ins2, lab2);

        void Closed(SessionStatus st, Asset? a, Student? s, User? ins, ApprovalDecision? decision, string? failure, double hoursAgo)
        {
            var at = Ago(hoursAgo);
            var sid = Guid.NewGuid();
            db.CheckoutSessions.Add(new CheckoutSession
            {
                Id = sid, StationId = kiosk1.Id, Type = SessionType.Checkout, Status = st, InstructorUserId = ins?.Id, StudentId = s?.Id, AssetId = a?.Id,
                DestinationRoomId = decision is null ? null : lab1.Id, ClassLengthMinutes = decision is null ? null : (short)90, FailureCode = failure,
                FingerprintAttempts = (short)(failure is null ? 1 : 3), ExpiresAt = at.AddMinutes(5), CreatedAt = at, CompletedAt = at.AddMinutes(2),
            });
            if (decision is { } d && ins is not null)
                db.Approvals.Add(new Approval
                {
                    Id = Guid.NewGuid(), SessionId = sid, InstructorUserId = ins.Id, Decision = d, DestinationRoomId = lab1.Id, ClassLengthMinutes = 90,
                    RequestedAt = at, DecidedAt = d == ApprovalDecision.Expired ? at.AddMinutes(5) : at.AddMinutes(1), DecidedDevice = d == ApprovalDecision.Declined ? "seed" : null,
                });
        }
        Closed(SessionStatus.Declined, X["LT-013"], S[4], ins1, ApprovalDecision.Declined, null, 26);
        Closed(SessionStatus.Declined, X["PRJ-004"], S[5], ins2, ApprovalDecision.Declined, null, 27);
        Closed(SessionStatus.Expired, X["LT-013"], S[6], ins3, ApprovalDecision.Expired, null, 28);
        Closed(SessionStatus.Expired, X["EC-001"], S[7], ins1, ApprovalDecision.Expired, null, 29);
        Closed(SessionStatus.Cancelled, X["CAM-002"], S[8], null, null, null, 30);
        Closed(SessionStatus.Cancelled, X["PRJ-005"], S[9], null, null, null, 31);
        Closed(SessionStatus.Failed, null, null, null, null, "FINGERPRINT_NO_MATCH", 32);
        Closed(SessionStatus.Failed, null, null, null, null, "FINGERPRINT_NO_MATCH", 33);

        // ---------------------------------------------------------------- events and event loans
        Event Evt(string title, string venue, Room room, double startHours, double lengthHours, User coordinator, EventStatus status) => db.Events.Add(new Event
        {
            Id = Guid.NewGuid(), Title = title, VenueText = venue, VenueRoomId = room.Id, StartsAt = now.AddHours(startHours), EndsAt = now.AddHours(startHours + lengthHours),
            CoordinatorUserId = coordinator.Id, Status = status, CreatedByUserId = admin.Id, CreatedAt = now.AddDays(-14), UpdatedAt = now.AddDays(-14),
        }).Entity;
        Evt("Freshman Orientation", "Lecture Hall 1", lect, 72, 4, ins1, EventStatus.Scheduled);
        Evt("IT Week Opening", "AV Room", avr, 168, 5, ins3, EventStatus.Scheduled);
        var eActive1 = Evt("Programming Contest", "Computer Lab 3", lab3, -2, 6, ins1, EventStatus.Active);
        var eActive2 = Evt("Robotics Workshop", "AV Room", avr, -1, 6, ins3, EventStatus.Active);
        var eClosed1 = Evt("Intramurals Setup", "AV Room", avr, -240, 8, ins1, EventStatus.Closed);
        var eClosed2 = Evt("Career Fair", "AV Room", avr, -144, 8, ins3, EventStatus.Closed);
        Evt("Foundation Day Concert", "AV Room", avr, 240, 6, ins1, EventStatus.Cancelled);
        Evt("Seminar on AI", "Lecture Hall 1", lect, -72, 3, ins3, EventStatus.Cancelled);

        Loan EventLoan(Asset a, Event e, User coordinator, string borrower, string idNo, Station? reader)
        {
            var sid = Guid.NewGuid();
            var at = e.StartsAt.AddMinutes(-20);
            db.CheckoutSessions.Add(new CheckoutSession
            {
                Id = sid, StationId = kiosk1.Id, Type = SessionType.EventBatch, Status = SessionStatus.Completed, InstructorUserId = coordinator.Id, AssetId = a.Id,
                EventId = e.Id, DestinationRoomId = e.VenueRoomId, ExpiresAt = at.AddMinutes(5), CreatedAt = at.AddMinutes(-2), CompletedAt = at,
            });
            var l = new Loan
            {
                Id = Guid.NewGuid(), LoanNo = $"L-{now.Year}-{++loanSeq:D6}", Type = LoanType.Event, AssetId = a.Id, CoordinatorUserId = coordinator.Id, EventId = e.Id,
                BorrowerName = borrower, BorrowerIdNo = idNo, SessionId = sid, CheckoutStationId = kiosk1.Id, DestinationRoomId = e.VenueRoomId,
                Status = LoanStatus.Active, CustodyState = CustodyState.AwaitingArrival, CheckedOutAt = at, TransitDeadlineAt = at.AddMinutes(15),
                ClassEndsAt = e.EndsAt, GraceMinutes = 15, DueAt = e.EndsAt,
            };
            db.Loans.Add(l);
            Led(LedgerEvent.Checkout, l, at, kiosk1.Id);
            a.Status = AssetStatus.InTransit; a.CurrentRoomId = null;
            if (reader is not null) Verify(l, a, reader, (e.VenueRoomId == lab3.Id ? lab3 : avr), 10);
            else l.TransitDeadlineAt = now.AddHours(12); // still on its way; keep the sweeper off it
            return l;
        }
        EventLoan(X["SPK-001"], eActive1, ins1, "Rico Dela Pena", "2024-1188", reader103);   // verified at the venue
        EventLoan(X["SPK-002"], eActive2, ins3, "Sheila Ong", "2023-0456", null);            // still on its way
        var el3 = EventLoan(X["SPK-003"], eClosed1, ins1, "Dario Lim", "2024-2210", readerAvr);
        GiveBack(el3, X["SPK-003"], 9);
        var el4 = EventLoan(X["SPK-004"], eClosed2, ins3, "Gina Tan", "2022-0871", readerAvr);
        GoMissing(el4, X["SPK-004"]);
        Liab(el4, X["SPK-004"], LiabilityReason.Missing, 9500m, LiabilityStatus.Outstanding, liableUser: ins3.Id);

        // ---------------------------------------------------------------- notifications
        Note(tech, "anomaly.raised", "Wrong room", "Projector #1 was read in LAB-103.", l5, false);
        Note(tech, "anomaly.raised", "Never arrived", "Projector #2 missed its transit window.", l6, false);
        Note(tech, "anomaly.resolved", "Anomaly resolved", "Projector #5 anomaly was resolved.", l17, true);
        Note(tech, "anomaly.resolved", "Anomaly resolved", "Laptop LT-014 anomaly was resolved.", l18, true);
        Note(ins2, "loan.overdue", "Loan overdue", $"{l7.LoanNo} is overdue.", l7, false);
        Note(ins3, "loan.overdue", "Loan overdue", $"{l8.LoanNo} is overdue.", l8, false);
        Note(ins1, "loan.created", "Loan created", $"{l3.LoanNo} was checked out.", l3, true);
        Note(ins3, "loan.created", "Loan created", $"{l4.LoanNo} was checked out.", l4, true);

        await db.SaveChangesAsync();

        // ---------------------------------------------------------------- inventory audits (QR scan on the phone)
        var all = await db.Assets.AsNoTracking().ToListAsync();
        Asset P(string code) => all.First(a => a.AssetCode == code);
        void SeedAudit(Room room, AuditStatus status, string[] matched, string[] misplaced, int unknown, double startedHoursAgo)
        {
            var roster = all.Where(a => a.HomeRoomId == room.Id && a.Status != AssetStatus.Retired).ToList();
            var au = new Audit
            {
                Id = Guid.NewGuid(), RoomId = room.Id, Source = AuditSource.Mobile, Status = status, StartedByUserId = tech.Id,
                StartedAt = Ago(startedHoursAgo), ExpectedCount = roster.Count,
            };
            db.Audits.Add(au);
            var seq = 0;
            void Item(Asset? a, string value, AuditResult r, DateTimeOffset? at = null) => db.AuditItems.Add(new AuditItem
            {
                Id = Guid.NewGuid(), AuditId = au.Id, AssetId = a?.Id, ScannedValue = value, Result = r, ClientScanId = Guid.NewGuid(), ScannedByUserId = tech.Id,
                ScannedAt = at ?? au.StartedAt.AddMinutes(++seq), SyncedAt = (at ?? au.StartedAt.AddMinutes(seq)).AddSeconds(20),
            });
            foreach (var c in matched) Item(P(c), QrCodes.Payload(P(c).Id), AuditResult.Matched);
            foreach (var c in misplaced) Item(P(c), QrCodes.Payload(P(c).Id), AuditResult.Misplaced);
            for (var i = 0; i < unknown; i++) Item(null, QrCodes.Payload(Guid.NewGuid()), AuditResult.Unknown);
            if (status == AuditStatus.InProgress) return;

            au.CompletedAt = au.StartedAt.AddMinutes(30);
            var unscanned = roster.Where(r => !matched.Contains(r.AssetCode)).ToList();
            foreach (var m in unscanned) Item(m, QrCodes.Payload(m.Id), AuditResult.Missing, au.CompletedAt);
            au.MatchedCount = roster.Count - unscanned.Count; au.MissingCount = unscanned.Count;
            au.MisplacedCount = misplaced.Length; au.UnknownCount = unknown;
            if (status == AuditStatus.SignedOff) { au.SignedOffByUserId = admin.Id; au.SignedOffAt = au.CompletedAt.Value.AddHours(1); }
        }
        SeedAudit(store2, AuditStatus.InProgress, ["PRJ-004", "PRJ-005", "CAM-002"], ["LT-001"], 1, 0.5);
        SeedAudit(lab1, AuditStatus.InProgress, ["PC-101-01", "PC-101-02", "PC-101-03"], [], 0, 0.3);
        SeedAudit(lab2, AuditStatus.Completed, ["PC-102-01", "PC-102-02", "MON-003"], ["MON-001"], 1, 20);
        SeedAudit(lab3, AuditStatus.Completed, ["PC-103-01", "PC-103-02"], ["MON-002"], 1, 22);
        SeedAudit(lab201, AuditStatus.SignedOff, ["NET-001", "NET-002"], [], 1, 72);
        SeedAudit(avr, AuditStatus.SignedOff, ["SPK-003"], [], 0, 96);

        await db.SaveChangesAsync();
        services.GetRequiredService<ILoggerFactory>().CreateLogger("DevSeed")
            .LogWarning("Seed:Scenarios loaded {Assets} assets, {Students} students and {Loans} loans of test data.",
                await db.Assets.CountAsync(), await db.Students.CountAsync(), await db.Loans.CountAsync());
    }
}
