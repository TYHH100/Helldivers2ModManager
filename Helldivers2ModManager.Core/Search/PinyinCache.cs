using ToolGood.Words.Pinyin;

namespace Helldivers2ModManager.Core.Search;

/// <summary>
/// 拼音转换的统一入口：输出小写全拼与首字母，供调用方按名称缓存后传给
/// <see cref="FuzzySearchMatcher.IsMatch"/>，避免过滤热路径内重复转换。
/// </summary>
public static class PinyinCache
{
    public static (string FullPinyin, string FirstLetters) Get(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return (
            WordsHelper.GetPinyin(name, false).ToLowerInvariant(),
            WordsHelper.GetFirstPinyin(name).ToLowerInvariant());
    }
}
