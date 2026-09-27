using System.Diagnostics;
using Helldivers2ModManager.Jalium.Host;
using Helldivers2ModManager.Services;

if (args is ["--texture"])
{
    var nativeProbe = new byte[4];
    Console.WriteLine("Zig available: " + NativeTextureConverter.TryConvert(
        [1, 2, 3, 4], nativeProbe, PatchTextureChannel.Rgb, force: true));
    foreach (var pixelCount in new[] { 1_048_576, 4_194_304, 16_777_216 })
    {
        var source = new byte[checked(pixelCount * 4)];
        Array.Fill(source, (byte)127);
        foreach (var channel in new[] { PatchTextureChannel.Rgb, PatchTextureChannel.Alpha })
        {
            MeasureTexture("C#", () => PatchResourceViewerPageView.ConvertChannelManaged(source, channel));
            MeasureTexture("Auto", () => PatchResourceViewerPageView.ConvertChannel(source, channel));

            void MeasureTexture(string implementation, Func<byte[]> convert)
            {
                convert();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var before = GC.GetTotalAllocatedBytes(precise: true);
                var watch = Stopwatch.StartNew();
                for (var run = 0; run < 5; run++)
                    convert();
                watch.Stop();
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
                Console.WriteLine($"{implementation} {channel} {pixelCount:N0} pixels: " +
                    $"{watch.Elapsed.TotalMilliseconds / 5:F2} ms/run, " +
                    $"{allocated / 5 / 1048576d:F1} MiB/run allocated");
            }
        }
    }
    return 0;
}

if (args.Length is < 1 or > 2 || !File.Exists(args[0]) ||
    (args.Length == 2 && (!int.TryParse(args[1], out var requestedRuns) || requestedRuns is < 1 or > 30)))
{
    Console.Error.WriteLine("Usage: dotnet run --project next/jalium/benchmarks -- <patch file> [runs 1..30]");
    return 2;
}

var runs = args.Length == 2 ? int.Parse(args[1]) : 1;
var patch = new FileInfo(Path.GetFullPath(args[0]));
var modDirectory = patch.Directory!;
var patchFiles = new[] { patch };
var service = new PatchResourceInspectionService();

async Task<T> MeasureAsync<T>(string name, Func<Task<T>> work, Func<T, string> describe)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    var stopwatch = Stopwatch.StartNew();
    var result = await work();
    stopwatch.Stop();
    var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
    Console.WriteLine($"{name}: {stopwatch.Elapsed.TotalMilliseconds:F0} ms, " +
        $"{allocatedBytes / 1048576d:F1} MiB allocated, {describe(result)}");
    return result;
}

Console.WriteLine($"Patch: {patch.Length / 1048576d:F1} MiB");
var gpu = new FileInfo(patch.FullName + ".gpu_resources");
Console.WriteLine($"GPU: {(gpu.Exists ? gpu.Length / 1048576d : 0):F1} MiB");
var inspection = await MeasureAsync("InspectAsync", () => service.InspectAsync(modDirectory, patchFiles),
    result => $"{result.TocEntries.Count} TOC entries, {result.Textures.Count} textures, {result.GpuStreams.Count} GPU streams");
var preview = await MeasureAsync($"PreviewModelAsync x{runs}", async () =>
    {
        var result = await service.PreviewModelAsync(modDirectory, patchFiles);
        for (var run = 1; run < runs; run++)
            result = await service.PreviewModelAsync(modDirectory, patchFiles);
        return result;
    },
    result => $"{result.Meshes.Count} meshes, {result.Textures.Count} textures, error={result.Error ?? "none"}");
if (preview.Meshes.Count > 0)
{
    const int repetitions = 20;
    var stopwatch = Stopwatch.StartNew();
    for (var repetition = 0; repetition < repetitions; repetition++)
        foreach (var mesh in preview.Meshes)
            PatchResourceInspectionService.BuildSmoothedNormals(mesh.Positions, mesh.TriangleIndices);
    stopwatch.Stop();
    Console.WriteLine($"BuildSmoothedNormals: {stopwatch.Elapsed.TotalMilliseconds / repetitions:F2} ms per model, " +
        $"{preview.Meshes.Sum(mesh => mesh.TriangleIndices.Length) / 3:N0} triangles");
}
var texture = inspection.Textures.OrderByDescending(static item => (long)item.Width * item.Height).FirstOrDefault();
if (texture is not null)
    await MeasureAsync("PreviewTextureAsync", () => service.PreviewTextureAsync(modDirectory, texture, 4_194_304),
        result => result is null ? "no preview" : $"{result.Width} x {result.Height}");
return 0;
