package Plume.Http.Routes.Server

import Plume.Configuration.ConfigManager
import Plume.Http.RouteModule
import Plume.Server.Manager
import Plume.Http.Routes.errorJson
import Plume.Http.Routes.successJson
import Plume.JSON
import io.ktor.server.request.receiveText
import io.ktor.server.response.respond
import io.ktor.server.routing.Route
import io.ktor.server.routing.post
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import kotlinx.serialization.json.putJsonObject

object UsagesRoute : RouteModule {
    override fun register(route: Route) {

        route.post("/servers/usage") {
            val body = call.receiveText()
            val json = Json.parseToJsonElement(body).jsonObject

            val serverId = json["serverId"]?.jsonPrimitive?.content

            if (serverId == null ) {
                call.respond(errorJson("Missing required field: serverId"))
                return@post
            }

            if(!ConfigManager.validateServerID(serverId)) {
                call.respond(errorJson("Invalid serverId"))
                return@post
            }

            val server = Manager.get(serverId)
            if (server == null) {
                call.respond(errorJson("Server not found"))
                return@post
            }

            call.respond(
                successJson {
                    "usage" to {
                        "cpu" to server.usageCache.cpu
                        "memory" to server.usageCache.memory
                        "memoryLimit" to server.usageCache.memoryLimit
                        "disk" to server.usageCache.disk
                        "state" to server.getStatus()
                    }

                }
            )
        }
    }
}