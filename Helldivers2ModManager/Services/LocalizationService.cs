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

	private static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		PropertyNameCaseInsensitive = true
	};

	public LocalizationService(ILogger<LocalizationService> logger)
	{
		_logger = logger;
		// Resources/Language 文件夹位于应用程序基目录下
		_localesDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Language");

		// 1. 扫描可用 Language 文件
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
	/// 扫描 Locales 文件夹，加载所有可用的 locale 文件。
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
				var json = File.ReadAllText(file);
				var data = JsonSerializer.Deserialize<LocaleData>(json, s_jsonOptions);
				if (data is null || string.IsNullOrEmpty(data.Locale))
				{
					_logger.LogWarning("本地化文件格式无效: {File}", file);
					continue;
				}

				_localeCache[data.Locale] = data;
				AvailableLanguages.Add(new LanguageItem(
					data.LanguageName,
					data.Locale
				));
				_logger.LogInformation("已加载本地化文件: {Locale} ({Name})", data.Locale, data.LanguageName);
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
			if (_localeCache.ContainsKey(systemCulture))
				return systemCulture;

			// 尝试匹配语言族（zh → zh-CN, zh-TW）
			var langPrefix = systemCulture.Split('-')[0];
			var match = _localeCache.Keys.FirstOrDefault(k => k.StartsWith(langPrefix + "-", StringComparison.OrdinalIgnoreCase));
			if (match is not null)
				return match;

			// 回退到第一个可用语言
			var first = _localeCache.Keys.FirstOrDefault();
			return first ?? "en-US";
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "自动检测系统语言失败");
			return "en-US";
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

		// 确保该 locale 已加载
		if (!_localeCache.TryGetValue(targetLocale, out var data))
		{
			_logger.LogWarning("语言 '{Locale}' 未加载，回退到 en-US", targetLocale);
			if (!_localeCache.TryGetValue("en-US", out data))
			{
				// 回退到第一个可用语言
				var first = _localeCache.Values.FirstOrDefault();
				if (first is null)
				{
					_logger.LogError("没有可用的本地化资源！");
					return;
				}
				data = first;
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
