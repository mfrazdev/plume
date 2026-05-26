package Plume.Server

import com.github.dockerjava.api.DockerClient
import kotlinx.coroutines.Job
import Plume.Events.LiveBus
import Plume.Types.UsageMetrics
import java.nio.file.Files
import java.nio.file.Path
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.locks.ReentrantReadWriteLock
import kotlin.concurrent.read
import kotlin.concurrent.write

class Server(
    val id: String,
    needsInstall: Boolean,
    internal val docker: DockerClient,
    internal val basePath: Path,
) {
    internal val lock = ReentrantReadWriteLock()

    internal var containerId: String = ""
    internal var status: String = "stopped" // stopped, initializing, installing, running

    var isRestarting: Boolean = false

    var isStopping: Boolean = false

    var startedAt: Long? = null
        internal set
    var diskLimitMb: Long = 0

    internal var needsInstallFlag: Boolean = needsInstall

    var usageCache: UsageMetrics = UsageMetrics()
        internal set

    internal val monitoring = AtomicBoolean(false)
    internal var statsJob: Job? = null
    internal var diskJob: Job? = null

    val live = LiveBus()

    fun emitLive(category: String, message: String) = live.emit(category, message)

    fun getStatus(): String = lock.read { if (isStopping) "stopping" else status }
    fun getUsages(): UsageMetrics = lock.read { usageCache }

    fun setContainerId(cid: String) = lock.write { containerId = cid }
    fun setStatus(s: String) = lock.write { status = s }

    internal fun serverPath(): Path {
        val path = basePath.resolve("servers").resolve(id)
        Files.createDirectories(path)
        return path
    }
}