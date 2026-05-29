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
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Plume.Configuration;
using Plume.Http.Routes;
using Plume.Http.Routes.Server;
using Plume.Http.Routes.Websocket;
using Plume.Logging;
using PlumeSFTP.System;

namespace Plume.Http;

    // =========================================================================
    // HACK DO NATIVE AOT: Helper para converter qualquer coisa em JSON automaticamente
    // =========================================================================
    public static class Reply
    {
        private static readonly JsonSerializerSettings _settings = new()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Formatting = Formatting.None
        };

        public static IResult Json(object data, int statusCode = 200)
        {
            // O Newtonsoft usa reflexão em tempo de execução (automático!)
            string jsonString = JsonConvert.SerializeObject(data, _settings);
            // Retornamos como Content puro para o Minimal API não tentar usar o System.Text.Json chato
            return Results.Content(jsonString, "application/json", statusCode: statusCode);
        }
    }

    public class HttpServer(ILogger<HttpServer> logger)
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("Iniciando o servidor Lunar Plume na porta {AppPort}...",
                ConfigManager.GlobalConfig.App.Port);

            var builder = WebApplication.CreateBuilder();

            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
            });

            builder.Logging.ClearProviders();
            builder.Logging.AddPlumeLogger();
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);

            builder.WebHost.ConfigureKestrel(options =>
            {
                if (ConfigManager.GlobalConfig.Ssl.Enabled)
                {
                    try
                    {
                        var cert = X509Certificate2.CreateFromPemFile(
                            certPemFilePath: ConfigManager.GlobalConfig.Ssl.CertPath,
                            keyPemFilePath: ConfigManager.GlobalConfig.Ssl.KeyPath
                        );

                        options.ListenAnyIP(ConfigManager.GlobalConfig.App.Port,
                            listenOptions => { listenOptions.UseHttps(cert); });
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
                    context.HttpContext.Response.ContentType = "application/json";

                    var errorJson = JsonConvert.SerializeObject(new Responses.ErrorResponse(
                        Status: 429,
                        Error: "Too Many Requests",
                        Message: "Rate limit exceeded"
                    ), new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() });

                    await context.HttpContext.Response.WriteAsync(errorJson, token);
                };
            });

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

            ConfigureSecurity(app);
            ConfigureStatusPages(app);
            RouteRegistry.RegisterAll(app);

            app.MapPost("/update", async context =>
            {
                try
                {
                    await VersionManager.UpdateAsync();

                    Reply.Json(new
                    {
                        status = "success",
                    });
                }
                catch (Exception e)
                {
                    Reply.Json(new
                    {
                        status = "error",
                        error = e.Message
                    });
                    throw e;
                    
                }
            });

            await app.RunAsync();
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
                
                if (isWebSocketUpgrade || path.StartsWith("/ws") || path.Contains("filemanager") || path.Contains("/servers/usages"))
                {
                    await next();
                    return;
                }

                if (method == HttpMethods.Post && contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                {
                    await next();
                    return;
                }

                string? authHeader = context.Request.Headers.Authorization;
                string? bodyToken = null;

                if (string.IsNullOrEmpty(authHeader) && (method == HttpMethods.Post || method == HttpMethods.Put || method == HttpMethods.Patch))
                {
                    context.Request.EnableBuffering();
                    
                    try
                    {
                        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
                        string bodyText = await reader.ReadToEndAsync();
                        context.Request.Body.Position = 0;

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
                    
                    var errorJson = JsonConvert.SerializeObject(new Responses.ErrorResponse(
                        Status: 403,
                        Error: "Forbidden",
                        Message: "Invalid token"
                    ), new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() });
                    
                    await context.Response.WriteAsync(errorJson);
                    return; 
                }

                await next();
            });
        }

        private void ConfigureStatusPages(WebApplication app)
        {
            app.UseExceptionHandler(exceptionHandlerApp =>
            {
                exceptionHandlerApp.Run(async context =>
                {
                    var exceptionHandlerPathFeature = context.Features.Get<IExceptionHandlerPathFeature>();
                    var exception = exceptionHandlerPathFeature?.Error;

                    if (exception is OperationCanceledException) return;

                    context.Response.ContentType = "application/json";
                    var jsonSettings = new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() };

                    if (exception is BadHttpRequestException)
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        var errorJson = JsonConvert.SerializeObject(new Responses.ErrorResponse(
                            Status: 400,
                            Error: "Bad Request",
                            Message: exception.Message
                        ), jsonSettings);
                        await context.Response.WriteAsync(errorJson);
                        return;
                    }

                    logger.LogError(exception, "Erro interno detectado");
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    
                    var internalJson = JsonConvert.SerializeObject(new Responses.ErrorResponse(
                        Status: 500,
                        Error: "Internal Server Error",
                        Message: exception?.Message ?? "Ocorreu um erro inesperado."
                    ), jsonSettings);
                    
                    await context.Response.WriteAsync(internalJson);
                });
            });
        }
    }

    public static class RouteRegistry
    {
        public static void RegisterAll(WebApplication app)
        {
            app.MapGet("/", () => Results.Text("Plume HTTP OK", "text/plain"));

            app.MapGet("/stop", () =>
            {
                Environment.Exit(0);
                return Results.Ok();
            });

            var apiGroup = app.MapGroup("/api/v1");

            StatusRoute.Register(apiGroup);
            WebsocketUsages.Register(apiGroup);
            WebsocketConsole.Register(apiGroup);
            ActionsRoute.Register(apiGroup);
            CreateRoute.Register(apiGroup);
            DeleteRoute.Register(apiGroup);
            UsagesRoute.Register(apiGroup);
            FileManagerRoute.Register(apiGroup);

            app.MapFallback(() => Reply.Json(new Responses.ErrorResponse(
                Status: 404,
                Error: "Not Found",
                Message: "Route not found"
            ), statusCode: 404));
        }
    }

    public static class Responses
    {
        public record ErrorResponse(int Status, string Error, string Message);
    }

