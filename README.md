# FCL Sync

Local Windows-friendly GymMaster sync service.

## Current Shape

- `POST /webhooks/gymmaster` receives GymMaster webhooks.
- The `X-Gymmaster-Token` header is validated before the event is accepted.
- Webhook events are stored in SQLite with `event_id` as the idempotency key.
- The service then fetches the full member from GymMaster, applies the local access policy, records the member state, and sends a provider-neutral access command to the configured access provider.
- A background worker runs reconciliation every hour.

## Local Configuration

Secrets should not be committed. Copy `.env.example` to `.env` and put real site values there:

```bash
cp .env.example .env
```

`.env` is ignored by git. The app loads it at startup, and normal environment variables can still override the same keys.

.NET nested config uses double underscores in environment variable names, for example:

```bash
GymMaster__Webhooks__SecretToken=secret
ZKBio__AccessToken=token
```

## Access Providers

The app is intentionally not tied to ZKBio. Configure the target system with:

```json
{
  "AccessProvider": {
    "Type": "ZKBio"
  }
}
```

Supported values right now:

- `ZKBio` - posts create/update payloads to the ZKBio HTTP API.
- `Noop` - accepts commands without sending them anywhere; useful for local tests and future non-ZKBio integrations.

New integrations should implement `IAccessProvider` and consume `AccessPersonCommand`.

Provider selection is lazy. A non-ZKBio deployment can use `Noop` or a future provider without configuring ZKBio settings.

## GymMaster

Gatekeeper API config:

```json
{
  "GymMaster": {
    "SiteName": "nextgen",
    "ApiKey": "your-api-key",
    "GatekeeperBaseUrl": "https://nextgen.gymmasteronline.com/gatekeeper_api/v2",
    "PortalMembersUrl": "https://nextgen.gymmasteronline.com/portal/api/v1/members?api_key=your-api-key",
    "MaxSyncPages": 100,
    "StaffApi": {
      "Enabled": false,
      "BaseUrl": "https://nextgen.gymmasteronline.com",
      "AuthorizationScheme": "Basic",
      "AuthorizationValue": ""
    }
  }
}
```

The service prefers the Portal API `GET /portal/api/v1/members` for both webhook member lookup and hourly reconciliation because it returns member profile details in `result`. If `PortalMembersUrl` is blank, the service derives it from `GymMaster:SiteName` and `GymMaster:ApiKey`. The legacy `GMS_API_URL` env var is also supported.

When `StaffApi:Enabled` is true, the service also calls `GET /member/{memberid}` to enrich profile fields before saving to SQLite and sending the access command. If `AuthorizationValue` is blank, the staff call reuses the Gatekeeper Basic Auth credentials. Keep this disabled until the correct staff API base URL/auth is confirmed.

## ZKBio

ZKBio config:

```json
{
  "ZKBio": {
    "BaseUrl": "https://zkbio-server",
    "AccessToken": "token",
    "AllowInvalidServerCertificate": true
  }
}
```

The current client posts provider-neutral commands to `/api/person/add`, matching the existing `zkbio.ts` implementation.

## SQLite

Configured with:

```json
{
  "Sqlite": {
    "ConnectionString": "Data Source=data/fcl-sync.db"
  }
}
```

Tables created automatically:

- `webhook_events`
- `members`
- `access_commands`
- `sync_runs`
- `integration_errors`

## Remaining Details To Confirm

Before live testing, confirm:

- Real `GymMaster:SiteName`, `GymMaster:ApiKey`, and `GymMaster:GatekeeperBaseUrl`.
- Real `ZKBio:BaseUrl` and `ZKBio:AccessToken` for the site.
- Whether the staff API accepts the same Basic Auth as Gatekeeper. If not, set `GymMaster:StaffApi:AuthorizationScheme` and `GymMaster:StaffApi:AuthorizationValue`.
