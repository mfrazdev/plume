using System.Text.Json;
using System.Text.Json.Serialization; // Adicionado para o AOT

namespace Plume.Database;

// --- ADICIONADO: Contexto específico para o banco de dados no Native AOT ---
[JsonSerializable(typeof(DBData))]
[JsonSerializable(typeof(ServerModel))]
public partial class DatabaseJsonContext : JsonSerializerContext
{
}
// --------------------------------------------------------------------------

// Equivalente ao @Serializable data class ServerModel
// Usamos 'record' para imutabilidade e o recurso 'with' (semelhante ao .copy() do Kotlin)
public record ServerModel(
  int Id,
  string ServerId,
  int Installed,
  bool Maintainable,
  bool AllowRoot,
  long Disk
);

// Estrutura raiz do banco de dados
public class DBData {
  public Dictionary < string, ServerModel > Servers { get; set; } = new();
}

public class JsonDB {
  private readonly string _path;
  private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

  // Otimização: Usando opções padrão para evitar overhead, configurado para camelCase
  private readonly JsonSerializerOptions _jsonConfig = new JsonSerializerOptions {
    WriteIndented = true,
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      PropertyNameCaseInsensitive = true,
      TypeInfoResolver = DatabaseJsonContext.Default // <-- ADICIONADO: Avisa o serializer para usar o código AOT gerado
  };

  public DBData Data {
    get;
    private set;
  } = new DBData();

  public JsonDB(string path) {
    _path = path;
    Load();
  }

  // Otimizado: Parse pesado é feito FORA do lock de escrita.
  private void Load() {
    if (!File.Exists(_path)) {
      var directory = Path.GetDirectoryName(_path);
      if (!string.IsNullOrEmpty(directory)) {
        Directory.CreateDirectory(directory);
      }

      // Evita concorrência na criação inicial
      _lock.EnterWriteLock();
      try {
        SaveLocked();
      } finally {
        _lock.ExitWriteLock();
      }
      return;
    }

    DBData loadedData;
    // Tenta ler e parsear o JSON de forma concorrente sem bloquear leituras/escritas desnecessariamente
    try {
      string content = File.ReadAllText(_path); // I/O rápido
      loadedData = JsonSerializer.Deserialize < DBData > (content, _jsonConfig) ?? new DBData(); // Parse CPU-bound fora do lock
    } catch (Exception) {
      loadedData = new DBData();
    }

    // Só aplica o Lock no momento de injetar o dado na memória (operação atômica/instantânea)
    _lock.EnterWriteLock();
    try {
      Data = loadedData;
    } finally {
      _lock.ExitWriteLock();
    }
  }

  public void Save() {
    string jsonString;
    // Para salvar no disco, usamos lock de leitura para extrair a String
    // e liberamos o lock antes de escrever no arquivo (I/O não bloqueia o banco)
    _lock.EnterReadLock();
    try {
      jsonString = JsonSerializer.Serialize(Data, _jsonConfig);
    } finally {
      _lock.ExitReadLock();
    }

    try {
      File.WriteAllText(_path, jsonString);
    } catch (Exception) {
      // Tratamento de erro de IO
    }
  }

  // Mantido para compatibilidade interna, mas otimizado para não segurar o lock se puder
  private void SaveLocked() {
    string jsonString = JsonSerializer.Serialize(Data, _jsonConfig);
    try {
      File.WriteAllText(_path, jsonString);
    } catch (Exception) {
      // Tratamento de erro de IO
    }
  }

  public ServerModel ? GetServer(string serverId) {
    _lock.EnterReadLock();
    try {
      if (Data.Servers.TryGetValue(serverId, out
          var server)) {
        return server;
      }
      return null;
    } finally {
      _lock.ExitReadLock();
    }
  }

  public void SaveServer(ServerModel server) {
    _lock.EnterWriteLock();
    try {
      var updatedServer = server;

      // Otimização: Pegar o maior ID atual + 1 para evitar duplicados.
      if (updatedServer.Id == 0) {
        int maxId = Data.Servers.Values.Any() ? Data.Servers.Values.Max(s => s.Id) : 0;
        // 'with' no C# substitui o método 'copy()' do Kotlin nos records
        updatedServer = updatedServer with {
          Id = maxId + 1
        };
      }

      Data.Servers[updatedServer.ServerId] = updatedServer;
      SaveLocked();
    } finally {
      _lock.ExitWriteLock();
    }
  }

  public void DeleteServer(string serverId) {
    _lock.EnterWriteLock();
    try {
      if (Data.Servers.Remove(serverId)) {
        SaveLocked();
      }
    } finally {
      _lock.ExitWriteLock();
    }
  }

  public List < ServerModel > GetAllServers() {
    _lock.EnterReadLock();
    try {
      return Data.Servers.Values.ToList();
    } finally {
      _lock.ExitReadLock();
    }
  }
}

public static class DatabaseManager {
  public static JsonDB Db {
    get;
    private set;
  } = null!;

  public static void InitDB(string dbPath) {
    Db = new JsonDB(dbPath);
  }
}