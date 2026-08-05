using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Services;

/// <summary>
/// 本地化服务 - 单例，负责加载 JSON 格式的本地化资源并提供运行时切换语言能力。
/// 实现 INotifyPropertyChanged 以便 WPF 绑定在语言切换时自动更新。
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class LocalizationService : INotifyPropertyChanged
{
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>
	/// 当前选中的语言代码（如 "zh-CN"、"en-US"）。
	/// 空字符串或 null 表示自动检测。
	/// </summary>
	public string SelectedLanguage
	{
		get => _selectedLanguage;
		set
		{
			if (_selectedLanguage == value)
				return;
			_selectedLanguage = value;
			ApplyLanguage(value);
		}
	}

	/// <summary>
	/// 实际生效的语言代码（自动检测后的结果）。
	/// </summary>
	public string CurrentLanguage => _currentLanguage;

	/// <summary>
	/// 当前语言的自定义名称（如 "中文"、"English"）。
	/// </summary>
	public string CurrentLanguageName => _currentLanguageName;

	/// <summary>
	/// 可用的语言列表（显示名称, 语言代码）。
	/// 第一个条目为「自动检测」。
	/// </summary>
	public ObservableCollection<LanguageItem> AvailableLanguages { get; } = [];

	/// <summary>
	/// 本地化字符串字典（扁平化键值对）。
	/// </summary>
	private readonly Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// 每个 locale 对应的完整数据（用于切换时重新加载）。
	/// key: locale code ("zh-CN"), value: 解析后的 locale 数据
	/// </summary>
	private readonly Dictionary<string, LocaleData> _localeCache = [];

	/// <summary>
	/// 语言文件元数据列表（仅扫描结果，strings 未解析）。
	/// </summary>
	private readonly List<LocaleFileInfo> _localeFiles = [];

	private readonly ILogger<LocalizationService> _logger;
	private readonly string _localesDirectory;

	private string _selectedLanguage = string.Empty;
	private string _currentLanguage = "en-US";
	private string _currentLanguageName = "English";

	/// <summary>
	/// 语言配置（用于 JSON 反序列化）。
	/// </summary>
	private sealed record LocaleData
	{
		public string Locale { get; init; } = string.Empty;
		public string LanguageName { get; init; } = string.Empty;
		public Dictionary<string, string> Strings { get; init; } = [];
	}

	/// <summary>
	/// 语言文件元数据（仅 locale 与显示名，避免构造时解析全部 strings）。
	/// </summary>
	private sealed record LocaleMeta
	{
		public string Locale { get; init; } = string.Empty;
		public string LanguageName { get; init; } = string.Empty;
	}

	/// <summary>
	/// 语言文件信息（用于按需完整解析）。
	/// </summary>
	private sealed record LocaleFileInfo(string FilePath, string Locale, string LanguageName);

	private static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		PropertyNameCaseInsensitive = true
	};

	public LocalizationService(ILogger<LocalizationService> logger)
		: this(logger, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Language"))
	{
	}

	/// <summary>
	/// 测试用构造：注入语言目录，避免依赖应用基目录下的资源文件。
	/// </summary>
	internal LocalizationService(ILogger<LocalizationService> logger, string localesDirectory)
	{
		_logger = logger;
		_localesDirectory = localesDirectory;

		// 1. 扫描可用 Language 文件（仅元数据）
		LoadAvailableLocales();

		// 2. 自动检测系统语言（此时仅记录检测结果，稍后在 ApplyLanguage 中设置）
		var detected = DetectSystemLanguage();

		// 3. 应用语言（默认使用自动检测结果）
		ApplyLanguage(detected);
	}

	/// <summary>
	/// 获取指定键的本地化字符串。
	/// 如果键不存在，返回 "[Key]" 格式的占位符。
	/// </summary>
	public string this[string key]
	{
		get
		{
			if (string.IsNullOrEmpty(key))
				return string.Empty;

			if (_strings.TryGetValue(key, out var value))
				return value;

			_logger.LogWarning("本地化键 '{Key}' 不存在（当前语言: {Lang}）", key, _currentLanguage);
			return $"[{key}]";
		}
	}

	/// <summary>
	/// 获取本地化字符串（指定默认值）。
	/// 如果键不存在，返回 fallback 值。
	/// </summary>
	public string Get(string key, string fallback = "")
	{
		if (_strings.TryGetValue(key, out var value))
			return value;
		return fallback;
	}

	/// <summary>
	/// 扫描 Locales 文件夹，仅解析元数据（locale/显示名），strings 按需完整解析。
	/// </summary>
	private void LoadAvailableLocales()
	{
		AvailableLanguages.Clear();

		// 第一个选项为"自动检测"
		AvailableLanguages.Add(new LanguageItem("Auto Detect", string.Empty));

		if (!Directory.Exists(_localesDirectory))
		{
			_logger.LogWarning("本地化文件夹不存在: {Path}", _localesDirectory);
			// 至少添加默认语言选项
			AvailableLanguages.Add(new LanguageItem("English", "en-US"));
			return;
		}

		foreach (var file in Directory.EnumerateFiles(_localesDirectory, "*.json"))
		{
			try
			{
				var meta = JsonSerializer.Deserialize<LocaleMeta>(File.ReadAllText(file), s_jsonOptions);
				if (meta is null || string.IsNullOrEmpty(meta.Locale))
				{
					_logger.LogWarning("本地化文件格式无效: {File}", file);
					continue;
				}

				_localeFiles.Add(new LocaleFileInfo(file, meta.Locale, meta.LanguageName));
				AvailableLanguages.Add(new LanguageItem(
					meta.LanguageName,
					meta.Locale
				));
				_logger.LogInformation("已扫描本地化文件: {Locale} ({Name})", meta.Locale, meta.LanguageName);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "加载本地化文件失败: {File}", file);
			}
		}

		// 确保至少有一个默认语言
		if (AvailableLanguages.Count == 0)
		{
			AvailableLanguages.Add(new LanguageItem("English", "en-US"));
		}
	}

	/// <summary>
	/// 自动检测系统语言。
	/// 返回匹配的语言代码，如果都不匹配则返回第一个可用语言。
	/// </summary>
	private string DetectSystemLanguage()
	{
		try
		{
			var systemCulture = CultureInfo.InstalledUICulture.Name; // "zh-CN", "en-US" 等
			_logger.LogInformation("系统语言: {Culture}", systemCulture);

			// 尝试精确匹配（zh-CN → zh-CN）
			if (_localeFiles.Any(f => string.Equals(f.Locale, systemCulture, StringComparison.OrdinalIgnoreCase)))
				return systemCulture;

			// 尝试匹配语言族（zh → zh-CN, zh-TW）
			var langPrefix = systemCulture.Split('-')[0];
			var match = _localeFiles.FirstOrDefault(f => f.Locale.StartsWith(langPrefix + "-", StringComparison.OrdinalIgnoreCase));
			if (match is not null)
				return match.Locale;

			// 回退到第一个可用语言
			var first = _localeFiles.FirstOrDefault();
			return first?.Locale ?? "en-US";
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "自动检测系统语言失败");
			return "en-US";
		}
	}

	/// <summary>
	/// 获取指定 locale 的完整数据；未解析时按需读取并解析对应 JSON 文件。
	/// 解析结果缓存在 <see cref="_localeCache"/> 中，后续切换语言直接复用。
	/// </summary>
	private LocaleData? GetOrLoadLocale(string locale)
	{
		lock (_localeCache)
		{
			if (_localeCache.TryGetValue(locale, out var cached))
				return cached;

			var info = _localeFiles.FirstOrDefault(f => string.Equals(f.Locale, locale, StringComparison.OrdinalIgnoreCase));
			if (info is null)
				return null;

			try
			{
				var json = File.ReadAllText(info.FilePath);
				var data = JsonSerializer.Deserialize<LocaleData>(json, s_jsonOptions);
				if (data is null || string.IsNullOrEmpty(data.Locale))
				{
					_logger.LogWarning("本地化文件格式无效: {File}", info.FilePath);
					return null;
				}

				_localeCache[data.Locale] = data;
				return data;
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "加载本地化文件失败: {File}", info.FilePath);
				return null;
			}
		}
	}

	/// <summary>
	/// 应用指定语言（加载对应 JSON 数据）。
	/// </summary>
	private void ApplyLanguage(string localeCode)
	{
		string targetLocale;

		if (string.IsNullOrEmpty(localeCode))
		{
			// 自动检测
			targetLocale = DetectSystemLanguage();
		}
		else
		{
			targetLocale = localeCode;
		}

		// 确保该 locale 已加载（未加载时按需解析）
		var data = GetOrLoadLocale(targetLocale);
		if (data is null)
		{
			_logger.LogWarning("语言 '{Locale}' 未加载，回退到 en-US", targetLocale);
			data = GetOrLoadLocale("en-US");
			if (data is null)
			{
				// 回退到第一个可用语言
				var first = _localeFiles.FirstOrDefault();
				if (first is null)
				{
					_logger.LogError("没有可用的本地化资源！");
					return;
				}
				data = GetOrLoadLocale(first.Locale);
				if (data is null)
				{
					_logger.LogError("没有可用的本地化资源！");
					return;
				}
				targetLocale = data.Locale;
			}
			else
			{
				targetLocale = "en-US";
			}
		}

		// 更新字符串字典
		_strings.Clear();
		foreach (var kvp in data.Strings)
		{
			_strings[kvp.Key] = kvp.Value;
		}

		_currentLanguage = targetLocale;
		_currentLanguageName = data.LanguageName;

		_logger.LogInformation("已切换到语言: {Locale} ({Name})", _currentLanguage, _currentLanguageName);

		// 通知所有绑定（"Item[]" 是 WPF 索引器绑定的标准通知名）
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguageName)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedLanguage)));
	}
}

/// <summary>
/// 语言选项（用于 ComboBox 绑定）。
/// </summary>
internal sealed record LanguageItem(string DisplayName, string LocaleCode)
{
	public override string ToString() => DisplayName;
}
