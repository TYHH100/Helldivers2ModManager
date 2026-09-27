using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;
using Jalium.UI.Media.Imaging;
using Jalium.UI.Media.Media3D;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class ModelPreviewPageView
{
    private readonly Dictionary<ulong, ImageSource> _loadedTextures = [];
    private CancellationTokenSource? _textureCancellation;
    private int _loadGeneration;

    private void RefreshMods(ModData? preferred)
    {
        if (_disposed)
            return;
        var state = ModelPreviewModSelection.Resolve(
            _mods().OrderBy(static mod => mod.Manifest.Name, StringComparer.CurrentCultureIgnoreCase),
            preferred ?? _initialMod);
        _changingMod = true;
        try
        {
            _modList.ItemsSource = state.Mods;
            _modList.SelectedItem = state.SelectedMod;
        }
        finally { _changingMod = false; }
        SelectMod(state.SelectedMod);
    }

    private void SelectMod(ModData? mod)
    {
        if (_disposed || ReferenceEquals(_selectedMod, mod))
            return;
        _loadCancellation?.Cancel();
        _textureCancellation?.Cancel();
        _selectedMod = mod;
        _result = null;
        ResetAnimationState();
        _scene.Clear();
        _meshGrid.ItemsSource = null;
        _armor.ItemsSource = null;
        _textures.ItemsSource = null;
        _loadedTextures.Clear();
        BuildPreviewOptions(mod);
        if (mod is null)
        {
            _status.Text = _localization["ModelPreviewPage.EmptyMods"];
            return;
        }
        PendingLoad = LoadSelectedModAsync(resetCamera: true);
    }

    private void BuildPreviewOptions(ModData? mod)
    {
        _options.Clear();
        _optionRows.Children.Clear();
        if (mod?.Manifest is V1ModManifest { Options: { } options })
        {
            for (var index = 0; index < options.Count; index++)
            {
                var option = options[index];
                var row = new ModelPreviewOptionRow(index,
                    index < mod.EnabledOptions.Length && mod.EnabledOptions[index],
                    index < mod.SelectedOptions.Length ? mod.SelectedOptions[index] : 0,
                    option.SubOptions?.Select(static sub => sub.Name).ToArray() ?? []);
                AddOptionRow(row, option.Name, option.Description,
                    LoadOptionImage(mod.Directory, option.Image), canToggle: true);
            }
        }
        else if (mod?.Manifest is LegacyModManifest { Options: { Count: > 0 } legacy })
        {
            var row = new ModelPreviewOptionRow(0, true, mod.SelectedOptions.FirstOrDefault(),
                legacy.ToArray());
            AddOptionRow(row, _localization["ModelPreviewPage.LegacyVariants"], string.Empty,
                null, canToggle: false);
        }
        _noOptions.Visibility = _options.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _optionRows.Visibility = _options.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AddOptionRow(ModelPreviewOptionRow row, string name, string description,
        ImageSource? source, bool canToggle)
    {
        _options.Add(row);
        var card = new Grid { Width = 270, Margin = new Thickness(3) };
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var thumbnail = new Border { Width = 42, Height = 42,
            Background = Paint(0x20, 0x20, 0x20), CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Image { Source = source, Stretch = Stretch.UniformToFill } };
        Grid.SetRowSpan(thumbnail, 2);
        card.Children.Add(thumbnail);
        var text = new StackPanel { Margin = new Thickness(8, 0, 8, 0) };
        text.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = name });
        text.Children.Add(new TextBlock { Text = description, FontSize = 11,
            Foreground = Secondary, TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = description });
        Grid.SetColumn(text, 1);
        card.Children.Add(text);
        if (canToggle)
        {
            var toggle = new CheckBox { IsChecked = row.Enabled };
            toggle.Checked += (_, _) =>
            {
                row.Enabled = true;
                PendingLoad = LoadSelectedModAsync(resetCamera: false);
            };
            toggle.Unchecked += (_, _) =>
            {
                row.Enabled = false;
                PendingLoad = LoadSelectedModAsync(resetCamera: false);
            };
            Grid.SetColumn(toggle, 2);
            card.Children.Add(toggle);
        }
        if (row.SubOptions.Length > 0)
        {
            var choices = new ComboBox { ItemsSource = row.SubOptions,
                SelectedIndex = Math.Clamp(row.SelectedIndex, 0, row.SubOptions.Length - 1),
                MinWidth = 130, Margin = new Thickness(8, 7, 0, 0) };
            row.SelectedIndex = choices.SelectedIndex;
            choices.SelectionChanged += (_, _) =>
            {
                row.SelectedIndex = choices.SelectedIndex;
                PendingLoad = LoadSelectedModAsync(resetCamera: false);
            };
            Grid.SetRow(choices, 1);
            Grid.SetColumn(choices, 1);
            Grid.SetColumnSpan(choices, 2);
            card.Children.Add(choices);
        }
        _optionRows.Children.Add(new Border
        {
            Child = card, Background = Surface, BorderBrush = Stroke,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 5),
        });
    }

    private static ImageSource? LoadOptionImage(DirectoryInfo directory, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            return null;
        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(directory.FullName, relative));
            if (!fullPath.StartsWith(Path.GetFullPath(directory.FullName)
                    .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
                return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 96;
            image.UriSource = new Uri(fullPath, UriKind.Absolute);
            image.EndInit();
            return image;
        }
        catch (Exception) { return null; }
    }

    private async Task LoadSelectedModAsync(bool resetCamera)
    {
        var mod = _selectedMod;
        if (_disposed || mod is null)
            return;
        _loadCancellation?.Cancel();
        _textureCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        var generation = ++_loadGeneration;
        var enabled = _options.Select(static option => option.Enabled).ToArray();
        var selected = _options.Select(static option => option.SelectedIndex).ToArray();
        _status.Text = _localization["ModelPreviewPage.Loading"]
            .Replace("{name}", mod.Manifest.Name);
        _loadingOverlay.Visibility = resetCamera || _result is null
            ? Visibility.Visible : Visibility.Collapsed;
        if (_loadingOverlay.Visibility == Visibility.Visible)
            _scene.Surface.SetRenderingVisible(false);
        _refreshButton.IsEnabled = false;
        try
        {
            var result = await _preview(mod, enabled, selected,
                _forceDecode.IsChecked == true, cancellation.Token);
            if (!IsCurrent(mod, cancellation, generation))
                return;
            _result = result;
            SetAnimationChoices(result);
            _selection = ModelPreviewMeshSelector.Select(result.Meshes);
            _loadedTextures.Clear();
            _changingSelection = true;
            try
            {
                _meshGrid.ItemsSource = result.Meshes;
                _textures.ItemsSource = result.Textures;
                _armor.ItemsSource = result.Armors;
                _armor.SelectedItem = result.Armors.FirstOrDefault(static armor => !armor.IsAll)
                    ?? result.Armors.FirstOrDefault();
                _stocky.IsChecked = true;
                _showFiltered.IsChecked = false;
                _isolateMesh.IsChecked = false;
                _automaticMaterials.IsChecked = true;
                _selectedMesh = result.Meshes.FirstOrDefault(mesh =>
                    mesh.RenderStatus == ModelPreviewMeshRenderStatus.Visible)
                    ?? result.Meshes.FirstOrDefault();
                _meshGrid.SelectedItem = _selectedMesh;
                _textures.SelectedItem = ChoosePreferredTexture(result);
            }
            finally { _changingSelection = false; }
            RefreshResultLabels();
            RenderSelection(resetCamera);
            await LoadAutomaticTexturesAsync(mod, result, cancellation, generation);
            if (!IsCurrent(mod, cancellation, generation))
                return;
            RenderSelection(resetCamera: false);
            _status.Text = result.Meshes.Count > 0
                ? _localization["ModelPreviewPage.Loaded"]
                    .Replace("{meshes}", _scene.DisplayedMeshCount.ToString())
                    .Replace("{triangles}", GetVisibleMeshes().Sum(static mesh => mesh.TriangleCount).ToString())
                    .Replace("{skipped}", result.SkippedStreams.ToString())
                    .Replace("{patches}", result.PatchFileCount.ToString())
                    .Replace("{textures}", _loadedTextures.Count.ToString())
                : _localization["ModelPreviewPage.NoGeometry"];
            if (_selection.HiddenMeshCount > 0)
                _status.Text += " " + _localization["ModelPreviewPage.HiddenOutliers"]
                    .Replace("{count}", _selection.HiddenMeshCount.ToString());
            if (!string.IsNullOrWhiteSpace(result.Error))
                _status.Text += " " + result.Error;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrent(mod, cancellation, generation))
                _status.Text = _localization["ModelPreviewPage.LoadFailed"]
                    .Replace("{message}", ex.Message);
        }
        finally
        {
            if (IsCurrent(mod, cancellation, generation))
            {
                _loadingOverlay.Visibility = Visibility.Collapsed;
                _scene.Surface.SetRenderingVisible(true);
                _refreshButton.IsEnabled = true;
            }
            if (ReferenceEquals(_loadCancellation, cancellation))
                _loadCancellation = null;
        }
    }

    private bool IsCurrent(ModData mod, CancellationTokenSource cancellation, int generation) =>
        !_disposed && !cancellation.IsCancellationRequested
        && ReferenceEquals(_selectedMod, mod) && generation == _loadGeneration;

    private void RenderSelection(bool resetCamera = false)
    {
        if (_disposed || _result is null || _changingSelection)
            return;
        var visible = GetVisibleMeshes();
        _scene.SetMeshes(visible, _result.Meshes, ResolveMaterial, resetCamera);
        if (_animationApplied)
            QueueAnimationFrame();
        _isolateMesh.IsEnabled = _selectedMesh is not null;
        _armor.IsEnabled = _result.Armors.Count > 2;
    }

    private IReadOnlyList<ModelPreviewMesh> GetVisibleMeshes()
    {
        if (_result is null)
            return [];
        var armor = _armor.SelectedItem as ModelPreviewArmorOption;
        var armorMeshes = ModelPreviewBackend.FilterByArmor(_result.Meshes, armor?.Ids);
        var renderable = _selection.VisibleMeshes.Where(armorMeshes.Contains).ToArray();
        var bodyMeshes = ModelPreviewBodyShapeSelection.Filter(armorMeshes, renderable,
            _stocky.IsChecked == true);
        var bodySet = bodyMeshes.ToHashSet();
        var selection = new ModelPreviewSelection(
            _selection.VisibleMeshes.Where(bodySet.Contains).ToArray(), _selection.HiddenMeshCount);
        var selectedMesh = _selectedMesh is not null && bodySet.Contains(_selectedMesh)
            ? _selectedMesh : null;
        return ModelPreviewMeshSelector.GetRenderMeshes(selection, bodyMeshes,
            selectedMesh, _isolateMesh.IsChecked == true, _showFiltered.IsChecked == true);
    }

    private void RefreshHiddenSummary()
    {
        _hiddenSummary.Text = _localization["ModelPreviewPage.AutomaticallyHiddenSummary"]
            .Replace("{count}", _selection.HiddenMeshCount.ToString());
    }

    private void RefreshResultLabels()
    {
        RefreshHiddenSummary();
        if (_result is null)
            return;
        foreach (var mesh in _result.Meshes)
        {
            mesh.PreviewStatusText = _localization["ModelPreviewPage." + (mesh.RenderStatus switch
            {
                ModelPreviewMeshRenderStatus.HiddenCullingBody => "HiddenCullingBody",
                ModelPreviewMeshRenderStatus.HiddenLargeOutlier => "HiddenLargeOutlier",
                ModelPreviewMeshRenderStatus.HiddenProxyGeometry => "HiddenProxyGeometry",
                ModelPreviewMeshRenderStatus.HiddenCollisionSphere => "HiddenCollisionSphere",
                _ => "PreviewVisible",
            })];
            mesh.UvStatusText = _localization[mesh.HasTextureCoordinates
                ? "ModelPreviewPage.HasUv" : "ModelPreviewPage.NoUv"];
        }
        foreach (var texture in _result.Textures)
            texture.PreviewRoleText = _localization[texture.PreviewRole switch
            {
                TexturePreviewRole.ColorCandidate => "ModelPreviewPage.ColorCandidate",
                TexturePreviewRole.LikelyNormalMap => "ModelPreviewPage.LikelyNormalMap",
                _ => "ModelPreviewPage.UnclassifiedTexture",
            }];
        foreach (var armor in _result.Armors.Where(static item => item.IsAll))
            armor.Name = _localization["ModelPreviewPage.AllArmors"];
    }

    private static TextureInspectionItem? ChoosePreferredTexture(ModelPreviewResult result)
    {
        var preferredIds = result.Meshes.Select(static mesh => mesh.ColorTextureId)
            .Where(static id => id.HasValue).Select(static id => id!.Value).ToHashSet();
        var referencedIds = result.Meshes
            .SelectMany(static mesh => mesh.MaterialTextures.AllTextureIds.Concat(mesh.TextureIds))
            .ToHashSet();
        return result.Textures.Where(texture => referencedIds.Contains(texture.TextureId))
            .OrderBy(texture => preferredIds.Contains(texture.TextureId) ? 0 :
                texture.PreviewRole == TexturePreviewRole.ColorCandidate ? 1 :
                texture.PreviewRole == TexturePreviewRole.Unknown ? 2 : 3)
            .ThenByDescending(static texture => (long)texture.Width * texture.Height)
            .FirstOrDefault() ?? result.Textures.FirstOrDefault();
    }

    private async Task LoadAutomaticTexturesAsync(ModData mod, ModelPreviewResult result,
        CancellationTokenSource cancellation, int generation)
    {
        var map = ModelPreviewTextureIndex.Create(result.Textures);
        var original = _originalResolution.IsChecked == true;
        var limit = original ? 16_777_216 : 1_048_576;
        var maxCount = original ? 8 : 16;
        foreach (var textureId in SelectAutomaticTextureIds(result.Meshes, maxCount))
        {
            if (!map.TryGetValue(textureId, out var texture))
                continue;
            cancellation.Token.ThrowIfCancellationRequested();
            var image = await DecodeTextureAsync(mod, texture, limit, cancellation.Token);
            if (!IsCurrent(mod, cancellation, generation))
                return;
            if (image is not null)
                _loadedTextures[textureId] = image;
        }
    }

    private static IReadOnlyList<ulong> SelectAutomaticTextureIds(
        IReadOnlyList<ModelPreviewMesh> meshes, int maxCount)
    {
        var priorities = new Dictionary<ulong, int>();
        foreach (var mesh in meshes)
        {
            var hasColor = false;
            foreach (var id in mesh.MaterialTextures.Get(ModelPreviewTextureRole.BaseColor)
                         .Concat(mesh.MaterialTextures.Get(ModelPreviewTextureRole.Iridescence))
                         .Concat(mesh.ColorTextureId is ulong color ? [color] : []))
            {
                if (id == 0) continue;
                priorities[id] = priorities.TryGetValue(id, out var old) ? Math.Min(old, 0) : 0;
                hasColor = true;
            }
            foreach (var id in mesh.MaterialTextures.Get(ModelPreviewTextureRole.Emissive))
                if (id != 0)
                    priorities[id] = priorities.TryGetValue(id, out var old) ? Math.Min(old, 1) : 1;
            if (!hasColor)
                foreach (var id in mesh.TextureIds)
                    if (id != 0)
                        priorities[id] = priorities.TryGetValue(id, out var old) ? Math.Min(old, 2) : 2;
        }
        return priorities.OrderBy(static entry => entry.Value).Take(maxCount)
            .Select(static entry => entry.Key).ToArray();
    }

    private async Task<ImageSource?> DecodeTextureAsync(ModData mod,
        TextureInspectionItem texture, int limit, CancellationToken cancellationToken)
    {
        var preview = await _texturePreview(mod, texture, limit, cancellationToken);
        if (preview?.BgraPixels is { } bgra)
        {
            if (preview.Width <= 0 || preview.Height <= 0
                || bgra.Length != checked(preview.Width * preview.Height * 4))
                return null;
            var opaque = await Task.Run(() =>
            {
                var copy = (byte[])bgra.Clone();
                for (var offset = 3; offset < copy.Length; offset += 4)
                    copy[offset] = byte.MaxValue;
                return copy;
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return BitmapImage.FromPixels(opaque, preview.Width, preview.Height,
                preview.Width * 4);
        }
        if (preview?.EncodedImageBytes is { } encoded)
        {
            using var stream = new MemoryStream(encoded, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
            bitmap.CopyPixels(new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight),
                pixels, bitmap.PixelWidth * 4, 0);
            for (var offset = 3; offset < pixels.Length; offset += 4)
                pixels[offset] = byte.MaxValue;
            return BitmapImage.FromPixels(pixels, bitmap.PixelWidth, bitmap.PixelHeight,
                bitmap.PixelWidth * 4);
        }
        return null;
    }

    private async Task LoadSelectedTextureAsync()
    {
        if (_changingSelection || _disposed || _selectedMod is not { } mod
            || _textures.SelectedItem is not TextureInspectionItem texture)
            return;
        _automaticMaterials.IsChecked = false;
        if (_loadedTextures.ContainsKey(texture.TextureId))
        {
            RenderSelection();
            return;
        }
        _textureCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _textureCancellation = cancellation;
        try
        {
            var image = await DecodeTextureAsync(mod, texture,
                _originalResolution.IsChecked == true ? 16_777_216 : 1_048_576,
                cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested
                || !ReferenceEquals(_selectedMod, mod)
                || !ReferenceEquals(_textures.SelectedItem, texture))
                return;
            if (image is not null)
            {
                _loadedTextures[texture.TextureId] = image;
                RenderSelection();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _status.Text = _localization["ModelPreviewPage.LoadFailed"]
                .Replace("{message}", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_textureCancellation, cancellation))
                _textureCancellation = null;
        }
    }

    private Task ReloadTexturesAsync()
    {
        if (_changingSelection || _result is null)
            return Task.CompletedTask;
        return LoadSelectedModAsync(resetCamera: false);
    }

    private Material ResolveMaterial(ModelPreviewMesh mesh)
    {
        ImageSource? image = null;
        if (_automaticMaterials.IsChecked == true)
        {
            var candidates = mesh.MaterialTextures.Get(ModelPreviewTextureRole.BaseColor)
                .Concat(mesh.MaterialTextures.Get(ModelPreviewTextureRole.Iridescence))
                .Concat(mesh.ColorTextureId is ulong color ? [color] : mesh.TextureIds);
            foreach (var id in candidates)
                if (_loadedTextures.TryGetValue(id, out image))
                    break;
        }
        else if (_textures.SelectedItem is TextureInspectionItem selected)
            _loadedTextures.TryGetValue(selected.TextureId, out image);
        return new DiffuseMaterial(image is null
            ? new SolidColorBrush(Color.FromRgb(184, 193, 202))
            : new ImageBrush(image));
    }

    private sealed class ModelPreviewOptionRow(int index, bool enabled, int selectedIndex,
        string[] subOptions)
    {
        public int Index { get; } = index;
        public bool Enabled { get; set; } = enabled;
        public int SelectedIndex { get; set; } = selectedIndex;
        public string[] SubOptions { get; } = subOptions;
    }
}
