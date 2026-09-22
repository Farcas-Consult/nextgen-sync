# FCL Sync Runbook

## Configure

Create a local `.env` file from the example:

```bash
cp .env.example .env
```

Set the real values in `.env`:

```bash
ASPNETCORE_URLS=http://0.0.0.0:5050
Dashboard__AllowRemoteAccess=false

GymMaster__SiteName=nextgen
GymMaster__ApiKey=your-gymmaster-api-key
GymMaster__PortalMembersUrl=https://nextgen.gymmasteronline.com/portal/api/v1/members?api_key=your-gymmaster-api-key
GymMaster__Webhooks__SecretToken=your-webhook-token

AccessProvider__Type=ZKBio
ZKBio__BaseUrl=https://your-zkbio-server
ZKBio__AccessToken=your-zkbio-token
# Optional site scope. Omit to process all GymMaster companies.
# ZKBio__CompanyIds__0=4
ZKBio__AllowInvalidServerCertificate=true
BioStar__BaseUrl=https://your-biostar-server
BioStar__LoginId=your-login
BioStar__Password=your-password
BioStar__UserGroupId=1052
BioStar__AccessGroupId=3
BioStar__StartDateTime=2001-01-01T00:00:00Z
BioStar__ExpiryDateTime=2030-12-31T23:59:00Z
BioStar__AllowInvalidServerCertificate=true
BioStar__UseNumericUserIdWhenPossible=false
BioStar__MaxNameLength=48
BioStar__MaxEmailLength=128
BioStar__CompanyIds__0=3
Reconciliation__AccessProviderTimeoutMinutes=20
Reconciliation__FullAuditIntervalHours=24
Reconciliation__FullAuditOnStartup=true
Reconciliation__ProviderCacheMaxAgeHours=24
HistoryRetention__Enabled=true
HistoryRetention__CleanupIntervalHours=24
HistoryRetention__AccessCommandRetentionDays=0
HistoryRetention__KeepLatestAccessCommandPerPin=true
HistoryRetention__FailedAccessCommandRetentionDays=30
HistoryRetention__MaxFailedAccessCommandsPerPin=5
HistoryRetention__WebhookRetentionDays=30
HistoryRetention__SyncRunRetentionDays=30
HistoryRetention__IntegrationErrorRetentionDays=90
HistoryRetention__VacuumAfterCleanup=true
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

ZKBio can optionally be limited to a GymMaster company:

```text
ZKBio__CompanyIds__0=4
```

Replace `4` with the company ID belonging to this installation. Add `ZKBio__CompanyIds__1`, `__2`, and so on when one physical ZKBio installation intentionally serves multiple GymMaster companies. If every `ZKBio__CompanyIds__*` setting is omitted, ZKBio processes members from all companies. BioStar settings are not required when ZKBio is selected.

To run this installation with BioStar instead, select it as the sole provider:

```text
AccessProvider__Type=BioStar
```

Only one provider is active at a time; the service does not send the same member to both systems.

The production sync order is:

```text
1. Fetch current members from GymMaster Portal API
2. Save/update members in SQLite
3. Compute desired access/profile fingerprints
4. Map each member into the selected provider's access model
5. Fast sync: skip members whose latest provider confirmation fingerprint is still fresh
6. Full audit: bypass the cache shortcut and force provider confirmation
7. Fetch matching people from the selected provider by PIN
8. Skip people that already match
9. Create/update missing or changed people through the provider API
10. Update the provider-specific local cache after successful confirmation
11. Record Applied, Skipped, or Failed status for the selected provider
```

The service does not delete ZKBio people.

ZKBio person-level failures do not stop the rest of the batch. The failed PIN is marked `Failed`, the error is saved on the access command, and the sync continues with the next person.

If ZKBio rejects a person because the mailbox/email already exists, the service retries that same person without email so access permissions can still sync.

ZKBio does not accept punctuation or symbols in person names. The service removes those characters from the ZKBio-bound `name` and `lastName`, collapses whitespace, and uses the member PIN if no usable name remains. GymMaster and the local member record retain the original name.

There are two reconciliation modes:

```text
Fast      - hourly, uses local provider confirmation fingerprints to avoid unnecessary API calls
FullAudit - slower confirmation pass, refreshes the provider cache and guarantees freshness
```

The full audit cadence is controlled by:

```text
Reconciliation__FullAuditIntervalHours=24
Reconciliation__FullAuditOnStartup=true
Reconciliation__ProviderCacheMaxAgeHours=24
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

## Database Retention

The service automatically cleans history after startup and then at the configured interval. It keeps current state in `members` and `zkbio_people`, and only prunes old audit/history rows:

```text
access_commands
webhook_events
sync_runs
integration_errors
```

Recommended production settings:

```text
HistoryRetention__Enabled=true
HistoryRetention__CleanupIntervalHours=24
HistoryRetention__AccessCommandRetentionDays=0
HistoryRetention__KeepLatestAccessCommandPerPin=true
HistoryRetention__FailedAccessCommandRetentionDays=30
HistoryRetention__MaxFailedAccessCommandsPerPin=5
HistoryRetention__WebhookRetentionDays=30
HistoryRetention__SyncRunRetentionDays=30
HistoryRetention__IntegrationErrorRetentionDays=90
HistoryRetention__VacuumAfterCleanup=true
```

`Pending` access commands are never deleted by retention cleanup. With `HistoryRetention__KeepLatestAccessCommandPerPin=true`, the latest command for each PIN is also kept so the dashboard still has a current command snapshot. Older failed commands are kept for `HistoryRetention__FailedAccessCommandRetentionDays` so recent provider issues remain inspectable.

## Install As Windows Service

Install the .NET 10 SDK on the Windows PC that performs the publish. A self-contained published application does not require .NET to be installed at runtime.

From the repository root, publish into a staging directory. In Command Prompt, enter this as one line:

```cmd
dotnet publish .\src\Fcl.Sync.Service\Fcl.Sync.Service.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64
```

Copy the published application into its permanent directory:

```cmd
mkdir C:\FclSync
robocopy .\publish\win-x64 C:\FclSync /E
```

Create `C:\FclSync\.env` with the production settings. The SQLite database should also live under this folder by default:

```text
Sqlite__ConnectionString=Data Source=C:\FclSync\data\fcl-sync.db
```

Restrict the configuration file so ordinary Windows users cannot read integration credentials:

```cmd
icacls C:\FclSync\.env /inheritance:r
icacls C:\FclSync\.env /grant:r "SYSTEM:R" "Administrators:R"
```

The dashboard defaults to local-machine access only even when the webhook listener binds to `0.0.0.0`. Keep `Dashboard__AllowRemoteAccess=false` unless an authenticated reverse proxy protects it.

Install the Windows service from an Administrator PowerShell:

```powershell
New-Service `
  -Name "FclSyncService" `
  -BinaryPathName "C:\FclSync\Fcl.Sync.Service.exe" `
  -DisplayName "FCL GymMaster Sync" `
  -Description "Syncs GymMaster members to the local access provider." `
  -StartupType Automatic
```

Start it:

```powershell
Start-Service FclSyncService
```

Check status:

```powershell
Get-Service FclSyncService
```

Open the dashboard on that PC:

```text
http://127.0.0.1:5050/dashboard
```

View logs in Windows Event Viewer:

```text
Event Viewer > Windows Logs > Application
```

Stop or restart:

```powershell
Stop-Service FclSyncService
Start-Service FclSyncService
Restart-Service FclSyncService
```

Uninstall:

```powershell
Stop-Service FclSyncService
sc.exe delete FclSyncService
```

## Update The Windows PC After Code Changes

The source code and published application are separate. After every code change, publish new Windows binaries and copy them to `C:\FclSync`. Copying source files alone does not update the running service.

### 1. Update the source

On the Windows PC, open Command Prompt in the repository. If the repository uses Git, fetch the latest committed code:

```cmd
cd /d C:\Users\USER\Desktop\FCL\nextgen-sync
git pull
```

If code is transferred another way, copy the updated repository into that location before continuing.

### 2. Build and publish to staging

Confirm that a .NET 10 SDK is installed:

```cmd
dotnet --list-sdks
```

Build and publish:

```cmd
dotnet build .\Fcl.Sync.slnx -c Release
dotnet publish .\src\Fcl.Sync.Service\Fcl.Sync.Service.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64
```

Never publish directly into `C:\FclSync` while the application is running.

### 3. Stop the current application

If it is installed as a Windows service, use Administrator Command Prompt or PowerShell:

```cmd
sc.exe stop FclSyncService
```

Wait until it reports `STOPPED`:

```cmd
sc.exe query FclSyncService
```

If it is running interactively in a console, press `Ctrl+C` instead.

### 4. Back up the current binaries

This backup excludes `.env` and the SQLite `data` directory because those are persistent production data:

```cmd
mkdir C:\FclSync-backup
robocopy C:\FclSync C:\FclSync-backup /E /XD data /XF .env
```

### 5. Copy the new version

Run this from the repository root:

```cmd
robocopy .\publish\win-x64 C:\FclSync /E
```

Do not use `/MIR`. The `/E` copy updates application files without deleting:

- `C:\FclSync\.env`
- `C:\FclSync\data\fcl-sync.db`

Confirm the required files are present:

```cmd
dir /a C:\FclSync
```

The listing must include `Fcl.Sync.Service.exe` and `.env`.

### 6. Start and verify

For a Windows service:

```cmd
sc.exe start FclSyncService
sc.exe query FclSyncService
```

For an interactive test:

```cmd
cd /d C:\FclSync
Fcl.Sync.Service.exe
```

Open the dashboard using the port configured by `ASPNETCORE_URLS`, for example:

```text
http://127.0.0.1:4040/dashboard
```

Verify that the service is running, the latest reconciliation completes, pending commands return to zero, and no new integration errors appear.

### 7. Roll back if necessary

Stop the service, restore the previous binaries, and start it again:

```cmd
sc.exe stop FclSyncService
robocopy C:\FclSync-backup C:\FclSync /E
sc.exe start FclSyncService
```

Rollback preserves the existing `.env` and SQLite database.

## Webhook Endpoint

GymMaster should post to:

```text
POST /webhooks/gymmaster
```

The request must include:

```text
X-Gymmaster-Token: your-webhook-token
```

Local smoke test with the safe `Noop` provider:

```bash
AccessProvider__Type=Noop \
GymMaster__Webhooks__SecretToken=test-webhook-token \
dotnet run --project src/Fcl.Sync.Service/Fcl.Sync.Service.csproj --no-launch-profile --urls http://127.0.0.1:5050
```

Then post a sample event:

```bash
curl -i -X POST http://127.0.0.1:5050/webhooks/gymmaster \
  -H 'Content-Type: application/json' \
  -H 'X-Gymmaster-Token: test-webhook-token' \
  --data '{
    "event_id": 990002,
    "event_type": "member_update",
    "event_timestamp": "2026-06-24T19:28:10.472+12:00",
    "processed_at": "2026-06-24T19:28:25.608+12:00",
    "payload": {
      "memberid": 974303,
      "companyid": 4
    }
  }'
```

Expected response:

```text
202 Accepted
```

The dashboard should show the event under `Recent Webhooks`, and SQLite should have a new access command for that member.
