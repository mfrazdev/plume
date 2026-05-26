package Plume.Server.Extending

import Plume.Server.Server
import com.github.dockerjava.api.async.ResultCallback
import com.github.dockerjava.api.exception.NotFoundException
import com.github.dockerjava.api.model.Statistics
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.nio.file.Path
import kotlin.concurrent.read
import kotlin.concurrent.write
import kotlin.time.Duration.Companion.milliseconds

// ---- usage monitor ----
fun Server.startUsageMonitor() {
    if (!monitoring.compareAndSet(false, true)) return

    statsJob = CoroutineScope(Dispatchers.IO).launch {
        while (isActive && monitoring.get()) {
            updateStats()
            delay(1000.milliseconds)
        }
    }

    diskJob = CoroutineScope(Dispatchers.IO).launch {
        val sp = serverPath()
        updateDisk(sp)
        while (isActive && monitoring.get()) {
            delay(5000.milliseconds)
            updateDisk(sp)
        }
    }
}

fun Server.destroyMonitor() {
    monitoring.set(false)
    statsJob?.cancel()
    diskJob?.cancel()
    statsJob = null
    diskJob = null
}

internal fun Server.updateStats() {
    val cId = lock.read { containerId }
    if (cId.isBlank()) return

    try {
        // Primeiro: Verificação rápida de existência para evitar exceções desnecessárias
        // Se o container estiver parado ou sendo removido, o stats pode falhar
        val container = docker.inspectContainerCmd(cId).exec()
        if (container.state?.running != true) {
            clearUsageStats()
            return
        }

        val cb = object : ResultCallback.Adapter<Statistics>() {
            override fun onNext(st: Statistics) {
                val memUsage = st.memoryStats?.usage ?: 0L
                val memLimit = st.memoryStats?.limit ?: 0L

                val cpuDelta = (st.cpuStats?.cpuUsage?.totalUsage ?: 0L) -
                        (st.preCpuStats?.cpuUsage?.totalUsage ?: 0L)
                val systemDelta = (st.cpuStats?.systemCpuUsage ?: 0L) -
                        (st.preCpuStats?.systemCpuUsage ?: 0L)
                val numCpus = (st.cpuStats?.onlineCpus?.toDouble() ?: 1.0).let { if (it <= 0.0) 1.0 else it }

                val cpuPercent = if (systemDelta > 0 && cpuDelta > 0) {
                    (cpuDelta.toDouble() / systemDelta.toDouble()) * numCpus * 100.0
                } else 0.0

                var netIn = 0L
                var netOut = 0L
                st.networks?.values?.forEach { n ->
                    netIn += (n.rxBytes ?: 0L)
                    netOut += (n.txBytes ?: 0L)
                }

                lock.write {
                    usageCache = usageCache.copy(
                        cpu = cpuPercent,
                        memory = memUsage,
                        memoryLimit = memLimit,
                        networkIn = netIn,
                        networkOut = netOut,
                    )
                }
            }

            override fun onError(throwable: Throwable) {
                val msg = throwable.toString()
                if (msg.contains("pipe foi finalizado", ignoreCase = true) || msg.contains("Broken pipe", ignoreCase = true)) {
                    try { close() } catch (_: Exception) {}
                    return
                }
                super.onError(throwable)
            }
        }

        docker.statsCmd(cId).withNoStream(true).exec(cb).awaitCompletion()

    } catch (e: NotFoundException) {
        clearUsageStats()
    } catch (_: Exception) {
        // Apenas log de erro em caso de falha crítica de conexão, ignora falhas de estado
        // Opcional: Logger.logger.debug("Falha ao coletar stats: ${e.message}")
        clearUsageStats()
    }
}

internal fun Server.clearUsageStats() {
    lock.write {
        usageCache = usageCache.copy(cpu = 0.0, memory = 0, networkIn = 0, networkOut = 0)
    }
}

internal fun Server.updateDisk(sp: Path) {
    val sizeBytes = runCatching { directorySize(sp) }.getOrDefault(0L)

    val shouldKill = lock.write {
        usageCache = usageCache.copy(disk = sizeBytes)

        val mb = sizeBytes.toDouble() / (1024.0 * 1024.0)
        (diskLimitMb != 0L && mb > diskLimitMb) && (status == "running" || status == "initializing")
    }

    if (shouldKill) {
        kill()
        emitLive("error", "O servidor foi desligado porque o uso de disco ultrapassou o limite.")
    }
}