using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Plume.Server;


namespace Plume.Http.Routes
{
    public static class StatusRoute
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Used by JSON serialization")]
        private record StatusResponse(
            string Status,
            string Ram,
            string Os,
            string Cpu,
            string Version,
            bool HasUpdate,
            string LatestVersion,
            string Uptime
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
            // Calcula o uso de CPU do processo atual
            using var proc = Process.GetCurrentProcess();
            return (proc.TotalProcessorTime.TotalMilliseconds / 1000.0) * 100.0;
        }

        public static void Register(RouteGroupBuilder group)
        {
            group.MapPost("/status", () =>
            {
                var currentVersion = "1.0.0"; 
                var updateAvailable = false;  
                var latestVersion = "1.0.0";  

                long uptimeMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Manager.WhenStarted;

                // Uso de memória RAM do processo atual (WorkingSet64 retorna bytes)
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

                return Results.Ok(responseData);
            });
        }
    }
}