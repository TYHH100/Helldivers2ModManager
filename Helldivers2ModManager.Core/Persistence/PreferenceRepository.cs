using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Helldivers2ModManager.Core.Persistence;

public sealed class PreferenceRepository(Database database)
{
    public async Task<AppSettings?> GetAppSettingsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var json = await database.ExecuteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM preferences WHERE Key=$key";
            command.Parameters.AddWithValue("$key", key);
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result as string;
        }, cancellationToken).ConfigureAwait(false);
        return json is null ? null : JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.AppSettings);
    }

    public Task SetAppSettingsAsync(string key, AppSettings value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var json = JsonSerializer.Serialize(value, PersistenceJsonContext.Default.AppSettings);
        return database.ExecuteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO preferences(Key, Value, UpdatedAtUtc) VALUES($key,$value,$updated)
                ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value, UpdatedAtUtc=excluded.UpdatedAtUtc
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", json);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM preferences WHERE Key=$key";
            command.Parameters.AddWithValue("$key", key);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
}
