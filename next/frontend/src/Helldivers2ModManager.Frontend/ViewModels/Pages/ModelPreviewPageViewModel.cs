using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Preview;
using Helldivers2ModManager.Frontend.Common;
using Helldivers2ModManager.Frontend.Models;
using Helldivers2ModManager.Frontend.Services;

namespace Helldivers2ModManager.Frontend.ViewModels.Pages;

public sealed class ModelPreviewPageViewModel : FrontendPageViewModel
{
    private readonly PatchResourceInspector _inspector;
    private readonly ModLibraryService _library;
    private readonly ModSelectionStore _selection;
    private readonly LocalizationCatalog _localization;
    private CancellationTokenSource? _loadCancellation;

    public ObservableCollection<ModItem> Mods { get; } = [];
    public ObservableCollection<ModelPreviewMesh> Meshes { get; } = [];
    public ObservableCollection<TextureInspectionItem> Textures { get; } = [];

    private ModItem? _selectedMod;
    private bool _isBusy;
    private string _status = string.Empty;
    private Model3DGroup? _modelGroup;
    private ModelPreviewMesh? _selectedMesh;
    private double _cameraDistance = 5;

    public ModItem? SelectedMod
    {
        get => _selectedMod;
        set
        {
            if (SetProperty(ref _selectedMod, value) && value is not null)
            {
                _ = LoadAsync(value);
            }
        }
    }

    public ModelPreviewMesh? SelectedMesh { get => _selectedMesh; set => SetProperty(ref _selectedMesh, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public Model3DGroup? ModelGroup { get => _modelGroup; private set => SetProperty(ref _modelGroup, value); }
    public double CameraDistance { get => _cameraDistance; private set => SetProperty(ref _cameraDistance, value); }
    public bool HasModel => ModelGroup is not null;
    public ICommand RefreshCommand { get; }
    public ICommand CancelCommand { get; }

    public override string Title => _localization.GetString("Nav.ModelPreview");

    public ModelPreviewPageViewModel(
        PatchResourceInspector inspector,
        ModLibraryService library,
        ModSelectionStore selection,
        LocalizationCatalog localization)
    {
        _inspector = inspector;
        _library = library;
        _selection = selection;
        _localization = localization;
        RefreshCommand = new DelegateCommand(async _ => await LoadModsAsync(), _ => !IsBusy);
        CancelCommand = new DelegateCommand(_ => _loadCancellation?.Cancel(), _ => IsBusy);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var preserve = SelectedMod;
        await LoadModsAsync().ConfigureAwait(true);
        SelectedMod = _selection.Selected ?? preserve ?? Mods.FirstOrDefault();
    }

    private async Task LoadModsAsync()
    {
        var result = await _library.LoadAsync().ConfigureAwait(true);
        Mods.Clear();
        foreach (var mod in result.Mods.OrderBy(mod => mod.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Mods.Add(mod);
        }
    }

    private async Task LoadAsync(ModItem mod)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        var ownedCancellation = new CancellationTokenSource();
        _loadCancellation = ownedCancellation;
        var token = _loadCancellation.Token;
        IsBusy = true;
        Status = string.Format(_localization.GetString("Preview.LoadingFormat"), mod.Name);
        Meshes.Clear();
        Textures.Clear();
        SelectedMesh = null;
        ModelGroup = null;
        try
        {
            var patchFiles = ModPatchSelection.GetSelectedPatchFiles(
                mod.Directory,
                mod.Source.Manifest,
                mod.EnabledOptions,
                mod.SelectedOptions);
            var result = await _inspector.PreviewModelAsync(mod.Directory, patchFiles, token).ConfigureAwait(true);
            if (!ReferenceEquals(mod, SelectedMod))
            {
                return;
            }

            foreach (var mesh in result.Meshes) Meshes.Add(mesh);
            foreach (var texture in result.Textures) Textures.Add(texture);
            var selection = ModelPreviewMeshSelector.Select([.. Meshes]);
            SelectedMesh = selection.VisibleMeshes.FirstOrDefault() ?? Meshes.FirstOrDefault();
            ModelGroup = CreateModel(selection.VisibleMeshes);
            Status = string.Format(
                _localization.GetString("Preview.LoadedFormat"),
                selection.VisibleMeshes.Count,
                selection.VisibleMeshes.Sum(mesh => mesh.TriangleCount),
                result.SkippedStreams,
                result.PatchFileCount,
                result.Textures.Count);
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                Status += " " + result.Error;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Status = _localization.GetString("Preview.Canceled");
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, ownedCancellation))
            {
                IsBusy = false;
            }
        }
    }

    private Model3DGroup CreateModel(IReadOnlyList<ModelPreviewMesh> meshes)
    {
        var group = new Model3DGroup();
        if (meshes.Count == 0)
        {
            return group;
        }

        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var minZ = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        var maxZ = double.MinValue;
        foreach (var mesh in meshes)
        {
            for (var index = 0; index + 2 < mesh.Positions.Length; index += 3)
            {
                minX = Math.Min(minX, mesh.Positions[index]);
                minY = Math.Min(minY, mesh.Positions[index + 1]);
                minZ = Math.Min(minZ, mesh.Positions[index + 2]);
                maxX = Math.Max(maxX, mesh.Positions[index]);
                maxY = Math.Max(maxY, mesh.Positions[index + 1]);
                maxZ = Math.Max(maxZ, mesh.Positions[index + 2]);
            }
        }

        var centerX = (minX + maxX) / 2;
        var centerY = (minY + maxY) / 2;
        var centerZ = (minZ + maxZ) / 2;
        var diagonal = Math.Sqrt(
            Math.Pow(maxX - minX, 2) +
            Math.Pow(maxY - minY, 2) +
            Math.Pow(maxZ - minZ, 2));
        CameraDistance = Math.Max(1, diagonal * 1.8);
        var transform = new Transform3DGroup();
        transform.Children.Add(new TranslateTransform3D(-centerX, -centerY, -centerZ));
        var scale = diagonal > 0 ? 5 / diagonal : 1;
        transform.Children.Add(new ScaleTransform3D(scale, scale, scale));
        group.Transform = transform;
        foreach (var mesh in meshes.Take(512))
        {
            var geometry = new MeshGeometry3D();
            foreach (var value in mesh.Positions)
            {
                geometry.Positions.Add(new Point3D(value, 0, 0));
            }

            for (var index = 0; index + 2 < mesh.Positions.Length; index += 3)
            {
                var offset = index / 3;
                geometry.Positions[offset] = new Point3D(mesh.Positions[index], mesh.Positions[index + 1], mesh.Positions[index + 2]);
            }

            foreach (var index in mesh.TriangleIndices)
            {
                geometry.TriangleIndices.Add(index);
            }

            var model = new GeometryModel3D(geometry, new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xB8, 0xC7, 0xD9))))
            {
                BackMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x70, 0x80, 0x90))),
            };
            group.Children.Add(model);
        }

        return group;
    }
}
