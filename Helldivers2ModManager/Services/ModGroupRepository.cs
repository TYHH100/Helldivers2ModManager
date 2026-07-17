using Helldivers2ModManager.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace Helldivers2ModManager.Services;

[RegisterService(ServiceLifetime.Singleton)]
internal sealed class ModGroupRepository
{
    private readonly ILogger<ModGroupRepository> _logger;
    private readonly DatabaseService _databaseService;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false,
    };

    public ModGroupRepository(ILogger<ModGroupRepository> logger, DatabaseService databaseService)
    {
        _logger = logger;
        _databaseService = databaseService;
    }

    public List<ModGroup> LoadGroups(string storageDirectory)
    {
        using var connection = _databaseService.OpenConnection(storageDirectory);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, DisplayIndex, ModGuids, CreatedAtUtc FROM mod_groups ORDER BY DisplayIndex ASC;";

        var groups = new List<ModGroup>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            try
            {
                var id = Guid.Parse(reader.GetString(0));
                var name = reader.GetString(1);
                var displayIndex = reader.GetInt32(2);
                var modGuids = ParseGuidList(reader.GetString(3));
                var createdAtText = reader.GetString(4);
                var createdAtUtc = DateTime.TryParse(createdAtText, out var createdAt)
                    ? DateTime.SpecifyKind(createdAt, DateTimeKind.Utc)
                    : DateTime.UtcNow;

                groups.Add(new ModGroup
                {
                    Id = id,
                    Name = name,
                    DisplayIndex = displayIndex,
                    CreatedAtUtc = createdAtUtc,
                    ModGuids = new ObservableCollection<Guid>(modGuids),
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解析分组记录失败，跳过该条记录");
            }
        }

        return groups;
    }

    public async Task SaveGroupsAsync(string storageDirectory, IEnumerable<ModGroup> groups)
    {
        _databaseService.EnsureWritable(storageDirectory);
        var groupList = groups.ToList();
        await _writeLock.WaitAsync();
        try
        {
            using var connection = _databaseService.OpenConnection(storageDirectory);
            using var transaction = connection.BeginTransaction();
            try
            {
                using (var deleteCmd = connection.CreateCommand())
                {
                    deleteCmd.Transaction = transaction;
                    deleteCmd.CommandText = "DELETE FROM mod_groups;";
                    deleteCmd.ExecuteNonQuery();
                }

                using (var insertCmd = connection.CreateCommand())
                {
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = @"
						INSERT INTO mod_groups (Id, Name, DisplayIndex, ModGuids, CreatedAtUtc)
						VALUES (@Id, @Name, @DisplayIndex, @ModGuids, @CreatedAtUtc);
					";
                    var idParam = insertCmd.Parameters.Add("@Id", SqliteType.Text);
                    var nameParam = insertCmd.Parameters.Add("@Name", SqliteType.Text);
                    var displayIndexParam = insertCmd.Parameters.Add("@DisplayIndex", SqliteType.Integer);
                    var modGuidsParam = insertCmd.Parameters.Add("@ModGuids", SqliteType.Text);
                    var createdAtUtcParam = insertCmd.Parameters.Add("@CreatedAtUtc", SqliteType.Text);

                    for (int i = 0; i < groupList.Count; i++)
                    {
                        var group = groupList[i];
                        group.DisplayIndex = i;
                        idParam.Value = group.Id.ToString();
                        nameParam.Value = group.Name;
                        displayIndexParam.Value = group.DisplayIndex;
                        modGuidsParam.Value = JsonSerializer.Serialize(group.ModGuids.Distinct().Select(static g => g.ToString()), s_jsonOptions);
                        createdAtUtcParam.Value = group.CreatedAtUtc.ToString("O");
                        insertCmd.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public List<GroupedEnabledData> LoadStates(string storageDirectory, Guid groupId)
    {
        using var connection = _databaseService.OpenConnection(storageDirectory);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT GroupId, Guid, Enabled, Toggled, Selected, SortOrder FROM group_enabled_mods WHERE GroupId = @GroupId ORDER BY SortOrder ASC;";
        cmd.Parameters.AddWithValue("@GroupId", groupId.ToString());

        var states = new List<GroupedEnabledData>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            try
            {
                states.Add(new GroupedEnabledData
                {
                    GroupId = Guid.Parse(reader.GetString(0)),
                    Guid = Guid.Parse(reader.GetString(1)),
                    Enabled = reader.GetInt32(2) != 0,
                    Toggled = JsonSerializer.Deserialize<bool[]>(reader.GetString(3), s_jsonOptions) ?? [],
                    Selected = JsonSerializer.Deserialize<int[]>(reader.GetString(4), s_jsonOptions) ?? [],
                    SortOrder = reader.GetInt32(5),
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解析分组状态记录失败，跳过该条记录");
            }
        }

        return states;
    }

    public async Task SaveStatesAsync(string storageDirectory, Guid groupId, IEnumerable<GroupedEnabledData> states)
    {
        _databaseService.EnsureWritable(storageDirectory);
        var stateList = states.ToList();
        await _writeLock.WaitAsync();
        try
        {
            using var connection = _databaseService.OpenConnection(storageDirectory);
            using var transaction = connection.BeginTransaction();
            try
            {
                using (var deleteCmd = connection.CreateCommand())
                {
                    deleteCmd.Transaction = transaction;
                    deleteCmd.CommandText = "DELETE FROM group_enabled_mods WHERE GroupId = @GroupId;";
                    deleteCmd.Parameters.AddWithValue("@GroupId", groupId.ToString());
                    deleteCmd.ExecuteNonQuery();
                }

                using (var insertCmd = connection.CreateCommand())
                {
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = @"
						INSERT INTO group_enabled_mods (GroupId, Guid, Enabled, Toggled, Selected, SortOrder)
						VALUES (@GroupId, @Guid, @Enabled, @Toggled, @Selected, @SortOrder);
					";
                    var groupIdParam = insertCmd.Parameters.Add("@GroupId", SqliteType.Text);
                    var guidParam = insertCmd.Parameters.Add("@Guid", SqliteType.Text);
                    var enabledParam = insertCmd.Parameters.Add("@Enabled", SqliteType.Integer);
                    var toggledParam = insertCmd.Parameters.Add("@Toggled", SqliteType.Text);
                    var selectedParam = insertCmd.Parameters.Add("@Selected", SqliteType.Text);
                    var sortOrderParam = insertCmd.Parameters.Add("@SortOrder", SqliteType.Integer);

                    for (int i = 0; i < stateList.Count; i++)
                    {
                        var state = stateList[i];
                        groupIdParam.Value = groupId.ToString();
                        guidParam.Value = state.Guid.ToString();
                        enabledParam.Value = state.Enabled ? 1 : 0;
                        toggledParam.Value = JsonSerializer.Serialize(state.Toggled, s_jsonOptions);
                        selectedParam.Value = JsonSerializer.Serialize(state.Selected, s_jsonOptions);
                        sortOrderParam.Value = i;
                        insertCmd.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteGroupAsync(string storageDirectory, Guid groupId)
    {
        _databaseService.EnsureWritable(storageDirectory);
        await _writeLock.WaitAsync();
        try
        {
            using var connection = _databaseService.OpenConnection(storageDirectory);
            using var transaction = connection.BeginTransaction();
            try
            {
                using (var statesCmd = connection.CreateCommand())
                {
                    statesCmd.Transaction = transaction;
                    statesCmd.CommandText = "DELETE FROM group_enabled_mods WHERE GroupId = @GroupId;";
                    statesCmd.Parameters.AddWithValue("@GroupId", groupId.ToString());
                    statesCmd.ExecuteNonQuery();
                }

                using (var groupCmd = connection.CreateCommand())
                {
                    groupCmd.Transaction = transaction;
                    groupCmd.CommandText = "DELETE FROM mod_groups WHERE Id = @GroupId;";
                    groupCmd.Parameters.AddWithValue("@GroupId", groupId.ToString());
                    groupCmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteStatesByGuidsAsync(string storageDirectory, IEnumerable<Guid> guids)
    {
        _databaseService.EnsureWritable(storageDirectory);
        var guidList = guids.Distinct().ToList();
        if (guidList.Count == 0)
            return;

        await _writeLock.WaitAsync();
        try
        {
            using var connection = _databaseService.OpenConnection(storageDirectory);
            using var transaction = connection.BeginTransaction();
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM group_enabled_mods WHERE Guid = @Guid;";
                var guidParam = cmd.Parameters.Add("@Guid", SqliteType.Text);

                foreach (var guid in guidList)
                {
                    guidParam.Value = guid.ToString();
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public bool HasStates(string storageDirectory, Guid groupId)
    {
        using var connection = _databaseService.OpenConnection(storageDirectory);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM group_enabled_mods WHERE GroupId = @GroupId;";
        cmd.Parameters.AddWithValue("@GroupId", groupId.ToString());
        return (long)cmd.ExecuteScalar()! > 0;
    }

    private List<Guid> ParseGuidList(string json)
    {
        try
        {
            var values = JsonSerializer.Deserialize<List<string>>(json, s_jsonOptions) ?? [];
            var result = new List<Guid>();
            foreach (var value in values)
            {
                if (Guid.TryParse(value, out var guid) && !result.Contains(guid))
                    result.Add(guid);
                else
                    _logger.LogWarning("分组成员 GUID 无效或重复，已跳过: {Guid}", value);
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "解析分组成员列表失败，默认使用空列表");
            return [];
        }
    }
}
