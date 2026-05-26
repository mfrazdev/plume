package Plume.Http.Routes.Websocket

import Plume.Configuration.ConfigManager
import Plume.Http.RouteModule
import Plume.Logger
import Plume.Server.Manager
import Plume.Server.Extending.sendCommand
import Plume.Server.Extending.streamLogs
import io.ktor.server.routing.Route
import io.ktor.server.websocket.*
import io.ktor.websocket.*
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.channels.Channel
import kotlinx.serialization.Serializable
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference

object WebsocketConsole : RouteModule {
    // Data classes para serialização rápida e compatível com CBOR
    @Serializable
    data class WsLogMessage(
        val type: String,
        val prefix: String,
        val category: String,
        val message: String,
        val timestamp: Long,
        val line: String
    )

    @Serializable
    data class WsClearMessage(val type: String = "clear")

    @Serializable
    data class WsErrorResponse(val category: String = "error", val message: String)

    @Serializable
    data class IncomingCommand(val type: String? = null, val command: String? = null)

    override fun register(route: Route) {
        route.webSocket("/servers/console") {
            val serverId = call.request.queryParameters["serverId"]
            val userUuid = call.request.queryParameters["userUuid"]
            val tailReq = call.request.queryParameters["tail"]?.toIntOrNull() ?: 200

            if (serverId.isNullOrEmpty() || userUuid.isNullOrEmpty()) {
                close(CloseReason(CloseReason.Codes.NORMAL, "Parâmetros ausentes"))
                return@webSocket
            }

            if (!ConfigManager.validateServerID(serverId)) {
                close(CloseReason(CloseReason.Codes.NORMAL, "Invalid serverId"))
                return@webSocket
            }

            if (!ConfigManager.validateUUID(userUuid)) {
                close(CloseReason(CloseReason.Codes.NORMAL, "Invalid userUuid"))
                return@webSocket
            }

            val srv = Manager.get(serverId)
            if (srv == null) {
                sendSerialized(WsErrorResponse(message = "Servidor inexistente"))
                close(CloseReason(CloseReason.Codes.NORMAL, "Servidor inexistente"))
                return@webSocket
            }

            if (!WsCommon.checkPermissionCached(userUuid, srv)) {
                sendSerialized(WsErrorResponse(message = "Sem permissão"))
                close(CloseReason(CloseReason.Codes.NORMAL, "Sem permissão"))
                return@webSocket
            }

            var lastLineHash = 0
            val streamStarted = AtomicBoolean(false)
            val logCleanup = AtomicReference<(() -> Unit)?>(null)
            val logChannel = Channel<String>(capacity = 2048, onBufferOverflow = BufferOverflow.DROP_OLDEST)

            suspend fun sendStructured(category: String, message: String, timestamp: Long = System.currentTimeMillis()) {
                if (category == "internal") return

                val currentHash = 31 * category.hashCode() + message.hashCode()
                if (lastLineHash == currentHash) return
                lastLineHash = currentHash

                val isLog = category == "log"
                val prefixOut = if (isLog) "" else {
                    if (category == "info" || category == "error") WsCommon.PrefixLabel2 else WsCommon.PrefixLabel
                }

                val catColor = WsCommon.getColorForCategory(category)
                val line = if (isLog) {
                    "$catColor$message${WsCommon.ResetColor}"
                } else {
                    "$catColor${WsCommon.gradient(prefixOut)}$catColor $message${WsCommon.ResetColor}"
                }

                sendSerialized(WsLogMessage("line", prefixOut, category, message, timestamp, line))
            }

            val logSenderJob = launch {
                for (line in logChannel) {
                    sendStructured("log", line)
                }
            }

            fun beginStream() {
                if (!streamStarted.compareAndSet(false, true)) return
                try {
                    val cleanup = srv.streamLogs(tailReq) { line ->
                        logChannel.trySend(line)
                    }
                    logCleanup.set(cleanup)
                } catch (e: Exception) {
                    Logger.logger.error("[Server Console] Erro ao iniciar stream de logs: ${e.message}")
                }
            }

            when (val initialStatus = srv.getStatus().lowercase()) {
                "running", "initializing", "installing", "stopping" -> {
                    if (initialStatus != "running") {
                        sendStructured("status", "Servidor marcado como $initialStatus...")
                    }
                    beginStream()
                }
                else -> sendStructured("status", "Servidor marcado como offline...")
            }

            val eventsJob = launch {
                srv.live.events.collect { evt ->
                    val cat = evt.category
                    val msg = evt.message
                    val ts = System.currentTimeMillis()

                    if (cat == "clear") {
                        lastLineHash = 0
                        sendSerialized(WsClearMessage())
                    } else if (cat == "internal") {
                        if (msg == "initializing" || msg == "running") {
                            beginStream()
                        }
                    } else {
                        sendStructured(cat, msg, ts)
                        if (cat == "status" && (msg.contains("offline") || msg.contains("desligado"))) {
                            logCleanup.getAndSet(null)?.invoke()
                            streamStarted.set(false)
                        }
                    }
                }
            }

            try {
                for (frame in incoming) {
                    if (frame is Frame.Text) {
                        try {
                            // Aqui mantemos o decode do JSON pois o comando vem do cliente em texto puro via WS
                            val payload = kotlinx.serialization.json.Json.decodeFromString<IncomingCommand>(frame.readText())
                            if (payload.type == "command" && !payload.command.isNullOrEmpty()) {
                                srv.sendCommand(payload.command)
                            }
                        } catch (_: Exception) {}
                    }
                }
            } catch (_: Exception) {
            } finally {
                logChannel.close()
                logSenderJob.cancel()
                eventsJob.cancel()
                logCleanup.get()?.invoke()
            }
        }
    }
}