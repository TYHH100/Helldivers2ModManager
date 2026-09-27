using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Helldivers2ModManager.Models;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;
using Jalium.UI.Threading;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class ModelPreviewPageView
{
    private readonly TextBlock _animationHeading = new();
    private readonly VirtualizedAnimationPicker _animationPicker = new();
    private readonly Button _animationPlayButton = new();
    private readonly Button _animationResetButton = new();
    private readonly Slider _animationTime = new();
    private readonly TextBlock _animationTimeText = new();
    private Border _animationGroup = null!;
    private readonly Dispatcher _animationDispatcher = Dispatcher.CurrentDispatcher;
    private readonly Stopwatch _animationClock = new();
    private System.Threading.Timer? _animationTimer;
    private CancellationTokenSource _animationCancellation = new();
    private IReadOnlyList<AnimationChoice> _animationChoices = [];
    private AnimationChoice? _selectedAnimation;
    private ModelPreviewAnimationClip? _selectedClip;
    private ConcurrentDictionary<ModelPreviewSkeleton, ModelPreviewAnimationBinding> _animationBindings = new();
    private bool _changingAnimationSelection;
    private bool _animationApplied;
    private bool _animationPlaying;
    private bool _animationFrameBusy;
    private bool _animationFrameRequested;
    private int _animationGeneration;
    private double _animationDuration;

    internal VirtualizedAnimationPicker AnimationPicker => _animationPicker;
    internal Slider AnimationTime => _animationTime;
    internal Task PendingAnimationSelection { get; private set; } = Task.CompletedTask;
    internal Task PendingAnimationFrame { get; private set; } = Task.CompletedTask;

    private void BuildAnimationControls(WrapPanel groups)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _animationHeading.Foreground = Secondary;
        _animationHeading.VerticalAlignment = VerticalAlignment.Center;
        _animationHeading.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(_animationHeading);
        _animationPicker.MinWidth = 210;
        _animationPicker.Margin = new Thickness(0, 0, 6, 0);
        _animationPicker.SelectionChanged += index =>
        {
            if (!_changingAnimationSelection)
                SelectAnimation(index);
        };
        row.Children.Add(_animationPicker);
        _animationPlayButton.Width = _animationPlayButton.Height = 30;
        _animationPlayButton.Margin = new Thickness(0, 0, 4, 0);
        _animationPlayButton.Click += (_, _) => ToggleAnimation();
        row.Children.Add(_animationPlayButton);
        _animationResetButton.Width = _animationResetButton.Height = 30;
        _animationResetButton.Margin = new Thickness(0, 0, 6, 0);
        _animationResetButton.Content = new TextBlock { Text = "\uE777",
            FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 14 };
        _animationResetButton.Click += (_, _) => ResetAnimationPose();
        row.Children.Add(_animationResetButton);
        _animationTime.Minimum = 0;
        _animationTime.Maximum = 0;
        _animationTime.Width = 110;
        _animationTime.VerticalAlignment = VerticalAlignment.Center;
        _animationTime.Margin = new Thickness(0, 0, 6, 0);
        _animationTime.ValueChanged += (_, _) =>
        {
            if (_changingAnimationSelection || _selectedClip is null)
                return;
            _animationApplied = true;
            RefreshAnimationTimeText();
            QueueAnimationFrame();
        };
        row.Children.Add(_animationTime);
        _animationTimeText.MinWidth = 78;
        _animationTimeText.Foreground = Secondary;
        _animationTimeText.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(_animationTimeText);
        _animationGroup = ToolGroup(row);
        _animationGroup.Visibility = Visibility.Collapsed;
        groups.Children.Add(_animationGroup);
        _animationTimer = new System.Threading.Timer(_ =>
            _animationDispatcher.BeginInvoke(AdvanceAnimation), null,
            Timeout.Infinite, Timeout.Infinite);
        RefreshAnimationTexts();
    }

    private void RefreshAnimationTexts()
    {
        _animationHeading.Text = _localization["ModelPreviewPage.Animation"];
        _animationPicker.SetLabels(_localization["ModelPreviewPage.AnimationPlaceholder"],
            _localization["ModelPreviewPage.AnimationSearchHint"],
            _localization["ModelPreviewPage.AnimationNoMatch"]);
        _animationResetButton.ToolTip = _localization["ModelPreviewPage.ResetAnimation"];
        _animationPlayButton.ToolTip = _localization[_animationPlaying
            ? "ModelPreviewPage.PauseAnimation" : "ModelPreviewPage.PlayAnimation"];
        _animationPlayButton.Content = new TextBlock
        {
            Text = _animationPlaying ? "\uE769" : "\uE768",
            FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 14,
        };
        RefreshAnimationTimeText();
    }

    private void RefreshAnimationTimeText() =>
        _animationTimeText.Text = $"{_animationTime.Value:0.00} / {_animationDuration:0.00} s";

    private void SetAnimationChoices(ModelPreviewResult result)
    {
        ResetAnimationState();
        _animationChoices = result.AnimationLibraries.SelectMany(library =>
            library.Animations.Select(option => new AnimationChoice(library, option,
                option.DisplayName + (library.IsFromMod
                    ? _localization["ModelPreviewPage.ModAnimationSource"] : string.Empty))))
            .ToArray();
        _changingAnimationSelection = true;
        try
        {
            _animationPicker.SetItems(_animationChoices.Select(static choice => choice.Name).ToArray());
        }
        finally { _changingAnimationSelection = false; }
        _animationGroup.Visibility = _animationChoices.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ResetAnimationState()
    {
        StopAnimation();
        _animationCancellation.Cancel();
        _animationCancellation.Dispose();
        _animationCancellation = new CancellationTokenSource();
        _animationGeneration++;
        _animationChoices = [];
        _selectedAnimation = null;
        _selectedClip = null;
        _animationDuration = 0;
        _animationApplied = false;
        _animationBindings = new();
        _changingAnimationSelection = true;
        try
        {
            _animationPicker.SetItems([]);
            _animationTime.Maximum = 0;
            _animationTime.Value = 0;
        }
        finally { _changingAnimationSelection = false; }
        _animationGroup.Visibility = Visibility.Collapsed;
        RefreshAnimationTimeText();
    }

    private void SelectAnimation(int index) =>
        PendingAnimationSelection = SelectAnimationAsync(index);

    private async Task SelectAnimationAsync(int index)
    {
        StopAnimation();
        _animationCancellation.Cancel();
        _animationCancellation.Dispose();
        _animationCancellation = new CancellationTokenSource();
        var token = _animationCancellation.Token;
        var generation = ++_animationGeneration;
        _selectedAnimation = index >= 0 && index < _animationChoices.Count
            ? _animationChoices[index] : null;
        _selectedClip = null;
        _animationDuration = 0;
        _animationApplied = false;
        _animationBindings = new();
        _changingAnimationSelection = true;
        try
        {
            _animationTime.Maximum = 0;
            _animationTime.Value = 0;
        }
        finally { _changingAnimationSelection = false; }
        _scene.ApplyPose(null);
        RefreshAnimationTimeText();
        if (_selectedAnimation is not { } choice)
            return;
        try
        {
            // Game clips are decoded lazily and can read large archives.
            var clip = await Task.Run(choice.Option.ResolveClip, token);
            if (_disposed || token.IsCancellationRequested || generation != _animationGeneration)
                return;
            _selectedClip = clip;
            _animationDuration = clip?.LengthSeconds ?? 0;
            _animationTime.Maximum = _animationDuration;
            RefreshAnimationTimeText();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed && generation == _animationGeneration)
                _status.Text = _localization["ModelPreviewPage.LoadFailed"]
                    .Replace("{message}", ex.Message);
        }
    }

    private void ToggleAnimation()
    {
        if (_selectedClip is null || _animationDuration <= 0)
            return;
        if (_animationPlaying)
        {
            StopAnimation();
            return;
        }
        _animationApplied = true;
        _animationPlaying = true;
        _animationClock.Restart();
        _animationTimer?.Change(0, 30);
        RefreshAnimationTexts();
        QueueAnimationFrame();
    }

    private void StopAnimation()
    {
        _animationTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _animationClock.Reset();
        _animationPlaying = false;
        RefreshAnimationTexts();
    }

    private void ResetAnimationPose()
    {
        StopAnimation();
        _animationApplied = false;
        _changingAnimationSelection = true;
        try { _animationTime.Value = 0; }
        finally { _changingAnimationSelection = false; }
        _scene.ApplyPose(null);
        RefreshAnimationTimeText();
    }

    private void AdvanceAnimation()
    {
        if (_disposed || !_animationPlaying || _animationDuration <= 0)
            return;
        var elapsed = _animationClock.Elapsed.TotalSeconds;
        _animationClock.Restart();
        _changingAnimationSelection = true;
        try { _animationTime.Value = (_animationTime.Value + elapsed) % _animationDuration; }
        finally { _changingAnimationSelection = false; }
        RefreshAnimationTimeText();
        QueueAnimationFrame();
    }

    private void QueueAnimationFrame()
    {
        if (!_animationApplied || _selectedClip is null || _selectedAnimation is null
            || _result is null || _disposed)
            return;
        _animationFrameRequested = true;
        if (!_animationFrameBusy)
            PendingAnimationFrame = RunAnimationFramesAsync();
    }

    private async Task RunAnimationFramesAsync()
    {
        _animationFrameBusy = true;
        try
        {
            while (_animationFrameRequested && !_disposed)
            {
                _animationFrameRequested = false;
                var choice = _selectedAnimation;
                var clip = _selectedClip;
                if (choice is null || clip is null)
                    return;
                var generation = _animationGeneration;
                var token = _animationCancellation.Token;
                var meshes = GetVisibleMeshes().ToArray();
                var time = (float)_animationTime.Value;
                var bindings = _animationBindings;
                var pose = await Task.Run(() => SkinFrame(meshes, choice, clip,
                    time, bindings, token), token);
                if (!_disposed && !token.IsCancellationRequested
                    && generation == _animationGeneration && _animationApplied)
                    _scene.ApplyPose(pose);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed)
                _status.Text = _localization["ModelPreviewPage.LoadFailed"]
                    .Replace("{message}", ex.Message);
        }
        finally
        {
            _animationFrameBusy = false;
            if (_animationFrameRequested && !_disposed)
                PendingAnimationFrame = RunAnimationFramesAsync();
        }
    }

    private static Dictionary<ModelPreviewMesh, float[]> SkinFrame(
        IReadOnlyList<ModelPreviewMesh> meshes, AnimationChoice choice,
        ModelPreviewAnimationClip clip, float time,
        ConcurrentDictionary<ModelPreviewSkeleton, ModelPreviewAnimationBinding> bindings,
        CancellationToken token)
    {
        var pose = new Dictionary<ModelPreviewMesh, float[]>();
        var transforms = new Dictionary<ModelPreviewSkeleton, IReadOnlyList<Matrix4x4>>();
        foreach (var mesh in meshes)
        {
            token.ThrowIfCancellationRequested();
            if (mesh.Skinning is not { } skinning
                || !ModelPreviewAnimationCompatibility.IsCompatible(skinning.Skeleton,
                    choice.Library))
                continue;
            if (!transforms.TryGetValue(skinning.Skeleton, out var matrices))
            {
                matrices = bindings.GetOrAdd(skinning.Skeleton,
                    skeleton => new ModelPreviewAnimationBinding(skeleton,
                        choice.Library.BoneHashes, clip, choice.Library.SourceSkeleton))
                    .SampleSkinningTransforms(time);
                transforms.Add(skinning.Skeleton, matrices);
            }
            pose.Add(mesh, ModelPreviewCpuSkinner.Skin(mesh, matrices,
                skinNormals: false).Positions);
        }
        return pose;
    }

    private void DisposeAnimation()
    {
        _animationTimer?.Dispose();
        _animationTimer = null;
        _animationCancellation.Cancel();
        _animationCancellation.Dispose();
    }

    private sealed record AnimationChoice(ModelPreviewAnimationLibrary Library,
        ModelPreviewAnimationOption Option, string Name);
}
