package Plume.Http.Routes.Websocket

import Plume.Configuration.ConfigManager
import Plume.Http.RouteModule
import Plume.Server.Manager
import io.ktor.server.routing.Route
import io.ktor.server.websocket.sendSerialized
import io.ktor.server.websocket.webSocket
import io.ktor.websocket.CloseReason
import io.ktor.websocket.close
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.serialization.Serializable
import kotlin.time.Duration.Companion.milliseconds



object WebsocketUsages: RouteModule {

    // Estruturas de dados agnósticas (funcionam tanto com JSON quanto com CBOR)
    @Serializable
    data class WsUsageData(
        val cpu: Double,
        val memory: Long,
        val memoryLimit: Long,
        val memoryPercent: Double,
        val networkIn: Long,
        val networkOut: Long,
        val disk: Long,
        val startedAt: Long? = null,
        val uptimeMs: Long,
        val state: String
    )

    @Serializable
    data class WsUsagePayload(
        val type: String,
        val timestamp: Long,
        val usage: WsUsageData
    )

    override fun register(route: Route) {
        route.webSocket("/servers/usages") {
            val serverId = call.request.queryParameters["serverId"]
            val userUuid = call.request.queryParameters["userUuid"]

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
                sendSerialized(mapOf("category" to "error", "message" to "Servidor inexistente"))
                close(CloseReason(CloseReason.Codes.NORMAL, "Servidor inexistente"))
                return@webSocket
            }

            if (!WsCommon.checkPermissionCached(userUuid, srv)) {
                sendSerialized(mapOf("category" to "error", "message" to "Sem permissão"))
                close(CloseReason(CloseReason.Codes.NORMAL, "Sem permissão"))
                return@webSocket
            }

            try {
                // Função real de montagem e envio do payload
                suspend fun sendFrame() {
                    val usage = srv.getUsages()
                    val status = srv.getStatus() // ou srv.getStatus() dependendo de como você mapeou no Kotlin
                    val startedAt = srv.startedAt

                    val uptimeMs = if (startedAt != null) System.currentTimeMillis() - startedAt else 0L

                    val memPercent = if (usage.memoryLimit > 0) {
                        (usage.memory.toDouble() / usage.memoryLimit.toDouble()) * 100.0
                    } else 0.0

                    // Removido o buildJsonObject, usando a data class que o CBOR entende
                    val payload = WsUsagePayload(
                        type = "usage",
                        timestamp = System.currentTimeMillis(),
                        usage = WsUsageData(
                            cpu = usage.cpu.toDouble(),
                            memory = usage.memory.toLong(),
                            memoryLimit = usage.memoryLimit.toLong(),
                            memoryPercent = memPercent,
                            networkIn = usage.networkIn.toLong(),
                            networkOut = usage.networkOut.toLong(),
                            disk = usage.disk.toLong(),
                            startedAt = startedAt,
                            uptimeMs = uptimeMs,
                            state = status.toString()
                        )
                    )

                    sendSerialized(payload)
                }

                // 1. Envia o primeiro frame imediatamente
                sendFrame()

                // 2. Escuta mudanças de status para push reativo (Flow)
                val eventsJob = launch {
                    srv.live.events.collect { evt ->
                        if (evt.category == "status" || evt.category == "internal") {
                            sendFrame()
                        }
                    }
                }

                // 3. Loop de envio a cada 1 segundo
                while (isActive) {
                    // Verifica se o servidor ainda existe
                    if (Manager.get(serverId) == null) {
                        break
                    }

                    sendFrame()
                    delay(1000.milliseconds)
                }

                eventsJob.cancel()

            } catch (e: Exception) {
                e.printStackTrace()
            }
        }
    }
}