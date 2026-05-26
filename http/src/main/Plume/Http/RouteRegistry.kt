package Plume.Http



import Plume.Http.Routes.StatusRoute
import Plume.Http.Routes.Server.*
import Plume.Http.Routes.Websocket.*
import io.ktor.server.routing.Route

class RouteRegistry {
    private val modules: List<RouteModule> = listOf(
        StatusRoute,
        CreateRoute,
        WebsocketUsages,
        WebsocketConsole,
        ActionsRoute,
        DeleteRoute,
        UsagesRoute,
        SvStatusRoute,
        FileManagerRoute
    )

    fun registerAll(route: Route) {
        modules.forEach { module ->
            module.register(route)
        }
    }
}