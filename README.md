# Le Crocodile reviews API

See [Docker deployment and recovery](DOCKER.md) for the current migration status, guarded database import,
Linux verification steps, certificate renewal and outstanding external-network tests.

ASP.NET Core API targeting **.NET 10**, with persistent **SQLite SQL** storage, direct Kestrel HTTPS hosting, and shared C#
contracts for the Avalonia app. Windows 11 can run the published server without Docker, IIS, or a database-server install.
The API has no dependency on the mobile application. The shared contracts target .NET 10 so the existing mobile projects
can reference them.

## Delivered behavior

- One review per installation UUID and cocktail. PUT creates or atomically replaces that review; retries do not duplicate votes.
- Ratings are integers from 1 to 5. Nicknames contain 1–40 characters and comments 1–2000, after trimming.
- Only known cocktail IDs from `LeCrocodile.Reviews.Api/Data/cocktail-ids.json` are accepted. This file contains the current
  337 menu IDs, including apostrophes. Update it when adding menu cocktails; existing reviews are retained.
- Comments use descending immutable numeric IDs and keyset pagination. Rating counts and averages cover all reviews,
  independently of the requested page. A read transaction keeps each page consistent with its aggregate.
- SQLite uses WAL, pooled connections, parameterized SQL, database constraints, an index for comment paging, and a unique
  cocktail/installation constraint. Schema version 2 is created transactionally at startup; version 1 is migrated transactionally. Newer schemas are refused.
- Five accepted requests per IP in **any rolling second**, with no waiting queue. All endpoints share the limit.
  Excess requests receive HTTP 429 and `Retry-After: 1`. Idle IP partitions are reclaimed by ASP.NET Core's limiter.
- A 16 KiB request body limit, request header timeout, structured problem responses, no-store responses, and HTTPS enforcement.
- Installation IDs are never returned in public reviews or written to application logs. UUIDs identify installations;
  they provide no authentication, account ownership, or protection against a script inventing new UUIDs.

There is no push subscription: the app reads current data when opening a cocktail, after sending a review, and on manual
refresh. An already-open page does not update automatically when somebody else submits a review.

## SDK and local development

The solution uses the installed .NET 10 SDK. `global.json` accepts stable .NET 10 feature bands (10.0.100 or newer).
It no longer requires the private .NET 11 RC SDK in `.tools/dotnet`. Visual Studio and the command line use the same SDK.

From this backend directory in PowerShell:

```powershell
dotnet --version
dotnet restore
dotnet build -c Release
dotnet run --project LeCrocodile.Reviews.Tests -c Release
dotnet dev-certs https --trust
dotnet run --project LeCrocodile.Reviews.Api
```

The trust command is only for local development. Test `https://localhost:7443/health` on the same computer.
For loopback-only development, set `$env:Kestrel__Endpoints__Https__Url = 'https://localhost:7443'`.
The default wildcard listener supports IPv4 and IPv6. Phones require a certificate valid for the actual server address.

## Connect the mobile app

Edit `../AppliLeCrocodile/AppliLeCrocodile/Assets/Configuration/reviews-api.json`:

```json
{
  "baseUrl": "https://reviews.example.com:7443/"
}
```

Rebuild the app after changing this bundled asset. Both repositories must remain sibling directories because the app
references `LeCrocodile.Reviews.Contracts`. The rest of the menu remains bundled locally.

For physical Android/iOS devices, use a DNS name or public IP reachable from the device and a valid server certificate for it,
including the intermediate chain. The development localhost certificate does not work automatically on phones.
Use a publicly trusted certificate, or provision a development CA trusted by the device using the platform's normal
device-management tooling. The app never bypasses certificate validation. `localhost` on a phone refers to that phone,
not the Windows server. Permit the selected TCP port through Windows Firewall and configure DNS/router forwarding
where required by the deployment network. No firewall or router settings were changed automatically.

### Direct public IPv6 deployment on a Bbox

Use the computer's global, non-temporary IPv6 from `ipconfig`, not the Bbox address, a temporary address, `fe80::`, or `::1`.
The bundled client URL uses brackets around the IPv6: `https://[PUBLIC_IPV6]:7443/`.
An IPv6-only URL requires IPv6 connectivity on every client network. An ISP prefix change requires a new certificate
and an application update. A non-temporary interface address does not guarantee a permanent ISP prefix.

1. Open the Bbox administration page at its local gateway address. In its IPv6 firewall/pinhole settings, allow inbound
   TCP 7443 to the computer's global IPv6. Allow TCP 80 to that same address for automated ACME HTTP validation.
   Menu names depend on the Bbox model. Do not disable the entire firewall. IPv6 uses firewall rules, not IPv4 NAT forwarding.
2. In an elevated Windows PowerShell, allow the same ports through Windows Firewall. Scope the rule to the server's
   global IPv6 and its network interface. Check `Get-NetConnectionProfile`: a Private-only rule will not match a Public network.
3. Request a publicly trusted certificate with an IP subject alternative name. A localhost development certificate
   is insufficient. Let's Encrypt supports IPv4 and IPv6 certificates through the `shortlived` profile, lasting about six days.
   See [IP certificate availability](https://letsencrypt.org/2026/01/15/6day-and-ip-general-availability).
4. The locally downloaded `.tools/simple-acme/wacs.exe` is simple-acme 2.4.1.2444. Its archive SHA256 was checked against
   the official download page. Copy its entire directory to a durable location such as `C:\LeCrocodile\Acme` before setting
   up renewal. Run elevated, use manual identifier input with the public IP, `shortlived`, HTTP-01 self-hosting validation,
   and PFX file storage. Use a stable output filename and keep the PFX password in local configuration only.
   Port 80 must be reachable externally for issuance and every renewal; it is separate from the HTTPS API on 7443.
   See [CLI options](https://simple-acme.com/reference/cli) and [PFX storage](https://simple-acme.com/reference/plugins/store/pfxfile).
5. Configure the published API with the certificate and an absolute database path as shown below. The wildcard HTTPS URL
   supports both IP families. Start it and test `/health` from a phone with Wi-Fi disabled, using the exact IPv6 URL.
6. Use Task Scheduler to start the API at boot with its deployment directory as the working directory, a dedicated account,
   no execution time limit, restart on failure, and no concurrent instances. Do not use `sc.exe create` with this console app:
   it does not implement the Windows service lifetime.
7. Enable simple-acme's renewal task and verify its next run. Configure a successful-renewal deployment action to restart
   the API task so Kestrel loads the new PFX. Keep the computer awake and connected. Verify renewal and certificate expiry
   monitoring before relying on the server: a manually issued certificate alone is not a complete deployment.

For IPv4 instead, reserve the computer's LAN IPv4 in DHCP and forward Bbox public TCP 7443 to LAN TCP 7443, plus public
TCP 80 to LAN TCP 80 for ACME. Use the Bbox WAN public IPv4 in the client and in the certificate. If the WAN address is
private/shared or inbound ports are unavailable, ask Bouygues about public IPv4 availability; a local firewall rule cannot
overcome carrier-grade NAT. Do not use `192.168.x.x` as the public mobile endpoint.

At verification time, the published executable returned `{"status":"Healthy"}` over localhost HTTPS, 44 backend checks
and 101 client checks passed. Public certificate issuance, Windows/Bbox firewall changes, and startup/renewal tasks
remain deployment steps; external Internet reachability has not been verified.

## Publish and run on Windows 11

Install the **ASP.NET Core Runtime 10 x64** corresponding to the build, or use the installed system SDK. The base .NET
runtime alone does not include ASP.NET Core. A Windows x64 framework-dependent publication is already in `publish/`.
Regenerate it from this backend directory with:

```powershell
dotnet publish LeCrocodile.Reviews.Api -c Release -r win-x64 --self-contained false -o publish
```

Copy the contents of `publish/` to a durable installation directory. Set an absolute database path outside the binary
directory so future deployments cannot replace the data. Run from the directory containing the published files:

```powershell
Set-Location .\publish
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:Database__Path = 'C:\LeCrocodile\Data\reviews.db'
$env:Kestrel__Endpoints__Https__Url = 'https://*:7443'
$env:Kestrel__Certificates__Default__Path = 'C:\LeCrocodile\Certificates\reviews.pfx'
$env:Kestrel__Certificates__Default__Password = '<PFX password supplied securely>'
dotnet .\LeCrocodile.Reviews.Api.dll
```

The .NET 10 SDK already includes the ASP.NET Core runtime needed on this computer.
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

The schema is managed with `PRAGMA user_version`; startup creates version 2 or migrates version 1 transactionally. Back up the database before upgrading; older servers cannot open version 2.
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
aggregate correctness, backups, input validation, limits, and a real local Kestrel HTTPS exchange and restart. Tests pin
only a temporary test certificate; they do not add it to a trust store. App regression checks run separately:

```powershell
dotnet run --project ..\AppliLeCrocodile\AppliLeCrocodile.Tests
```
