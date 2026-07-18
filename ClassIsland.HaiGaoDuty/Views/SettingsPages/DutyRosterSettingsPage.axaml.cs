using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Helpers.UI;
using ClassIsland.HaiGaoDuty.Services;
using ClassIsland.Platforms.Abstraction;
using ClassIsland.Shared;

namespace ClassIsland.HaiGaoDuty.Views.SettingsPages;

[SettingsPageInfo("cn.haigao.duty-roster.settings", "大周值日生")]
[HidePageTitle]
public partial class DutyRosterSettingsPage : SettingsPageBase
{
    public DutyRosterService DutyRoster { get; }

    public DutyRosterSettingsPage()
    {
        DutyRoster = IAppHost.GetService<DutyRosterService>();
        InitializeComponent();
        DataContext = DutyRoster;
    }

    private void ApplyDraft_OnClick(object? sender, RoutedEventArgs e) => DutyRoster.ApplyDraft();

    private async void ImportRoster_OnClick(object? sender, RoutedEventArgs e)
    {
        PopupHelper.DisableAllPopups();
        try
        {
            var files = await PlatformServices.FilePickerService.OpenFilesPickerAsync(new FilePickerOpenOptions
            {
                Title = "导入值日生名单",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Excel 或文本名单")
                    {
                        Patterns = ["*.xlsx", "*.xls", "*.xlsm", "*.xlsb", "*.csv", "*.tsv", "*.txt"]
                    }
                ]
            }, TopLevel.GetTopLevel(this) ?? AppBase.Current.GetRootWindow());
            if (files.Count > 0)
            {
                DutyRoster.ImportRoster(files[0]);
            }
        }
        finally
        {
            PopupHelper.RestoreAllPopups();
        }
    }

    private void CancelPendingConfiguration_OnClick(object? sender, RoutedEventArgs e) =>
        DutyRoster.CancelPendingConfiguration();
}
