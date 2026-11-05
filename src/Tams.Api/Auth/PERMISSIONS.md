# Role and permission matrix (TAMS-39)

Source: PRD 3.1 (roles), 6.3 (routes), FR-5.x, FR-9.x, NFR-10, schema-v1-decisions #7/#8/#10.
The PRD names roles per screen, not per route, so the route mapping below is a judgment call.

Roles: A = ADMIN, T = TECH_STAFF, C = CUSTODIAN, I = INSTRUCTOR. EC = `IsEventCoordinator` flag
(claim `is_event_coordinator=true`). Routes omit the `/api` prefix. Default is deny.

| Routes | A | T | C | I | Policy |
| :-- | :-: | :-: | :-: | :-: | :-- |
| `/health`, `POST /auth/login`, `POST /auth/refresh`, openapi and scalar docs | anon | anon | anon | anon | AllowAnonymous, listed in `Policies.AnonymousRoutes` |
| `POST /auth/logout`, change-password, `GET /notifications`, `POST /notifications/{id}/read`, `GET /assets*` (read-only lookup) | x | x | x | x | AnyUser |
| `/users` (all), `PUT /settings`, restore, `GET /audit-log`, `GET /stations/health`, `POST /stations`, `POST /warnings/reset-term`, `POST /audits/import` | x | | | | AdminOnly |
| `GET /settings`, `GET /stations`, rooms, `/assets` writes + history + qr, `/students`, `/sections`, fingerprint enrollment, `/loans*`, `/anomalies*`, violations (standing, clear, settle, lift), `/events` CRUD and close | x | x | | | Staff |
| `GET /loans?approvedBy=me` (own approved loans; endpoint must force the filter to self) | x | x | | x | StaffOrInstructor |
| `GET /approvals/pending`, `POST /approvals/{id}/approve`, `/decline` | | | | x | InstructorOnly |
| Audits: `GET /rooms/{id}/roster`, `POST /audits`, `/scans:batch`, `/complete`, `GET /audits/{id}/report`, `GET /rooms/{id}/checklist:export` | x | x | x | | AuditStaff |
| Event batch lend / bulk return on web (FR-5.2 to 5.6) | x* | x* | | x* | EventCoordinator (* only with EC flag) |
| `/kiosk/*`, `POST /stations/{code}/scans`, `/heartbeat` | | | | | Device credential, not a user JWT (see below) |

Notes
- Admin is a superset of Staff (PRD: "Everything").
- Custodian is limited to audits plus read-only asset lookup.
- Row scoping (an instructor sees only loans they approved) belongs in the endpoint, not the policy.
- Kiosk and station routes are authenticated by station code/key, owned by the kiosk tickets. They must register
  their own scheme and `.RequireAuthorization(...)`; otherwise the fallback policy rejects them and the coverage
  test fails.
