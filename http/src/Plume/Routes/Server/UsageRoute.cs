using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Plume.Configuration;
using Plume.Http;
using Plume.Server;

namespace Plume.Http.Routes.Server
{
    public static class UsagesRoute
    {
        // DTO para mapear o JSON recebido no body
        public record UsagesPayload(string ServerId = "");

        public static void Register(RouteGroupBuilder group)
        {
            group.MapPost("/servers/usage", (UsagesPayload body) =>
            {
                // Verifica se o serverId veio nulo ou vazio
                if (string.IsNullOrEmpty(body.ServerId))
                    return Reply.Json(new { error = "Missing required field: serverId" }, statusCode: 400);

                // Validação do Server ID
                if (!ConfigManager.ValidateServerID(body.ServerId))
                    return Reply.Json(new { error = "Invalid serverId" }, statusCode: 400);

                // Verifica se o servidor existe no Manager
                var server = Manager.Get(body.ServerId);
                if (server == null)
                    return Reply.Json(new { error = "Server not found" }, statusCode: 404);

                // Retorna o JSON de sucesso com os dados de uso
                return Reply.Json(new
                {
                    status = "success",
                    usage = new
                    {
                        cpu = server.UsageCache.Cpu,
                        memory = server.UsageCache.Memory,
                        memoryLimit = server.UsageCache.MemoryLimit,
                        disk = server.UsageCache.Disk,
                        state = server.GetStatus()
                    }
                });
            });
        }
    }
}