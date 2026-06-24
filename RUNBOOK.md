# FCL Sync Runbook

## Configure

Create a local `.env` file from the example:

```bash
cp .env.example .env
```

Set the real values in `.env`:

```bash
GymMaster__SiteName=nextgen
GymMaster__ApiKey=your-gymmaster-api-key
GymMaster__PortalMembersUrl=https://nextgen.gymmasteronline.com/portal/api/v1/members?api_key=your-gymmaster-api-key
GymMaster__Webhooks__SecretToken=your-webhook-token

AccessProvider__Type=ZKBio
ZKBio__BaseUrl=https://your-zkbio-server
ZKBio__AccessToken=your-zkbio-token
ZKBio__AllowInvalidServerCertificate=true
Reconciliation__AccessProviderTimeoutMinutes=20
Reconciliation__FullAuditIntervalHours=24
Reconciliation__FullAuditOnStartup=true
Reconciliation__ZKBioCacheMaxAgeHours=24
```

`GymMaster__PortalMembersUrl` is optional if `GymMaster__SiteName` and `GymMaster__ApiKey` are set. The service will derive:

```text
https://{site}.gymmasteronline.com/portal/api/v1/members?api_key={api_key}
```

## Build

```bash
dotnet build Fcl.Sync.slnx
```

## Safe Fresh Sync Test

Use `Noop` so the service fetches GymMaster and writes SQLite without updating ZKBio:

```bash
AccessProvider__Type=Noop dotnet run --project src/Fcl.Sync.Service/Fcl.Sync.Service.csproj --no-launch-profile --urls http://127.0.0.1:5050
```

The service runs one reconciliation immediately at startup, then repeats every hour.

Stop it with `Ctrl+C` after the first sync completes.

## Dashboard

Open the local dashboard while the service is running:

```text
http://127.0.0.1:5050/dashboard
```

It refreshes every 30 seconds and shows:

- members currently stored in SQLite
- latest sync status, start/completion time, duration, and members checked
- sync mode: `Fast` or `FullAudit`
- latest access command outcomes: `Applied`, `Skipped`, `Failed`, or `Pending`
- recent sync runs
- recent integration errors

If `Latest Sync` shows `Running`, the service is actively working. If it shows `Failed`, check `Recent Integration Errors`.

`Pending Commands` should normally return to `0` after each run. If it grows and stays non-zero, the access-provider phase is not completing.

The same data is available as JSON:

```text
http://127.0.0.1:5050/api/dashboard
```

## Verify SQLite

The default database path is:

```text
src/Fcl.Sync.Service/bin/Debug/net10.0/data/fcl-sync.db
```

Check the member count:

```bash
sqlite3 src/Fcl.Sync.Service/bin/Debug/net10.0/data/fcl-sync.db 'select count(*) from members;'
```

Check the latest sync run:

```bash
sqlite3 -header -column src/Fcl.Sync.Service/bin/Debug/net10.0/data/fcl-sync.db \
  'select id, started_at, completed_at, members_checked from sync_runs order by id desc limit 1;'
```

Check latest access commands for that sync window:

```bash
sqlite3 src/Fcl.Sync.Service/bin/Debug/net10.0/data/fcl-sync.db \
  "select status, count(*) from access_commands where created_at >= (select started_at from sync_runs order by id desc limit 1) group by status order by status;"
```

For a successful safe sync, `members_checked` should equal `count(*) from members`, and the latest command count should match both.

Latest clean local verification:

```text
members: 10371
latest sync_run members_checked: 10371
latest sync access_commands Applied: 10371
```

## Reset Local DB

Stop the service first, then remove the SQLite files:

```bash
rm -f src/Fcl.Sync.Service/bin/Debug/net10.0/data/fcl-sync.db*
```

The service recreates the schema on the next run.

## Run With ZKBio

After the safe test passes, run with the real provider:

```bash
dotnet run --project src/Fcl.Sync.Service/Fcl.Sync.Service.csproj --no-launch-profile --urls http://0.0.0.0:5050
```

`AccessProvider__Type=ZKBio` should be set in `.env`.

The production sync order is:

```text
1. Fetch current members from GymMaster Portal API
2. Save/update members in SQLite
3. Compute desired access/profile fingerprints
4. Fast sync: skip members whose latest ZKBio cache fingerprint is still fresh
5. Full audit: bypass the cache shortcut and force ZKBio confirmation
6. Fetch matching people from ZKBio by PIN for members that need provider confirmation
7. Skip people that already match
8. Create/update missing or changed people through ZKBio API
9. Update the local ZKBio cache after successful provider confirmation
10. Record Applied, Skipped, or Failed command status in SQLite
```

The service does not delete ZKBio people.

There are two reconciliation modes:

```text
Fast      - hourly, uses local ZKBio cache fingerprints to avoid unnecessary ZKBio calls
FullAudit - slower confirmation pass, refreshes the ZKBio cache and guarantees freshness
```

The full audit cadence is controlled by:

```text
Reconciliation__FullAuditIntervalHours=24
Reconciliation__FullAuditOnStartup=true
Reconciliation__ZKBioCacheMaxAgeHours=24
```

The access-provider phase has a timeout controlled by:

```text
Reconciliation__AccessProviderTimeoutMinutes=20
```

If ZKBio hangs or is unreachable, the run is marked `Failed`, pending commands from that run are marked `Failed`, and the next hourly sync can try again.

Check latest command outcomes:

```bash
sqlite3 src/Fcl.Sync.Service/bin/Debug/net10.0/data/fcl-sync.db \
  "select status, count(*) from access_commands where created_at >= (select started_at from sync_runs order by id desc limit 1) group by status order by status;"
```

## Webhook Endpoint

GymMaster should post to:

```text
POST /webhooks/gymmaster
```

The request must include:

```text
X-Gymmaster-Token: your-webhook-token
```
