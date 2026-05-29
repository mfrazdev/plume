namespace Plume.Logging;

using Microsoft.Extensions.Logging;



// A implementação do Logger
public class PlumeLogger : ILogger
{
    private readonly string _categoryName;

    public PlumeLogger(string categoryName)
    {
        // Limita o nome do logger a 36 caracteres (igual ao %logger{36} do Logback)
        if (categoryName.Length > 36)
        {
            var parts = categoryName.Split('.');
            _categoryName = parts[^1].Length > 36 ? parts[^1].Substring(0, 36) : parts[^1];
        }
        else
        {
            _categoryName = categoryName;
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        string message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception == null) return;

        string time = DateTime.Now.ToString("HH:mm:ss");

        // Formatando o Level (%-5level)
        string levelStr = logLevel switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "FATAL",
            _ => "NONE "
        };

        // Cores baseadas no %highlight do logback
        string levelColor = logLevel switch
        {
            LogLevel.Trace => "\x1b[37m", // Branco
            LogLevel.Debug => "\x1b[36m", // Ciano
            LogLevel.Information => "\x1b[34m", // Azul/Verde
            LogLevel.Warning => "\x1b[33m", // Amarelo
            LogLevel.Error => "\x1b[31m", // Vermelho
            LogLevel.Critical => "\x1b[35m", // Magenta
            _ => "\x1b[37m"
        };

        const string RESET = "\x1b[0m";
        const string CYAN = "\x1b[36m";
        const string YELLOW = "\x1b[33m";
        const string GRAY = "\x1b[90m";

        // Imprime no padrão: %cyan(%d{HH:mm:ss}) %highlight(%-5level) %yellow(%logger{36}) %gray(|) %msg%n
        Console.WriteLine(
            $"{CYAN}{time}{RESET} {levelColor}{levelStr}{RESET} {YELLOW}{_categoryName}{RESET} {GRAY}|{RESET} {message}");

        // Se o log tiver uma exception embutida, passa pelo nosso CrashHandler
        if (exception != null)
        {
            CrashHandler.HandleException(exception, Thread.CurrentThread.Name ?? "Thread");
        }
    }
}

// Provedor para injetar o nosso logger no LoggerFactory
public class PlumeLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new PlumeLogger(categoryName);

    public void Dispose()
    {
    }
}

// Extensão para facilitar o uso no Program.cs
public static class PlumeLoggerExtensions
{
    public static ILoggingBuilder AddPlumeLogger(this ILoggingBuilder builder)
    {
        builder.ClearProviders(); // Remove o console padrão feio do .NET
        builder.AddProvider(new PlumeLoggerProvider());
        return builder;
    }
}