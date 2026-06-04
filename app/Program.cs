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
    {RESET}";

            Console.WriteLine(art);

            try
            {
                using var shutdownCts = new CancellationTokenSource();

                // FIX: O Ctrl+C agora envia o sinal correto para derrubar o SFTP também
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    shutdownCts.Cancel();
                    InternalSftpServer.Stop();
                };

                AppDomain.CurrentDomain.ProcessExit += (_, _) => 
                {
                    shutdownCts.Cancel();
                    InternalSftpServer.Stop();
                };

                using var loggerFactory = LoggerFactory.Create(builder =>
                {
                    _ = builder.AddPlumeLogger();
                    _ = builder.SetMinimumLevel(LogLevel.Information);
                    _ = builder.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
                    _ = builder.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
                });

                var defaultLogger = loggerFactory.CreateLogger("Program");
                var httpLogger = loggerFactory.CreateLogger<HttpServer>();

                ConfigManager.LoadConfig(defaultLogger);

                var dbPath = Path.Combine(ConfigManager.GlobalConfig.App.Path, "db.json");
                DatabaseManager.InitDB(dbPath);

                Manager.InitAsync().GetAwaiter().GetResult();
                var sftpLogger = loggerFactory.CreateLogger("Plume.SFTP");
                
                InternalSftpServer.Start(sftpLogger);

                var http = new HttpServer(httpLogger);
                http.StartAsync(shutdownCts.Token).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                CrashHandler.HandleException(e, "Main");
            }
        }
    }
}