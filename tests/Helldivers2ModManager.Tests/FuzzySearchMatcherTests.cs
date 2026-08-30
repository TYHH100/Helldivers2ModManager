using Helldivers2ModManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

/// <summary>
/// 搜索框模糊匹配核心（FuzzySearchMatcher）测试：
/// 名称子串（保留原有行为）、拼音全拼/首字母、字符子序列、混合查询、大小写开关。
/// </summary>
[TestClass]
public sealed class FuzzySearchMatcherTests
{
    // ===== 名称子串（原有精确子串语义保留） =====

    [TestMethod]
    public void ExactSubstring_ChineseName_Matches() =>
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("地狱潜兵", "地狱", caseSensitive: false));

    [TestMethod]
    public void ExactSubstring_EnglishName_Matches() =>
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("Helldivers 2", "diver", caseSensitive: false));

    [TestMethod]
    public void ExactSubstring_RespectsCaseSensitive()
    {
        // 不区分大小写时英文子串命中；区分大小写时大小写必须完全一致（且不做拼音/子序列模糊）
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("Helldivers 2", "HELL", caseSensitive: false));
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("Helldivers 2", "Hell", caseSensitive: true));
        Assert.IsFalse(FuzzySearchMatcher.IsMatch("Helldivers 2", "hell", caseSensitive: true));
        Assert.IsFalse(FuzzySearchMatcher.IsMatch("Helldivers 2", "HELL", caseSensitive: true));
        // 区分大小写且不一致时，小写拼音缓存不得让大写查询命中（回归：早期实现会命中）
        Assert.IsFalse(FuzzySearchMatcher.IsMatch("Helldivers 2", "hell2", caseSensitive: true));
    }

    // ===== 拼音全拼 =====

    [TestMethod]
    public void FullPinyin_ChineseName_Matches()
    {
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("雷霆战甲", "leitingzhanjia", caseSensitive: false));
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("地狱潜兵", "diyuqianbing", caseSensitive: false));
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("装甲战士", "zhuangjiazhanshi", caseSensitive: false));
    }

    [TestMethod]
    public void PartialPinyin_ChineseName_Matches()
    {
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("雷霆战甲", "leiting", caseSensitive: false));
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("地狱潜兵", "diyu", caseSensitive: false));
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("装甲战士", "zhuang", caseSensitive: false));
    }

    // ===== 首字母 =====

    [TestMethod]
    public void FirstLetters_ChineseName_Matches()
    {
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("雷霆战甲", "lt", caseSensitive: false));
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("雷霆战甲", "ltzj", caseSensitive: false));
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("地狱潜兵", "dyqb", caseSensitive: false));
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("超级地球防卫军", "cjdqfwj", caseSensitive: false));
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("装甲战士", "zjs", caseSensitive: false), "首字母子序列也应命中");
    }

    // ===== 字符子序列（英文+数字名） =====

    [TestMethod]
    public void Subsequence_EnglishName_Matches()
    {
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("Helldivers 2", "hd2", caseSensitive: false));
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("Helldivers 2", "hld2", caseSensitive: false));
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("HD Textures", "hdtx", caseSensitive: false));
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("HD2 Enhanced Graphics", "hdg", caseSensitive: false));
    }

    // ===== 混合查询（汉字+字母） =====

    [TestMethod]
    public void MixedQuery_ChinesePlusInitial_Matches()
    {
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("超级地球防卫军", "超J", caseSensitive: false));
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("超级地球防卫军", "超级J", caseSensitive: false));
    }

    // ===== 不匹配 =====

    [TestMethod]
    public void NoMatch_ReturnsFalse()
    {
        Assert.IsFalse(FuzzySearchMatcher.IsMatch("地狱潜兵", "sadfsadf", caseSensitive: false));
        Assert.IsFalse(FuzzySearchMatcher.IsMatch("Helldivers 2", "xyz", caseSensitive: false));
        Assert.IsFalse(FuzzySearchMatcher.IsMatch("雷霆战甲", "mengxiang", caseSensitive: false));
    }

    // ===== 与 ModViewModel 预计算缓存配合 =====

    [TestMethod]
    public void WithPrecomputedPinyinCache_Matches()
    {
        string full = ToolGood.Words.Pinyin.WordsHelper.GetPinyin("地狱潜兵 4K 材质包", false).ToLowerInvariant();
        string first = ToolGood.Words.Pinyin.WordsHelper.GetFirstPinyin("地狱潜兵 4K 材质包").ToLowerInvariant();

        Assert.IsTrue(FuzzySearchMatcher.IsMatch("地狱潜兵 4K 材质包", "dyqb", false, full, first));
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("地狱潜兵 4K 材质包", "czb", false, full, first));
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("地狱潜兵 4K 材质包", "4k", false, full, first));
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("地狱潜兵 4K 材质包", "d4", false, full, first), "跨中英混排的子序列");
        Assert.IsFalse(FuzzySearchMatcher.IsMatch("地狱潜兵 4K 材质包", "zzzz", false, full, first));
    }

    // ===== 边界：空查询 =====

    [TestMethod]
    public void EmptyQuery_MatchesAll()
    {
        // 空查询由上层（IsSearchEmpty）短路，这里保证数学语义：空查询视为匹配
        Assert.IsTrue(FuzzySearchMatcher.IsMatch("任意名称", string.Empty, caseSensitive: false));
    }

    // ===== 子序列工具 =====

    [TestMethod]
    public void IsSubsequence_Basic()
    {
        Assert.IsTrue(FuzzySearchMatcher.IsSubsequence("hd2", "helldivers 2"));
        Assert.IsTrue(FuzzySearchMatcher.IsSubsequence("", "anything"));
        Assert.IsFalse(FuzzySearchMatcher.IsSubsequence("dh", "helldivers 2"));
        Assert.IsFalse(FuzzySearchMatcher.IsSubsequence("abc", "ab"));
    }
}