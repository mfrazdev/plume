using Docker.DotNet;
using Docker.DotNet.Models;

// ReSharper disable once CheckNamespace
namespace Plume.Server.Extending;

public static class ServerMonitor {

  extension(Server server)
  {
    public void StartUsageMonitor() {
      server.Lock.EnterWriteLock();
      try {
        if (server.Monitoring) return;
        server.Monitoring = true;
      } finally {
        server.Lock.ExitWriteLock();
      }

      server.StatsJob = Task.Run(async () => {
        while (server.Monitoring) {
          await server.UpdateStatsAsync();
          await Task.Delay(1000);
        }
      });

      server.DiskJob = Task.Run(async () => {
        string sp = server.ServerPath();
        await server.UpdateDiskAsync(sp);

        while (server.Monitoring) {
          await Task.Delay(5000);
          await server.UpdateDiskAsync(sp);
        }
      });
    }

    public void DestroyMonitor() {
      server.Lock.EnterWriteLock();
      try {
        server.Monitoring = false;
        server.StatsJob = null;
        server.DiskJob = null;
      } finally {
        server.Lock.ExitWriteLock();
      }
    }

    private async Task UpdateStatsAsync() {
      string cId;
      server.Lock.EnterReadLock();
      try {
        cId = server.ContainerId;
      } finally {
        server.Lock.ExitReadLock();
      }

      if (string.IsNullOrWhiteSpace(cId)) return;

      try {
        var inspect = await server.Docker.Containers.InspectContainerAsync(cId);
        if (inspect.State?.Running != true) {
          server.ClearUsageStats();
          return;
        }

        ContainerStatsResponse? st = null;
        var tcs = new TaskCompletionSource<bool>();

        // A MÁGICA: Usando nosso Progress Síncrono para evitar a "condição de corrida"
        // que fazia a variável ler nulo e jogar a RAM pro zero do nada.
        var progress = new SyncProgress<ContainerStatsResponse>(response => {
          st = response;
          tcs.TrySetResult(true);
        });

        using var cts = new CancellationTokenSource();

        await server.Docker.Containers.GetContainerStatsAsync(
          cId,
          new ContainerStatsParameters { Stream = false },
          progress,
          cts.Token
        );

        // Aguarda o resultado da API OU cancela se demorar mais de 5 segundos
        var timeoutTask = Task.Delay(5000, cts.Token);
        var completed = await Task.WhenAny(tcs.Task, timeoutTask);

        if (completed == timeoutTask) {
          cts.Cancel(); // Estourou o tempo
          server.ClearUsageStats();
          return;
        }

        if (st == null || st.MemoryStats == null) {
          server.ClearUsageStats();
          return;
        }

        // Cálculo real de RAM (Desconta o cache de I/O do disco - igual o 'docker stats' faz)
        ulong memUsage = st.MemoryStats.Usage;
        if (st.MemoryStats.Stats != null) {
          if (st.MemoryStats.Stats.ContainsKey("cache")) {
            memUsage -= st.MemoryStats.Stats["cache"]; // Cgroups v1
          } else if (st.MemoryStats.Stats.ContainsKey("inactive_file")) {
            memUsage -= st.MemoryStats.Stats["inactive_file"]; // Cgroups v2
          }
        }

        // Proteção para não ficar negativo caso o cache reportado seja estranho
        if (memUsage > st.MemoryStats.Usage) memUsage = st.MemoryStats.Usage; 

        ulong memLimit = st.MemoryStats.Limit;

        ulong cpuDelta = (st.CPUStats?.CPUUsage?.TotalUsage ?? 0L) - (st.PreCPUStats?.CPUUsage?.TotalUsage ?? 0L);
        ulong systemDelta = (st.CPUStats?.SystemUsage ?? 0L) - (st.PreCPUStats?.SystemUsage ?? 0L);

        double numCpus = st.CPUStats?.OnlineCPUs ?? 1.0;
        if (numCpus <= 0.0) numCpus = 1.0;

        double cpuPercent = 0.0;
        if (systemDelta > 0 && cpuDelta > 0) {
          cpuPercent = ((double) cpuDelta / systemDelta) * numCpus * 100.0;
        }

        ulong netIn = 0L;
        ulong netOut = 0L;

        if (st.Networks != null) {
          foreach(var net in st.Networks.Values) {
            netIn += net.RxBytes;
            netOut += net.TxBytes;
          }
        }

        long ToLong(ulong value) => value > long.MaxValue ? long.MaxValue : (long) value;

        server.Lock.EnterWriteLock();
        try {
          server.UsageCache = server.UsageCache with {
            Cpu = cpuPercent,
            Memory = ToLong(memUsage),
            MemoryLimit = ToLong(memLimit),
            NetworkIn = ToLong(netIn),
            NetworkOut = ToLong(netOut)
          };
        } finally {
          server.Lock.ExitWriteLock();
        }
      } catch (DockerContainerNotFoundException) {
        server.ClearUsageStats();
      } catch (Exception) {
        server.ClearUsageStats();
      }
    }

    internal void ClearUsageStats() {
      server.Lock.EnterWriteLock();
      try {
        server.UsageCache = server.UsageCache with {
          Cpu = 0.0,
          Memory = 0,
          NetworkIn = 0,
          NetworkOut = 0
        };
      } finally {
        server.Lock.ExitWriteLock();
      }
    }

    internal async Task UpdateDiskAsync(string sp) {
      long sizeBytes = ServerUtils.DirectorySize(sp);
      bool shouldKill = false;

      server.Lock.EnterWriteLock();
      try {
        server.UsageCache = server.UsageCache with {
          Disk = sizeBytes
        };

        double mb = sizeBytes / (1024.0 * 1024.0);

        if (server.DiskLimitMb != 0L && mb > server.DiskLimitMb &&
            (server.Status == "running" || server.Status == "initializing")) {
          shouldKill = true;
        }
      } finally {
        server.Lock.ExitWriteLock();
      }

      if (shouldKill) {
        await server.KillAsync();
        server.EmitLive("error", "O servidor foi desligado porque o uso de disco ultrapassou o limite.");
      }
    }
  }
}

// === HELPER MÁGICO ===
// Essa classe força a função de Callback rodar na MESMA thread, instantaneamente.
internal class SyncProgress<T> : IProgress<T> {
    private readonly Action<T> _handler;
    public SyncProgress(Action<T> handler) => _handler = handler;
    public void Report(T value) => _handler(value);
}