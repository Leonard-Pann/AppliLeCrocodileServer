# Le Crocodile reviews API

ASP.NET Core API targeting **.NET 11**, with persistent **SQLite SQL** storage, direct Kestrel HTTPS hosting, and shared C#
contracts for the Avalonia app. Windows 11 can run the published server without Docker, IIS, or a database-server install.
The API has no dependency on the mobile application. The shared contracts target .NET 10 so the existing mobile projects
can reference them; .NET 11 can load that assembly.

## Delivered behavior

- One review per installation UUID and cocktail. PUT creates or atomically replaces that review; retries do not duplicate votes.
- Ratings are integers from 1 to 5. Nicknames contain 1–40 characters and comments 1–2000, after trimming.
- Only known cocktail IDs from `LeCrocodile.Reviews.Api/Data/cocktail-ids.json` are accepted. This file contains the current
  337 menu IDs, including apostrophes. Update it when adding menu cocktails; existing reviews are retained.
- Comments use descending immutable numeric IDs and keyset pagination. Rating counts and averages cover all reviews,
  independently of the requested page. A read transaction keeps each page consistent with its aggregate.
- SQLite uses WAL, pooled connections, parameterized SQL, database constraints, an index for comment paging, and a unique
  cocktail/installation constraint. Schema version 1 is created transactionally at startup. Newer schemas are refused.
- Five accepted requests per IP in **any rolling second**, with no waiting queue. All endpoints share the limit.
  Excess requests receive HTTP 429 and `Retry-After: 1`. Idle IP partitions are reclaimed by ASP.NET Core's limiter.
- A 16 KiB request body limit, request header timeout, structured problem responses, no-store responses, and HTTPS enforcement.
- Installation IDs are never returned in public reviews or written to application logs. UUIDs identify installations;
  they provide no authentication, account ownership, or protection against a script inventing new UUIDs.

There is no push subscription: the app reads current data when opening a cocktail, after sending a review, and on manual
refresh. An already-open page does not update automatically when somebody else submits a review.

## SDK and local development

As of 2026-10-31, this workspace uses the .NET 11 RC3 SDK pinned in `global.json`. Microsoft publishes it on the
[.NET 11 download page](https://dotnet.microsoft.com/en-us/download/dotnet/11.0). Upgrade the SDK/runtime to the final
.NET 11 release when available. `Microsoft.Data.Sqlite` 10.0.13 is compatible with this target.

The downloaded SDK is already available in this workspace under `.tools/dotnet`; it did not replace the system SDK.
From this backend directory in PowerShell:

```powershell
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.tools/cli'
$dotnetExecutable = Join-Path (Get-Location) '.tools/dotnet/dotnet.exe'
& $dotnetExecutable build
& $dotnetExecutable dev-certs https --trust
& $dotnetExecutable run --project LeCrocodile.Reviews.Api
```

The trust command is a local Desktop development step that prompts Windows to trust the development certificate.
It was not run automatically. The API listens on **https://localhost:7443** (bound to all interfaces by default).
Stop the console server with Ctrl+C. For loopback-only development, set
`$env:Kestrel__Endpoints__Https__Url = 'https://localhost:7443'` before starting it.

If .NET 11 is installed normally, use `dotnet` instead of the local executable. Builds also require .NET 10 reference
assemblies for the contracts; the SDK restores them when necessary. No .NET 10 runtime is required to run the published API.

## Connect the mobile app

Edit `../AppliLeCrocodile/AppliLeCrocodile/Assets/Configuration/reviews-api.json`:

```json
{
  "baseUrl": "https://reviews.example.com:7443/"
}
```

Rebuild the app after changing this bundled asset. Both repositories must remain sibling directories because the app
references `LeCrocodile.Reviews.Contracts`. The rest of the menu remains bundled locally.

For physical Android/iOS devices, use a DNS name reachable from the device and a valid server certificate for that name,
including the intermediate chain. The development localhost certificate does not work automatically on phones.
Use a publicly trusted certificate, or provision a development CA trusted by the device using the platform's normal
device-management tooling. The app never bypasses certificate validation. `localhost` on a phone refers to that phone,
not the Windows server. Permit the selected TCP port through Windows Firewall and configure DNS/router forwarding
where required by the deployment network. No firewall or router settings were changed automatically.

## Publish and run on Windows 11

Install the **ASP.NET Core Runtime 11 x64** corresponding to the build, or keep the local SDK installed. The base .NET
runtime alone does not include ASP.NET Core. A Windows x64 framework-dependent publication is already in `publish/`.
Regenerate it from this backend directory with:

```powershell
& $dotnetExecutable publish LeCrocodile.Reviews.Api -c Release -r win-x64 --self-contained false -o publish
```

Copy the contents of `publish/` to a durable installation directory. Set an absolute database path outside the binary
directory so future deployments cannot replace the data. Run from the directory containing the published files:

```powershell
Set-Location .\publish
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:Database__Path = 'C:\LeCrocodile\Data\reviews.db'
$env:Kestrel__Endpoints__Https__Url = 'https://0.0.0.0:7443'
$env:Kestrel__Certificates__Default__Path = 'C:\LeCrocodile\Certificates\reviews.pfx'
$env:Kestrel__Certificates__Default__Password = '<PFX password supplied securely>'
dotnet .\LeCrocodile.Reviews.Api.dll
```

If using the downloaded SDK rather than an installed runtime, invoke its absolute `dotnet.exe` path in the last command.
Do not commit a PFX password or certificate private key. Restrict directory permissions to the account running the server.
The account needs write access to the database directory, including the SQLite WAL/SHM sidecars. Certificate renewal is
an operator responsibility; restart the process after replacing a certificate to ensure it is loaded.

Settings come from `appsettings.json`, optional untracked `appsettings.Local.json`, environment variables, then command-line
arguments. Relative database paths resolve against the server content root. No HTTP listener is configured by default;
even a manually added HTTP listener rejects plaintext requests. A missing usable TLS certificate prevents HTTPS startup.

For automatic startup, create a Windows Task Scheduler task running the published executable with its installation
directory as **Start in**, the environment/configuration above, restart-on-failure, and a dedicated local account.
This avoids a Windows service dependency. Run one server process against a local database file; this design does not
provide multi-machine rate limiting or a shared network-filesystem database.

Direct Kestrel hosting uses the TCP peer's IP. Forwarded headers are deliberately ignored, so a client cannot bypass
the limit with `X-Forwarded-For`. If a reverse proxy is introduced later, add explicit trusted-proxy configuration before
changing that behavior; otherwise all requests behind that proxy share its IP limit.

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

The schema is managed with `PRAGMA user_version`; startup applies version 1 transactionally to a new database.
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
& $dotnetExecutable run --project LeCrocodile.Reviews.Tests
```

The dependency-free backend executable tests real SQLite persistence, upserts, concurrent writes, keyset pagination,
aggregate correctness, backups, input validation, limits, and a real local Kestrel HTTPS exchange and restart. Tests pin
only a temporary test certificate; they do not add it to a trust store. App regression checks run separately:

```powershell
dotnet run --project ..\AppliLeCrocodile\AppliLeCrocodile.Tests
```
