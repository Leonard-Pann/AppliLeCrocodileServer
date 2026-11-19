# Le Crocodile reviews API

See [Docker deployment and recovery](DOCKER.md) for Linux deployment and database migration.

ASP.NET Core API targeting **.NET 10**, with persistent **SQLite SQL** storage, HTTP hosting behind a TLS-terminating reverse proxy, and shared C#
contracts for the Avalonia app. Windows 11 can run the published server without Docker, IIS, or a database-server install.
The API has no dependency on the mobile application. The shared contracts target .NET 10 so the existing mobile projects
can reference them.

## Delivered behavior

- One review per installation UUID and cocktail. PUT creates or atomically replaces that review; retries do not duplicate votes.
- Ratings are integers from 1 to 5. Nicknames contain 1–40 characters and comments 0–2000, after trimming.
- A review can include one optional alcohol vote per installation: 0 (low), 1 (medium), or 2 (strong).
  Omitting it when editing preserves the previous vote. The server maintains cached levels transactionally;
  cocktails without votes have no level. Means below 2/3 are low, below 4/3 are medium, and the rest are strong.
- Only known cocktail IDs from `LeCrocodile.Reviews.Api/Data/cocktail-ids.json` are accepted. This file contains the current
  337 menu IDs, including apostrophes. Update it when adding menu cocktails; existing reviews are retained.
- Comments use descending immutable numeric IDs and keyset pagination. Rating counts and averages cover all reviews,
  independently of the requested page. A read transaction keeps each page consistent with its aggregate.
- SQLite uses WAL, pooled connections, parameterized SQL, database constraints, an index for comment paging, and a unique
  cocktail/installation constraint. Schema version 3 is created or migrated transactionally at startup. Newer schemas are refused.
- `Compatibility:MinimumVersion` in `appsettings.json` sets the minimum mobile version as three numeric components.
  The app checks `GET /api/v1/compatibility` at startup and on return after one hour, and displays an optional update prompt.
- Five accepted requests per IP in **any rolling second**, with no waiting queue. All endpoints share the limit.
  Excess requests receive HTTP 429 and `Retry-After: 1`. Idle IP partitions are reclaimed by ASP.NET Core's limiter.
- A 16 KiB request body limit, request header timeout, structured problem responses, no-store responses, with HTTPS handled by the hosting provider.
- Installation IDs are never returned in public reviews or written to application logs. UUIDs identify installations;
  they provide no authentication, account ownership, or protection against a script inventing new UUIDs.

There is no push subscription: the app reads current data when opening a cocktail, after sending a review, and on manual
refresh. An already-open page does not update automatically when somebody else submits a review.

## Deployment and development

The API listens on HTTP IPv4 port 7443. The hosting provider must terminate HTTPS and forward requests to the backend.
Restrict direct access to port 7443 to the trusted proxy or private network: HTTP is not encrypted.
See [Docker deployment and recovery](DOCKER.md) for startup, migration and backup instructions.

For local development, run `dotnet run --project LeCrocodile.Reviews.Api` and request `http://127.0.0.1:7443/health`.
The .NET 10 SDK is required.

The mobile app bundles its API URL in `AppliLeCrocodile/Assets/Configuration/reviews-api.json`. Set it to the public HTTPS
URL provided by the host and rebuild the app if the URL changes. The provider handles DNS and the public TLS certificate.

The rate limiter uses the TCP peer's address and does not trust forwarding headers. When the proxy uses one address,
all users share the limit of five requests per second. Configure trusted proxy addresses explicitly before enabling
forwarded headers; accepting arbitrary client headers would allow rate-limit bypass.

## API contract

Every `/api/v1/cocktails` request requires a nonempty UUID in the `userId` query parameter. `/health` is operational and
does not require one. The API is public; HTTPS protects transport but the UUID is not a secret or login token.

| Method | Path | Result |
| --- | --- | --- |
| GET | `/health` | Process availability after database initialization |
| GET | `/api/v1/cocktails/review-summaries?userId={uuid}` | Array of global summaries for cocktails with reviews |
| GET | `/api/v1/cocktails/{id}/reviews?userId={uuid}&limit=20&before={id}` | Page, total count, global average, next cursor |
| PUT | `/api/v1/cocktails/{id}/reviews?userId={uuid}` | Persisted review, HTTP 200 for creation or replacement |

The `before` cursor is optional and exclusive. `limit` defaults to 20 and must be between 1 and 100. Pass `nextBefore`
from the previous response to retrieve older comments. A null cursor indicates the final page. New submissions do not
shift earlier comments between pages; updates preserve their original ordering and `createdAt`.

PUT JSON:

```json
{
  "userName": "Croco visitor",
  "rating": 5,
  "comment": "Excellent cocktail !"
}
```

Page JSON:

```json
{
  "items": [
    {
      "id": 42,
      "userName": "Croco visitor",
      "rating": 5,
      "comment": "Excellent cocktail !",
      "createdAt": "2026-10-31T12:00:00+00:00",
      "updatedAt": "2026-10-31T12:00:00+00:00",
      "isOwnReview": true
    }
  ],
  "totalCount": 1,
  "averageRating": 5,
  "nextBefore": null
}
```

Unknown cocktails return 404. Invalid UUIDs, ratings, names, comments, cursors, or JSON return 400; oversized requests
return 413. SQL contention exceeding the busy timeout returns 503 with Retry-After. Unexpected failures return generic
500 problem details; diagnostic exceptions stay in server logs. The mobile client keeps drafts on submission failure.

## Backups and maintenance

The schema is managed with `PRAGMA user_version`; startup creates version 3 or migrates versions 1 and 2 transactionally. Back up the database before upgrading; older servers cannot open version 3.
Future schema changes should add ordered transactional migrations rather than edit an existing database manually.

Use SQLite's online backup API, including when the main server is running. From the published directory, with the same
`Database__Path` setting as the server:

```powershell
dotnet .\LeCrocodile.Reviews.Api.dll --backup 'C:\LeCrocodile\Backups\reviews-2026-10-31.db'
```

This command exits after backup and never opens a listener. The destination must not already exist, preventing accidental
overwrites. Check for a successful exit before using the backup. To restore, stop the server, retain the old database and
sidecars elsewhere, place a backup at the configured database path with no leftover WAL/SHM sidecars, then restart.
Do not copy only the live `.db` file while writes are occurring: recent commits can still be in its WAL.

The application has no public moderation/delete endpoint. Operators can inspect or moderate stored comments through
a SQLite administration tool while following their own moderation and retention policy.

## Verification

```powershell
dotnet run --project LeCrocodile.Reviews.Tests
```

The dependency-free backend executable tests real SQLite persistence, upserts, concurrent writes, keyset pagination,
aggregate correctness, backups, input validation, limits, and a real local Kestrel HTTP exchange and restart. App regression checks run separately:

```powershell
dotnet run --project ..\AppliLeCrocodile\AppliLeCrocodile.Tests
```
