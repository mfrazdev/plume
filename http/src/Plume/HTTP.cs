using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Plume.Configuration;
using Plume.Http.Routes;
using Plume.Http.Routes.Server;
using Plume.Http.Routes.Websocket;
using Plume.Logging;

namespace Plume.Http;

    public class HttpServer(ILogger<HttpServer> logger)
    {
        public void Start()
        {
            logger.LogInformation("Iniciando o servidor Lunar Plume na porta {AppPort}...", ConfigManager.GlobalConfig.App.Port);

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Logging.AddPlumeLogger();
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
            // Configuração do Servidor Web Embutido (Kestrel) - Substitui o Netty/CIO do Ktor
            builder.WebHost.ConfigureKestrel(options =>
            {
                if (ConfigManager.GlobalConfig.Ssl.Enabled)
                {
                    try
                    {
                        // No .NET, não precisamos mais daquele parser gigante do Kotlin.
                        // O C# moderno lê arquivos .pem/.key nativamente para X509Certificate2!
                        var cert = X509Certificate2.CreateFromPemFile(
                            certPemFilePath: ConfigManager.GlobalConfig.Ssl.CertPath,
                            keyPemFilePath: ConfigManager.GlobalConfig.Ssl.KeyPath
                        );

                        options.ListenAnyIP(ConfigManager.GlobalConfig.App.Port, listenOptions =>
                        {
                            listenOptions.UseHttps(cert);
                        });
                    }
                    catch (Exception e)
                    {
                        logger.LogCritical(e, "Falha fatal ao iniciar o SSL. Verifique os arquivos .pem e .key.");
                        Environment.Exit(1);
                    }
                }
                else
                {
                    options.ListenAnyIP(ConfigManager.GlobalConfig.App.Port);
                }
            });

            // Otimização do ContentNegotiation (JSON)
            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.WriteIndented = false; // Igual Ktor: false pra voar na resposta
                options.SerializerOptions.PropertyNameCaseInsensitive = true;
            });

            // Configuração do Rate Limit (100 chamadas por 1 minuto usando o IP)
            builder.Services.AddRateLimiter(options =>
            {
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                {
                    var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter(remoteIp, _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 100,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    });
                });

                options.OnRejected = async (context, token) =>
                {
                    context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    await context.HttpContext.Response.WriteAsJsonAsync(new Responses.ErrorResponse(
                        Status: 429,
                        Error: "Too Many Requests",
                        Message: "Rate limit exceeded"
                    ), cancellationToken: token);
                };
            });

            // Configuração do CORS
            var remoteUrl = ConfigManager.GlobalConfig.Remote.Url;
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    if (!string.IsNullOrWhiteSpace(remoteUrl))
                    {
                        var uri = new Uri(remoteUrl);
                        string origin = $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}";
                        policy.WithOrigins(origin);
                    }
                    else
                    {
                        // Para funcionar AllowAnyHost junto com AllowCredentials no ASP.NET, usamos SetIsOriginAllowed
                        policy.SetIsOriginAllowed(_ => true);
                    }

                    policy.WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
                          .WithHeaders("Authorization", "Content-Type")
                          .AllowCredentials();
                });
            });

            var app = builder.Build();

            app.UseWebSockets();
            app.UseCors();
            app.UseRateLimiter();

            // Configurações Globais da Pipeline
            ConfigureSecurity(app);
            ConfigureStatusPages(app);
            RouteRegistry.RegisterAll(app);

            app.Run(); // start(wait = true)
        }

        private void ConfigureSecurity(WebApplication app)
        {
            app.Use(async (context, next) =>
            {
                if (context.Request.Method == HttpMethods.Options)
                {
                    await next();
                    return;
                }

                var path = context.Request.Path.Value ?? "";
                var method = context.Request.Method;
                var contentType = context.Request.ContentType ?? "";

                bool isWebSocketUpgrade = context.WebSockets.IsWebSocketRequest;
                
                // Rotas que pulam a autenticação restrita
                if (isWebSocketUpgrade || path.StartsWith("/ws") || path.Contains("filemanager") || path.Contains("/servers/usages"))
                {
                    await next();
                    return;
                }

                // Pula Multipart FormData (ex: upload de arquivos pesados)
                if (method == HttpMethods.Post && contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                {
                    await next();
                    return;
                }

                string? authHeader = context.Request.Headers.Authorization;
                string? bodyToken = null;

                // SEU JEITO CLÁSSICO DE LER O JSON ATRÁS DO TOKEN MANTIDO AQUI:
                if (string.IsNullOrEmpty(authHeader) && (method == HttpMethods.Post || method == HttpMethods.Put || method == HttpMethods.Patch))
                {
                    // Necessário ligar o buffering para que a Minimal API possa ler o corpo depois do Middleware
                    context.Request.EnableBuffering();
                    
                    try
                    {
                        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
                        string bodyText = await reader.ReadToEndAsync();
                        context.Request.Body.Position = 0; // Reseta o cursor do ponteiro pra quem for ler depois

                        if (bodyText.Contains("token"))
                        {
                            var match = Regex.Match(bodyText, @"""token""\s*:\s*""(.*?)""");
                            if (match.Success)
                            {
                                bodyToken = match.Groups[1].Value;
                            }
                        }
                    }
                    catch
                    {
                        // Se falhar a leitura, bodyToken será null e cairá na verificação de inválido
                    }
                }

                string expected = $"Bearer {ConfigManager.GlobalConfig.Remote.Token}";
                bool isValid = authHeader == expected || bodyToken == ConfigManager.GlobalConfig.Remote.Token;

                if (!isValid)
                {
                    string remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    logger.LogWarning($"Tentativa de acesso não autorizado de IP: {remoteIp}, authHeader: {authHeader}, bodyToken: {bodyToken}");

                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new Responses.ErrorResponse(
                        Status: 403,
                        Error: "Forbidden",
                        Message: "Invalid token"
                    ));
                    return; // Finaliza a requisição aqui.
                }

                await next();
            });
        }

        private void ConfigureStatusPages(WebApplication app)
        {
            // Middleware global de tratamento de erros (StatusPages do Ktor)
            app.UseExceptionHandler(exceptionHandlerApp =>
            {
                exceptionHandlerApp.Run(async context =>
                {
                    var exceptionHandlerPathFeature = context.Features.Get<IExceptionHandlerPathFeature>();
                    var exception = exceptionHandlerPathFeature?.Error;

                    if (exception is OperationCanceledException)
                    {
                        // Ignore/Silencia quando o cliente fecha a conexão
                        return;
                    }

                    // Se for erro de request inválido (ex: parser do JSON falhou)
                    if (exception is BadHttpRequestException)
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        await context.Response.WriteAsJsonAsync(new Responses.ErrorResponse(
                            Status: 400,
                            Error: "Bad Request",
                            Message: exception.Message
                        ));
                        return;
                    }

                    // Erro interno (Internal Server Error)
                    logger.LogError(exception, "Erro interno detectado");
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await context.Response.WriteAsJsonAsync(new Responses.ErrorResponse(
                        Status: 500,
                        Error: "Internal Server Error",
                        Message: exception?.Message ?? "Ocorreu um erro inesperado."
                    ));
                });
            });
        }
    }

    public static class RouteRegistry
    {
        // Central de roteamento - usando extension methods do ASP.NET Core
        public static void RegisterAll(WebApplication app)
        {
            app.MapGet("/stop", () =>
            {
                Environment.Exit(0);
                return Results.Ok();
            });

            // Agrupando com o prefixo "/api/v1"
            var apiGroup = app.MapGroup("/api/v1");

            StatusRoute.Register(apiGroup);
            WebsocketUsages.Register(apiGroup);
            WebsocketConsole.Register(apiGroup);
            ActionsRoute.Register(apiGroup);
            CreateRoute.Register(apiGroup);
            DeleteRoute.Register(apiGroup);
            UsagesRoute.Register(apiGroup);
            FileManagerRoute.Register(apiGroup);
        }
    }

    // Records globais auxiliares da API
    public static class Responses
    {
        public record ErrorResponse(int Status, string Error, string Message);
    }
