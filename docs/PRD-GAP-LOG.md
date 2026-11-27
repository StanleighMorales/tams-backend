# PRD gap log: event flow and related changes

> **Reminder.** When all the features below are implemented, review this file, `docs/PRD-GAP-LOG.md` and `docs/FLOW.md` before the team meeting where hardware and web confirm the flow. Update the Status lines in the gap log first.

Started 2026-11-15, status updated 2026-11-17 (the PRD itself is not changed yet, so nothing is `PRD updated`). Purpose: a reference of the gaps the PRD has, what the team decided, what we built, and the exact PRD wording that should change. Update the **Status** lines as work lands. The full work list is the index at the top of `docs/PRD-GAPS.md`. E1 to E5 are detailed there; E6 to E8 are detailed in this file.

Status values: `open` (decision needed), `decided` (agreed, not built), `built`, `PRD updated`.

**Scope note.** Items marked "not in PRD" are additions. Per the team's rule to follow the PRD strictly, each needs team agreement and a PRD update before or with the code.

---

## Part 1: The five gaps in the event flow (F5)

What the PRD says today (FR-5.1 to 5.6, US-12): staff create an event; the coordinator taps their card during the active event; each item tap creates a loan for the coordinator and event, due at event end plus grace, with no fingerprint; bulk return closes loans when "Done" is pressed.

### Gap 1. A damaged item can be lent
- **Problem.** Neither the event flow nor normal checkout checks the item's condition. An item marked `DAMAGED` can be borrowed.
- **Decision (decided).** Refuse DAMAGED items at the kiosk in **both** flows, with a clear LCD message. Show the item's name and condition when it is tapped.
- **Kiosk buttons in the PRD.** The PRD gives the kiosk **two buttons: Confirm and Cancel** (F1 step 8; §6.2 step 6 "confirm or /cancel from the pushbuttons"; LCD line "OK/X"). In normal checkout, Confirm creates the loan and Cancel ends the session. The PRD defines no other button. In event batch the code uses Confirm as **Done** ("OK when done") and Cancel as **leave batch mode** (loans already made stay open).
- **Action.** E6: refuse DAMAGED (and say why on the LCD, e.g. "Item damaged / See lab staff"); show name and condition on the confirm and batch screens. Tell hardware (TAMS-76, 77, 78) about the new screen text.
- **PRD change.** Add to F1 step 4 and FR-5.3: "Denied if the item's condition is DAMAGED." Add a LCD state to §6.7.
- **Status.** built (merged 2026-11-17); PRD wording still to change.

### Gap 2. No confirmation or way to undo at the kiosk
- **Problem.** In batch mode an item becomes a loan the moment it is tapped, and the Cancel button cannot remove a wrongly tapped item. The borrower would have to ask staff.
- **Decision (decided).** No confirmation after every item (it would slow a batch). Instead the **Cancel button undoes the last tapped item**, so nobody needs staff for a wrong tap. If no items are left to undo, Cancel leaves batch mode. Staff can also remove an item from the web before the items leave.
- **Action.** E7: a "void loan" operation that cancels an event loan, returns the item to IN_STORAGE and writes a ledger entry and audit entry (the ledger is append-only, so nothing is deleted). Kiosk Cancel in batch mode calls it for the last loan of the session. Web removal calls the same operation and records who did it. Needs a design check against loan statuses.
- **PRD change.** FR-5.3 addition: "Cancel undoes the most recent item tap in batch mode."
- **Status.** built (merged 2026-11-17); PRD wording still to change.

### Gap 3. No borrower record
- **Problem, in plain words.** In a student loan the fingerprint identifies who borrowed. In an event loan the kiosk only knows the **coordinator's card**. If the coordinator hands items to someone else (a student or colleague), the system has no record of who really holds them.
- **Decision (decided).** Staff register the borrower in the web PWA before the items leave: full name and ID number, checked against a real ID. The system matches a registered student by name and ID, links the loan to them and flags any open problem (block, hold, overdue, missing, unsettled liability); staff can override only with a written reason, saved in the audit log. The same check also applies to normal students.
- **Action.** E1.
- **PRD change.** F5 and §7.3 loans: add `borrower_name`, `borrower_id_no`, `borrower_student_id`; add FR-5.7 for the flag and override.
- **Status.** built (merged 2026-11-17); PRD wording still to change.

### Gap 4. No event penalties
- **Problem.** The penalty ladder (F6) only covers students.
- **Decision (decided).**
  - The coordinator (the card that unlocked batch mode) is accountable: frozen while any event loan is overdue or missing; a frozen coordinator cannot start a new batch.
  - Missing items: the liability goes to the recorded borrower (a linked student or the typed name), or to the coordinator if none was recorded.
  - A linked student also gets the normal mark and hold on overdue.
  - The person who closes the batch is recorded in the audit log, not made liable (see "Closing the batch" below).
- **Action.** E1.
- **PRD change.** Add an "event loans" row to the penalty ladder in F6 and a rule to §4.1.
- **Status.** built (merged 2026-11-17); PRD wording still to change.

### Gap 5. "Done" has no meaning for lending
- **Problem.** In batch lending, "Done" only ends the session. Nothing is finalized, because every tap already created a loan.
- **Decision (decided).** Keep this behaviour and write it down: each tap creates the loan at once; Done (the kiosk's Confirm button, or Done in the web PWA) closes the batch session and shows the count and due time. Done does not create or change loans.
- **Action.** Documentation only, plus recording who pressed Done (kiosk: the coordinator's session; web: the staff user).
- **PRD change.** FR-5.3: add "Done ends the batch session; loans are created on each tap."
- **Status.** built (merged 2026-11-17); PRD wording still to change.

### Closing the batch (gap 4): decided
The person who presses Done is **recorded in the audit log, not made liable**. On the kiosk, Done is pressed in the coordinator's own session, so the coordinator is accountable anyway. If staff press Done in the web PWA, they are closing someone else's batch and are not liable for items they did not take. Liability stays with the coordinator and the recorded borrower.

---

## Part 1b: Student checkout gap (S1): one item per transaction

### Gap S1. A student can borrow only one item per transaction
- **Problem.** F1 step 4 takes a single item tag, and the session ends in one loan. FR-1.9 only caps the number of **active loans** per student (default 1). A student who needs an HDMI cable and an extension cord has to run two full checkouts (card, fingerprint, photo, approval, Confirm each), which is slow and defeats the goal of a fast counter. A second tap during a checkout is refused today.
- **Decision (decided).**
  1. A new setting, **max items per transaction**, default **1** (so the PRD flow is unchanged until staff change it). Staff configure it; it is not hard coded.
  2. When the limit is above 1, the student taps several items in one checkout. The instructor sees **every item** on the approval screen and approves or declines the whole transaction. The student's confirm screen lists them too.
  3. One fingerprint, one photo and one approval cover the whole transaction.
- **Rules.**
  - The effective limit for a transaction is the smaller of **max items per transaction** and what is left of **max active loans per student** (FR-1.9). If the active-loan cap is lower, raise it too; the denial message says which limit was hit.
  - Each item is checked on its own: known tag, borrowable, in storage, not damaged (E6). A repeated tag is refused ("already added").
  - Result is **one loan per item** (own loan number, ledger entry and room check-in), all sharing the same room, class length, due time and approval.
  - With the limit at 1 the session moves on right after the first tap, exactly as the PRD says. Above 1, the kiosk waits for more taps; **Confirm** ends the item step (as "OK when done" does in event batch) and moves on to the photo; reaching the limit moves on by itself; **Cancel** undoes the last item (same rule as gap 2, E7).
  - **Return** works the same way: after the fingerprint, the kiosk keeps taking item taps and Confirm means Done, so returning three items needs one fingerprint scan, not three.
- **Further decisions (agreed 2026-11-15, recommendations accepted).**
  - **S1-D1. Penalties per item or per transaction.** The PRD issues one Late Arrival Warning per **loan** (FR-6.1) and one Non-Compliance Mark per **overdue loan** (FR-6.4). A student who borrows three items and misses the 10 minutes would get three warnings, and three overdue items would give three marks (two marks block the student). Decided: **one warning and one mark per transaction**, however many items it holds, with the items listed on it. This needs a rule in §4.1 and F6.
  - **S1-D2. Who can change the setting.** Today only ADMIN can edit settings; technical staff can only read them. You asked that admins or technical managers can configure it. Decided: let TECH_STAFF edit this one setting; other settings stay admin only.
  - **S1-D3. Partial approval.** The instructor approves or declines the whole transaction (as written above). Declining single items is possible but changes the approval screen and the mobile app more. Decided: whole transaction only.
- **PRD change.** F1: step 4 repeatable up to the limit; FR-1.1 and FR-1.5 reworded for "items"; new FR-1.10 for the setting; §4.1 new row (max items per transaction, default 1); §6.2 step 3 may repeat; §6.7 new LCD states (item count, item list); US-01 reworded; F6 penalty counting per transaction.
- **Status.** built (merged 2026-11-17), including the kiosk item loop and LCD hints below; PRD wording still to change.

### Kiosk item loop (agreed: Option 1) and LCD text
Only when "max items per transaction" is above 1. With the limit at 1 the PRD flow is unchanged: the first tap goes straight to the photo.

1. After the fingerprint the LCD says how many items are allowed.
2. Each item tap adds the item and shows its name and condition, the count, and a **hint line: "Scan item or OK"**.
3. The student can scan another item, or press **Confirm (OK)** to continue to the photo. **Cancel (X)** undoes the last item; with no items left, Cancel cancels the checkout.
4. Reaching the limit continues to the photo automatically.
5. If a tap is refused (damaged, unknown, not available, already added) and the student already has at least one item, the LCD shows the reason and the checkout carries on. With no item yet, a refusal denies the borrow as the PRD says.
6. Confirm with no item added does nothing (the hint stays on screen).
7. Photo, instructor approval and the student's final Confirm follow as before; the confirm screen shows the item count.

Proposed LCD text (20 characters per line, 4 lines; hardware to confirm it fits):

| Screen | Line 1 | Line 2 | Line 3 | Line 4 |
|---|---|---|---|---|
| after fingerprint | `Hi, Paolo R.` | `Tap item tag` | `Up to 3 items` | `` |
| item added, more allowed | `Added LP-014` | `Laptop - GOOD` | `1 of 3   X=undo` | `Scan item or OK` |
| limit reached | `Added LP-016` | `3 of 3 items` | `Max reached` | `Next: photo` |
| item refused, items already added | `Item damaged` | `See lab staff` | `2 of 3   X=undo` | `Scan item or OK` |
| repeat tag | `Already added` | `LP-014` | `2 of 3   X=undo` | `Scan item or OK` |
| confirm, several items | `Paolo R.` | `3 items: LP-014 +2` | `To: CompLab 10` | `Due 3:15PM  OK/X` |
| return after item | `Returned LP-014` | `1 returned` | `Tap next item` | `OK when done` |

**E8 tasks (about 3 d backend, plus coordination)**
- [x] Setting `max_items_per_transaction` (default 1, range 1 to 10) in `SettingDefinitions`, readable by all staff roles, editable per S1-D2.
- [x] `POST /kiosk/sessions/{id}/asset` accepts repeated taps while the item count is below the effective limit, with per-item checks; a repeated tag returns an "already added" denial; `GET /kiosk/sessions/{id}` returns an `items` list and LCD lines with the count.
- [x] LCD lines follow the table above, including the "Scan item or OK" hint, built in `LcdJson` for each state.
- [x] `POST .../confirm` in the item step ends item collection and moves to the photo; reaching the limit does it automatically; `POST .../cancel` undoes the last item (shares the void rule with E7).
- [x] The approval request (`approval.requested`, `GET /approvals/pending` and the FCM push) carries an `items` array; the current single `asset` fields stay and hold the first item so the mobile app keeps working until it is updated.
- [x] Loan creation on the student's Confirm makes one loan per item with the shared approval, room, class length and due time, one ledger entry per item, and the transit timers.
- [x] Return session accepts several taps; Confirm is Done; LCD shows the count.
- [x] Penalty counting per transaction once S1-D1 is decided.
- [x] Tests: limit 1 behaves as before; limit 3 with 2 and 3 items; repeated tag; damaged item; interaction with the active-loan cap; approval payload; multi-item return.
- [x] Update `docs/SIMULATOR.md`, TEST-DATA.md and the kiosk README.
- [x] Tell the other teams: **hardware** (TAMS-76, 77, 78: item count and list on the LCD, Confirm ends item step), **mobile** (TAMS-35, 74: item list on the approval screen), **web** (kiosk confirmation view).

---

## Part 2: Other PRD changes found

| # | Change | Where | Status |
|---|---|---|---|
| 1 | Hold wording: staff lift holds after a return; FR-6.5 reads as automatic | FR-6.5, FR-6.8 | built (E2, docs and a test); PRD wording to change |
| 2 | Kiosk camera is a USB webcam on the counter computer, not an ESP32-CAM | F1 step 5, §6.2 step 4, §6.1, §6.9, §6.7 | open (team) |
| 3 | Damaged items: staff can assign a liability to the last borrower at inventory | F6, §7.3 liabilities | built (E5); PRD wording to change |
| 4 | Excel next to CSV for checklist export and import, and audit reports | FR-7.6, FR-7.7 | built (E3); PRD wording to confirm |
| 5 | Station credentials for room stations | F3, §6.2 | built (G2); PRD wording to change; hardware must load the key |
| 6 | Staff mobile push for anomalies, transit timeouts, stations offline | FR-4.3, FR-8.3 | built (E4); PRD wording to change |
| 7 | Fingerprint images: server-side SourceAFIS on the sensor image, image never stored; reword the letter, FR-1.3, NFR-11 and §6.2 | biometric request s5, FR-1.3, NFR-11 | built, option A (G1); threshold needs real captures; PRD and letter wording to change |
| 8 | Several items per student checkout, configurable (gap S1) | F1, FR-1.1, FR-1.5, FR-1.9 | built (E8); PRD wording to change |
| 9 | "Add card" at the kiosk: staff register an instructor card by tapping it on the counter kiosk; revoked UIDs can be issued again; users list their cards | F1 step 1, §6.2, §7.3 | built, see docs/CARD-ENROLLMENT.md (simulator has Add card and a scenario); PRD wording to change; kiosk firmware must add card enrollment mode |
| 10 | Admin reports: `GET /reports` (card list) and `GET /reports/{key}` (detail) over loans, anomalies, penalties, kiosk, stations, audits and events. The PRD has only the audit report | §6.3, FR-7.7 | built (E9, merged PR #39, TAMS-134 Done), see docs/REPORTS.md; PRD §6.3 and the web screens table updated |

## New tasks from this log

All tasks below were built and merged 2026-11-17 (E6 PR #15, E7 PR #20, E8 PR #23). Still to do for each: the PRD wording and telling the other teams.

**E6, kiosk condition check and display (0.5 d)**
- [x] Refuse DAMAGED in normal checkout and event batch with its own error code and LCD text.
- [x] Return the item's name and condition with every kiosk response so the LCD can show them.
- [x] Tests for both flows; update README "Kiosk and stations", SIMULATOR.md and TEST-DATA.md.

**E7, undo the last tapped item (1 d)**
- [x] Void operation for event loans (kiosk Cancel and web removal): loan cancelled, asset back to IN_STORAGE, ledger and audit entries, notices stay consistent.
- [x] Kiosk Cancel in batch mode undoes the last loan of the session; with none left it leaves batch mode.
- [x] Web removal: `POST /loans/{id}/void` for staff, with a reason, only while the loan is unopened (no room check-in yet).
- [x] Tests, including that the append-only ledger gets a new row and nothing is deleted.

## Jira drafts to add
- `[Backend] - Configurable items per transaction for student checkout and return` (E8)
- `[Backend] - Refuse damaged items at the kiosk and show item condition` (E6)
- `[Backend] - Undo the last tapped item in event batch and remove a wrongly lent item` (E7)
