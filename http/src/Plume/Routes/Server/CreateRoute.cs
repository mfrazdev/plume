using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Plume.Configuration;
using Plume.Server;

namespace Plume.Http.Routes.Server
{
    public static class CreateRoute
    {
        // DTO para mapear o JSON recebido no body
        public record CreatePayload(
            string ServerId = "",
            string UserUuid = ""
        );

        public static void Register(RouteGroupBuilder group)
        {
            group.MapPost("/servers/create", (CreatePayload body) =>
            {
                // Verifica se os campos obrigatórios vieram nulos ou vazios
                if (string.IsNullOrEmpty(body.ServerId) || string.IsNullOrEmpty(body.UserUuid))
                    return Results.Json(new { error = "Missing required fields: serverId and userUuid" }, statusCode: 400);

                // Validação do Server ID
                if (!ConfigManager.ValidateServerID(body.ServerId))
                    return Results.Json(new { error = "Invalid serverId" }, statusCode: 400);

                // Validação do UUID do usuário
                if (!ConfigManager.ValidateUUID(body.UserUuid))
                    return Results.Json(new { error = "Invalid userUuid" }, statusCode: 400);

                // Validação de permissões de administrador
                if (!ConfigManager.UserIsAdmin(body.UserUuid))
                    return Results.Json(new { error = "User does not have admin permissions" }, statusCode: 403);

                // Cria o servidor usando o Manager
                Manager.Create(body.ServerId);

                // Retorna o JSON de sucesso
                return Results.Ok(new { status = "success" });
            });
        }
    }
}