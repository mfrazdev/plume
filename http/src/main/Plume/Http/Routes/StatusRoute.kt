package Plume.Http.Routes


import Plume.Http.RouteModule
import Plume.Server.Manager
import Plume.Version.Updater
import com.sun.management.OperatingSystemMXBean
import io.ktor.http.ContentType
import io.ktor.server.response.respondText
import io.ktor.server.routing.Route
import io.ktor.server.routing.post
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.lang.management.ManagementFactory

object StatusRoute :  RouteModule {

    @Serializable
    data class StatusResponse(
        val status: String,
        val ram: String,
        val os: String,
        val cpu: String,
        val version: String,
        val hasUpdate: Boolean,
        val latestVersion: String,
        val uptime: String
    )
    fun formatTime(millis: Long): String {
        val totalMinutes = millis / 1000 / 60
        val hours = totalMinutes / 60
        val minutes = totalMinutes % 60

        return "%dh %02dm".format(hours, minutes)
    }

    fun getCpuUsage(): Double {
        val osBean = ManagementFactory.getOperatingSystemMXBean()
                as OperatingSystemMXBean

        return osBean.cpuLoad * 100
    }

    override fun register(route: Route) {
        route.post("/status") {

            val update = Updater.checkForUpdates()

            val responseData = StatusResponse(
                status = "success",
                ram = "${Runtime.getRuntime().freeMemory() / (1024 * 1024)} MB",
                os = System.getProperty("os.name"),
                cpu = "${"%.2f".format(getCpuUsage())}%",
                version = update.currentVersion,
                hasUpdate = update.updateAvailable,
                latestVersion = update.latestVersion,
                uptime = formatTime(System.currentTimeMillis() - Manager.whenStarted)
            )

            // Serializa de forma estática (sem reflexão) e devolve como JSON
            val jsonString = Json.encodeToString(StatusResponse.serializer(), responseData)
            call.respondText(jsonString, ContentType.Application.Json)
        }
    }

}