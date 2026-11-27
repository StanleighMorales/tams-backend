# Admin reports (`/api/reports`)

Read-only report views for the web app's admin screens: a list of report cards, and a detail view for each. Nothing is
stored; every number is computed from the existing tables when the request arrives. The PRD does not list this
route (it only has the audit report under `/audits/{id}/report`), so it is a scope addition. See `PRD-GAP-LOG.md`, Part 2, row 10.

## Access

ADMIN only, like `GET /audit-log`. Every other role gets 403, and a missing token gets 401.

## The two routes

| Method | Route | Answer |
|---|---|---|
| GET | `/api/reports?from=&to=` | 200 `ReportList`: one card per report, in a fixed order, each with a headline stat. Use it for the reports landing screen. |
| GET | `/api/reports/{key}?from=&to=` | 200 `ReportDetail` for one report. 404 for an unknown `key`. Use it for the detail or quick-view screen. |

`from` and `to` are ISO 8601 timestamps with an offset (`2026-11-01T00:00:00%2B08:00`). Both are optional: `to` defaults to now and
`from` to 30 days before `to`. A `from` after `to` is 400 with a validation error on `from`.

### `ReportList`

```json
{
  "from": "2026-10-28T00:00:00+00:00",
  "to": "2026-11-27T00:00:00+00:00",
  "reports": [
    { "key": "loans", "title": "Loans", "description": "Checkout to return: ...", "headline": { "label": "Loans", "value": 42 } }
  ]
}
```

### `ReportDetail`

```json
{
  "key": "loans", "title": "Loans", "description": "...",
  "from": "...", "to": "...",
  "stats": [ { "label": "Loans", "value": 42 }, { "label": "Overdue", "value": 3 } ],
  "breakdowns": { "byStatus": [ { "label": "ACTIVE", "count": 10 } ] }
}
```

- `stats` is a list of `{ label, value }`. The first one is always the headline the list shows. Values are numbers; minutes and money are plain decimals.
- `breakdowns` maps a name to `{ label, count }` rows, largest first. Enum labels are the UPPER_SNAKE API values (`IN_TRANSIT`, `WRONG_ROOM`).
  Render a breakdown as a bar list or a small table; the names are listed below.

## The reports

| `key` | Period applies to | Headline | Stats besides the headline | Breakdowns |
|---|---|---|---|---|
| `loans` | loan `checkedOutAt` | Loans | Active, Overdue, Missing, Returned, Cancelled, average minutes to verify in room, average minutes to return | `byStatus`, `byType`, `byCustody`, `byRoom` (top 10 by destination room) |
| `anomalies` | `detectedAt` | Anomalies | Open, Acknowledged, Resolved, average minutes to resolve | `byStatus`, `byType` |
| `violations` | warning, mark, hold and freeze issue time; liabilities by their loan's checkout time | Warnings issued | Marks issued, Active holds, Active freezes, Outstanding liability amount | `marksByReason`, `freezesByReason`, `liabilitiesByStatus`, `liabilitiesByReason` |
| `kiosk` | session `createdAt` (enrollment sessions left out) | Sessions | Completed, Declined, Expired, Failed, Cancelled, average minutes to approve | `sessionsByStatus`, `sessionsByType`, `failureCodes`, `approvalsByDecision` |
| `stations` | not applicable: a snapshot of current health | Offline stations | Online, Warning, No heartbeat in the last hour | `byHealth`, `byType` |
| `audits` | audit `startedAt` | Audits | Completed, Signed off, and the Expected, Matched, Missing, Misplaced and Unknown totals of finished audits | `byStatus`, `bySource`, `unmatchedByRoom` (top 10) |
| `events` | event `startsAt` | Events | Scheduled, Active, Closed, Cancelled, Event loans | `byStatus` |

Notes for the web team:

- The stat **labels** are display text and may be reworded. Key your UI on `key` and on the position of the headline, not on a label.
- A report with no data in the period still returns 200 with zeros and empty breakdowns.
- `stations` ignores `from` and `to` (it echoes them). Station health is "now", the same data as `GET /stations/health`.
- Average minutes are 0 when nothing finished in the period, not null.
- `GET /reports` builds every report to read its headline, so call it once when the screen opens and use `GET /reports/{key}` for refreshes.

## Examples

```
GET /api/reports
GET /api/reports/loans?from=2026-11-01T00:00:00%2B08:00&to=2026-11-27T23:59:59%2B08:00
GET /api/reports/stations
```

## Adding a report later

Add a row to `Catalog` and a builder to the `switch` in `src/Tams.Api/Reports/ReportEndpoints.cs`. The list, the detail route and
the 404 follow from that. Add the key to `Keys` in `tests/Tams.Api.Tests/Integration/ReportTests.cs`.

## Tests

`ReportTests` runs every report against the real database (so each query is translated by EF) and checks:

- the card list, the 404 and 400 cases, and that each non-admin role gets 403;
- **numbers**: it inserts known anomaly and event rows in a one-day window in the far future (random per run, removed afterwards)
  and asserts the exact counts, the resolve-time average and the period filter (a row a day later is left out);
- an empty period returns zeros for every period-based report;
- loan totals equal the database count and the status breakdown adds up to the total;
- station health counts cover every station and ignore the period.

`RoleMatrixTests` also checks `/api/reports`. Jira: TAMS-134.
