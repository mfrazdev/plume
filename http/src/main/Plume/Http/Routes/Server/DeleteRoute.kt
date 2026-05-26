package Plume.Http.Routes.Server

import Plume.Configuration.ConfigManager
import Plume.Server.Manager
import Plume.Http.RouteModule
import Plume.Http.Routes.errorJson
import Plume.Http.Routes.successJson
import io.ktor.server.request.receiveText
import io.ktor.server.response.respond
import io.ktor.server.routing.Route
import io.ktor.server.routing.post
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

object DeleteRoute : RouteModule {
    override fun register(route: Route) {

        route.post("/servers/delete") {
            val body = call.receiveText()
            val json = Json.parseToJsonElement(body).jsonObject

            val serverId = json["serverId"]?.jsonPrimitive?.content
            val userUuid = json["userUuid"]?.jsonPrimitive?.content

            if (serverId == null || userUuid == null) {
                call.respond(errorJson("Missing required fields: serverId and userUuid"))
                return@post
            }

            if(!ConfigManager.validateServerID(serverId)) {
                call.respond(errorJson("Invalid serverId"))
                return@post
            }

            if(!ConfigManager.validateUUID(userUuid)) {
                call.respond(errorJson("Invalid userUuid"))
                return@post
            }

            if(!ConfigManager.userIsAdmin(userUuid)) {
                call.respond(errorJson("User does not have admin permissions"))
                return@post
            }

            if(Manager.get(serverId) == null) {
                call.respond(errorJson("Server not found"))
            }
            Manager.delete(serverId)
            call.respond(successJson())
        }
    }
}