#if DEBUG
using Microsoft.Extensions.Logging;

namespace FluentGallery.Helpers;

/// <summary>
/// 仅在 DEBUG 构建下生效。拦截 Warning 及以上级别的日志事件，
/// 通过静态事件 <see cref="WarningLogged"/> 通知 UI 层弹出告警 Toast。
/// </summary>
public sealed class DevWarningLoggerProvider : ILoggerProvider
{
    /// <summary>
    /// 当有 Warning+ 日志产生时触发。
    /// 参数：(category, message)
    /// 可能在任意线程触发，订阅者负责切回 UI 线程。
    /// </summary>
    public static event Action<string, string>? WarningLogged;

    internal static void RaiseWarning(string category, string message)
        => WarningLogged?.Invoke(category, message);

    public ILogger CreateLogger(string categoryName)
        => new DevWarningLogger(categoryName);

    public void Dispose() { }
}

internal sealed class DevWarningLogger : ILogger
{
    private readonly string _category;

    public DevWarningLogger(string category) => _category = category;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel < LogLevel.Warning) return;
        var message = formatter(state, exception);
        DevWarningLoggerProvider.RaiseWarning(_category, message);
    }
}
#endif
