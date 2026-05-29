using System.Collections.Concurrent;
using System.Text;
using Plume.Configuration;

namespace Plume.Http.Routes.Websocket
{
    public static class WsCommon
    {
        // --- CACHE DE PERMISSÃO ---
        private static readonly ConcurrentDictionary<string, long> PermCache = new();
        private const long TtlMs = 5 * 60 * 1000L; // 5 minutos de cache

        // Notar o alias explícito caso haja conflito de namespace.
        // Assumindo que a classe 'Server' está no namespace 'Plume.Server'
        public static bool CheckPermissionCached(string userUuid, Plume.Server.Server srv)
        {
            string cacheKey = $"{userUuid}:{srv.Id}";
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Verifica se tá no cache e não expirou
            if (PermCache.TryGetValue(cacheKey, out long expiry) && now < expiry)
            {
                return true;
            }

            // A chamada para a ConfigManager migrada anteriormente
            bool hasPerm = ConfigManager.HasPermission(userUuid, srv.Id);
            if (hasPerm)
            {
                PermCache[cacheKey] = now + TtlMs;
            }

            return hasPerm;
        }

        // --- CORES E GRADIENTE DO CONSOLE ---
        public const string PrefixLabel = "container@plume ❯";
        public const string PrefixLabel2 = "[Plume Daemon]  ❯"; // Espaços adicionados para alinhar perfeitamente com o de cima
        public const string ResetColor = "\x1b[0m"; // \x1b é o equivalente a \u001B no C#
        private const string Bold = "\x1b[1m";

        public static string Gradient(string text)
        {
            // Tons mais frios e modernos para painéis (Light Blue -> Indigo -> Purple)
            int[] start = { 56, 189, 248 };
            int[] middle = { 129, 140, 248 };
            int[] end = { 192, 132, 252 };

            var result = new StringBuilder();
            int length = text.Length;

            // Aplica negrito no prefixo para dar destaque
            result.Append(Bold);

            for (int i = 0; i < length; i++)
            {
                double t = (double)i / Math.Max(1, length - 1);
                int r, g, b;

                if (t < 0.5)
                {
                    double t2 = t * 2;
                    r = (int)(start[0] * (1 - t2) + middle[0] * t2);
                    g = (int)(start[1] * (1 - t2) + middle[1] * t2);
                    b = (int)(start[2] * (1 - t2) + middle[2] * t2);
                }
                else
                {
                    double t2 = (t - 0.5) * 2;
                    r = (int)(middle[0] * (1 - t2) + end[0] * t2);
                    g = (int)(middle[1] * (1 - t2) + end[1] * t2);
                    b = (int)(middle[2] * (1 - t2) + end[2] * t2);
                }
                result.Append($"\x1b[38;2;{r};{g};{b}m{text[i]}");
            }
            
            return result + ResetColor;
        }

        public static string GetColorForCategory(string category)
        {
            return category switch
            {
                "error" => "\x1b[38;2;255;107;129m",  // Vermelho/Rosa Pastel (destaca o erro, mas na mesma vibe neon)
                "warn" => "\x1b[38;2;253;224;71m",   // Amarelo Pastel Claro
                "info" => "\x1b[38;2;186;230;253m",  // Azul Gelo (combina com o início do seu gradiente)
                "status" => "\x1b[38;2;199;210;254m", // Lavanda Super Claro
                _ => "\x1b[0m"
            };
        }
    }
}