package Plume.Http

import io.ktor.server.routing.Route

interface RouteModule {
    fun register(route: Route)
}