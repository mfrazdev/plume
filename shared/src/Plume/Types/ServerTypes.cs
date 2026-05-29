

namespace Plume.Types;


// Equivalente ao '@Serializable data class AllocationData'
public record AllocationData(string Ip, int Port);


// Equivalente ao 'data class StartCore'
public record StartCore {
  public string InstallScript {
    get;
    init;
  } = "";
  public string InstallImage {
    get;
    init;
  } = "";
  public string InstallEntrypoint {
    get;
    init;
  } = "";
  public string StartupCommand {
    get;
    init;
  } = "";
  public string StartupScript {
    get;
    init;
  } = "";
  public string DockerEntrypoint {
    get;
    init;
  } = "";
  public string StopCommand {
    get;
    init;
  } = "";
  // Kotlin 'Any?' mapeia perfeitamente para 'object?' no C#
  public object ? ConfigSystem {
    get;
    init;
  } = null;
  public object ? StartupParser {
    get;
    init;
  } = null;
  // Mantida a grafia original do Kotlin (rootAcess)
  public long ? RootAcess {
    get;
    init;
  } = null;
  public long ? Maintainable {
    get;
    init;
  } = null;
}
// Equivalente ao 'data class StartData'
public record StartData {
  // Como no Kotlin era 'var', em C# permitimos escrita com 'get; set;'
  public string Image {
    get;
    set;
  } = "";
  public int Memory {
    get;
    init;
  }
  public int Cpu {
    get;
    init;
  }
  public object ? Environment {
    get;
    init;
  } = null;
  public AllocationData ? PrimaryAllocation {
    get;
    init;
  } = null;
  public List < AllocationData > AdditionalAllocation {
    get;
    init;
  } = new List < AllocationData > ();
  public long Disk {
    get;
    init;
  } = 0;
  public StartCore Core {
    get;
    init;
  } = null!;
}
// Equivalente ao 'data class UsageMetrics'
public record UsageMetrics {
  public double Cpu {
    get;
    init;
  } = 0.0;
  public long Memory {
    get;
    init;
  } = 0;
  public long MemoryLimit {
    get;
    init;
  } = 0;
  public long Disk {
    get;
    init;
  } = 0;
  public long NetworkIn {
    get;
    init;
  } = 0;
  public long NetworkOut {
    get;
    init;
  } = 0;
}