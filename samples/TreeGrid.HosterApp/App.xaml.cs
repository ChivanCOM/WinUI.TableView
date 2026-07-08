using Microsoft.UI.Xaml;

namespace TreeGrid.HosterApp;

/// <summary>
/// Hosts the tree-grid playground. The self-test kicks off automatically after launch
/// and reports [hoster] PASS/FAIL lines on stdout, so the whole tree-grid stack
/// (flattener → VectorChanged → Uno ListView realization → TableViewTreeColumn cells)
/// can be verified headlessly on any desktop OS.
/// </summary>
public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected Window? MainWindow { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new Window { Title = "TreeGrid Hoster" };
        MainWindow.Content = new HosterView();
        if (MainWindow.Content is FrameworkElement root)
            root.RequestedTheme = ElementTheme.Dark;
        MainWindow.Activate();
    }
}
