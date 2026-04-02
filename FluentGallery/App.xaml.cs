using FluentGallery.Data;
using FluentGallery.Services;
using FluentGallery.ViewModels;
using FluentGallery.Views;
using Microsoft.EntityFrameworkCore;
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



    /// <summary>Gets the application's main window (set after <see cref="OnLaunched"/>).</summary>
    public Window? MainWindow => _window;

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

        // Data layer — EF Core factory + service facade
        var dbFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FluentGallery");
        Directory.CreateDirectory(dbFolder);
        var dbPath = Path.Combine(dbFolder, "gallery.db");

        services.AddDbContextFactory<GalleryDbContext>(options =>
        {
            options.UseSqlite(
                $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared",
                sql => sql.CommandTimeout(30));
#if DEBUG
            options.EnableSensitiveDataLogging();
#endif
        }, ServiceLifetime.Singleton);

        services.AddSingleton<DatabaseService>();

        // Services
        services.AddSingleton<IThemeService, WinUiThemeService>();

        // ViewModels
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<AlbumListViewModel>();
        services.AddTransient<SettingsViewModel>();

        return services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Initialise DB schema before showing any UI.
        var db = Services.GetRequiredService<DatabaseService>();
        _ = db.InitializeAsync();

        _window = new MainWindow();
        _window.Activate();
    }
}
