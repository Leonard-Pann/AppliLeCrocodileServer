# Docker deployment and recovery

The hosting provider owns the public DNS name and HTTPS certificate and forwards traffic to this backend over HTTP.
The API listens on IPv4 port 7443; Docker publishes it on the host IPv4 port 7443. Restrict access to this port to the
provider's proxy or a private network, because traffic to the backend is unencrypted. Configure the public HTTPS URL in
the mobile app and rebuild it if the URL differs from the existing one. The provider's public port may differ from 7443.

## Start on Linux

Install Docker Engine and the Compose plugin, enable Docker at boot, then run from this directory:

```sh
docker compose up -d --build
docker compose ps
curl --fail http://127.0.0.1:7443/health
```

Check the public HTTPS health URL separately through the provider and test review reads and writes. The restart policy
restarts the container after failures or host reboot once Docker is running. It does not replace monitoring or backups.
The API uses the proxy's TCP address for rate limiting; all clients sharing that address share five requests per second.
Forwarding headers are not trusted. Configure trusted proxy addresses before using client IP headers.

## Existing reviews and backups

A fresh deployment creates an empty database. To preserve existing reviews, stop writes to the old server and take a
coherent SQLite backup with the running API's `--backup` command. Do not copy a live database file alone: SQLite may have
committed data in its WAL. Transfer the snapshot securely, inspect it, and import it into an empty database volume while
the new API is stopped. The import refuses to overwrite an existing database. For example, with a snapshot at
`/path/to/reviews-snapshot.db`:

```sh
docker compose build
docker compose run --rm --no-deps -v /path/to/reviews-snapshot.db:/import/snapshot.db:ro reviews --inspect /import/snapshot.db
docker compose run --rm --no-deps -v /path/to/reviews-snapshot.db:/import/snapshot.db:ro reviews --import /import/snapshot.db /data/reviews.db
docker compose run --rm --no-deps reviews --inspect /data/reviews.db
docker compose up -d
```

Verify the review count and content before directing production traffic to the new server. Do not run two independent
writers during the migration. The Compose project name `lecrocodile` preserves the existing `reviews-data` and
`reviews-backups` volume names. Never use `docker compose down -v` or prune retained volumes.

Create unique backup filenames and copy verified backups to a separate protected location:

```sh
docker compose exec reviews dotnet LeCrocodile.Reviews.Api.dll --backup /backups/reviews-UNIQUE.db
docker compose exec reviews dotnet LeCrocodile.Reviews.Api.dll --inspect /backups/reviews-UNIQUE.db
docker compose cp reviews:/backups/reviews-UNIQUE.db ./reviews-UNIQUE.db
```

No recurring backup or external availability monitor is configured by this repository. Test restoration separately.
