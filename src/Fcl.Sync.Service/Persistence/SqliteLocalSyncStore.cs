using System.Data;
using System.Text.Json;
using Fcl.Sync.Service.AccessControl;
using Fcl.Sync.Service.GymMaster;
using Fcl.Sync.Service.Webhooks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Fcl.Sync.Service.Persistence;

public sealed class SqliteLocalSyncStore : ILocalSyncStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string connectionString;
    private readonly ILogger<SqliteLocalSyncStore> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool initialized;

    public SqliteLocalSyncStore(IOptions<SqliteOptions> options, ILogger<SqliteLocalSyncStore> logger)
    {
        connectionString = options.Value.ConnectionString;
        this.logger = logger;
    }

    public async Task<bool> TryRecordWebhookAsync(GymMasterWebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

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

    public async Task UpsertMemberAsync(GymMasterMember member, AccessDecision accessDecision, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

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

    public async Task<long> RecordAccessCommandAsync(
        string providerName,
        string pin,
        object commandPayload,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

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

    public async Task MarkAccessCommandAsync(
        long commandId,
        AccessCommandStatus status,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

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

    public async Task RecordSyncRunAsync(DateTimeOffset startedAt, DateTimeOffset completedAt, int membersChecked, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_runs (started_at, completed_at, members_checked)
            VALUES ($started_at, $completed_at, $members_checked);
            """;

        command.Parameters.AddWithValue("$started_at", startedAt.ToString("O"));
        command.Parameters.AddWithValue("$completed_at", completedAt.ToString("O"));
        command.Parameters.AddWithValue("$members_checked", membersChecked);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordIntegrationErrorAsync(string source, string message, string? details, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

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
                    started_at TEXT NOT NULL,
                    completed_at TEXT NOT NULL,
                    members_checked INTEGER NOT NULL
                );

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

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
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
