using Docker.DotNet;
using Plume.Events;
using Plume.Types;

namespace Plume.Server;

public class Server {
  public string Id {
    get;
  }

  internal IDockerClient Docker {
    get;
  }
  internal string BasePath {
    get;
  }

  // Equivalente ao ReentrantReadWriteLock do Kotlin
  internal ReaderWriterLockSlim Lock {
    get;
  } = new ReaderWriterLockSlim();

  internal string ContainerId {
    get;
    set;
  } = "";
  internal string Status {
    get;
    set;
  } = "stopped"; // stopped, initializing, installing, running

  public bool IsRestarting {
    get;
    set;
  }
  public bool IsStopping {
    get;
    set;
  }

  public long ? StartedAt {
    get;
    internal set;
  }
  public long DiskLimitMb {
    get;
    set;
  }

  internal bool NeedsInstallFlag {
    get;
    set;
  }

  public UsageMetrics UsageCache { get; internal set; } = new();

  // Implementação equivalente ao AtomicBoolean(false) do Kotlin usando Interlocked
  private int _monitoring;
  internal bool Monitoring {
    get => Interlocked.CompareExchange(ref _monitoring, 0, 0) == 1;
    set => Interlocked.Exchange(ref _monitoring, value ? 1 : 0);
  }

  // Equivalente aos "Job?" do Kotlin usando a TPL do C#
  internal Task ? StatsJob {
    get;
    set;
  }
  internal Task ? DiskJob {
    get;
    set;
  }

  public LiveBus Live {
    get;
  } = new LiveBus();

  public Server(string id, bool needsInstall, IDockerClient docker, string basePath) {
    Id = id;
    NeedsInstallFlag = needsInstall;
    Docker = docker;
    BasePath = basePath;
  }

  public void EmitLive(string category, string message) => Live.Emit(category, message);

  public string GetStatus() {
    Lock.EnterReadLock();
    try {
      return IsStopping ? "stopping" : Status;
    } finally {
      Lock.ExitReadLock();
    }
  }

  public UsageMetrics GetUsages() {
    Lock.EnterReadLock();
    try {
      return UsageCache;
    } finally {
      Lock.ExitReadLock();
    }
  }

  public void SetContainerId(string cid) {
    Lock.EnterWriteLock();
    try {
      ContainerId = cid;
    } finally {
      Lock.ExitWriteLock();
    }
  }

  public void SetStatus(string s) {
    Lock.EnterWriteLock();
    try {
      Status = s;
    } finally {
      Lock.ExitWriteLock();
    }
  }

  internal string ServerPath() {
    // Usando Path.Combine e garantindo que os diretórios existam (equivalente a basePath.resolve)
    string path = Path.Combine(BasePath, "servers", Id);
    Directory.CreateDirectory(path);
    return path;
  }
}
