using Microsoft.Extensions.Logging;
using Plume.Configuration;
using Plume.Database;
using Plume.Http;
using Plume.Server;
using Plume.Logging;
using Plume.SFTP;

namespace app
{
    public class Program
    {
        public static void Main(string[] args)
        {
            // Instalando o manipulador de crashes global
            CrashHandler.Install();

            const string RESET = "\x1b[0m";
            const string CYAN = "\x1b[36m";
            const string WHITE = "\x1b[37m";

            var art = $@"{CYAN}
   ___  __   __  ____  _______
  / _ \/ /  / / / /  |/  / __/
 / ___/ /__/ /_/ / /|_/ / _/  
/_/  /____/\____/_/  /_/___/  
    {WHITE}
Copyright © 2026 - MurilloFz

Este software é disponibilizado sob os termos da Licença MIT.
A notificação de direitos autorais acima, bem como este aviso de permissão, devem ser
incluídos em todas as cópias ou partes substanciais deste software.
    {RESET}";

            Console.WriteLine(art);

            try
            {
                // Agora o LoggerFactory usa a NOSSA classe (PlumeLogger) em vez do padrão feio do C#
                using var loggerFactory = LoggerFactory.Create(builder =>
                {
                    _ = builder.AddPlumeLogger();
                    _ = builder.SetMinimumLevel(LogLevel.Information);

                    // Oculta o spam de logs de requisição HTTP (GET, POST) nativos do ASP.NET Core
                    _ = builder.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
                    _ = builder.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
                });

                var defaultLogger = loggerFactory.CreateLogger("PlumeCore");
                var httpLogger = loggerFactory.CreateLogger<HttpServer>();

                // Carrega a configuração (ConfigManager foi ajustado para receber ILogger)
                ConfigManager.LoadConfig(defaultLogger);

                // Inicializa o banco de dados via JSON
                var dbPath = Path.Combine(ConfigManager.GlobalConfig.App.Path, "db.json");
                DatabaseManager.InitDB(dbPath);

                // Chama a inicialização do Manager e aguarda a execução síncrona
                Manager.InitAsync().GetAwaiter().GetResult();
                var sftpLogger = loggerFactory.CreateLogger("Plume.SFTP");
                // Substitua/descomente quando migrar o SFTP Server para C#
                InternalSftpServer.Start(sftpLogger);

                // Inicializa e sobe o Kestrel (HttpServer)
                var http = new HttpServer(httpLogger);
                http.Start();
            }
            catch (Exception e)
            {
                // Cai direto no CrashHandler agora, fica perfeitamente formatado
                CrashHandler.HandleException(e, "Main");
            }
        }
    }
}
