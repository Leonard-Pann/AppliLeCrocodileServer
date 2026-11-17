# Docker deployment and recovery

## Windows deployment — 17 November 2026

- Docker Desktop's Linux engine built the image successfully; all 55 backend checks passed in the image build.
- The native API was stopped. A final SQLite snapshot was created at
  `D:\Program files\LeCrocodile\Backups\reviews-cutover-20261117-155221.db`, with SHA-256
  `258D9E3711AFF84710D7E535F67C125B9E8903DE956BEAF99CBA521FCC3BB6C4`.
- The snapshot and the imported `lecrocodile_reviews-data` database passed integrity and schema checks and contained
  one review. After a container restart, the review remained present and the HTTPS summaries endpoint returned it.
- `lecrocodile-reviews-1` runs as UID 1654 and publishes TCP 7443 on IPv4 and IPv6. Strict HTTPS health succeeded
  from Windows using the application's configured Ethernet IPv6 address. Phone access and writes remain to be verified.
- The deployment marker was created and `hosting.json` set to `docker`. The installed restart script's
  `-EnsureStarted` path succeeded and verified the served certificate. The certificate checked on this date expires
  on 24 November 2026 at 02:40:45 local time. Automatic renewal deployment and Windows logon startup remain unverified.
- Port publication binds all interfaces; existing firewall and router rules still determine external reachability.
  No router or firewall rules were changed during this deployment.

The following section records the earlier state and is retained as migration history.

## Verified state — 15 November 2026

- Docker Desktop 4.99.0, CLI 29.9.3 and Compose 5.7.0 installed. Microsoft WSL 3.0.5 repaired from its signed MSI.
- WSL and Virtual Machine Platform enabled without restarting. **Windows must restart before the Linux engine can run.**
  Docker Desktop reports `Virtual Machine Platform is not enabled` and `Engine stopped`.
- `docker compose config --quiet` succeeds. **The image has not been built, run or tested on Linux yet.**
- Native API still owns TCP 7443 and uses the original Windows SQLite database. No Docker import/cutover has occurred.
- Backend Release build and 55 checks pass; Desktop/Android Release builds and 101 application checks pass.
- Browser Release builds with a native-linking warning (`WasmBuildNative`/`RunAOTCompilation` are disabled); browser runtime
  rendering was not tested. iOS was not built or tested from this Windows host.
- Ethernet IPv6: `2001:861:5872:b2b0:492c:d813:fe01:4502`. Bbox and scoped Windows TCP 80/7443 rules now target Ethernet.
- Certificate: `D:\Program files\LeCrocodile\Certificates\reviews-ethernet.pfx`, empty password, expires
  **22 November 2026 at 11:25:20 Europe/Paris**. Old `reviews.pfx` retained for rollback.
- Strict HTTPS health, the existing ALASKA review aggregate and the served certificate thumbprint were checked locally.
  The user confirmed phone `/health` on Bbox Wi-Fi. In 5G it failed; test-ipv6.com reported no IPv6 address.
- Public HTTP-01 issuance succeeded for Ethernet. This proves the port 80 validation path at that time,
  not public API access on 7443. Independent Wi-Fi and phone review submission/readback remain untested.
- Bbox WAN IPv4 observed: `176.159.30.203`. IPv4 forwarding and an IPv4 certificate have not been configured or tested.

The image is designed for Linux with non-root `app`, a read-only root filesystem, dropped capabilities and a read-only
certificate mount. SQLite and backups use named volumes. These controls still need runtime verification on Linux.
The explicit Compose project name is `lecrocodile`; changing it can select different volumes.

## Build after restarting Windows

```powershell
docker version
docker info --format '{{.OSType}}'
docker compose version
docker compose config --quiet
docker compose --progress plain build
```

`version` must show Client and Server; `OSType` must be `linux`. A CLI installation is insufficient.
If PATH is stale, open a new terminal or use `$env:LOCALAPPDATA\Programs\DockerDesktop\resources\bin\docker.exe`.
The Dockerfile runs backend tests in its Linux SDK stage before publishing. A failed test fails the build.
The final image contains no SDK/test project. Build before stopping the native API.

`.env` is prepared locally and ignored by Git. Elsewhere, copy `.env.example` only when `.env` does not exist.
Set `CERT_DIRECTORY` to the absolute host directory and `PFX_FILENAME` to the actual file. Linux needs a Linux path.
The directory bind mount refuses a nonexistent host directory. Never commit private keys, databases or passwords.

## Backup and guarded import

Initial verified online SQLite backup (one review, schema 1, integrity OK):

`D:\Program files\LeCrocodile\Backups\reviews-before-docker-20261115-201453.db`

SHA-256: `CF493478C2788B466828520ED293E3FC5FB9D504C2AB3C171B3BFE25F2C1C7DD`.
A second backup was taken before changing the native certificate. These are same-disk backups; copy a verified snapshot
onto a separate protected device/location. Backup files contain review text and installation identifiers.

A standalone snapshot normalized to DELETE journal mode is also verified:
`D:\Program files\LeCrocodile\Backups\reviews-portable-20261115-204607.db` (one review, integrity OK), SHA-256
`258D9E3711AFF84710D7E535F67C125B9E8903DE956BEAF99CBA521FCC3BB6C4`.

At cutover, prevent new writes, stop only the identified native executable and any task that could restart it.
Keep its database and WAL/SHM intact. Create a final coherent snapshot with the rebuilt API, from the backend repository:

```powershell
$snapshot = 'D:\Program files\LeCrocodile\Backups\reviews-cutover-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.db'
dotnet .\LeCrocodile.Reviews.Api\bin\Release\net10.0\LeCrocodile.Reviews.Api.dll --backup $snapshot --Database:Path 'D:\Program files\LeCrocodile\Data\reviews.db' --contentRoot "$PWD/LeCrocodile.Reviews.Api"
if ($LASTEXITCODE -ne 0) { throw 'Backup failed; do not migrate.' }
Get-FileHash -LiteralPath $snapshot -Algorithm SHA256
```

Never copy just a live `.db`: committed writes may be in WAL. The backup API includes them and refuses overwrites.
Use the rebuilt API for this command: it normalizes snapshots to DELETE journal mode for read-only Docker import mounts.
From the backend repository, replace `FINAL-SNAPSHOT.db` below with the actual filename:

```powershell
docker compose run --rm --no-deps --volume 'D:/Program files/LeCrocodile/Backups:/import:ro' reviews --inspect /import/FINAL-SNAPSHOT.db
docker compose run --rm --no-deps --volume 'D:/Program files/LeCrocodile/Backups:/import:ro' reviews --import /import/FINAL-SNAPSHOT.db /data/reviews.db
docker compose run --rm --no-deps reviews --inspect /data/reviews.db
```

These commands open no listener and do not publish ports. Import validates integrity/schema, uses SQLite backup, and
atomically creates a new destination. It refuses existing files or leftover WAL/SHM. Run it only while the API is stopped.
It is not a merge: investigate an existing Docker database rather than deleting it to bypass the guard.
The import runs as `app`, giving the resulting file the runtime owner's permissions.

After verifying count and content, record snapshot/hash in
`D:\Program files\LeCrocodile\Deployment\docker-import-verified.txt` and change `Mode` to `docker` in `hosting.json`
if using the Windows lifecycle scripts. These changes may require administrator rights. Never create the marker early.

```powershell
Get-NetTCPConnection -State Listen -LocalPort 7443 -ErrorAction SilentlyContinue
docker compose up -d --no-build
docker compose ps
docker compose logs --tail 100 reviews
docker compose exec reviews id
docker compose exec reviews dotnet LeCrocodile.Reviews.Api.dll --inspect /data/reviews.db
curl.exe --fail --noproxy '*' 'https://[2001:861:5872:b2b0:492c:d813:fe01:4502]:7443/health'
```

Start only when 7443 is free. Verify non-root identity, mounts, imported review, host IPv6 publication and strict TLS.
Submit a labelled test review from a phone; reload it, restart Docker, and reload again. Record PC, Bbox Wi-Fi,
unrelated Wi-Fi and 5G results separately. A health response does not prove the write path.

Rollback before Docker accepts writes: `docker compose stop reviews`, restore `Mode=native`, and restart the native API
against its untouched database. After Docker accepts writes, first back it up and restore to a new Windows database path
with both instances stopped; reusing the old pre-migration database would lose new reviews. Never run two writers.

## Ongoing backups

```powershell
docker compose exec reviews dotnet LeCrocodile.Reviews.Api.dll --backup /backups/reviews-UNIQUE-TIMESTAMP.db
docker compose exec reviews dotnet LeCrocodile.Reviews.Api.dll --inspect /backups/reviews-UNIQUE-TIMESTAMP.db
docker compose cp reviews:/backups/reviews-UNIQUE-TIMESTAMP.db 'D:/Program files/LeCrocodile/Backups/'
```

Use a different filename and check exit codes. Copy off-host and rehearse restoration into a new volume.
`docker compose down` retains volumes. **Never use `down -v` or volume pruning on retained data.**
No recurring backup schedule has been installed; these commands are explicit operations.

## Certificate renewal and automatic startup

The existing elevated simple-acme renewal task is enabled and last succeeded. Its next observed run was 16 November
at 12:04:02 Europe/Paris. Wi-Fi and Ethernet renewals use different filenames, so they cannot overwrite one another.
Cancel the obsolete Wi-Fi renewal only after verifying the replacement and its deployment hook; do not revoke needlessly.

`Deployment/Restart-Reviews.ps1` supports native and Docker modes. It checks PFX expiry, restarts the selected server,
performs normal HTTPS validation and compares the served thumbprint with the PFX. No certificate bypass is used.
`Invoke-RenewalDeployment.ps1` lets elevated ACME invoke a task owned by the signed-in Docker user and waits for success.
Failures return nonzero to ACME. SYSTEM must not assume access to a per-user Docker Desktop engine.

Prepared task names: `LeCrocodile-ReloadCertificate` (on demand) and `LeCrocodile-Start` (user logon, 60-second delay).
Check the actual scheduler before treating them as installed: registration/ACL changes require approval and may be pending.
Script syntax and native HTTPS/thumbprint checking have passed; the Docker branch and renewal-triggered reload have not.
Configure simple-acme's successful issuance/renewal installation action with:

```text
--installation script --script "D:\Program files\LeCrocodile\Deployment\Invoke-RenewalDeployment.ps1"
```

Back up renewal configuration first; edit through simple-acme or recreate the same renewal ID with identical identifiers/store
and the new installation action. A directory mount does not itself reload Kestrel. Keep TCP 80 available for HTTP-01.
IP certificates last about six days: monitor renewal failures and expiry.

Enable Docker Desktop's `Start Docker Desktop when you sign in`. `restart: unless-stopped` works only after its engine starts;
it cannot boot Windows or log in a user. Explicitly stopped containers remain stopped. The user task needs an open session.
This is a test deployment, not an unattended boot-to-service Windows setup. Test reboot/login and renewal reload separately.

Certificates/backups should be accessible only to the operator, SYSTEM and Administrators, and deployment scripts writable
only by administrators/SYSTEM. Check actual ACLs; do not infer protection from the directory name. Protect parent directories
against replacement as well. Never print the PFX/private key or solve permission failures with world-writable permissions.

## Linux portability

On a real Linux host, build the same Dockerfile and run the same import, persistence, backup/restore and restart tests.
Set a Linux certificate directory and grant runtime `app` read/traverse access to it (verify image UID with `id`, expected
1654). Keep private keys non-world-readable and writable only by the ACME operator. Named volumes must be writable by `app`.
Check the case-sensitive `Data/cocktail-ids.json` path. Enable Docker at boot with systemd and test an actual reboot.
Use a successful-renewal deploy hook to restart Compose with an absolute project directory and run a strict HTTPS probe.
Docker socket access is administrative access. Verify firewall policy against Docker published-port rules; do not assume
UFW alone controls them. **No remote Linux host or local Linux container has yet been tested.**

## A stable URL usable from 5G

IPv6 literals require usable IPv6 on the client. A DNS AAAA record alone cannot solve an IPv4-only mobile network.
Recommended long-term: a hostname with IPv4 and IPv6 service on a Linux host, or an appropriately configured gateway/tunnel.
A home-hosted alternative is Bbox IPv4 forwarding plus a stable hostname and dynamic DNS if needed. Verify WAN address and
inbound ports first. The PC must remain powered on. Adding a proxy requires reviewing trusted forwarded headers and
per-client rate limiting. Rebuild and install the mobile app after URL changes; an installed bundled JSON does not update itself.

References: [Docker Windows setup](https://docs.docker.com/desktop/setup/install/windows-install/),
[Microsoft WSL](https://learn.microsoft.com/windows/wsl/install),
[ACME deployment scripts](https://simple-acme.com/reference/plugins/installation/script),
[IP certificates](https://letsencrypt.org/2026/01/15/6day-and-ip-general-availability).
