package Plume.Http.Routes.Server

import Plume.Configuration.ConfigManager
import Plume.Database.DatabaseManager
import Plume.Http.RouteModule
import Plume.Http.Routes.errorJson
import Plume.Http.Routes.successJson

import Plume.Server.Manager
import Plume.Server.Extending.install
import Plume.Server.Extending.kill
import Plume.Server.Extending.sendCommand
import Plume.Server.Extending.start
import Plume.Types.*
import io.ktor.http.HttpStatusCode
import io.ktor.server.request.receiveNullable
import io.ktor.server.response.respond
import io.ktor.server.routing.Route
import io.ktor.server.routing.post
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonElement
import kotlin.time.Duration.Companion.milliseconds

object ActionsRoute : RouteModule {
    // Pré-alocação de erros comuns para economizar RAM e evitar Garbage Collection sob spam
    private val ERR_INVALID_JSON = errorJson("Invalid JSON")
    private val ERR_INVALID_SERVER = errorJson("Invalid serverId")
    private val ERR_INVALID_UUID = errorJson("Invalid userUuid")
    private val ERR_INVALID_TOKEN = errorJson("Invalid token")
    private val ERR_MISSING_SERVER = errorJson("serverId is required")
    private val ERR_NOT_FOUND = errorJson("Server not found")
    private val ERR_INSTALLING = errorJson("server is installing")
    private val ERR_DB_NOT_FOUND = errorJson("Server not found in database")
    private val ERR_DISK_LIMIT = errorJson("Disk usage exceeds the limit")
    private val ERR_MISSING_FIELDS = errorJson("Missing required fields")
    private val ERR_NOT_STOPPED = errorJson("server not stopped")
    private val ERR_INVALID_ACTION = errorJson("Invalid action")
    private val SUCCESS_RESPONSE = successJson()

    // --- DTOs PARA LER O JSON DO FRONTEND ---
    @Serializable
    data class ActionPayloadCore(
        val installScript: String = "",
        val installImage: String = "",
        val installEntrypoint: String = "",
        val startupCommand: String = "",
        val startupScript: String = "",
        val dockerEntrypoint: String = "",
        val stopCommand: String = "",
        val configSystem: JsonElement? = null,
        val startupParser: JsonElement? = null,
        val rootAcess: Long? = null,
        val maintainable: Long? = null
    )

    @Serializable
    data class ActionPayload(
        val token: String = "",
        val serverId: String = "",
        val userUuid: String = "",
        val action: String = "",
        val command: String = "",

        val memory: Int = 0,
        val cpu: Int = 0,
        val disk: Long = 0,
        val image: String = "",
        val environment: JsonElement? = null,
        val primaryAllocation: AllocationData? = null,
        val additionalAllocation: List<AllocationData> = emptyList(),

        val core: ActionPayloadCore = ActionPayloadCore()
    )

    private val serverActionScope = CoroutineScope(Dispatchers.IO)

    override fun register(route: Route) {
        route.post("/servers/action") {
            // Tenta parsear o body
            val body = try {
                call.receiveNullable<ActionPayload>()
            } catch (_: Exception) {
                null
            }

            if (body == null) {
                call.respond(HttpStatusCode.BadRequest, ERR_INVALID_JSON)
                return@post
            }

            // Validações de Entrada Rigorosas
            if (!ConfigManager.validateServerID(body.serverId)) {
                call.respond(HttpStatusCode.BadRequest, ERR_INVALID_SERVER)
                return@post
            }

            if (!ConfigManager.validateUUID(body.userUuid)) {
                call.respond(HttpStatusCode.BadRequest, ERR_INVALID_UUID)
                return@post
            }


            if (body.token != ConfigManager.globalConfig.remote.token) {
                call.respond(HttpStatusCode.Forbidden, ERR_INVALID_TOKEN)
                return@post
            }

            if (body.serverId.isEmpty()) {
                call.respond(HttpStatusCode.BadRequest, ERR_MISSING_SERVER)
                return@post
            }

            val srv = Manager.get(body.serverId)
            if (srv == null) {
                call.respond(HttpStatusCode.NotFound, ERR_NOT_FOUND)
                return@post
            }

            // Utiliza equals(ignoreCase) ao invés de alocar nova String com .lowercase()
            val currentStatus = srv.getStatus()
            if (currentStatus.equals("installing", ignoreCase = true)) {
                srv.emitLive("error", "O servidor está atualmente em instalação. Tente novamente mais tarde.")
                call.respond(HttpStatusCode.BadRequest, ERR_INSTALLING)
                return@post
            }

            val serverDb = DatabaseManager.db.getServer(body.serverId)
            if (serverDb == null) {
                call.respond(HttpStatusCode.NotFound, ERR_DB_NOT_FOUND)
                return@post
            }

            // Só executa o I/O pesado de gravar no banco se a configuração realmente mudou
            if (serverDb.disk != body.disk) {
                DatabaseManager.db.saveServer(serverDb.copy(disk = body.disk))
                srv.diskLimitMb = body.disk // Atualiza na memória
            }

            val action = body.action
            val isStopOrKill = action == "kill" || action == "stop"

            // Evita processar a coleta de Usages da máquina se a ação for apenas stop/kill
            if (!isStopOrKill && body.disk != 0L) {
                val diskUsageMB = srv.getUsages().disk / 1048576L // 1048576 = 1024 * 1024
                if (diskUsageMB > body.disk) {
                    srv.emitLive("error", "O uso de disco do servidor excede o limite definido.")
                    call.respond(HttpStatusCode.BadRequest, ERR_DISK_LIMIT)
                    return@post
                }
            }

            when (action) {
                "install", "start", "restart" -> {
                    if (body.image.isEmpty() || body.primaryAllocation == null || body.environment == null) {
                        call.respond(HttpStatusCode.BadRequest, ERR_MISSING_FIELDS)
                        return@post
                    }

                    // Mapeia os dados do JSON para a sua estrutura StartData
                    val startData = StartData(
                        image = body.image,
                        memory = body.memory,
                        cpu = body.cpu,
                        environment = body.environment,
                        primaryAllocation = body.primaryAllocation,
                        additionalAllocation = body.additionalAllocation,
                        disk = body.disk,
                        core = StartCore(
                            installScript = body.core.installScript,
                            installImage = body.core.installImage,
                            installEntrypoint = body.core.installEntrypoint,
                            startupCommand = body.core.startupCommand,
                            startupScript = body.core.startupScript,
                            dockerEntrypoint = body.core.dockerEntrypoint,
                            stopCommand = body.core.stopCommand,
                            configSystem = body.core.configSystem,
                            startupParser = body.core.startupParser,
                            rootAcess = body.core.rootAcess,
                            maintainable = body.core.maintainable
                        )
                    )

                    when (action) {
                        "start" -> {
                            if (!currentStatus.equals("stopped", ignoreCase = true)) {
                                srv.emitLive("error", "Servidor já em operação.")
                            } else {
                                serverActionScope.launch { srv.start(startData) }
                            }
                        }
                        "restart" -> {
                            if (!srv.isRestarting) {
                                srv.isRestarting = true
                                srv.kill()
                                serverActionScope.launch {
                                    delay(1000.milliseconds)
                                    srv.start(startData)
                                }
                            }
                        }
                        "install" -> {
                            if (!currentStatus.equals("stopped", ignoreCase = true)) {
                                call.respond(HttpStatusCode.BadRequest, ERR_NOT_STOPPED)
                                return@post
                            }
                            serverActionScope.launch { srv.install(startData, skipPull = false, keepAlive = false) }
                        }
                    }
                }

                "stop" -> {
                    if (!srv.isStopping) {
                        srv.isStopping = true
                        srv.emitLive("status", "Servidor marcado como desligando...")

                        if (body.command == "^C" || body.command == "^K") {
                            serverActionScope.launch { srv.kill() }
                        } else {
                            serverActionScope.launch { srv.sendCommand(body.command) }
                        }
                    }
                }

                "kill" -> serverActionScope.launch { srv.kill() }

                "command" -> serverActionScope.launch { srv.sendCommand(body.command) }

                else -> {
                    call.respond(HttpStatusCode.BadRequest, ERR_INVALID_ACTION)
                    return@post
                }
            }

            // Sucesso!
            call.respond(HttpStatusCode.OK, SUCCESS_RESPONSE)
        }
    }
}