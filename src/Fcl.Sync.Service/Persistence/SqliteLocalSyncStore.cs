using System.Data;
using System.Text.Json;
using Fcl.Sync.Service.AccessControl;
using Fcl.Sync.Service.AccessProviders;
using Fcl.Sync.Service.Dashboard;
using Fcl.Sync.Service.GymMaster;
using Fcl.Sync.Service.Webhooks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Fcl.Sync.Service.Persistence;

public sealed class SqliteLocalSyncStore : ILocalSyncStore, ISyncDashboardStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string connectionString;
    private readonly ILogger<SqliteLocalSyncStore> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private bool initialized;

    public SqliteLocalSyncStore(IOptions<SqliteOptions> options, ILogger<SqliteLocalSyncStore> logger)
    {
        connectionString = options.Value.ConnectionString;
        this.logger = logger;
    }

    public async Task<bool> TryRecordWebhookAsync(GymMasterWebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await writeGate.WaitAsync(cancellationToken);

        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO webhook_events (
                    event_id,
                    event_type,
                    event_timestamp,
                    processed_at,
                    member_id,
                    company_id,
                    raw_payload,
                    received_at
                )
                VALUES (
                    $event_id,
                    $event_type,
                    $event_timestamp,
                    $processed_at,
                    $member_id,
                    $company_id,
                    $raw_payload,
                    $received_at
                );
                """;

            command.Parameters.AddWithValue("$event_id", webhookEvent.EventId);
            command.Parameters.AddWithValue("$event_type", webhookEvent.EventType);
            command.Parameters.AddWithValue("$event_timestamp", webhookEvent.EventTimestamp.ToString("O"));
            command.Parameters.AddWithValue("$processed_at", (object?)webhookEvent.ProcessedAt?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$member_id", (object?)webhookEvent.Payload.MemberId ?? DBNull.Value);
            command.Parameters.AddWithValue("$company_id", (object?)webhookEvent.Payload.CompanyId ?? DBNull.Value);
            command.Parameters.AddWithValue("$raw_payload", JsonSerializer.Serialize(webhookEvent, JsonOptions));
            command.Parameters.AddWithValue("$received_at", DateTimeOffset.UtcNow.ToString("O"));

            var inserted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;

            if (!inserted)
            {
                logger.LogInformation("Ignoring duplicate webhook event {EventId}.", webhookEvent.EventId);
            }

            return inserted;
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task UpsertMemberAsync(GymMasterMember member, AccessDecision accessDecision, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await writeGate.WaitAsync(cancellationToken);

        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO members (
                    member_id,
                    company_id,
                    first_name,
                    surname,
                    gender,
                    status,
                    owing,
                    email,
                    mobile_phone,
                    join_date,
                    access_level_ids,
                    is_disabled,
                    department_code,
                    access_reason,
                    raw_payload,
                    updated_at
                )
                VALUES (
                    $member_id,
                    $company_id,
                    $first_name,
                    $surname,
                    $gender,
                    $status,
                    $owing,
                    $email,
                    $mobile_phone,
                    $join_date,
                    $access_level_ids,
                    $is_disabled,
                    $department_code,
                    $access_reason,
                    $raw_payload,
                    $updated_at
                )
                ON CONFLICT(member_id) DO UPDATE SET
                    company_id = excluded.company_id,
                    first_name = excluded.first_name,
                    surname = excluded.surname,
                    gender = excluded.gender,
                    status = excluded.status,
                    owing = excluded.owing,
                    email = excluded.email,
                    mobile_phone = excluded.mobile_phone,
                    join_date = excluded.join_date,
                    access_level_ids = excluded.access_level_ids,
                    is_disabled = excluded.is_disabled,
                    department_code = excluded.department_code,
                    access_reason = excluded.access_reason,
                    raw_payload = excluded.raw_payload,
                    updated_at = excluded.updated_at;
                """;

            command.Parameters.AddWithValue("$member_id", member.MemberId);
            command.Parameters.AddWithValue("$company_id", (object?)member.CompanyId ?? DBNull.Value);
            command.Parameters.AddWithValue("$first_name", (object?)member.FirstName ?? DBNull.Value);
            command.Parameters.AddWithValue("$surname", (object?)member.Surname ?? DBNull.Value);
            command.Parameters.AddWithValue("$gender", (object?)member.Gender ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", (object?)member.Status ?? DBNull.Value);
            command.Parameters.AddWithValue("$owing", member.Owing);
            command.Parameters.AddWithValue("$email", (object?)member.Email ?? DBNull.Value);
            command.Parameters.AddWithValue("$mobile_phone", (object?)member.MobilePhone ?? DBNull.Value);
            command.Parameters.AddWithValue("$join_date", (object?)member.JoinDate?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$access_level_ids", accessDecision.AccessLevelIds);
            command.Parameters.AddWithValue("$is_disabled", accessDecision.IsDisabled ? 1 : 0);
            command.Parameters.AddWithValue("$department_code", accessDecision.DepartmentCode);
            command.Parameters.AddWithValue("$access_reason", accessDecision.Reason);
            command.Parameters.AddWithValue("$raw_payload", JsonSerializer.Serialize(member, JsonOptions));
            command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task<long> RecordAccessCommandAsync(
        string providerName,
        string pin,
        object commandPayload,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await writeGate.WaitAsync(cancellationToken);

        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO access_commands (
                    provider_name,
                    pin,
                    command_payload,
                    status,
                    attempt_count,
                    created_at,
                    updated_at
                )
                VALUES (
                    $provider_name,
                    $pin,
                    $command_payload,
                    $status,
                    0,
                    $created_at,
                    $updated_at
                )
                RETURNING id;
                """;

            var now = DateTimeOffset.UtcNow.ToString("O");
            command.Parameters.AddWithValue("$provider_name", providerName);
            command.Parameters.AddWithValue("$pin", pin);
            command.Parameters.AddWithValue("$command_payload", JsonSerializer.Serialize(commandPayload, JsonOptions));
            command.Parameters.AddWithValue("$status", AccessCommandStatus.Pending.ToString());
            command.Parameters.AddWithValue("$created_at", now);
            command.Parameters.AddWithValue("$updated_at", now);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt64(result);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task MarkAccessCommandAsync(
        long commandId,
        AccessCommandStatus status,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await writeGate.WaitAsync(cancellationToken);

        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE access_commands
                SET status = $status,
                    error_message = $error_message,
                    attempt_count = attempt_count + 1,
                    updated_at = $updated_at
                WHERE id = $id;
                """;

            command.Parameters.AddWithValue("$id", commandId);
            command.Parameters.AddWithValue("$status", status.ToString());
            command.Parameters.AddWithValue("$error_message", (object?)errorMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task MarkStalePendingAccessCommandsAsync(string reason, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await writeGate.WaitAsync(cancellationToken);

        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE access_commands
                SET status = $status,
                    error_message = $error_message,
                    updated_at = $updated_at
                WHERE status = 'Pending';
                """;

            command.Parameters.AddWithValue("$status", AccessCommandStatus.Failed.ToString());
            command.Parameters.AddWithValue("$error_message", reason);
            command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task<IReadOnlySet<string>> GetFreshConfirmedPinsAsync(
        IReadOnlyDictionary<string, string> desiredHashesByPin,
        DateTimeOffset freshAfter,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        if (desiredHashesByPin.Count == 0)
        {
            return new HashSet<string>();
        }

        var confirmedPins = new HashSet<string>(StringComparer.Ordinal);
        await using var connection = await OpenConnectionAsync(cancellationToken);

        foreach (var chunk in desiredHashesByPin.Chunk(500))
        {
            var parameters = chunk.Select((_, index) => $"$pin{index}").ToArray();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT pin, sync_hash
                FROM zkbio_people
                WHERE last_confirmed_at >= $fresh_after
                  AND pin IN ({string.Join(", ", parameters)});
                """;

            command.Parameters.AddWithValue("$fresh_after", freshAfter.ToString("O"));

            var index = 0;
            foreach (var (pin, _) in chunk)
            {
                command.Parameters.AddWithValue(parameters[index], pin);
                index++;
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var pin = reader.GetString(0);
                var observedHash = reader.GetString(1);

                if (desiredHashesByPin.TryGetValue(pin, out var desiredHash) &&
                    string.Equals(observedHash, desiredHash, StringComparison.Ordinal))
                {
                    confirmedPins.Add(pin);
                }
            }
        }

        return confirmedPins;
    }

    public async Task UpsertZKBioCacheAsync(
        AccessPersonCommand command,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await writeGate.WaitAsync(cancellationToken);

        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var dbCommand = connection.CreateCommand();
            dbCommand.CommandText = """
                INSERT INTO zkbio_people (
                    pin,
                    name,
                    last_name,
                    access_level_ids,
                    department_code,
                    is_disabled,
                    email,
                    mobile_phone,
                    join_date,
                    access_hash,
                    profile_hash,
                    sync_hash,
                    last_confirmed_at,
                    updated_at
                )
                VALUES (
                    $pin,
                    $name,
                    $last_name,
                    $access_level_ids,
                    $department_code,
                    $is_disabled,
                    $email,
                    $mobile_phone,
                    $join_date,
                    $access_hash,
                    $profile_hash,
                    $sync_hash,
                    $last_confirmed_at,
                    $updated_at
                )
                ON CONFLICT(pin) DO UPDATE SET
                    name = excluded.name,
                    last_name = excluded.last_name,
                    access_level_ids = excluded.access_level_ids,
                    department_code = excluded.department_code,
                    is_disabled = excluded.is_disabled,
                    email = excluded.email,
                    mobile_phone = excluded.mobile_phone,
                    join_date = excluded.join_date,
                    access_hash = excluded.access_hash,
                    profile_hash = excluded.profile_hash,
                    sync_hash = excluded.sync_hash,
                    last_confirmed_at = excluded.last_confirmed_at,
                    updated_at = excluded.updated_at;
                """;

            dbCommand.Parameters.AddWithValue("$pin", command.Pin);
            dbCommand.Parameters.AddWithValue("$name", command.Name);
            dbCommand.Parameters.AddWithValue("$last_name", (object?)command.LastName ?? DBNull.Value);
            dbCommand.Parameters.AddWithValue("$access_level_ids", command.AccessLevelIds);
            dbCommand.Parameters.AddWithValue("$department_code", command.DepartmentCode);
            dbCommand.Parameters.AddWithValue("$is_disabled", command.IsDisabled ? 1 : 0);
            dbCommand.Parameters.AddWithValue("$email", (object?)command.Email ?? DBNull.Value);
            dbCommand.Parameters.AddWithValue("$mobile_phone", (object?)command.MobilePhone ?? DBNull.Value);
            dbCommand.Parameters.AddWithValue("$join_date", (object?)command.JoinDate?.ToString("O") ?? DBNull.Value);
            dbCommand.Parameters.AddWithValue("$access_hash", command.AccessHash);
            dbCommand.Parameters.AddWithValue("$profile_hash", command.ProfileHash);
            dbCommand.Parameters.AddWithValue("$sync_hash", command.SyncHash);
            dbCommand.Parameters.AddWithValue("$last_confirmed_at", observedAt.ToString("O"));
            dbCommand.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));

            await dbCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task<long> StartSyncRunAsync(SyncRunMode mode, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await writeGate.WaitAsync(cancellationToken);

        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO sync_runs (mode, started_at, completed_at, members_checked, status, error_message)
                VALUES ($mode, $started_at, $started_at, 0, $status, NULL)
                RETURNING id;
                """;

            command.Parameters.AddWithValue("$mode", mode.ToString());
            command.Parameters.AddWithValue("$started_at", startedAt.ToString("O"));
            command.Parameters.AddWithValue("$status", SyncRunStatus.Running.ToString());

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt64(result);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task CompleteSyncRunAsync(
        long syncRunId,
        DateTimeOffset completedAt,
        int membersChecked,
        SyncRunStatus status,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await writeGate.WaitAsync(cancellationToken);

        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE sync_runs
                SET completed_at = $completed_at,
                    members_checked = $members_checked,
                    status = $status,
                    error_message = $error_message
                WHERE id = $id;
                """;

            command.Parameters.AddWithValue("$id", syncRunId);
            command.Parameters.AddWithValue("$completed_at", completedAt.ToString("O"));
            command.Parameters.AddWithValue("$members_checked", membersChecked);
            command.Parameters.AddWithValue("$status", status.ToString());
            command.Parameters.AddWithValue("$error_message", (object?)errorMessage ?? DBNull.Value);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task RecordIntegrationErrorAsync(string source, string message, string? details, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await writeGate.WaitAsync(cancellationToken);

        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO integration_errors (source, message, details, created_at)
                VALUES ($source, $message, $details, $created_at);
                """;

            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$message", message);
            command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
            command.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToString("O"));

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task<SyncDashboardSnapshot> GetDashboardSnapshotAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var latestSyncRun = await GetLatestSyncRunAsync(connection, cancellationToken);
        var latestCommandCounts = latestSyncRun is null
            ? []
            : await GetCommandCountsAsync(connection, "created_at >= $started_at", latestSyncRun.StartedAt, cancellationToken);

        return new SyncDashboardSnapshot(
            GeneratedAt: DateTimeOffset.UtcNow,
            MemberCount: await GetScalarIntAsync(connection, "SELECT COUNT(*) FROM members;", cancellationToken),
            WebhookEventCount: await GetScalarIntAsync(connection, "SELECT COUNT(*) FROM webhook_events;", cancellationToken),
            PendingCommandCount: await GetScalarIntAsync(connection, "SELECT COUNT(*) FROM access_commands WHERE status = 'Pending';", cancellationToken),
            FailedCommandCount: await GetScalarIntAsync(connection, "SELECT COUNT(*) FROM access_commands WHERE status = 'Failed';", cancellationToken),
            LatestSyncRun: latestSyncRun,
            LatestCommandCounts: latestCommandCounts,
            AllCommandCounts: await GetCommandCountsAsync(connection, null, null, cancellationToken),
            RecentSyncRuns: await GetRecentSyncRunsAsync(connection, cancellationToken),
            RecentWebhookEvents: await GetRecentWebhookEventsAsync(connection, cancellationToken),
            RecentErrors: await GetRecentErrorsAsync(connection, cancellationToken));
    }

    private static async Task<SyncRunSummary?> GetLatestSyncRunAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, mode, started_at, completed_at, members_checked, status, error_message
            FROM sync_runs
            ORDER BY id DESC
            LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadSyncRun(reader)
            : null;
    }

    private static async Task<IReadOnlyList<SyncRunSummary>> GetRecentSyncRunsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, mode, started_at, completed_at, members_checked, status, error_message
            FROM sync_runs
            ORDER BY id DESC
            LIMIT 5;
            """;

        var runs = new List<SyncRunSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            runs.Add(ReadSyncRun(reader));
        }

        return runs;
    }

    private static async Task<IReadOnlyList<CommandStatusCount>> GetCommandCountsAsync(
        SqliteConnection connection,
        string? whereClause,
        DateTimeOffset? startedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = whereClause is null
            ? """
                SELECT status, COUNT(*)
                FROM access_commands
                GROUP BY status
                ORDER BY status;
                """
            : $"""
                SELECT status, COUNT(*)
                FROM access_commands
                WHERE {whereClause}
                GROUP BY status
                ORDER BY status;
                """;

        if (startedAt is not null)
        {
            command.Parameters.AddWithValue("$started_at", startedAt.Value.ToString("O"));
        }

        var counts = new List<CommandStatusCount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            counts.Add(new CommandStatusCount(reader.GetString(0), reader.GetInt32(1)));
        }

        return counts;
    }

    private static async Task<IReadOnlyList<IntegrationErrorSummary>> GetRecentErrorsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source, message, created_at
            FROM integration_errors
            ORDER BY id DESC
            LIMIT 10;
            """;

        var errors = new List<IntegrationErrorSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            errors.Add(new IntegrationErrorSummary(
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2))));
        }

        return errors;
    }

    private static async Task<IReadOnlyList<WebhookEventSummary>> GetRecentWebhookEventsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, event_type, member_id, received_at
            FROM webhook_events
            ORDER BY received_at DESC
            LIMIT 10;
            """;

        var events = new List<WebhookEventSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new WebhookEventSummary(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                DateTimeOffset.Parse(reader.GetString(3))));
        }

        return events;
    }

    private static async Task<int> GetScalarIntAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static SyncRunSummary ReadSyncRun(SqliteDataReader reader)
    {
        return new SyncRunSummary(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? SyncRunMode.Fast.ToString() : reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? SyncRunStatus.Completed.ToString() : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (initialized)
        {
            return;
        }

        await gate.WaitAsync(cancellationToken);

        try
        {
            if (initialized)
            {
                return;
            }

            EnsureDatabaseDirectoryExists();

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;

                CREATE TABLE IF NOT EXISTS webhook_events (
                    event_id INTEGER PRIMARY KEY,
                    event_type TEXT NOT NULL,
                    event_timestamp TEXT NOT NULL,
                    processed_at TEXT NULL,
                    member_id INTEGER NULL,
                    company_id INTEGER NULL,
                    raw_payload TEXT NOT NULL,
                    received_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS members (
                    member_id INTEGER PRIMARY KEY,
                    company_id INTEGER NULL,
                    first_name TEXT NULL,
                    surname TEXT NULL,
                    gender TEXT NULL,
                    status TEXT NULL,
                    owing REAL NOT NULL,
                    email TEXT NULL,
                    mobile_phone TEXT NULL,
                    join_date TEXT NULL,
                    access_level_ids TEXT NOT NULL,
                    is_disabled INTEGER NOT NULL,
                    department_code TEXT NOT NULL,
                    access_reason TEXT NOT NULL,
                    raw_payload TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS access_commands (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    provider_name TEXT NOT NULL,
                    pin TEXT NOT NULL,
                    command_payload TEXT NOT NULL,
                    status TEXT NOT NULL,
                    attempt_count INTEGER NOT NULL DEFAULT 0,
                    error_message TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_access_commands_status_updated_at
                    ON access_commands(status, updated_at);

                CREATE TABLE IF NOT EXISTS sync_runs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    mode TEXT NOT NULL DEFAULT 'Fast',
                    started_at TEXT NOT NULL,
                    completed_at TEXT NULL,
                    members_checked INTEGER NOT NULL,
                    status TEXT NOT NULL DEFAULT 'Completed',
                    error_message TEXT NULL
                );
                """;

            await command.ExecuteNonQueryAsync(cancellationToken);

            command.CommandText = """
                ALTER TABLE sync_runs ADD COLUMN mode TEXT NOT NULL DEFAULT 'Fast';
                """;

            await ExecuteSchemaCommandAsync(connection, command.CommandText, cancellationToken);

            command.CommandText = """
                ALTER TABLE sync_runs ADD COLUMN status TEXT NOT NULL DEFAULT 'Completed';
                """;

            await ExecuteSchemaCommandAsync(connection, command.CommandText, cancellationToken);

            command.CommandText = """
                ALTER TABLE sync_runs ADD COLUMN error_message TEXT NULL;
                """;

            await ExecuteSchemaCommandAsync(connection, command.CommandText, cancellationToken);

            command.CommandText = """
                CREATE TABLE IF NOT EXISTS zkbio_people (
                    pin TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    last_name TEXT NULL,
                    access_level_ids TEXT NOT NULL,
                    department_code TEXT NOT NULL,
                    is_disabled INTEGER NOT NULL,
                    email TEXT NULL,
                    mobile_phone TEXT NULL,
                    join_date TEXT NULL,
                    access_hash TEXT NOT NULL,
                    profile_hash TEXT NOT NULL,
                    sync_hash TEXT NOT NULL,
                    last_confirmed_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_zkbio_people_sync_hash_confirmed
                    ON zkbio_people(sync_hash, last_confirmed_at);

                CREATE TABLE IF NOT EXISTS integration_errors (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    source TEXT NOT NULL,
                    message TEXT NOT NULL,
                    details TEXT NULL,
                    created_at TEXT NOT NULL
                );
                """;

            await command.ExecuteNonQueryAsync(cancellationToken);
            initialized = true;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task ExecuteSchemaCommandAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 30000;";
        await command.ExecuteNonQueryAsync(cancellationToken);

        return connection;
    }

    private void EnsureDatabaseDirectoryExists()
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;

        if (string.IsNullOrWhiteSpace(dataSource) || dataSource is ":memory:")
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
