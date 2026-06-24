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

## Webhook Endpoint

GymMaster should post to:

```text
POST /webhooks/gymmaster
```

The request must include:

```text
X-Gymmaster-Token: your-webhook-token
```
