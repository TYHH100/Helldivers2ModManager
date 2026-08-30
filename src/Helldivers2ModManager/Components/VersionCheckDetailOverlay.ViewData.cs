using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Helldivers2ModManager.Components;

internal partial class VersionCheckDetailOverlay : UserControl, IRecipient<VersionCheckDetailMessage>
{
    private VersionCheckDetailViewData BuildViewData(VersionCheckDetailMessage message)
    {
        var issues = BuildIssues(message);
        var unitCount = message.Analysis.PatchFiles.Sum(p => p.UnitDetails.Count);
        var problematicUnits = message.Analysis.PatchFiles
            .SelectMany(p => p.UnitDetails.Select(u => (Patch: p, Unit: u)))
            .Count(x => IsProblematicUnit(x.Unit));
        var healthyUnits = Math.Max(0, unitCount - problematicUnits);
        var truncatedCount = message.Analysis.PatchFiles
            .SelectMany(p => p.UnitDetails)
            .Count(u => u.IsTruncated);

        var statusBrush = message.Status switch
        {
            ModVersionStatus.Compatible => GetBrush("SuccessBrush", Colors.ForestGreen),
            ModVersionStatus.Incompatible or ModVersionStatus.Error => GetBrush("DangerBrush", Colors.IndianRed),
            ModVersionStatus.Checking => GetBrush("SystemAccentBrush", Colors.DodgerBlue),
            _ => GetBrush("WarningBrush", Colors.Goldenrod)
        };
        var statusColor = statusBrush is SolidColorBrush solid ? solid.Color : Colors.Gray;
        var statusBackground = new SolidColorBrush(Color.FromArgb(28, statusColor.R, statusColor.G, statusColor.B));

        var statusText = message.Status switch
        {
            ModVersionStatus.Compatible => L("Converters.Compatible", "Compatible"),
            ModVersionStatus.Incompatible => L("Converters.Incompatible", "Incompatible"),
            ModVersionStatus.Checking => L("Converters.Checking", "Checking"),
            ModVersionStatus.Error => L("VersionCheck.CheckFailed", "Check failed"),
            _ => L("Converters.UnableToConfirm", "Unable to confirm")
        };
        var statusIcon = message.Status switch
        {
            ModVersionStatus.Compatible => "\uE73E",
            ModVersionStatus.Incompatible or ModVersionStatus.Error => "\uE783",
            ModVersionStatus.Checking => "\uE895",
            _ => "\uE946"
        };

        string summary;
        if (truncatedCount > 0)
        {
            summary = L("VersionCheckDetail.SummaryTruncated", "{count} Unit resource(s) are truncated by their TOC size.")
                .Replace("{count}", truncatedCount.ToString());
        }
        else if (message.Analysis.CorruptedFileCount > 0)
        {
            summary = L("VersionCheckDetail.SummaryCorrupted", "{count} patch file(s) contain structural damage.")
                .Replace("{count}", message.Analysis.CorruptedFileCount.ToString());
        }
        else if (message.Status == ModVersionStatus.Incompatible)
        {
            summary = L("VersionCheckDetail.SummaryVersionMismatch", "One or more Unit versions differ from the reference version.");
        }
        else if (message.Status == ModVersionStatus.Compatible)
        {
            summary = L("VersionCheckDetail.SummaryHealthy", "No blocking compatibility issues were detected.");
        }
        else
        {
            summary = L("VersionCheckDetail.SummaryUnknown", "There is not enough Unit version information to confirm compatibility.");
        }

        var hiddenCount = Math.Max(0, issues.Count - MaxVisibleIssues);
        return new VersionCheckDetailViewData
        {
            ModName = message.ModName,
            StatusIcon = statusIcon,
            StatusText = statusText,
            Summary = summary,
            StatusBrush = statusBrush,
            StatusBackground = statusBackground,
            IssueCountBrush = issues.Count > 0 ? GetBrush("DangerBrush", Colors.IndianRed) : GetBrush("SuccessBrush", Colors.ForestGreen),
            PatchFileCount = message.Analysis.TotalPatchFiles,
            ResourceCount = message.Analysis.PatchFiles.Sum(p => p.TotalResources),
            UnitCount = unitCount,
            TotalIssueCount = issues.Count,
            HealthyUnitSummary = healthyUnits > 0
                ? L("VersionCheckDetail.HealthyUnits", "{count} Unit(s) passed structural checks").Replace("{count}", healthyUnits.ToString())
                : string.Empty,
            VisibleIssues = issues.Take(MaxVisibleIssues).ToList(),
            HiddenIssueSummary = hiddenCount > 0
                ? L("VersionCheckDetail.HiddenIssues", "{count} more issue(s) are available in technical details.").Replace("{count}", hiddenCount.ToString())
                : string.Empty,
            IssuesVisibility = issues.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
            NoIssuesVisibility = issues.Count == 0 ? Visibility.Visible : Visibility.Collapsed,
            HiddenIssuesVisibility = hiddenCount > 0 ? Visibility.Visible : Visibility.Collapsed,
            TechnicalReport = message.FullReport
        };
    }

    private List<VersionCheckDiagnosticIssue> BuildIssues(VersionCheckDetailMessage message)
    {
        var issues = new List<VersionCheckDiagnosticIssue>();
        var danger = GetBrush("DangerBrush", Colors.IndianRed);
        var warning = GetBrush("WarningBrush", Colors.Goldenrod);
        var versionMismatches = message.PatchUnits
            .Where(u => message.GameVersion != 0 && u.Version != message.GameVersion &&
                        !message.UnitsMissingGameReference.Contains(u.FileId))
            .ToList();

        if (versionMismatches.Count > 0)
        {
            var versions = string.Join(", ", versionMismatches.Select(u => $"0x{u.Version:X8}").Distinct());
            issues.Add(new VersionCheckDiagnosticIssue
            {
                Icon = "\uE7BA",
                Brush = danger,
                Title = L("VersionCheckDetail.VersionMismatchTitle", "Unit version mismatch"),
                FileName = string.Empty,
                Description = L("VersionCheckDetail.VersionMismatchDescription", "{count} Unit(s) use {versions}; reference is {reference}.")
                    .Replace("{count}", versionMismatches.Count.ToString())
                    .Replace("{versions}", versions)
                    .Replace("{reference}", $"0x{message.GameVersion:X8}")
            });
        }

        foreach (var patch in message.Analysis.PatchFiles)
        {
            var fileIssueCountBefore = issues.Count;
            AddFileIssues(issues, patch, danger, warning);

            foreach (var unit in patch.UnitDetails)
            {
                if (unit.IsTruncated)
                {
                    issues.Add(new VersionCheckDiagnosticIssue
                    {
                        Icon = "\uE7BA",
                        Brush = danger,
                        Title = L("VersionCheckDetail.UnitTruncatedTitle", "Unit #{index} data is truncated")
                            .Replace("{index}", unit.EntryIndex.ToString()),
                        FileName = patch.FileName,
                        Description = L("VersionCheckDetail.UnitTruncatedDescription", "TOC declares {declared} bytes, internal size is {expected}; {difference} bytes are missing. ID {fileId}")
                            .Replace("{declared}", unit.DataSize.ToString())
                            .Replace("{expected}", unit.ExpectedDataSize.ToString())
                            .Replace("{difference}", Math.Max(0, unit.ExpectedDataSize - unit.DataSize).ToString())
                            .Replace("{fileId}", $"0x{unit.FileId:X16}")
                    });
                }
                else if (!unit.DeclaredSizeMatchesInternal)
                {
                    issues.Add(new VersionCheckDiagnosticIssue
                    {
                        Icon = "\uE7BA",
                        Brush = warning,
                        Title = L("VersionCheckDetail.UnitSizeMismatchTitle", "Unit #{index} size mismatch")
                            .Replace("{index}", unit.EntryIndex.ToString()),
                        FileName = patch.FileName,
                        Description = L("VersionCheckDetail.UnitSizeMismatchDescription", "TOC declares {declared} bytes; internal size is {expected}. ID {fileId}")
                            .Replace("{declared}", unit.DataSize.ToString())
                            .Replace("{expected}", unit.ExpectedDataSize.ToString())
                            .Replace("{fileId}", $"0x{unit.FileId:X16}")
                    });
                }

                if (!unit.UnitDataInBounds)
                    AddSimpleIssue(issues, danger, patch.FileName, "VersionCheckDetail.UnitBoundsTitle", "Unit data exceeds patch bounds", unit.Warning);
                else if (!unit.LODGroupInBounds)
                    AddSimpleIssue(issues, danger, patch.FileName, "VersionCheckDetail.LodBoundsTitle", "Unit LOD data exceeds its declared bounds", unit.Warning);

                if (unit.LayoutFormatChecked && !unit.LayoutFormatValid)
                    AddSimpleIssue(issues, danger, patch.FileName, "VersionCheckDetail.LayoutTitle", "Legacy Unit layout requires repair", unit.Warning);
            }

            if (patch.HealthStatus is PatchHealthStatus.Corrupted or PatchHealthStatus.Warning &&
                issues.Count == fileIssueCountBefore && !string.IsNullOrWhiteSpace(patch.Message))
            {
                AddSimpleIssue(issues,
                    patch.HealthStatus == PatchHealthStatus.Corrupted ? danger : warning,
                    patch.FileName,
                    "VersionCheckDetail.GenericFileIssueTitle",
                    "Patch file warning",
                    patch.Message);
            }
        }

        return issues;
    }

    private void AddFileIssues(List<VersionCheckDiagnosticIssue> issues, PatchFileAnalysis patch, Brush danger, Brush warning)
    {
        if (!patch.HeaderValid || !patch.FileEntriesInBounds)
            AddSimpleIssue(issues, danger, patch.FileName, "VersionCheckDetail.HeaderIssueTitle", "Invalid patch header or TOC", patch.Message);
        if (!patch.TypeDistributionValid)
            AddSimpleIssue(issues, danger, patch.FileName, "VersionCheckDetail.TypeTableTitle", "Resource type table is inconsistent", L("VersionCheckDetail.TypeTableDescription", "The type table does not match the {count} actual file entries.").Replace("{count}", patch.NumFiles.ToString()));
        if (!patch.MainDataBoundsValid)
            AddSimpleIssue(issues, danger, patch.FileName, "VersionCheckDetail.MainBoundsTitle", "Main resource data is out of bounds", L("VersionCheckDetail.MainBoundsDescription", "{count} invalid or overlapping range(s).").Replace("{count}", patch.MainDataIssueCount.ToString()));
        if (!patch.EntryIndicesValid)
            AddSimpleIssue(issues, warning, patch.FileName, "VersionCheckDetail.EntryIndexTitle", "TOC entry indices are not sequential", L("VersionCheckDetail.EntryIndexDescription", "{count} invalid index value(s).").Replace("{count}", patch.EntryIndexIssueCount.ToString()));
        if (patch.RequiresGpuResources && !patch.HasGpuResources)
            AddSimpleIssue(issues, danger, patch.FileName, "VersionCheckDetail.MissingGpuTitle", "Required GPU resource file is missing", L("VersionCheckDetail.MissingGpuDescription", "The patch contains non-zero GPU resource references."));
        if (patch.RequiresStream && !patch.HasStream)
            AddSimpleIssue(issues, danger, patch.FileName, "VersionCheckDetail.MissingStreamTitle", "Required stream file is missing", L("VersionCheckDetail.MissingStreamDescription", "The patch contains non-zero stream resource references."));
        if (!patch.GpuResourceBoundsValid || patch.GpuAlignmentIssueCount > 0)
            AddSimpleIssue(issues, patch.GpuResourceBoundsValid ? warning : danger, patch.FileName, "VersionCheckDetail.GpuIssueTitle", "GPU resource range problem", L("VersionCheckDetail.ResourceRangeDescription", "Out of bounds: {bounds}; misaligned: {alignment}.").Replace("{bounds}", patch.GpuResourceIssueCount.ToString()).Replace("{alignment}", patch.GpuAlignmentIssueCount.ToString()));
        if (!patch.StreamBoundsValid || patch.StreamAlignmentIssueCount > 0)
            AddSimpleIssue(issues, patch.StreamBoundsValid ? warning : danger, patch.FileName, "VersionCheckDetail.StreamIssueTitle", "stream resource range problem", L("VersionCheckDetail.ResourceRangeDescription", "Out of bounds: {bounds}; misaligned: {alignment}.").Replace("{bounds}", patch.StreamIssueCount.ToString()).Replace("{alignment}", patch.StreamAlignmentIssueCount.ToString()));
    }

    private void AddSimpleIssue(List<VersionCheckDiagnosticIssue> issues, Brush brush, string fileName, string titleKey, string titleFallback, string? description)
    {
        issues.Add(new VersionCheckDiagnosticIssue
        {
            Icon = "\uE7BA",
            Brush = brush,
            Title = L(titleKey, titleFallback),
            FileName = fileName,
            Description = description ?? string.Empty
        });
    }

    private static bool IsProblematicUnit(UnitResourceDetail unit)
    {
        return !unit.UnitDataInBounds || !unit.LODGroupInBounds || !unit.DeclaredSizeMatchesInternal ||
               (unit.LayoutFormatChecked && !unit.LayoutFormatValid);
    }

    private string L(string key, string fallback)
    {
        return _localizationService?.Get(key, fallback) ?? fallback;
    }
}
