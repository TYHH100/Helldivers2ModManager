namespace Helldivers2ModManager.Models;

/// <summary>
/// Determines whether a decoded customization model needs a presentation-only rotation
/// for WPF's Y-up viewport. Resource coordinates are left untouched.
/// </summary>
internal static class ModelPreviewCharacterOrientation
{
    private const double DominanceRatio = 1.5;
    // 无标注路径的顶点标准差低于该值视为小道具，不施加呈现旋转。
    private const double MinimumCharacterStdDev = 0.05;
    // slot 标注网格的顶点数须占全模型的至少该比例，其质心才能代表躯干/腿的位置。
    // 实测 B08：6 个 Torso 标注网格共 81 顶点（全模型 18.4 万顶点的 0.04%）——
    // 这种"顺带替换的小标签件"质心是任意的，会让质心差判定产生倒立；
    // 主体顶点分布（标准差路径）才是可靠信号。
    private const double MinimumLabeledPointFraction = 0.02;
    private const long MinimumLabeledPointCount = 100;
    private static readonly IReadOnlySet<ModelPreviewCustomizationSlot> TorsoSlots =
        new HashSet<ModelPreviewCustomizationSlot> { ModelPreviewCustomizationSlot.Torso };
    private static readonly IReadOnlySet<ModelPreviewCustomizationSlot> LegSlots =
        new HashSet<ModelPreviewCustomizationSlot>
        {
            ModelPreviewCustomizationSlot.LeftLeg,
            ModelPreviewCustomizationSlot.RightLeg
        };

    public static ModelPreviewPresentationRotation GetRequiredRotation(IReadOnlyList<ModelPreviewMesh> meshes)
    {
        ArgumentNullException.ThrowIfNull(meshes);

        var totalPointCount = 0L;
        foreach (var mesh in meshes)
            totalPointCount += mesh.Positions.Length / 3;

        // 最强证据：躯干与双腿都被可信地标注定位于不同高度（解剖学质心差）。
        // 质心差方向不显著（实测 VRC Milltina Marette：裙摆/臂饰把两质心的横向
        // 差拉开，撞上 dominance 门槛返回 None）时回退顶点分布路径，而不是放弃
        // 旋转——"证据弱"不等于"模型不需要旋转"。
        if (HasEnoughLabeledPoints(meshes, TorsoSlots, totalPointCount) &&
            HasEnoughLabeledPoints(meshes, LegSlots, totalPointCount) &&
            TryGetCentroid(meshes, TorsoSlots, out var torso) &&
            TryGetCentroid(meshes, LegSlots, out var legs))
        {
            var rotation = GetRotation(torso, legs);
            if (rotation != ModelPreviewPresentationRotation.None)
                return rotation;
        }

        // 其余情形（无标注、只有零散小标签件、torso-only、质心差不显著）一律交给
        // 顶点分布：旧启发式假设剩余件位于躯干上方，而实际剩余件是髋/腿/手臂
        // （在下方），实测 B08/Kaguya 因此倒立或拒绝旋转。
        return GetUnlabeledCharacterRotation(meshes);
    }

    private static bool HasEnoughLabeledPoints(
        IReadOnlyList<ModelPreviewMesh> meshes,
        IReadOnlySet<ModelPreviewCustomizationSlot> slots,
        long totalPointCount)
    {
        long labeledPointCount = 0;
        foreach (var mesh in meshes)
        {
            if (slots.Contains(mesh.CustomizationSlot))
                labeledPointCount += mesh.Positions.Length / 3;
        }

        return labeledPointCount >= MinimumLabeledPointCount &&
               labeledPointCount >= totalPointCount * MinimumLabeledPointFraction;
    }

    /// <summary>
    /// 顶点分布路径：处理无标注、torso-only、小标签件混杂等一切无可靠解剖学证据
    /// 的模型。逐轴标准差反映主体分布（bounds 会被张开的 T-pose 手臂、翅膀、裙摆
    /// 等局部凸出拖偏——实测安德莉亚子集 X 跨度 ≈ Z 跨度导致翻轴）。HD2 人物资源
    /// 为 Z-up（原点在脚底、+Z 指向头顶），故 Z 轴获得 1.15× 先验权重：张臂/翅膀
    /// 把 X/Y 抬到与 Z 接近时仍按 Z 站立处理（实测千代澪、Rosetta 的 X/Z 标准差
    /// 仅差 0.01-0.03）。主轴优势不足（<1.05×）视为各向同性，保守不旋转。
    /// </summary>
    private static ModelPreviewPresentationRotation GetUnlabeledCharacterRotation(
        IReadOnlyList<ModelPreviewMesh> meshes)
    {
        const int minimumMeshCount = 3;
        const double zAxisPriorWeight = 1.15;
        const double axisDominanceRatio = 1.05;
        if (meshes.Count < minimumMeshCount ||
            !TryGetBounds(meshes, out var bounds) ||
            !TryGetAxisStdDevs(meshes, out var stdX, out var stdY, out var stdZ))
            return ModelPreviewPresentationRotation.None;

        // Very small props (keychains, trinkets) must not receive a presentation turn.
        var zScore = stdZ * zAxisPriorWeight;
        var best = Math.Max(zScore, Math.Max(stdX, stdY));
        if (best < MinimumCharacterStdDev)
            return ModelPreviewPresentationRotation.None;

        var second = zScore >= stdX && zScore >= stdY
            ? Math.Max(stdX, stdY)
            : stdZ >= stdX && stdZ >= stdY
                ? Math.Max(stdX, stdY)
                : Math.Min(Math.Max(zScore, stdX), Math.Max(zScore, stdY));
        if (best < second * axisDominanceRatio)
            return ModelPreviewPresentationRotation.None;

        if (zScore >= stdX && zScore >= stdY)
            return Math.Abs(bounds.MaxZ) > Math.Abs(bounds.MinZ)
                ? ModelPreviewPresentationRotation.PositiveZToPositiveY
                : ModelPreviewPresentationRotation.NegativeZToPositiveY;

        // X/Y 主导的资源（武器、机甲、载具、VFX——实测正式库的"充满power的机甲"
        // 被 X 翻转误伤）不做呈现旋转：保持原始朝向比猜一个"立正"更不容易错。
        return ModelPreviewPresentationRotation.None;
    }

    public static double GetSuggestedFrontYaw(ModelPreviewPresentationRotation rotation) => rotation switch
    {
        ModelPreviewPresentationRotation.PositiveXToPositiveY => 180d,
        ModelPreviewPresentationRotation.NegativeXToPositiveY => 0d,
        ModelPreviewPresentationRotation.PositiveZToPositiveY => -90d,
        ModelPreviewPresentationRotation.NegativeZToPositiveY => 90d,
        _ => 0d
    };

    private static ModelPreviewPresentationRotation GetRotation(
        (double X, double Y, double Z) torso,
        (double X, double Y, double Z) lowerBody)
    {
        var upX = torso.X - lowerBody.X;
        var upY = torso.Y - lowerBody.Y;
        var upZ = torso.Z - lowerBody.Z;
        var components = new[]
        {
            (Magnitude: Math.Abs(upX), Axis: 0),
            (Magnitude: Math.Abs(upY), Axis: 1),
            (Magnitude: Math.Abs(upZ), Axis: 2)
        }.OrderByDescending(static component => component.Magnitude).ToArray();

        if (components[0].Magnitude <= 0 ||
            components[0].Magnitude < components[1].Magnitude * DominanceRatio)
            return ModelPreviewPresentationRotation.None;

        return components[0].Axis switch
        {
            0 when upX > 0 => ModelPreviewPresentationRotation.PositiveXToPositiveY,
            0 => ModelPreviewPresentationRotation.NegativeXToPositiveY,
            2 when upZ > 0 => ModelPreviewPresentationRotation.PositiveZToPositiveY,
            2 => ModelPreviewPresentationRotation.NegativeZToPositiveY,
            _ => ModelPreviewPresentationRotation.None
        };
    }

    private static bool TryGetCentroid(
        IReadOnlyList<ModelPreviewMesh> meshes,
        IReadOnlySet<ModelPreviewCustomizationSlot>? slots,
        out (double X, double Y, double Z) centroid)
    {
        var x = 0d;
        var y = 0d;
        var z = 0d;
        var pointCount = 0L;
        foreach (var mesh in meshes)
        {
            if (slots is not null && !slots.Contains(mesh.CustomizationSlot))
                continue;

            for (var index = 0; index < mesh.Positions.Length; index += 3)
            {
                x += mesh.Positions[index];
                y += mesh.Positions[index + 1];
                z += mesh.Positions[index + 2];
                pointCount++;
            }
        }

        if (pointCount == 0)
        {
            centroid = default;
            return false;
        }

        centroid = (x / pointCount, y / pointCount, z / pointCount);
        return true;
    }

    private static bool TryGetAxisStdDevs(
        IReadOnlyList<ModelPreviewMesh> meshes,
        out double stdX,
        out double stdY,
        out double stdZ)
    {
        double sumX = 0, sumY = 0, sumZ = 0;
        double sumX2 = 0, sumY2 = 0, sumZ2 = 0;
        long pointCount = 0;
        foreach (var mesh in meshes)
        {
            for (var index = 0; index + 2 < mesh.Positions.Length; index += 3)
            {
                var x = mesh.Positions[index];
                var y = mesh.Positions[index + 1];
                var z = mesh.Positions[index + 2];
                sumX += x;
                sumY += y;
                sumZ += z;
                sumX2 += x * x;
                sumY2 += y * y;
                sumZ2 += z * z;
                pointCount++;
            }
        }

        if (pointCount == 0)
        {
            stdX = stdY = stdZ = 0;
            return false;
        }

        var meanX = sumX / pointCount;
        var meanY = sumY / pointCount;
        var meanZ = sumZ / pointCount;
        stdX = Math.Sqrt(Math.Max(0, sumX2 / pointCount - meanX * meanX));
        stdY = Math.Sqrt(Math.Max(0, sumY2 / pointCount - meanY * meanY));
        stdZ = Math.Sqrt(Math.Max(0, sumZ2 / pointCount - meanZ * meanZ));
        return true;
    }

    private static bool TryGetBounds(
        IReadOnlyList<ModelPreviewMesh> meshes,
        out (double MinX, double MaxX, double MinY, double MaxY, double MinZ, double MaxZ) bounds)
    {
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var minZ = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        var maxZ = double.NegativeInfinity;
        var pointCount = 0;
        foreach (var mesh in meshes)
        {
            for (var index = 0; index < mesh.Positions.Length; index += 3)
            {
                var x = mesh.Positions[index];
                var y = mesh.Positions[index + 1];
                var z = mesh.Positions[index + 2];
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                minZ = Math.Min(minZ, z);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                maxZ = Math.Max(maxZ, z);
                pointCount++;
            }
        }

        bounds = (minX, maxX, minY, maxY, minZ, maxZ);
        return pointCount > 0;
    }
}

internal enum ModelPreviewPresentationRotation
{
    None,
    PositiveXToPositiveY,
    NegativeXToPositiveY,
    PositiveZToPositiveY,
    NegativeZToPositiveY
}
