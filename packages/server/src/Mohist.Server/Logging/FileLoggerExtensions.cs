using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mohist.Server.Logging;

/// <summary>
/// Extension methods that wire the custom <see cref="FileLoggerProvider"/>
/// into the host's <see cref="ILoggingBuilder"/>.
/// </summary>
public static class FileLoggerExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="FileLoggerProvider"/> bound to
    /// <see cref="ILogPathResolver"/>. Registering the provider as
    /// <see cref="ILoggerProvider"/> on the application service collection
    /// is enough — the logging framework enumerates all
    /// <see cref="ILoggerProvider"/> services when building the logger
    /// factory, so this is the standard dependency-injected wiring pattern
    /// (the same one <c>AddConsole</c>/<c>AddDebug</c>/<c>AddEventLog</c>
    /// use). The provider is also exposed as itself so other components
    /// (and tests) can resolve it directly when they need to write a
    /// record outside the standard <c>ILogger&lt;T&gt;</c> pipeline.
    /// </summary>
    public static ILoggingBuilder AddFileLogger(this ILoggingBuilder builder)
    {
        builder.Services.AddSingleton<FileLoggerProvider>();
        builder.Services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<FileLoggerProvider>());
        return builder;
    }
}
