using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Plume.Configuration;
using Plume.Database;
using Plume.Http;
using Plume.Server;
using Plume.Server.Extending;
using Plume.Types;

namespace Plume.Http.Routes.Server;

public static class ActionsRoute
{
    // Estrutura de DTOs para o payload (ActionPayload e ActionPayloadCore)
    public record ActionPayloadCore(
        string InstallScript = "",
        string InstallImage = "",
        string InstallEntrypoint = "",
        string StartupCommand = "",
        string StartupScript = "",
        string DockerEntrypoint = "",
        string StopCommand = "",
        JsonElement? ConfigSystem = null,
        JsonElement? StartupParser = null,
        long? RootAcess = null,
        long? Maintainable = null
    );

    public record ActionPayload(
        string Token = "",
        string ServerId = "",
        string UserUuid = "",
        string Action = "",
        string Command = "",
        int Memory = 0,
        int Cpu = 0,
        long Disk = 0,
        string Image = "",
        JsonElement? Environment = null,
        AllocationData? PrimaryAllocation = null,
        List<AllocationData>? AdditionalAllocation = null,
        ActionPayloadCore? Core = null
    );

    public static void Register(RouteGroupBuilder group)
    {
        group.MapPost("/servers/action", async (ActionPayload body, HttpContext context) =>
        {
            var result = await HandleAction(body);
            await result.ExecuteAsync(context);
        });
    }

    private static async Task<IResult> HandleAction(ActionPayload body)
    {
        // Validações Básicas
        if (string.IsNullOrEmpty(body.ServerId) || !ConfigManager.ValidateServerID(body.ServerId))
            return Reply.Json(new { error = "Invalid serverId" }, statusCode: 400);

        if (string.IsNullOrEmpty(body.UserUuid) || !ConfigManager.ValidateUUID(body.UserUuid))
            return Reply.Json(new { error = "Invalid userUuid" }, statusCode: 400);

        if (body.Token != ConfigManager.GlobalConfig.Remote.Token)
            return Reply.Json(new { error = "Invalid token" }, statusCode: 403);

        var srv = Manager.Get(body.ServerId);
        if (srv == null)
            return Reply.Json(new { error = "Server not found" }, statusCode: 404);

        var currentStatus = srv.GetStatus();
        if (currentStatus.Equals("installing", StringComparison.OrdinalIgnoreCase))
        {
            srv.EmitLive("error", "O servidor está atualmente em instalação. Tente novamente mais tarde.");
            return Reply.Json(new { error = "server is installing" }, statusCode: 400);
        }

        var serverDb = DatabaseManager.Db.GetServer(body.ServerId);
        if (serverDb == null)
            return Reply.Json(new { error = "Server not found in database" }, statusCode: 404);

        // Sincroniza limite de disco no DB e memória
        if (serverDb.Disk != body.Disk)
        {
            DatabaseManager.Db.SaveServer(serverDb with { Disk = body.Disk });
            srv.DiskLimitMb = body.Disk;
        }

        // Validação de limite de disco
        bool isStopOrKill = body.Action == "kill" || body.Action == "stop";
        if (!isStopOrKill && body.Disk != 0L)
        {
            long diskUsageMB = srv.GetUsages().Disk / 1048576L;
            if (diskUsageMB > body.Disk)
            {
                srv.EmitLive("error", "O uso de disco do servidor excede o limite definido.");
                return Reply.Json(new { error = "Disk usage exceeds the limit" }, statusCode: 400);
            }
        }

        // Processamento de Ações
        switch (body.Action.ToLower())
        {
            case "start":
            case "restart":
            case "install":
                if (body.Core == null || string.IsNullOrEmpty(body.Image) || body.PrimaryAllocation == null ||
                    body.Environment == null)
                    return Reply.Json(new { error = "Missing required fields" }, statusCode: 400);

                var startData = new StartData
                {
                    Image = body.Image,
                    Memory = body.Memory,
                    Cpu = body.Cpu,
                    Environment = body.Environment,
                    PrimaryAllocation = body.PrimaryAllocation,
                    AdditionalAllocation = body.AdditionalAllocation ?? new List<AllocationData>(),
                    Disk = body.Disk,
                    Core = new StartCore
                    {
                        InstallScript = body.Core.InstallScript,
                        InstallImage = body.Core.InstallImage,
                        InstallEntrypoint = body.Core.InstallEntrypoint,
                        StartupCommand = body.Core.StartupCommand,
                        StartupScript = body.Core.StartupScript,
                        DockerEntrypoint = body.Core.DockerEntrypoint,
                        StopCommand = body.Core.StopCommand,
                        ConfigSystem = body.Core.ConfigSystem,
                        StartupParser = body.Core.StartupParser,
                        RootAcess = body.Core.RootAcess,
                        Maintainable = body.Core.Maintainable
                    }
                };

                if (body.Action.Equals("start", StringComparison.OrdinalIgnoreCase))
                {
                    if (!currentStatus.Equals("stopped", StringComparison.OrdinalIgnoreCase))
                        srv.EmitLive("error", "Servidor já em operação.");
                    else
                        _ = Task.Run(() => srv.StartAsync(startData));
                }
                else if (body.Action.Equals("restart", StringComparison.OrdinalIgnoreCase))
                {
                    if (!srv.IsRestarting)
                    {
                        srv.IsRestarting = true;
                        _ = Task.Run(async () =>
                        {
                            await srv.KillAsync();
                            await Task.Delay(1000);
                            await srv.StartAsync(startData);
                        });
                    }
                }
                else // install
                {
                    if (!currentStatus.Equals("stopped", StringComparison.OrdinalIgnoreCase))
                        return Reply.Json(new { error = "server not stopped" }, statusCode: 400);

                    _ = Task.Run(() => srv.InstallAsync(startData, false, false));
                }

                break;

            case "stop":
                if (!srv.IsStopping)
                {
                    srv.IsStopping = true;
                    srv.EmitLive("status", "Servidor marcado como desligando...");
                    if (body.Command == "^C" || body.Command == "^K")
                        _ = Task.Run(() => srv.KillAsync());
                    else
                        _ = Task.Run(() => srv.SendCommandAsync(body.Command));
                }

                break;

            case "kill":
                _ = Task.Run(() => srv.KillAsync());
                break;

            case "command":
                _ = Task.Run(() => srv.SendCommandAsync(body.Command));
                break;

            default:
                return Reply.Json(new { error = "Invalid action" }, statusCode: 400);
        }

        return Reply.Json(new { status = "success" });
    }
}