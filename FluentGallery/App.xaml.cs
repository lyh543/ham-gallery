using FluentGallery.Data;
using FluentGallery.Decoders;
using FluentGallery.Helpers;
using FluentGallery.Services;
using FluentGallery.ViewModels;
using FluentGallery.Views;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Serilog;
using Serilog.Events;

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

        // ── File logging via Serilog ──────────────────────────────────────────
        AppDataPaths.EnsureDirectoriesExist();
        var logPath = Path.Combine(AppDataPaths.LogsDirectory, "app-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                logPath,
                rollingInterval:        RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
#if DEBUG
            .WriteTo.Debug()
#endif
            .CreateLogger();

        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddSerilog(dispose: true);
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

        // ── Image decoder pipeline ────────────────────────────────────────────
        // Register decoders in descending priority order per extension.
        // For HEIC/HEIF: WIC first (requires system HEVC codec), Magick.NET fallback.
        services.AddSingleton<ImageDecoderPipeline>(_ =>
        {
            var pipeline = new ImageDecoderPipeline();
            pipeline.Register(WicImageDecoder.CreateForStandardFormats()); // jpg/png/bmp/gif/webp/tif
            pipeline.Register(WicImageDecoder.CreateForHeic());            // heic/heif via WIC (if codec present)
            pipeline.Register(new MagickImageDecoder());                   // heic/heif built-in fallback
            return pipeline;
        });

        // Data services
        services.AddSingleton<ExifService>();
        services.AddSingleton<ThumbnailService>();
        services.AddSingleton<ScanService>();

        // Services
        services.AddSingleton<IThemeService, WinUiThemeService>();

        // ViewModels
        // AlbumListViewModel is Singleton so it can hold a long-lived ScanService subscription
        services.AddTransient<MainWindowViewModel>();
        services.AddSingleton<AlbumListViewModel>();
        services.AddTransient<PhotoListViewModel>();
        services.AddTransient<SettingsViewModel>();
        // PhotoDetailViewModel is Transient: each navigation creates a fresh instance
        services.AddTransient<PhotoDetailViewModel>();
        // SearchViewModel is Transient: each navigation creates a fresh instance
        services.AddTransient<SearchViewModel>();

        return services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        // Initialise DB, then kick off background scan — fire-and-forget,
        // the UI already shows whatever is already in the database.
        _ = InitAndScanAsync();
    }

    private async Task InitAndScanAsync()
    {
        var db   = Services.GetRequiredService<DatabaseService>();
        var scan = Services.GetRequiredService<ScanService>();

        await db.InitializeAsync();

        // Remove DeletedPhoto snapshots older than one month (fire-and-forget)
        _ = db.CleanupOldDeletedPhotosAsync();

        var settings   = await db.LoadSettingsAsync();
        var dispatcher = _window?.DispatcherQueue;
        await scan.StartAsync(settings, dispatcher);
    }
}
