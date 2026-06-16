using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis; // Adicionado para o DynamicDependency
using Docker.DotNet;
using Docker.DotNet.Models;
using Plume.Configuration;
using Plume.Database;
using Plume.Docker;
using Plume.Server.Extending; // Assumindo que os métodos de extensão ficarão aqui

namespace Plume.Server;

public static class Manager {
  // Fonte da verdade dos servidores (Equivalente ao ConcurrentHashMap)
  public static ConcurrentDictionary < string, Server > Servers {
    get;
  } = new ConcurrentDictionary < string, Server > ();
  public static long WhenStarted {
    get;
    private set;
  } = 0;

  // OTIMIZAÇÃO: Cache de Short ID
  private static readonly ConcurrentDictionary < string, Server > ShortIdCache = new();

  public static IDockerClient Docker {
    get;
  } = DockerClientFactory.FromEnv();

  // Protege os modelos do Docker.DotNet contra o Trimming do NativeAOT
  [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ContainersListParameters))]
  [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ContainerListResponse))]
  // Protege os conversores internos que o Docker.DotNet usa via Reflexão (Reflection) para montar a URL
  [DynamicDependency(DynamicallyAccessedMemberTypes.All, "Docker.DotNet.BoolQueryStringConverter", "Docker.DotNet")]
  [DynamicDependency(DynamicallyAccessedMemberTypes.All, "Docker.DotNet.TimeSpanSecondsQueryStringConverter", "Docker.DotNet")]
  [DynamicDependency(DynamicallyAccessedMemberTypes.All, "Docker.DotNet.TimeSpanQueryStringConverter", "Docker.DotNet")]
  public static async Task InitAsync() {
    string basePath = ConfigManager.GlobalConfig.App.Path;
    
    var serverModels = DatabaseManager.Db.GetAllServers();

    // Map para lookup rápido do DB
    var modelMap = serverModels.ToDictionary(m => m.ServerId);
    
    // Cria servers primeiro e popula os caches
    foreach(var model in serverModels) {
      var server = new Server(
        id: model.ServerId,
        needsInstall: model.Installed == 0,
        docker: Docker,
        basePath: basePath
      );

      Servers[model.ServerId] = server;

      if (model.ServerId.Length >= 8) {
        ShortIdCache[model.ServerId.Substring(0, 8)] = server;
      }
    }
    
    // Lista containers do Docker (UMA vez só)
    var containers = await Docker.Containers.ListContainersAsync(new ContainersListParameters {
      All = true
    });
    
    // Transforma em map: nome -> container de forma otimizada
    var containerMap = new Dictionary < string,
      ContainerListResponse > ();
    foreach(var c in containers) {
      foreach(var name in c.Names) {
        string cleanName = name.StartsWith("/") ? name.Substring(1) : name;
        containerMap[cleanName] = c;
      }
    }
    
    // Iterar pelo DB de forma performática
    foreach(var kvp in Servers) {
      var id = kvp.Key;
      var srv = kvp.Value;
      
      srv.StartUsageMonitor(); // Método de extensão a ser criado em C#

      if (!containerMap.TryGetValue(id, out
          var container)) continue;

      modelMap.TryGetValue(id, out
        var model);
      srv.ContainerId = container.ID;

      if (model != null) {
        srv.DiskLimitMb = model.Disk;
      }

      if (container.State == "running") {
        // Docker.DotNet retorna 'Created' como um struct DateTime.
        // Convertendo para o formato UNIX que você usava (System.currentTimeMillis)
        if (container.Created !=
          default) {
          srv.StartedAt = new DateTimeOffset(container.Created).ToUnixTimeMilliseconds();
        }
        srv.Status = "running";
        srv.WaitContainerExitAsync(); // Método de extensão
      }
    }
    
    WhenStarted = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
  }

  public static void Create(string id) {
    try {
      var serverModel = new ServerModel(
        Id: 0, // Como otimizamos a classe DatabaseManager, Id=0 forçará a auto-incrementar
        ServerId: id,
        Installed: 0,
        Maintainable: false,
        AllowRoot: false,
        Disk: 0L
      );

      DatabaseManager.Db.SaveServer(serverModel);

      var server = new Server(
        id: id,
        needsInstall: true,
        docker: Docker, // OTIMIZAÇÃO: Mesma instância global
        basePath: ConfigManager.GlobalConfig.App.Path
      );

      Servers[id] = server;
      if (id.Length >= 8) {
        ShortIdCache[id.Substring(0, 8)] = server;
      }

      // ==== FIX: O monitor não estava sendo iniciado para servidores recém criados! ====
      server.StartUsageMonitor();

    } catch (Exception e) {
      // TODO: Chamar o Logger padrão do C# que você está utilizando.
      Console.WriteLine($"Failed to save server {id} to database: {e.Message}");
    }
  }

  public static Server ? Get(string id) {
    Servers.TryGetValue(id, out
      var server);
    return server;
  }

  // OTIMIZAÇÃO MÁXIMA
  public static Server ? GetByShortId(string shortId) {
    if (shortId.Length >= 8) {
      if (ShortIdCache.TryGetValue(shortId.Substring(0, 8), out
          var cached)) {
        return cached;
      }
    }

    // Fallback rápido
    if (Servers.TryGetValue(shortId, out
        var directMatch)) {
      return directMatch;
    }

    return Servers.Values.FirstOrDefault(s => s.Id.StartsWith(shortId));
  }

  public static void Delete(string id) {
    if (!Servers.TryGetValue(id, out
        var server)) return;

    server.DeleteAsync().GetAwaiter().GetResult();

    Servers.TryRemove(id, out _);

    if (id.Length >= 8) {
      ShortIdCache.TryRemove(id.Substring(0, 8), out _);
    }

    DatabaseManager.Db.DeleteServer(id);
  }
}