# Role and permission matrix (TAMS-39)

Source: PRD 3.1 (roles), 6.3 (routes), FR-5.x, FR-9.x, NFR-10, schema-v1-decisions #7/#8/#10.
This table lists the roles ACTUALLY applied in code for every mapped endpoint (read from the `Map*`
files). Routes that are not implemented yet (loans, approvals, audits, events, notifications, ...) are
not listed; add them here when they land. Default is deny (fallback policy requires a signed-in user).

Roles: A = ADMIN, T = TECH_STAFF, C = CUSTODIAN, I = INSTRUCTOR. Routes omit the `/api` prefix.

| Routes | A | T | C | I | Applied as |
| :-- | :-: | :-: | :-: | :-: | :-- |
| `GET /health`, `POST /auth/login`, `POST /auth/refresh`, `/openapi`, `/scalar` | anon | anon | anon | anon | AllowAnonymous |
| `POST /auth/logout`, `POST /auth/change-password` | x | x | x | x | any signed-in user |
| `GET /rooms`, `GET /rooms/{id}` | x | x | x | x | `Policies.AnyUser` (custodians pick audit rooms, instructors pick a destination) |
| `GET /assets`, `GET /assets/{id}`, `GET /assets/{id}/image`, `GET /assets/{id}/qr`, `POST /assets/qr-sheet` | x | x | x | | ADMIN, TECH_STAFF, CUSTODIAN |
| `GET /asset-categories`, `GET /asset-categories/{id}` | x | x | x | | ADMIN, TECH_STAFF, CUSTODIAN |
| `POST/PUT/DELETE /assets`, `/assets/{id}`, `POST/DELETE /assets/{id}/image`, `POST/PUT/DELETE /asset-categories` | x | x | | | ADMIN, TECH_STAFF |
| `GET /stations`, `GET /stations/{id}`, `GET /stations/health` | x | x | x | | ADMIN, TECH_STAFF, CUSTODIAN |
| `GET /auth-cards`, `GET /auth-cards/{id}` | x | x | x | | ADMIN, TECH_STAFF, CUSTODIAN |
| `POST/PUT/DELETE /rooms`, `/stations`; `POST/PUT /auth-cards`, `/auth-cards/{id}/revoke`, `/replace` | x | x | | | ADMIN, TECH_STAFF |
| `POST /{assets,rooms,stations,users,students}/{id}/restore` | x | | | | ADMIN only (decision #7) |
| `GET /students`, `GET /students/{id}`, `GET /sections`, `GET /sections/{id}` | x | x | x | | ADMIN, TECH_STAFF, CUSTODIAN |
| `PUT/DELETE /students/{id}`, `POST/PUT /sections`, `POST /students:import` | x | x | | | ADMIN, TECH_STAFF |
| `GET /users`, `GET /users/{id}` | x | x | | | ADMIN, TECH_STAFF (see Open decisions) |
| `POST/PUT/DELETE /users`, `/users/{id}/deactivate`, `/activate`, `/reset-password` | x | | | | ADMIN only |
| `GET /settings`, `GET /settings/{key}` | x | x | x | | ADMIN, TECH_STAFF, CUSTODIAN |
| `PUT /settings`, `PUT /settings/{key}` | x | | | | ADMIN only |
| `GET /audit-log` | x | | | | ADMIN only (decision #10; read-only, no write routes) |
| `POST /enrollment/sessions`, `GET /enrollment/sessions/{id}`, `POST .../cancel`, `GET/DELETE /students/{id}/fingerprints` | x | x | x | | ADMIN, TECH_STAFF, CUSTODIAN (see Open decisions) |
| `/kiosk/enrollment/sessions/*` | anon | anon | anon | anon | AllowAnonymous: the enrollment session id/token is the credential, not a user JWT |

`Policies.cs` also defines `Staff`, `AuditStaff`, `StaffOrInstructor`, `InstructorOnly` and `EventCoordinator`
policies for tickets not yet merged; no mapped endpoint uses them yet.

Notes
- Admin is a superset of Staff (PRD: "Everything").
- `?includeDeleted=true` on lists and `GET /{id}` for soft-deleted assets, rooms and stations is ADMIN and
  TECH_STAFF only; others get 403 on the list flag and 404 on a deleted id.
- `isEventCoordinator=true` is rejected (400 VALIDATION_FAILED) for CUSTODIAN users: the flag has no effect there.
- Row scoping (an instructor sees only loans they approved) belongs in the endpoint, not the policy.
- Deviation: the asset category collection is served at `/asset-categories` (the ticket says `/categories`).
  It is paged like the asset list (`page`, `pageSize` 1..100, `q`, `sort`, `{items,page,pageSize,total}`).

Open decisions (current behaviour kept; needs a product call)
- `/users` reads are ADMIN + TECH_STAFF; the earlier matrix said admin-only.
- Custodians may start enrollments and delete fingerprints; FR-9.4 says enrollment is admin-initiated.
- Kiosk enrollment routes are `/kiosk/enrollment/sessions/...`, not the PRD's shared `/api/kiosk/sessions/{id}/...`.
