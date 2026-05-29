using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Plume.Configuration;
using Plume.Server;
using Plume.Server.Extending;

namespace Plume.Http.Routes.Websocket
{
    public static class WebsocketConsole
    {
        // Data records para serialização rápida
        public record WsLogMessage(
            string Type,
            string Prefix,
            string Category,
            string Message,
            long Timestamp,
            string Line
        );

        public record WsClearMessage(string Type = "clear");

        public record WsErrorResponse(string Message, string Category = "error");

        public record IncomingCommand(string? Type = null, string? Command = null);

        // Opções de serialização (camelCase nativo)
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static void Register(RouteGroupBuilder group)
        {
            // Mapeia o endpoint WS. No ASP.NET Core, aceitamos o request e verificamos se é WS.
            group.Map("/servers/console", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                string? serverId = context.Request.Query["serverId"];
                string? userUuid = context.Request.Query["userUuid"];
                int tailReq = int.TryParse(context.Request.Query["tail"], out int t) ? t : 200;

                using var ws = await context.WebSockets.AcceptWebSocketAsync();

                // Função utilitária para fechar WS rapidamente
                async Task CloseWs(string reason)
                {
                    if (ws.State == WebSocketState.Open)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
                    }
                }

                // Função utilitária para enviar JSON
                async Task SendSerialized<T>(T payload)
                {
                    if (ws.State != WebSocketState.Open) return;
                    var json = JsonSerializer.Serialize(payload, JsonOpts);
                    var bytes = Encoding.UTF8.GetBytes(json);
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }

                if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(userUuid))
                {
                    await CloseWs("Parâmetros ausentes");
                    return;
                }

                if (!ConfigManager.ValidateServerID(serverId))
                {
                    await CloseWs("Invalid serverId");
                    return;
                }

                if (!ConfigManager.ValidateUUID(userUuid))
                {
                    await CloseWs("Invalid userUuid");
                    return;
                }

                var srv = Manager.Get(serverId);
                if (srv == null)
                {
                    await SendSerialized(new WsErrorResponse(Message: "Servidor inexistente"));
                    await CloseWs("Servidor inexistente");
                    return;
                }

                if (!WsCommon.CheckPermissionCached(userUuid, srv))
                {
                    await SendSerialized(new WsErrorResponse(Message: "Sem permissão"));
                    await CloseWs("Sem permissão");
                    return;
                }

                int streamStarted = 0; // Utilizado como AtomicBoolean via Interlocked
                Action? logCleanup = null;

                // FIX: Mudado para Unbounded para garantir que NENHUM log seja dropado,
                // independente do volume que o servidor cuspir.
                var logChannel = Channel.CreateUnbounded<string>();

                // CancellationToken para encerrar as rotinas em background quando o WS fechar
                using var cts = new CancellationTokenSource();

                async Task SendStructured(string category, string message, long timestamp = 0)
                {
                    if (category == "internal") return;
                    if (timestamp == 0) timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    // FIX: Removida a lógica de lastLineHash que engolia linhas idênticas/repetidas

                    bool isLog = category == "log";
                    string prefixOut = isLog ? "" : ((category == "info" || category == "error") ? WsCommon.PrefixLabel2 : WsCommon.PrefixLabel);
                    string catColor = WsCommon.GetColorForCategory(category);

                    string line = isLog
                        ? $"{catColor}{message}{WsCommon.ResetColor}"
                        : $"{catColor}{WsCommon.Gradient(prefixOut)}{catColor} {message}{WsCommon.ResetColor}";

                    await SendSerialized(new WsLogMessage("line", prefixOut, category, message, timestamp, line));
                }

                // Rotina (Coroutine): Ler do Channel e enviar pro cliente
                var logSenderJob = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var line in logChannel.Reader.ReadAllAsync(cts.Token))
                        {
                            await SendStructured("log", line);
                        }
                    }
                    catch (OperationCanceledException) { }
                });

                void BeginStream()
                {
                    // Evita múltiplas instâncias da stream rodando ao mesmo tempo (Atomic compareAndSet)
                    if (Interlocked.CompareExchange(ref streamStarted, 1, 0) != 0) return;

                    try
                    {
                        logCleanup = srv.StreamLogs(tailReq, line =>
                        {
                            logChannel.Writer.TryWrite(line);
                        });
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"[Server Console] Erro ao iniciar stream de logs: {e.Message}");
                    }
                }

                string initialStatus = srv.GetStatus().ToLower();
                switch (initialStatus)
                {
                    case "running":
                    case "initializing":
                    case "installing":
                    case "stopping":
                        if (initialStatus != "running")
                        {
                            await SendStructured("status", $"Servidor marcado como {initialStatus}...");
                        }
                        BeginStream();
                        break;
                    default:
                        await SendStructured("status", "Servidor marcado como offline...");
                        break;
                }

                // Rotina (Coroutine): Ouvir eventos globais do LiveBus
                var eventsJob = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var evt in srv.Live.Events.WithCancellation(cts.Token))
                        {
                            string cat = evt.Category;
                            string msg = evt.Message;
                            long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                            if (cat == "clear")
                            {
                                await SendSerialized(new WsClearMessage());
                            }
                            else if (cat == "internal")
                            {
                                if (msg == "initializing" || msg == "running")
                                {
                                    BeginStream();
                                }
                            }
                            else
                            {
                                await SendStructured(cat, msg, ts);
                                if (cat == "status" && (msg.Contains("offline") || msg.Contains("desligado")))
                                {
                                    logCleanup?.Invoke();
                                    logCleanup = null;
                                    Interlocked.Exchange(ref streamStarted, 0); // set(false)
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                });

                // Loop Principal do WebSocket
                var buffer = new byte[8192];
                try
                {
                    while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                        
                        if (result.MessageType == WebSocketMessageType.Close) break;

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            string frameText = Encoding.UTF8.GetString(buffer, 0, result.Count);
                            try
                            {
                                var payload = JsonSerializer.Deserialize<IncomingCommand>(frameText, JsonOpts);
                                if (payload?.Type == "command" && !string.IsNullOrEmpty(payload.Command))
                                {
                                    Console.WriteLine($"[WebSocket Console] Comando recebido: {payload.Command}");
                                    await srv.SendCommandAsync(payload.Command);
                                }
                            }
                            catch { /* Ignora JSON inválido */ }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (WebSocketException) { /* Caiu a conexão brusca */ }
                finally
                {
                    cts.Cancel();
                    logChannel.Writer.Complete();
                    logCleanup?.Invoke();
                }
            });
        }
    }
}