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

The app uses one access provider for the complete installation. Select either ZKBio or BioStar:

```json
{
  "AccessProvider": {
    "Type": "BioStar"
  }
}
```

Supported values right now:

- `ZKBio` - posts create/update payloads to the ZKBio HTTP API.
- `BioStar` - authenticates with BioStar 2, then creates, enables, or disables users.
- `Noop` - accepts commands without sending them anywhere; useful for local tests and future non-ZKBio integrations.

ZKBio and BioStar are alternatives, not simultaneous targets. All reconciliation and webhook commands are sent only to the selected provider.

New integrations should implement `IAccessProvider` and consume the provider-neutral `AccessPersonCommand`.

Provider selection is lazy. A non-ZKBio deployment can use `Noop` or a future provider without configuring ZKBio settings.

## BioStar

```json
{
  "BioStar": {
    "BaseUrl": "https://biostar-server",
    "LoginId": "admin",
    "Password": "secret",
    "UserGroupId": "1052",
    "AccessGroupId": "3",
    "StartDateTime": "2001-01-01T00:00:00Z",
    "ExpiryDateTime": "2030-12-31T23:59:00Z",
    "AllowInvalidServerCertificate": true,
    "CompanyIds": []
  }
}
```

The client uses `/api/login`, retains the `bs-session-id` without logging it, retries once after an expired session, and supports the BioStar response shapes used by the supplied JavaScript.
By default, BioStar processes all GymMaster members. Configure `BioStar:CompanyIds` only when an installation must be restricted to selected companies.

## GymMaster

Portal API config:

```json
{
  "GymMaster": {
    "SiteName": "nextgen",
    "ApiKey": "your-api-key",
    "PortalMembersUrl": "https://nextgen.gymmasteronline.com/portal/api/v1/members?api_key=your-api-key"
  }
}
```

The service uses the Portal API `GET /portal/api/v1/members` for both webhook member lookup and hourly reconciliation because it returns member profile details in `result`. If `PortalMembersUrl` is blank, the service derives it from `GymMaster:SiteName` and `GymMaster:ApiKey`. The legacy `GMS_API_URL` env var is also supported.

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
- `provider_people`
- `sync_runs`
- `integration_errors`

## Remaining Details To Confirm

Before live testing, confirm:

- Real `GymMaster:SiteName` and `GymMaster:ApiKey`, or `GymMaster:PortalMembersUrl`.
- Real `ZKBio:BaseUrl` and `ZKBio:AccessToken` for the site.
