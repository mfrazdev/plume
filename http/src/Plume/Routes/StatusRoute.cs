using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Plume.Server;
using PlumeSFTP.System;

namespace Plume.Http.Routes;

public static class StatusRoute
{
    // 1. O RECORD AGORA É PUBLIC (Obrigatório para o AOT conseguir serializar)
    public record StatusResponse(
        string Status,
        string Ram,
        string Os,
        string Cpu,
        string Version,
        bool HasUpdate,
        string LatestVersion,
        string Uptime
    );

    // Novo record para as respostas do Update, substituindo os objetos anônimos
    public record UpdateResponse(
        string Status, 
        string Message
    );

    private static string FormatTime(long millis)
    {
        var totalMinutes = millis / 1000 / 60;
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;

        return $"{hours}h {minutes:D2}m";
    }

    private static double GetCpuUsage()
    {
        using var proc = Process.GetCurrentProcess();
        return (proc.TotalProcessorTime.TotalMilliseconds / 1000.0) * 100.0;
    }

    public static void Register(RouteGroupBuilder group)
    {
        // Adicionado Task<IResult> explícito para corrigir o erro de inferência de delegate
        group.MapPost("/update", async Task<IResult> () =>
        {
            try
            {
                var update = await VersionManager.CheckForUpdatesAsync();
                
                if (update == null)
                {
                    return TypedResults.BadRequest(new UpdateResponse("error", "Failed to check for updates."));
                }

                if (update.UpdateAvailable)
                {
                    // Aguarda o processo de update terminar. Se der erro, o catch abaixo vai capturar.
                    await VersionManager.UpdateAsync();

                    // Obs: Se o update der certo, o VersionManager.UpdateAsync() chama Environment.Exit(0) e derruba o app.
                    // Então, no cenário de SUCESSO, o cliente vai receber uma "conexão encerrada" (o que é normal em updates de daemon).
                    // Mas no cenário de ERRO, ele receberá a resposta do BadRequest abaixo!
                    return TypedResults.Ok(new UpdateResponse("success", "Update applied. The server is restarting."));
                }
                else
                {
                    return TypedResults.Ok(new UpdateResponse("success", "No updates available."));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Updater] Erro na rota de update: {ex}");
                return TypedResults.BadRequest(new UpdateResponse("error", ex.Message));
            }
        });

        // Adicionado Task<IResult> aqui também por consistência
        group.MapPost("/status", async Task<IResult> () =>
        {
            var versionInfo = await PlumeSFTP.System.VersionManager.CheckForUpdatesAsync();
            var currentVersion = versionInfo.CurrentVersion; 
            var updateAvailable = versionInfo.UpdateAvailable;  
            var latestVersion = versionInfo.LatestVersion;  

            long uptimeMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Manager.WhenStarted;

            long usedRamBytes = Process.GetCurrentProcess().WorkingSet64;
            long usedRamMb = usedRamBytes / (1024 * 1024);

            var responseData = new StatusResponse(
                "success",
                $"{usedRamMb} MB",
                RuntimeInformation.OSDescription,
                $"{GetCpuUsage():0.00}%",
                currentVersion,
                updateAvailable,
                latestVersion,
                FormatTime(uptimeMillis)
            );

            // 2. MUDAMOS PARA TypedResults.Ok (Diz pro AOT exatamente qual é o tipo da resposta)
            return TypedResults.Ok(responseData);
        });
    }
}