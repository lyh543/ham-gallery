using FluentGallery.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace FluentGallery;

public partial class App : Application
{
    /// <summary>Gets the current <see cref="App"/> instance.</summary>
    public static new App Current => (App)Application.Current;

    /// <summary>Gets the DI service provider for the application.</summary>
    public IServiceProvider Services { get; }

    private Window? _window;

    public App()
    {
        Services = ConfigureServices();
        this.InitializeComponent();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Logging
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Debug);
#if DEBUG
            logging.AddDebug();
#endif
        });

        // ViewModels will be registered here as they are implemented.
        // Services (DatabaseService, ThumbnailService, etc.) will be added in Step 2+.

        return services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
