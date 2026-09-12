using Helldivers2ModManager.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

/// <summary>
/// 游戏库动画条目采用惰性解码，且解码只能发生在后台线程。这里固定
/// <see cref="ModelPreviewAnimationOption"/> 的缓存契约：UI 绑定读取的
/// <c>CachedLengthSeconds</c> 绝不触发解码，<c>ResolveClip</c> 只解码一次。
/// </summary>
[TestClass]
public sealed class ModelPreviewAnimationOptionTests
{
    [TestMethod]
    public void ResolveClip_LazyLoader_DecodesOnceAndCachesResult()
    {
        var loads = 0;
        var option = CreateLazyOption(() =>
        {
            loads++;
            return CreateClip(2.5f);
        });

        Assert.IsFalse(option.IsClipReady);
        Assert.AreEqual(0d, option.CachedLengthSeconds, "未解码时缓存时长必须为 0。");

        var first = option.ResolveClip();
        Assert.IsNotNull(first);
        Assert.AreEqual(2.5f, first.LengthSeconds);
        Assert.IsTrue(option.IsClipReady);
        Assert.AreEqual(2.5d, option.CachedLengthSeconds);

        var second = option.ResolveClip();
        Assert.AreSame(first, second);
        Assert.AreEqual(1, loads, "同一选项只允许解码一次。");
    }

    [TestMethod]
    public void CachedLengthSeconds_NeverTriggersDecode()
    {
        var loads = 0;
        var option = CreateLazyOption(() =>
        {
            loads++;
            return CreateClip(1f);
        });

        // UI 的时长/进度条/时间文本绑定的就是这个属性：读多少次都不得引发解码，
        // 否则选中动画的瞬间就会在 UI 线程上读游戏档案并冻结界面。
        for (var index = 0; index < 128; index++)
            _ = option.CachedLengthSeconds;

        Assert.AreEqual(0, loads, "CachedLengthSeconds 是只读缓存，绝不触发解码。");
        Assert.IsFalse(option.IsClipReady);
    }

    [TestMethod]
    public void ResolveClip_FailedLoadIsRemembered()
    {
        var loads = 0;
        var option = CreateLazyOption(() =>
        {
            loads++;
            return null;
        });

        Assert.IsNull(option.ResolveClip());
        Assert.IsNull(option.ResolveClip());

        Assert.AreEqual(1, loads, "解码失败同样记账，避免每次重建都重读游戏档案。");
        Assert.AreEqual(0d, option.CachedLengthSeconds);
    }

    [TestMethod]
    public void ResolveClip_EagerClipBypassesLazyLoader()
    {
        var loads = 0;
        var clip = CreateClip(0.5f);
        var option = new ModelPreviewAnimationOption
        {
            AnimationId = 0xDEADBEEF,
            StateNameHash = 0x1234,
            LayerIndex = 0,
            Clip = clip,
            ClipLoader = () =>
            {
                loads++;
                return null;
            }
        };

        Assert.IsTrue(option.IsClipReady);
        Assert.AreSame(clip, option.ResolveClip());
        Assert.AreEqual(0.5d, option.CachedLengthSeconds);
        Assert.AreEqual(0, loads, "构建期已解码的模组库条目不得再走惰性加载器。");
    }

    private static ModelPreviewAnimationOption CreateLazyOption(Func<ModelPreviewAnimationClip?> loader) => new()
    {
        AnimationId = 0xDEADBEEF,
        StateNameHash = 0x1234,
        LayerIndex = 0,
        ClipLoader = loader
    };

    private static ModelPreviewAnimationClip CreateClip(float lengthSeconds) => new()
    {
        AnimationId = 0xDEADBEEF,
        BoneCount = 1,
        LengthSeconds = lengthSeconds,
        IsAdditive = false,
        InitialPoses = [],
        Keyframes = [],
        Events = []
    };
}
