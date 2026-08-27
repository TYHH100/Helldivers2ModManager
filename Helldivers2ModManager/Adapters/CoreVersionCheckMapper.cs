using Helldivers2ModManager.Core.Versioning;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;

namespace Helldivers2ModManager.Adapters;

internal static class CoreVersionCheckMapper
{
    public static Models.ModVersionCheckResult ToLegacy(
        Core.Versioning.ModVersionCheckResult result,
        LocalizationService localization,
        bool includeDetailedAnalysis)
    {
        return new Models.ModVersionCheckResult
        {
            Status = (Models.ModVersionStatus)result.Status,
            GameVersion = result.GameVersion,
            UnitsMissingGameReference = result.UnitsMissingGameReference,
            LastChecked = result.LastChecked.LocalDateTime,
            PatchUnits = new System.Collections.ObjectModel.ObservableCollection<PatchUnitInfo>(
                result.Units.Select(ToLegacy)),
            DetailedAnalysis = includeDetailedAnalysis && result.DetailedAnalysis is not null
                ? ToLegacy(result.DetailedAnalysis, localization)
                : null,
        };
    }

    public static PatchUnitInfo ToLegacy(PatchUnitAnalysis unit) => new()
    {
        FileName = unit.FileName,
        FileId = unit.FileId,
        Version = unit.Version,
        DataSize = (int)unit.DataSize,
        GpuSize = unit.GpuSize,
    };

    public static UnitResourceDetail ToUnitDetail(PatchUnitAnalysis unit, LocalizationService localization)
    {
        return new UnitResourceDetail
        {
            FileName = unit.FileName,
            EntryIndex = unit.EntryIndex,
            FileId = unit.FileId,
            Version = unit.Version,
            DataSize = (int)unit.DataSize,
            GpuSize = unit.GpuSize,
            EndingOffset = unit.EndingOffset,
            ExpectedDataSize = unit.ExpectedDataSize,
            DeclaredSizeMatchesInternal = unit.DeclaredSizeMatchesInternal,
            IsTruncated = unit.IsTruncated,
            LODGroupOffset = unit.LodGroupOffset,
            JointListOffset = unit.JointListOffset,
            LODGroupSize = unit.LodGroupSize,
            LODGroupInBounds = unit.LodGroupInBounds,
            UnitDataInBounds = unit.UnitDataInBounds,
            LayoutFormatChecked = unit.LayoutFormatChecked,
            LayoutFormatValid = unit.LayoutFormatValid,
            LayoutFormatIssueCount = unit.LayoutFormatIssueCount,
            GpuStructureChecked = unit.GpuStructureChecked,
            GpuStructureValid = unit.GpuStructureValid,
            GpuStructureIssueCount = unit.GpuStructureIssueCount,
            GpuStreamCount = unit.GpuStreamCount,
            UnknownGpuComponentCount = unit.UnknownGpuComponentCount,
            Warning = CreateWarning(unit, localization),
        };
    }

    public static ModDetailedAnalysis ToLegacy(Core.Versioning.ModPatchAnalysis analysis, LocalizationService localization)
    {
        return new ModDetailedAnalysis
        {
            PatchFiles = analysis.PatchFiles.Select(file => ToLegacy(file, localization)).ToList(),
            ResourceTypes = analysis.ResourceTypes.Select(static type => new ResourceTypeDistribution
            {
                TypeId = type.TypeId,
                ResourceCount = type.ResourceCount,
            }).ToList(),
            HasStructuralIssues = analysis.HasStructuralIssues,
            HasCompanionFileIssues = analysis.HasCompanionFileIssues,
            HasUnitStructuralIssues = analysis.HasUnitStructuralIssues,
            HasGpuResourceIssues = analysis.HasGpuResourceIssues,
            HasStreamResourceIssues = analysis.HasStreamResourceIssues,
            TotalPatchFiles = analysis.TotalPatchFiles,
            FilesWithUnits = analysis.FilesWithUnits,
            HealthyFileCount = analysis.HealthyFileCount,
            WarningFileCount = analysis.WarningFileCount,
            CorruptedFileCount = analysis.CorruptedFileCount,
        };
    }

    public static Models.PatchFileAnalysis ToLegacy(Core.Versioning.PatchFileAnalysis file, LocalizationService localization)
    {
        return new Models.PatchFileAnalysis
        {
            FileName = file.FileName,
            FileSize = file.FileSize,
            HealthStatus = (Models.PatchHealthStatus)file.HealthStatus,
            NumTypes = file.NumTypes,
            NumFiles = file.NumFiles,
            TotalResources = file.TotalResources,
            ResourceTypes = file.ResourceTypes.Select(static type => new ResourceTypeDistribution
            {
                TypeId = type.TypeId,
                ResourceCount = type.ResourceCount,
            }).ToList(),
            TypeDistributionValid = file.TypeDistributionValid,
            TypeDistributionIssueCount = file.TypeDistributionIssueCount,
            HeaderValid = file.HeaderValid,
            FileEntriesInBounds = file.FileEntriesInBounds,
            MainDataBoundsValid = file.MainDataBoundsValid,
            MainDataIssueCount = file.MainDataIssueCount,
            EntryIndicesValid = file.EntryIndicesValid,
            EntryIndexIssueCount = file.EntryIndexIssueCount,
            HasGpuResources = file.HasGpuResources,
            RequiresGpuResources = file.RequiresGpuResources,
            HasStream = file.HasStream,
            RequiresStream = file.RequiresStream,
            GpuResourceBoundsValid = file.GpuResourceBoundsValid,
            GpuResourceIssueCount = file.GpuResourceIssueCount,
            GpuAlignmentIssueCount = file.GpuAlignmentIssueCount,
            StreamBoundsValid = file.StreamBoundsValid,
            StreamIssueCount = file.StreamIssueCount,
            StreamAlignmentIssueCount = file.StreamAlignmentIssueCount,
            UnitDetails = file.UnitDetails.Select(unit => ToUnitDetail(unit, localization)).ToList(),
        };
    }

    private static string? CreateWarning(PatchUnitAnalysis unit, LocalizationService localization)
    {
        string? warning = null;
        void Append(string value)
        {
            warning = string.IsNullOrWhiteSpace(warning) ? value : warning + Environment.NewLine + value;
        }

        if (!unit.UnitDataInBounds)
        {
            return localization["VersionCheck.UnitDataOutOfBounds"];
        }

        if (!unit.DeclaredSizeMatchesInternal)
        {
            Append(localization[unit.IsTruncated
                ? "VersionCheck.UnitDataSizeTruncated"
                : "VersionCheck.UnitDataSizeMismatch"]
                .Replace("{declared}", unit.DataSize.ToString())
                .Replace("{expected}", unit.ExpectedDataSize.ToString())
                .Replace("{difference}", Math.Abs(unit.ExpectedDataSize - unit.DataSize).ToString()));
        }

        if (!unit.LodGroupInBounds)
        {
            Append(localization["VersionCheck.LodDataOutOfBounds"]);
        }

        if (unit.LayoutFormatChecked && !unit.LayoutFormatValid)
        {
            Append(localization["VersionCheck.LayoutFormatIssues"]
                .Replace("{count}", unit.LayoutFormatIssueCount.ToString()));
        }

        return warning;
    }
}
