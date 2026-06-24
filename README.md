# FCL Sync

Local Windows-friendly GymMaster sync service.

## Current Shape

- `POST /webhooks/gymmaster` receives GymMaster webhooks.
- The `X-Gymmaster-Token` header is validated before the event is accepted.
- Webhook events are stored in SQLite with `event_id` as the idempotency key.
- The service then fetches the full member from GymMaster, applies the local access policy, records the member state, and sends a provider-neutral access command to the configured access provider.
- A background worker runs reconciliation every hour.

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

- `ZKBio` - current intended provider, still using a placeholder client until the real API details are wired.
- `Noop` - accepts commands without sending them anywhere; useful for local tests and future non-ZKBio integrations.

New integrations should implement `IAccessProvider` and consume `AccessPersonCommand`.

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

## Next API Details Needed

To finish the real integration, we need:

- GymMaster member lookup API by `memberid` and `companyid`.
- GymMaster full/current members API for hourly reconciliation.
- ZKBio create/update person API details, including auth, field names, and expected success/error response shape.
