package Plume.Server

import Plume.Configuration.ConfigManager
import Plume.Database.DatabaseManager
import Plume.Database.ServerModel
import Plume.Docker.DockerClientFactory
import Plume.Logger
import Plume.Server.Extending.delete
import Plume.Server.Extending.startUsageMonitor
import Plume.Server.Extending.waitContainerExitAsync
import java.nio.file.Path
import java.util.concurrent.ConcurrentHashMap

object Manager {

    // Fonte da verdade dos servidores
    val servers = ConcurrentHashMap<String, Server>()
    var whenStarted: Long = 0
    // OTIMIZAÇÃO: Cache de Short ID para o getByShortId ser instantâneo (O(1)) sem dar .find() no map inteiro
    private val shortIdCache = ConcurrentHashMap<String, Server>()

    val docker = DockerClientFactory.fromEnv()

    fun init() {
        val basePath = Path.of(ConfigManager.globalConfig.app.path)
        val serverModels = DatabaseManager.db.getAllServers()

        // Map para lookup rápido do DB
        val modelMap = serverModels.associateBy { it.serverId }

        // Cria servers primeiro e popula os caches
        for (model in serverModels) {
            val server = Server(
                id = model.serverId,
                needsInstall = model.installed == 0,
                docker = docker,
                basePath = basePath,
            )
            servers[model.serverId] = server

            // Registra os primeiros 8 caracteres (ou o tamanho padrão do seu shortId) no cache
            if (model.serverId.length >= 8) {
                shortIdCache[model.serverId.substring(0, 8)] = server
            }
        }

        // Lista containers do Docker (UMA vez só)
        val containers = docker.listContainersCmd()
            .withShowAll(true)
            .exec()

        // Transforma em map: nome -> container de forma otimizada
        val containerMap = containers
            .flatMap { c ->
                c.names.map { name ->
                    name.removePrefix("/") to c
                }
            }
            .toMap()

        // Iterar pelo DB de forma performática
        for ((id, srv) in servers) {
            srv.startUsageMonitor()
            val container = containerMap[id] ?: continue
            val model = modelMap[id]
            srv.containerId = container.id

            if (model != null) {
                srv.diskLimitMb = model.disk
            }

            if (container.state == "running") {
                if (container.created > 0) {
                    srv.startedAt = container.created * 1000
                }
                srv.status = "running"
                srv.waitContainerExitAsync()
            }
        }
        whenStarted = System.currentTimeMillis()
    }

    fun create(id: String) {
        try {
            DatabaseManager.db.saveServer(ServerModel(
                serverId = id,
                installed = 0,
                maintainable = false,
                allowRoot = false,
                disk = 0L
            ))

            val server = Server(
                id = id,
                needsInstall = true,
                docker = docker, // OTIMIZAÇÃO: Usa a mesma instância global do docker em vez de criar uma nova do zero
                basePath = Path.of(ConfigManager.globalConfig.app.path, "servers")
            )

            servers[id] = server
            if (id.length >= 8) {
                shortIdCache[id.substring(0, 8)] = server
            }

        } catch (e: Exception) {
            Logger.logger.error("Failed to save server $id to database: ${e.message}")
        }
    }

    fun get(id: String): Server? = servers[id]

    // OTIMIZAÇÃO MAXIMA: Busca direta por chave no cache em vez de varrer a lista com .find {}
    fun getByShortId(shortId: String): Server? {
        if (shortId.length >= 8) {
            val cached = shortIdCache[shortId.substring(0, 8)]
            if (cached != null) return cached
        }
        // Fallback rápido de segurança caso o shortId enviado venha menor ou diferente
        return servers[shortId] ?: servers.values.find { it.id.startsWith(shortId) }
    }

    fun delete(id: String) {
        val server = servers[id] ?: return
        server.delete()
        servers.remove(id)
        if (id.length >= 8) {
            shortIdCache.remove(id.substring(0, 8))
        }
        DatabaseManager.db.deleteServer(id)
    }
}