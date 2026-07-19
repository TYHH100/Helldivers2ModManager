using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Helldivers2ModManager.Components;

internal sealed class ModConflictDetailMessage
{
    public required string ModName { get; init; }
    public required IReadOnlyList<ModConflictRecord> Conflicts { get; init; }
}

internal sealed class ModConflictDetailItem
{
    public required string ResourceName { get; init; }
    public required string UnitText { get; init; }
    public required string ParticipantsText { get; init; }
    public required string WinnerText { get; init; }
    public required string Icon { get; init; }
    public required Brush Brush { get; init; }
}

internal sealed class ModConflictDetailViewData
{
    public required string ModName { get; init; }
    public required string StatusIcon { get; init; }
    public required string StatusText { get; init; }
    public required string Summary { get; init; }
    public required Brush StatusBrush { get; init; }
    public required Brush StatusBackground { get; init; }
    public required IReadOnlyList<ModConflictDetailItem> Items { get; init; }
    public int ConflictCount => Items.Count;
    public int DefiniteCount { get; init; }
    public int ModParticipantCount { get; init; }
    public Visibility NoConflictsVisibility => Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ConflictsVisibility => Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
}

internal partial class ModConflictDetailOverlay : UserControl, IRecipient<ModConflictDetailMessage>
{
    private readonly LocalizationService? _localizationService;

    public ModConflictDetailOverlay()
    {
        InitializeComponent();
        DataContext = null;
        WeakReferenceMessenger.Default.Register<ModConflictDetailMessage>(this);

        if (Application.Current is App app &&
            app.Host?.Services?.GetService(typeof(LocalizationService)) is LocalizationService localizationService)
        {
            _localizationService = localizationService;
        }
    }

    public void Receive(ModConflictDetailMessage message)
    {
        DataContext = BuildViewData(message);
        Visibility = Visibility.Visible;
        Focus();
    }

    private ModConflictDetailViewData BuildViewData(ModConflictDetailMessage message)
    {
        var items = message.Conflicts
            .Where(static conflict => !string.IsNullOrWhiteSpace(conflict.FriendlyName))
            .OrderBy(static conflict => conflict.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .Select(conflict =>
            {
                var participants = string.Join(", ", conflict.Participants
                    .Select(static participant => participant.ModName)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
                var definite = conflict.IsDefiniteConflict;
                return new ModConflictDetailItem
                {
                    ResourceName = conflict.FriendlyName,
                    UnitText = $"{L("ConflictDetail.Unit", "Unit")}: {conflict.OriginalName}",
                    ParticipantsText = $"{L("ConflictDetail.ConflictingMods", "Conflicting mods")}: {participants}",
                    WinnerText = $"{L("ConflictDetail.CurrentWinner", "Current winner")}: {conflict.Winner.ModName}",
                    Icon = definite ? "!" : "?",
                    Brush = definite
                        ? new SolidColorBrush(Color.FromRgb(220, 80, 55))
                        : new SolidColorBrush(Color.FromRgb(220, 155, 45)),
                };
            })
            .ToArray();

        var definiteCount = message.Conflicts.Count(static conflict => conflict.IsDefiniteConflict);
        var participantCount = message.Conflicts
            .SelectMany(static conflict => conflict.Participants)
            .Select(static participant => participant.ModGuid)
            .Distinct()
            .Count();
        var hasConflicts = items.Length > 0;
        var statusBrush = hasConflicts
            ? new SolidColorBrush(Color.FromRgb(220, 80, 55))
            : new SolidColorBrush(Color.FromRgb(40, 160, 95));

        return new ModConflictDetailViewData
        {
            ModName = message.ModName,
            StatusIcon = hasConflicts ? "!" : "✓",
            StatusText = hasConflicts
                ? L("ConflictDetail.ConflictFound", "Resource conflicts detected")
                : L("ConflictDetail.NoConflicts", "No resource conflicts"),
            Summary = hasConflicts
                ? L("ConflictDetail.ConflictSummary", "This mod shares armor resources with other enabled mods. The current deployment order determines the winner.")
                : L("ConflictDetail.NoConflictsDescription", "No other enabled mod currently provides the same armor resources."),
            StatusBrush = statusBrush,
            StatusBackground = hasConflicts ? new SolidColorBrush(Color.FromArgb(0x18, 0xDC, 0x50, 0x37)) : new SolidColorBrush(Color.FromArgb(0x18, 0x28, 0xA0, 0x5F)),
            Items = items,
            DefiniteCount = definiteCount,
            ModParticipantCount = participantCount,
        };
    }

    private string L(string key, string fallback) => _localizationService?[key] ?? fallback;

    private void Close()
    {
        Visibility = Visibility.Hidden;
        DataContext = null;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();
    private void Dialog_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void Overlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (Visibility == Visibility.Visible)
            Focus();
    }

    private void Overlay_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
