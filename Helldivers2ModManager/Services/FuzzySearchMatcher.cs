using System.Text;

namespace Helldivers2ModManager.Services;

/// <summary>
/// 搜索框模糊匹配核心 —— 纯函数、无状态。
/// 匹配层次（任一命中即匹配，保持原有列表顺序）：
///  1. 名称子串（保留原有精确子串语义，尊重大小写设置）；
///  2. 拼音子串（输入拼音全拼或首字母可搜中文名，如 "nx" / "ningxia" → 宁夏）；
///  3. 字符子序列（按顺序但不要求连续，如 "hd2" → "Helldivers 2"），对名称原文、全拼、首字母都适用；
///  4. 混合查询（查询中的汉字转首字母后匹配，如 "超J" → "cj"）。
/// 拼音/子序列部分没有大小写概念，始终不区分大小写；开启区分大小写时退化为纯精确子串匹配，
/// 避免英文名被拼音缓存小写化后破坏大小写敏感语义。
/// </summary>
internal static class FuzzySearchMatcher
{
	/// <summary>
	/// 判断名称是否命中模糊搜索查询。
	/// </summary>
	/// <param name="name">Mod 名称原文。</param>
	/// <param name="query">搜索词（保留原始大小写；大小写敏感由 caseSensitive 控制）。</param>
	/// <param name="caseSensitive">是否区分大小写（仅作用于名称子串/子序列；拼音部分不区分）。</param>
	/// <param name="fullPinyin">可选：全拼小写缓存（如 "diyuqianbing 4k caizhibao"），为空时内部转换。</param>
	/// <param name="firstLetters">可选：首字母小写缓存（如 "dyqb 4k czb"），为空时内部转换。</param>
	public static bool IsMatch(string name, string query, bool caseSensitive, string? fullPinyin = null, string? firstLetters = null)
	{
		if (string.IsNullOrEmpty(query))
			return true;

		var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

		// 1) 名称子串（最高优先级，保留用户已有的精确搜索习惯）
		if (name.Contains(query, comparison))
			return true;

		// 区分大小写模式退化为纯精确子串匹配，不再做模糊匹配：
		// 英文名经过拼音缓存小写化后，大小写敏感的子序列/拼音检查会让
		// 大写查询（如 "HELL"）错误命中 "hell..."，破坏区分大小写语义。
		if (caseSensitive)
			return false;

		fullPinyin ??= ToolGood.Words.Pinyin.WordsHelper.GetPinyin(name, false).ToLowerInvariant();
		firstLetters ??= ToolGood.Words.Pinyin.WordsHelper.GetFirstPinyin(name).ToLowerInvariant();

		// 拼音部分不区分大小写：查询统一小写后与缓存（已小写）做 Ordinal 比较
		var q = query.ToLowerInvariant();
		if (q.Length == 0)
			return false;

		// 2) 拼音/首字母子串
		if (fullPinyin.Contains(q, StringComparison.Ordinal)
			|| firstLetters.Contains(q, StringComparison.Ordinal))
			return true;

		// 3) 字符子序列（名称原文 / 全拼 / 首字母）
		var nameSeq = caseSensitive ? name : name.ToLowerInvariant();
		var qSeq = caseSensitive ? query : q;
		if (IsSubsequence(qSeq, nameSeq)
			|| IsSubsequence(q, fullPinyin)
			|| IsSubsequence(q, firstLetters))
			return true;

		// 4) 混合查询：查询中的汉字转首字母（如 "超J" → "cj"），再对拼音序列匹配
		if (TryBuildMixedQuery(q, out var mixed) && mixed.Length > 0 && mixed != q)
		{
			if (fullPinyin.Contains(mixed, StringComparison.Ordinal)
				|| firstLetters.Contains(mixed, StringComparison.Ordinal)
				|| IsSubsequence(mixed, fullPinyin)
				|| IsSubsequence(mixed, firstLetters))
				return true;
		}

		return false;
	}

	/// <summary>
	/// 子序列匹配：query 的字符按顺序出现在 target 中即可（不要求连续）。
	/// 调用方需保证 query 与 target 的大小写形式一致（要么都小写，要么都保持原文）。
	/// </summary>
	internal static bool IsSubsequence(ReadOnlySpan<char> query, ReadOnlySpan<char> target)
	{
		if (query.Length == 0)
			return true;
		if (query.Length > target.Length)
			return false;

		int qi = 0;
		for (int i = 0; i < target.Length && qi < query.Length; i++)
		{
			if (target[i] == query[qi])
				qi++;
		}
		return qi == query.Length;
	}

	/// <summary>
	/// 把查询串中的汉字逐个转为首字母（如 "超J" → "cj"），其余字符（字母/数字/空格）原样保留。
	/// 返回是否发生转换；未含汉字时 mixed 与原串相同，调用方应跳过这轮匹配。
	/// </summary>
	internal static bool TryBuildMixedQuery(string q, out string mixed)
	{
		mixed = q;
		bool changed = false;
		var sb = new StringBuilder(q.Length);
		foreach (var ch in q)
		{
			if (ch > 0x7F && char.IsLetter(ch))
			{
				var initial = ToolGood.Words.Pinyin.WordsHelper.GetFirstPinyin(ch.ToString()).ToLowerInvariant();
				if (initial.Length > 0)
				{
					sb.Append(initial[0]);
					changed = true;
					continue;
				}
			}
			sb.Append(ch);
		}
		if (changed)
			mixed = sb.ToString();
		return changed;
	}
}