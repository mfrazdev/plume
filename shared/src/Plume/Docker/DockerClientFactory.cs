using System.Runtime.InteropServices;
using Docker.DotNet;

namespace Plume.Docker;

    public static class DockerClientFactory
    {
        public static IDockerClient FromEnv()
        {
            // Tenta obter a URI do host do Docker a partir das variáveis de ambiente (comportamento "fromEnv")
            string? dockerHostEnv = Environment.GetEnvironmentVariable("DOCKER_HOST");
                
            Uri dockerUri;
            if (!string.IsNullOrEmpty(dockerHostEnv))
            {
                // Se a variável existir, usamos ela
                dockerUri = new Uri(dockerHostEnv);
            }
            else
            {
                // Fallback para o socket padrão dependendo do Sistema Operacional
                bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                dockerUri = isWindows ? new Uri("npipe://./pipe/docker_engine") : new Uri("unix:///var/run/docker.sock");
            }

            // Instancia a configuração
            // - connectionTimeout e maxConnections são abstraídos eficientemente pela pool nativa do .NET
            // - configuramos o responseTimeout para 60 segundos conforme seu código original
            var config = new DockerClientConfiguration(
                endpoint: dockerUri,
                defaultTimeout: TimeSpan.FromSeconds(60)
            );

            // Cria e retorna a interface do cliente Docker
            return config.CreateClient();
        }
    }
