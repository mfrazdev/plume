package Plume.Database

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.io.File
import java.util.concurrent.locks.ReentrantReadWriteLock
import kotlin.concurrent.read
import kotlin.concurrent.write

@Serializable
data class ServerModel(
    val id: Int = 0,
    val serverId: String,
    val installed: Int,
    val maintainable: Boolean,
    val allowRoot: Boolean,
    val disk: Long
)

@Serializable
data class DBData(
    var servers: MutableMap<String, ServerModel> = mutableMapOf()
)

class JsonDB(private val path: String) {

    private val lock = ReentrantReadWriteLock()

    // Otimização: Usando builders padrão para evitar overhead de reflexão
    private val jsonConfig = Json {
        prettyPrint = true
        ignoreUnknownKeys = true
    }

    var data: DBData = DBData()
        private set

    init {
        load()
    }

    // Otimizado: Parse pesado é feito FORA do lock de escrita.
    private fun load() {
        val file = File(path)
        if (!file.exists()) {
            file.parentFile?.mkdirs()
            // Evita concorrência na criação inicial
            lock.write { saveLocked() }
            return
        }

        // Tenta ler e parsear o JSON de forma concorrente sem bloquear leituras/escritas desnecessariamente
        val loadedData = try {
            val content = file.readText() // I/O rápido
            jsonConfig.decodeFromString<DBData>(content) // Parse CPU-bound fora do lock
        } catch (e: Exception) {
            DBData()
        }

        // Só aplica o Lock no momento de injetar o dado na memória (operação atômica/instantânea)
        lock.write {
            data = loadedData
        }
    }

    fun save() {
        // Corrigido: Para salvar no disco, usamos lock de leitura para extrair a String
        // e liberamos o lock antes de escrever no arquivo (I/O não bloqueia o banco)
        val jsonString = lock.read {
            jsonConfig.encodeToString(data)
        }

        try {
            File(path).writeText(jsonString)
        } catch (e: Exception) {
            // Tratamento de erro de IO
        }
    }

    // Mantido para compatibilidade interna, mas otimizado para não segurar o lock se puder
    private fun saveLocked() {
        val jsonString = jsonConfig.encodeToString(data)
        try {
            File(path).writeText(jsonString)
        } catch (e: Exception) {
            // Tratamento de erro de IO
        }
    }

    fun getServer(serverId: String): ServerModel? {
        lock.read {
            return data.servers[serverId]
        }
    }

    fun saveServer(server: ServerModel) {
        lock.write {
            var updatedServer = server

            // Otimização: Usar size + 1 pode gerar IDs duplicados se itens forem deletados.
            // O ideal para performance e consistência rápida é pegar o maior ID atual + 1.
            if (updatedServer.id == 0) {
                val maxId = data.servers.values.maxOfOrNull { it.id } ?: 0
                updatedServer = updatedServer.copy(id = maxId + 1)
            }

            data.servers[updatedServer.serverId] = updatedServer
            saveLocked()
        }
    }

    fun deleteServer(serverId: String) {
        lock.write {
            if (data.servers.remove(serverId) != null) {
                saveLocked()
            }
        }
    }

    fun getAllServers(): List<ServerModel> {
        lock.read {
            return data.servers.values.toList()
        }
    }
}

object DatabaseManager {
    lateinit var db: JsonDB
        private set

    fun initDB(dbPath: String) {
        db = JsonDB(dbPath)
    }
}