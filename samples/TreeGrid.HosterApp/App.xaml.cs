using Microsoft.UI.Xaml;
using Uno.UI; // UseStudio() — App MCP inspection channel (DEBUG only)

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
        // --virtual (or HOSTER_MODE=virtual) hosts the VirtualTreeItemsSource playground —
        // the paged, latency-simulating stack the library grid runs; default hosts the
        // in-memory flattener playground.
        var virtualMode = Environment.GetCommandLineArgs().Contains("--virtual")
            || Environment.GetEnvironmentVariable("HOSTER_MODE") == "virtual";

        MainWindow = new Window { Title = virtualMode ? "TreeGrid Hoster (virtual)" : "TreeGrid Hoster" };
        MainWindow.Content = virtualMode ? new VirtualHosterView() : new HosterView();
        if (MainWindow.Content is FrameworkElement root)
            root.RequestedTheme = ElementTheme.Dark;
        // Uno Studio/RemoteControl channel so the App MCP can screenshot/inspect live.
#if DEBUG
        MainWindow.UseStudio(showHotReloadIndicator: false);
#endif
        MainWindow.Activate();
    }
}
