using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Plume.Configuration;
using Plume.Server;

namespace Plume.Http.Routes.Websocket
{
    public static class WebsocketUsages
    {
        public record WsUsageData(
            double Cpu,
            long Memory,
            long MemoryLimit,
            double MemoryPercent,
            long NetworkIn,
            long NetworkOut,
            long Disk,
            long? StartedAt,
            long UptimeMs,
            string State
        );

        public record WsUsagePayload(
            string Type,
            long Timestamp,
            WsUsageData Usage
        );

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public static void Register(RouteGroupBuilder group)
        {
            group.Map("/servers/usages", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                string? serverId = context.Request.Query["serverId"];
                string? userUuid = context.Request.Query["userUuid"];

                if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(userUuid))
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                if (!ConfigManager.ValidateServerID(serverId) || !ConfigManager.ValidateUUID(userUuid))
                {
                    context.Response.StatusCode = 403;
                    return;
                }

                var srv = Manager.Get(serverId);
                if (srv == null)
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                if (!WsCommon.CheckPermissionCached(userUuid, srv))
                {
                    context.Response.StatusCode = 403;
                    return;
                }

                using var ws = await context.WebSockets.AcceptWebSocketAsync();
                using var cts = new CancellationTokenSource();

                async Task SendFrame()
                {
                    if (ws.State != System.Net.WebSockets.WebSocketState.Open) return;

                    var usage = srv.GetUsages();
                    string status = srv.GetStatus();
                    long? startedAt = srv.StartedAt;
                    long uptimeMs = startedAt.HasValue ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startedAt.Value : 0L;

                    double memPercent = usage.MemoryLimit > 0 ? ((double)usage.Memory / usage.MemoryLimit) * 100.0 : 0.0;

                    var payload = new WsUsagePayload(
                        Type: "usage",
                        Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Usage: new WsUsageData(
                            Cpu: usage.Cpu,
                            Memory: usage.Memory,
                            MemoryLimit: usage.MemoryLimit,
                            MemoryPercent: memPercent,
                            NetworkIn: usage.NetworkIn,
                            NetworkOut: usage.NetworkOut,
                            Disk: usage.Disk,
                            StartedAt: startedAt,
                            UptimeMs: uptimeMs,
                            State: status
                        )
                    );

                    string json = JsonSerializer.Serialize(payload, JsonOpts);
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    await ws.SendAsync(new ArraySegment<byte>(bytes), System.Net.WebSockets.WebSocketMessageType.Text, true, cts.Token);
                }

                // 1. Envio inicial
                await SendFrame();

                // 2. Loop de 1 segundo
                try
                {
                    while (ws.State == System.Net.WebSockets.WebSocketState.Open && !cts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(1000, cts.Token);
                        if (Manager.Get(serverId) == null) break;
                        await SendFrame();
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    cts.Cancel();
                }
            });
        }
    }
}