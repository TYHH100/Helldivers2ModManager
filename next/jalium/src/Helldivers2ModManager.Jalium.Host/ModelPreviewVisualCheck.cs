#if DEBUG
using System.Numerics;
using System.Runtime.InteropServices;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Threading;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helldivers2ModManager.Jalium.Host;

internal static class ModelPreviewVisualCheck
{
    public static int Run(string screenshotPath)
    {
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
            Path.Combine(AppContext.BaseDirectory, "Language"));
        localization.SelectedLanguage = "zh-CN";
        var mod = new ModData(new DirectoryInfo(Path.GetTempPath()), new LegacyModManifest
        {
            Guid = Guid.NewGuid(), Name = "Visual Check Model",
            Description = string.Empty, Options = [],
        });
        var result = new ModelPreviewResult { PatchFileCount = 1 };
        result.Meshes.Add(CreateBoxMesh());
        result.AnimationLibraries.Add(CreateAnimationLibrary());
        result.Textures.Add(new TextureInspectionItem
        {
            PatchFile = "visual.patch_0", PatchPath = "visual.patch_0",
            PatchOrder = 0, TocEntryIndex = 0, TextureId = 1,
            MainOffset = 0, MainSize = 0, GpuOffset = 0, GpuSize = 0,
            StreamOffset = 0, StreamSize = 0, Width = 2, Height = 2,
            MipCount = 1, DxgiFormat = 87, PayloadKind = "RGBA",
            PayloadSource = "synthetic",
        });
        using var view = new ModelPreviewPageView(localization, () => [mod],
            (_, _, _, _, _) => Task.FromResult(result),
            (_, _, _, _) => Task.FromResult<TexturePreviewData?>(new TexturePreviewData
            {
                Width = 2, Height = 2, Description = "Synthetic texture",
                BgraPixels =
                [
                    0, 0, 255, 255, 0, 255, 0, 255,
                    255, 0, 0, 255, 255, 255, 255, 255,
                ],
            }),
            () => { }, _ => { }, mod);
        var window = new Window { Title = "Model Preview Visual Check",
            Width = 1000, Height = 900, Content = view, Topmost = true };
        var dispatcher = Dispatcher.CurrentDispatcher;
        var captured = false;
        var scheduled = false;
        window.ContentRendered += (_, _) =>
        {
            if (scheduled)
                return;
            scheduled = true;
            System.Threading.Timer? timer = null;
            timer = new System.Threading.Timer(_ => dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await view.PendingLoad;
                    await Task.Delay(250);
                    if (view.Scene.DisplayedMeshCount != 1)
                        throw new InvalidOperationException("The synthetic model did not reach the 3D scene.");
                    var viewport = view.Scene.Surface;
                    Console.WriteLine($"MODEL_SCENE viewport={viewport.ActualWidth:F0}x{viewport.ActualHeight:F0} " +
                        $"native={viewport.IsRendererReady} " +
                        $"bounds={view.Scene.ModelGroup?.Bounds} " +
                        $"camera={view.Scene.Camera.Position} look={view.Scene.Camera.LookDirection}");
                    var (pixels, width, height) = viewport.CapturePixels();
                    var distinct = pixels.Chunk(4).Select(pixel =>
                        (pixel[0], pixel[1], pixel[2])).Distinct().Take(2).Count();
                    if (distinct < 2)
                        throw new InvalidOperationException("The native renderer produced a blank frame.");
                    using (var bitmap = new System.Drawing.Bitmap(width, height,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height),
                            System.Drawing.Imaging.ImageLockMode.WriteOnly,
                            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        try
                        {
                            for (var row = 0; row < height; row++)
                                Marshal.Copy(pixels, (height - 1 - row) * width * 4,
                                    data.Scan0 + row * data.Stride, width * 4);
                        }
                        finally { bitmap.UnlockBits(data); }
                        bitmap.Save(screenshotPath + ".native.png");
                    }
                    var beforeDrag = view.Scene.Camera.Position;
                    var handle = viewport.Handle;
                    SendMessage(handle, 0x0201, 1, (nint)((100 << 16) | 100));
                    SendMessage(handle, 0x0200, 1, (nint)((100 << 16) | 140));
                    SendMessage(handle, 0x0202, 0, (nint)((100 << 16) | 140));
                    var afterDrag = view.Scene.Camera.Position;
                    if (Math.Abs(afterDrag.X - beforeDrag.X) < 0.001
                        && Math.Abs(afterDrag.Z - beforeDrag.Z) < 0.001)
                        throw new InvalidOperationException("Native drag did not rotate the camera.");
                    SendMessage(handle, 0x020A, (nint)(120 << 16), 0);
                    if (Math.Abs(view.Scene.Camera.Position.X - afterDrag.X) < 0.001
                        && Math.Abs(view.Scene.Camera.Position.Z - afterDrag.Z) < 0.001)
                        throw new InvalidOperationException("Native wheel did not zoom the camera.");
                    view.Scene.ResetCamera();
                    var boundPixels = viewport.CapturePixels().Pixels;
                    if (viewport.GetPositionOverride(result.Meshes[0]) is not null)
                        throw new InvalidOperationException("An animation was applied before selection.");
                    view.AnimationPicker.SelectIndex(0);
                    await view.PendingAnimationSelection;
                    view.AnimationTime.Value = 0.5;
                    await view.PendingAnimationFrame;
                    var animatedPositions = viewport.GetPositionOverride(result.Meshes[0]);
                    if (animatedPositions is null
                        || Math.Abs(animatedPositions[0] - result.Meshes[0].Positions[0]) < 0.1)
                        throw new InvalidOperationException("Scrubbing did not skin the model.");
                    var animatedPixels = viewport.CapturePixels().Pixels;
                    if (boundPixels.SequenceEqual(animatedPixels))
                        throw new InvalidOperationException("The animation did not change the rendered frame.");
                    view.AnimationPicker.SelectIndex(-1);
                    await view.PendingAnimationSelection;
                    if (viewport.GetPositionOverride(result.Meshes[0]) is not null)
                        throw new InvalidOperationException("Clearing animation did not restore the bind pose.");
                    Console.WriteLine("ANIMATION_FRAME_OK");
                    var picker = view.AnimationPicker;
                    picker.SetItems(Enumerable.Range(0, 10_000)
                        .Select(index => $"Clip {index:00000}").ToArray());
                    picker.Open();
                    await Task.Delay(100);
                    picker.ScrollToItem(9_999);
                    if (!picker.IsOpen || picker.MaterializedRowCount is < 1 or > 20)
                        throw new InvalidOperationException("The animation picker did not virtualize its rows.");
                    picker.Search("Clip 09999");
                    if (picker.VisibleItemCount != 1 || picker.MaterializedRowCount > 1)
                        throw new InvalidOperationException("The animation picker search lost source rows.");
                    Console.WriteLine($"PICKER_ROWS_OK {picker.MaterializedRowCount}");
                    picker.SelectIndex(-1);
                    SetForegroundWindow(window.Handle);
                    await Task.Delay(150);
                    PatchViewerVisualCheck.Capture(window.Handle, screenshotPath);
                    CaptureScreen(window.Handle, screenshotPath + ".screen.png");
                    captured = true;
                    Console.WriteLine($"SCREENSHOT_OK {screenshotPath}");
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
                finally
                {
                    timer?.Dispose();
                    window.Close();
                }
            }), null, 800, Timeout.Infinite);
        };
        var builder = AppBuilder.CreateBuilder([]);
        builder.ConfigureApplication(app => app.MainWindow = window);
        using var jalium = builder.Build();
        jalium.Run();
        return captured ? 0 : 1;
    }

    private static ModelPreviewMesh CreateBoxMesh()
    {
        float[] positions =
        [
            -0.5f, -1, -0.5f, 0.5f, -1, -0.5f,
            0.5f, 1, -0.5f, -0.5f, 1, -0.5f,
            -0.5f, -1, 0.5f, 0.5f, -1, 0.5f,
            0.5f, 1, 0.5f, -0.5f, 1, 0.5f,
        ];
        int[] faces =
        [
            0, 1, 2, 0, 2, 3, 5, 4, 7, 5, 7, 6,
            4, 0, 3, 4, 3, 7, 1, 5, 6, 1, 6, 2,
            3, 2, 6, 3, 6, 7, 4, 5, 1, 4, 1, 0,
        ];
        return new ModelPreviewMesh
        {
            PatchFile = "visual.patch_0", UnitId = 1, StreamIndex = 0,
            Positions = positions, TriangleIndices = faces.Concat(faces).ToArray(),
            ColorTextureId = 1,
            Skinning = new ModelPreviewSkinningData
            {
                Skeleton = new ModelPreviewSkeleton
                {
                    BonesId = 1, StateMachineId = 2,
                    Bones =
                    [
                        new ModelPreviewSkeletonBone(-1, 0x11, Matrix4x4.Identity),
                        new ModelPreviewSkeletonBone(0, 0x22, Matrix4x4.Identity),
                    ],
                },
                TransformIndices = Enumerable.Range(0, 8)
                    .SelectMany(static _ => new[] { 1, 0, 0, 0 }).ToArray(),
                Weights = Enumerable.Range(0, 8)
                    .SelectMany(static _ => new[] { 1f, 0f, 0f, 0f }).ToArray(),
            },
            TextureCoordinates =
            [
                0, 0, 1, 0, 1, 1, 0, 1,
                0, 0, 1, 0, 1, 1, 0, 1,
            ],
        };
    }

    private static ModelPreviewAnimationLibrary CreateAnimationLibrary() => new()
    {
        BonesId = 1, StateMachineId = 2, BoneHashes = [0x11, 0x22],
        Animations =
        [
            new ModelPreviewAnimationOption
            {
                AnimationId = 3, StateNameHash = 0, LayerIndex = 0,
                Clip = new ModelPreviewAnimationClip
                {
                    AnimationId = 3, BoneCount = 2, LengthSeconds = 1,
                    IsAdditive = false,
                    InitialPoses = [ModelPreviewBonePose.Identity, ModelPreviewBonePose.Identity],
                    Keyframes =
                    [
                        new ModelPreviewAnimationKeyframe(1, 0.5f,
                            ModelPreviewAnimationChannel.Position,
                            new Vector3(0.5f, 0, 0), Quaternion.Identity, Vector3.One),
                    ],
                    Events = [],
                },
            },
        ],
    };

    private static void CaptureScreen(nint handle, string path)
    {
        if (!GetWindowRect(handle, out var rect))
            throw new InvalidOperationException("Model preview window is unavailable.");
        using var bitmap = new System.Drawing.Bitmap(rect.Right - rect.Left,
            rect.Bottom - rect.Top);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint handle, out WindowRect rect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint handle);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint handle, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
#endif
