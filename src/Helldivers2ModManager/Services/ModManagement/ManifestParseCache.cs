using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Services;

internal sealed class CachedModProblem
{
	public int Kind { get; set; }

	public string? ExtraData { get; set; }
}

/// <summary>
/// 单个模组目录的解析缓存条目。ManifestJson 与 Problems 是同一次解析的产物；
/// ManifestJson 为 null 时表示清单本身解析失败，FailureKind 记录对应的问题类型。
/// </summary>
internal sealed class CachedModEntry
{
	public string Fingerprint { get; set; } = string.Empty;

	public string? ManifestJson { get; set; }

	/// <summary>清单解析成功且 CheckPaths 未发现阻断性错误（可构造 ModData）。</summary>
	public bool Succeeded { get; set; }

	public int? FailureKind { get; set; }

	public List<CachedModProblem> Problems { get; set; } = [];
}

/// <summary>
/// 启动加载用的 manifest 解析结果缓存：以模组目录的文件树指纹为键，
/// 命中时跳过 manifest.json 反序列化和 CheckPaths 的成千上万次文件存在性检查。
/// 指纹覆盖目录内全部文件的相对路径、大小与修改时间，任何文件变动都会失效，
/// 因此缓存的清单与问题列表不会出现陈旧结果。
/// </summary>
internal sealed class ManifestParseCache
{
	private const int CurrentVersion = 1;

	private sealed class CacheFileModel
	{
		public int Version { get; set; }

		public Dictionary<string, CachedModEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
	}

	private readonly Dictionary<string, CachedModEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
	private readonly ILogger _logger;
	private bool _dirty;

	public ManifestParseCache(ILogger logger)
	{
		_logger = logger;
	}

	/// <summary>从存储目录读取缓存；文件缺失、损坏或版本不符时返回空缓存。</summary>
	public static ManifestParseCache Load(string storageDirectory, ILogger logger)
	{
		var cache = new ManifestParseCache(logger);
		var path = GetCacheFilePath(storageDirectory);
		if (!File.Exists(path))
			return cache;

		try
		{
			using var stream = File.OpenRead(path);
			var model = JsonSerializer.Deserialize<CacheFileModel>(stream);
			if (model is null || model.Version != CurrentVersion)
			{
				logger.LogInformation("Manifest parse cache missing or has incompatible version, rebuilding");
				return cache;
			}

			foreach (var (key, entry) in model.Entries)
			{
				if (entry?.Fingerprint is { Length: > 0 })
					cache._entries[key] = entry;
			}
			logger.LogInformation("Loaded manifest parse cache with {} entries", cache._entries.Count);
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Failed to read manifest parse cache, ignoring it");
		}

		return cache;
	}

	public bool TryGet(string directoryName, string fingerprint, [NotNullWhen(true)] out CachedModEntry? entry)
	{
		entry = null;
		if (!_entries.TryGetValue(directoryName, out var candidate) || candidate.Fingerprint != fingerprint)
			return false;
		entry = candidate;
		return true;
	}

	public void Store(string directoryName, CachedModEntry entry)
	{
		_entries[directoryName] = entry;
		_dirty = true;
	}

	public void Remove(string directoryName)
	{
		if (_entries.Remove(directoryName))
			_dirty = true;
	}

	/// <summary>把缓存原子写入存储目录（临时文件 + 替换），失败只记日志。</summary>
	public void Save(string storageDirectory)
	{
		if (!_dirty)
			return;

		try
		{
			var path = GetCacheFilePath(storageDirectory);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);

			var model = new CacheFileModel
			{
				Version = CurrentVersion,
				Entries = _entries,
			};
			var tempPath = path + ".tmp";
			var options = new JsonSerializerOptions
			{
				WriteIndented = false,
				DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			};
			using (var stream = File.Create(tempPath))
				JsonSerializer.Serialize(stream, model, options);
			File.Move(tempPath, path, overwrite: true);

			_dirty = false;
			_logger.LogDebug("Saved manifest parse cache with {} entries", _entries.Count);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to save manifest parse cache");
		}
	}

	private static string GetCacheFilePath(string storageDirectory)
		=> Path.Combine(storageDirectory, "cache", "manifest_parse_cache.json");

	/// <summary>
	/// 计算模组目录的文件树指纹：全部文件（递归，不进入符号链接目录）的
	/// 相对路径、长度和 UTC 修改时间的聚合哈希。单次顺序遍历，不做内容读取。
	/// </summary>
	public static string ComputeFingerprint(DirectoryInfo dir)
	{
		var sb = new StringBuilder(1024);
		var stack = new Stack<DirectoryInfo>();
		stack.Push(dir);

		while (stack.Count > 0)
		{
			var current = stack.Pop();

			foreach (var sub in current.EnumerateDirectories())
			{
				// 不进入符号链接目录，避免环形结构导致死循环
				if ((sub.Attributes & FileAttributes.ReparsePoint) == 0)
					stack.Push(sub);
			}

			foreach (var file in current.EnumerateFiles())
			{
				var relative = file.FullName[dir.FullName.Length..];
				sb.Append(relative).Append('|')
					.Append(file.Length).Append('|')
					.Append(file.LastWriteTimeUtc.Ticks).Append('\n');
			}
		}

		var bytes = Encoding.UTF8.GetBytes(sb.ToString());
		return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
	}
}
