using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Oxydriver.Services;

public sealed class AppLogStore
{
    private const int MaxStoredLength = 12000;
    private readonly string _dbPath;
    private readonly string _connectionString;

    public AppLogStore()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OXYDRIVER"
        );
        Directory.CreateDirectory(baseDir);
        _dbPath = Path.Combine(baseDir, "logs.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
        EnsureSchema();
    }

    public Task AddSystemLogAsync(string action, string? details = null)
        => AddLogAsync("systeme", action, details, null, null, null);

    public Task AddRequestLogAsync(
        string action,
        string? details,
        string? requestContent,
        string? responseContent,
        string? errorContent
    ) => AddLogAsync("requete", action, details, requestContent, responseContent, errorContent);

    public async Task<IReadOnlyList<PersistedLogEntry>> GetLogsAsync(int offset, int limit, string? category = null)
    {
        if (offset < 0) offset = 0;
        if (limit <= 0) limit = 30;
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT id, created_at_utc, category, action, details, request_content, response_content, error_content
FROM app_logs
WHERE (@category IS NULL OR category = @category)
ORDER BY id DESC
LIMIT @limit OFFSET @offset;";
        cmd.Parameters.AddWithValue("@category", string.IsNullOrWhiteSpace(category) ? DBNull.Value : category.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);

        var rows = new List<PersistedLogEntry>(limit);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var utcRaw = reader.GetString(1);
            var utc = DateTime.TryParse(utcRaw, out var dt) ? dt : DateTime.UtcNow;
            rows.Add(new PersistedLogEntry
            {
                Id = reader.GetInt64(0),
                CreatedAtUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc),
                Category = reader.GetString(2),
                Action = reader.GetString(3),
                Details = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                RequestContent = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                ResponseContent = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                ErrorContent = reader.IsDBNull(7) ? string.Empty : reader.GetString(7)
            });
        }

        return rows;
    }

    private async Task AddLogAsync(
        string category,
        string action,
        string? details,
        string? requestContent,
        string? responseContent,
        string? errorContent
    )
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO app_logs(created_at_utc, category, action, details, request_content, response_content, error_content)
VALUES(@createdAtUtc, @category, @action, @details, @request, @response, @error);";
        cmd.Parameters.AddWithValue("@createdAtUtc", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@category", (category ?? "systeme").Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@action", Truncate(action));
        cmd.Parameters.AddWithValue("@details", Truncate(details));
        cmd.Parameters.AddWithValue("@request", Truncate(requestContent));
        cmd.Parameters.AddWithValue("@response", Truncate(responseContent));
        cmd.Parameters.AddWithValue("@error", Truncate(errorContent));
        await cmd.ExecuteNonQueryAsync();
    }

    private void EnsureSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS app_logs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    created_at_utc TEXT NOT NULL,
    category TEXT NOT NULL,
    action TEXT NOT NULL,
    details TEXT NULL,
    request_content TEXT NULL,
    response_content TEXT NULL,
    error_content TEXT NULL
);
CREATE INDEX IF NOT EXISTS idx_app_logs_created_at ON app_logs(created_at_utc DESC);
CREATE INDEX IF NOT EXISTS idx_app_logs_category ON app_logs(category);";
        cmd.ExecuteNonQuery();
    }

    private static string Truncate(string? value)
    {
        var v = value ?? string.Empty;
        if (v.Length <= MaxStoredLength) return v;
        return v[..MaxStoredLength] + "...(tronqué)";
    }
}

public sealed class PersistedLogEntry
{
    public long Id { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
    public string RequestContent { get; init; } = string.Empty;
    public string ResponseContent { get; init; } = string.Empty;
    public string ErrorContent { get; init; } = string.Empty;
}
