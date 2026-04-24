using FluentGallery.Data;
using FluentGallery.Decoders;
using FluentGallery.Helpers;
using FluentGallery.Loaders;
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

        // ── Unhandled exception handlers — log crash then flush before dying ──
        this.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.Exception, "Unhandled WinUI exception (handled={Handled})", e.Handled);
            Log.CloseAndFlush();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled AppDomain exception (terminating={Terminating})", e.IsTerminating);
            Log.CloseAndFlush();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Log.Information("Process exiting");
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            Log.CloseAndFlush();
            e.SetObserved();
        };
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
                flushToDiskInterval:    TimeSpan.FromSeconds(1),
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
#if DEBUG
            logging.AddProvider(new DevWarningLoggerProvider());
#endif
        });

        // Data layer — EF Core factory + service facade
        var dbPath = AppDataPaths.DatabasePath;

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

        // Image loaders (singleton — share their preload cache across pages)
        services.AddSingleton<WicImageLoader>();
        services.AddSingleton<MagickImageLoader>();

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
        // AllPhotosViewModel is Transient: each navigation creates a fresh instance
        services.AddTransient<AllPhotosViewModel>();

        return services.BuildServiceProvider();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();

        // Init DB and restore window size before showing the window so there is
        // no visible resize flash.
        var db = Services.GetRequiredService<DatabaseService>();
        await db.InitializeAsync();
        var settings = await db.LoadSettingsAsync();
        var themeService = Services.GetRequiredService<IThemeService>();
        themeService.Apply(settings.Theme);
        var mainWindow = (MainWindow)_window;
        mainWindow.RestoreWindowSize(settings);
        if (settings.UseAcrylicBackdrop)
            mainWindow.ApplyBackdrop(true);

        _window.Activate();

        // For unpackaged apps, file associations pass the target path as a
        // command-line argument (args[0] is the executable, args[1] is the file).
        var cmdArgs = Environment.GetCommandLineArgs();
        if (cmdArgs.Length > 1 && File.Exists(cmdArgs[1]))
            mainWindow.NavigateToFile(cmdArgs[1]);

        // Background work that doesn't need to block the first paint.
        _ = db.CleanupOldDeletedPhotosAsync();
        _ = ScanAsync(db, settings);
    }

    private async Task ScanAsync(DatabaseService db, Models.AppSettings settings)
    {
        var scan       = Services.GetRequiredService<ScanService>();
        var dispatcher = _window?.DispatcherQueue;
        await scan.StartAsync(settings, dispatcher).ConfigureAwait(false);
    }
}
