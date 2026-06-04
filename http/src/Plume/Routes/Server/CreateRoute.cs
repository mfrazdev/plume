using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Plume.Configuration;
using Plume.Http;
using Plume.Server;

namespace Plume.Http.Routes.Server
{
    public static class CreateRoute
    {
        // DTO limpo, sem o token, já que o Middleware lida com isso.
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
                    return Reply.Json(new { error = "Missing required fields: serverId and userUuid" }, statusCode: 400);

                // Validação do Server ID
                if (!ConfigManager.ValidateServerID(body.ServerId))
                    return Reply.Json(new { error = "Invalid serverId" }, statusCode: 400);

                // Validação do UUID do usuário
                if (!ConfigManager.ValidateUUID(body.UserUuid))
                    return Reply.Json(new { error = "Invalid userUuid" }, statusCode: 400);

                // Validação de permissões de administrador
                if (!ConfigManager.UserIsAdmin(body.UserUuid))
                    return Reply.Json(new { error = "User does not have admin permissions" }, statusCode: 403);

                // CORREÇÃO: Previne que um servidor existente seja sobrescrito ou corrompido
                if (Manager.Get(body.ServerId) != null)
                    return Reply.Json(new { error = "Server already exists" }, statusCode: 409);

                // Cria o servidor usando o Manager
                Manager.Create(body.ServerId);

                // Retorna o JSON de sucesso
                return Reply.Json(new { status = "success" });
            });
        }
    }
}