using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;
using WinUI.TableView.SampleApp.Helpers;

namespace WinUI.TableView.SampleApp;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        themeMode.SelectedIndex = ThemeHelper.RootTheme switch
        {
            ElementTheme.Light => 0,
            ElementTheme.Dark => 1,
            _ => 2,
        };
    }

    private void OnThemeModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not UIElement senderUiLement ||
           (themeMode.SelectedItem as ComboBoxItem)?.Tag is not string selectedTheme)
        {
            return;
        }

        ThemeHelper.RootTheme = ThemeHelper.GetElementTheme(selectedTheme);
        var elementThemeResolved = ThemeHelper.RootTheme == ElementTheme.Default ? ThemeHelper.ActualTheme : ThemeHelper.RootTheme;
        TitleBarHelper.ApplySystemThemeToCaptionButtons(App.Current.MainWindow, elementThemeResolved);

        // announce visual change to automation
        UIHelper.AnnounceActionForAccessibility(
            senderUiLement,
            $"Theme changed to {elementThemeResolved}",
            "ThemeChangedNotificationActivityId");
    }

    public string Version
    {
        get
        {
            var version = typeof(SettingsPage).Assembly.GetName().Version!;
            return string.Format("{0}.{1}.{2}", version.Major, version.Minor, version.Build);
        }
    }
}
