package Plume.Server.Extending

import Plume.Logger
import Plume.Server.Server
import com.github.dockerjava.api.async.ResultCallback
import com.github.dockerjava.api.exception.NotFoundException
import com.github.dockerjava.api.model.Frame
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import java.io.ByteArrayInputStream
import java.nio.charset.StandardCharsets
import kotlin.concurrent.read
import kotlin.concurrent.write

// ---- SendCommand (attach stdin) ----
fun Server.sendCommand(input: String) {
    val cId = lock.read { containerId }
    if (cId.isBlank()) return

    // Verifica se o container ainda existe antes de tentar o attach
    try {
        docker.inspectContainerCmd(cId).exec()
    } catch (e: NotFoundException) {
        return // Silencia o erro, o container realmente não existe
    }

    try {
        docker.attachContainerCmd(cId)
            .withStdIn(ByteArrayInputStream((input + "\n").toByteArray(StandardCharsets.UTF_8)))
            .withFollowStream(true)
            .withStdOut(false)
            .withStdErr(false)
            .exec(object : ResultCallback.Adapter<Frame>() {
                override fun onError(throwable: Throwable) {
                    val msg = throwable.toString()
                    if (msg.contains("pipe foi finalizado", ignoreCase = true) || msg.contains("Broken pipe", ignoreCase = true)) {
                        try { close() } catch (_: Exception) {}
                        return
                    }
                    super.onError(throwable)
                }
            })
    } catch (e: NotFoundException) {
        // Silencia caso seja removido entre o inspect e o attach
    }
}

// ---- StreamLogs(tail, onLine) ----
fun Server.streamLogs(tail: Int, onLine: (String) -> Unit): () -> Unit {
    val cId = lock.read { containerId }
    if (cId.isBlank()) return { }

    val job = CoroutineScope(Dispatchers.IO).launch {
        try {
            // Verificação de existência inicial
            docker.inspectContainerCmd(cId).exec()

            val cb = object : ResultCallback.Adapter<Frame>() {
                override fun onNext(item: Frame) {
                    val txt = String(item.payload, StandardCharsets.UTF_8)
                    txt.split('\n').forEach { l0 ->
                        val l = l0.trimEnd('\r')
                        if (l.isNotBlank()) onLine(l)
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

            docker.logContainerCmd(cId)
                .withStdOut(true).withStdErr(true)
                .withFollowStream(true)
                .withTail(tail)
                .exec(cb)
                .awaitCompletion()
        } catch (e: NotFoundException) {
            // Silencioso: container morreu ou foi removido
        } catch (e: Exception) {
            // Log apenas para erros reais de conexão, não NotFound
            if (e !is NotFoundException) {
                Logger.logger.error("Erro na stream de logs: ${e.message}")
            }
        }
    }

    return { job.cancel() }
}

// ---- attachLogStream(doneStr) ----
fun Server.attachLogStream(doneStr: String) {
    val cId = lock.read { containerId }
    if (cId.isBlank()) return

    val ansiClear = Regex("""\u001B(c|\[[0-9;]*[HJ])""")

    CoroutineScope(Dispatchers.IO).launch {
        try {
            val cb = object : ResultCallback.Adapter<Frame>() {
                override fun onNext(item: Frame) {
                    val raw = String(item.payload, StandardCharsets.UTF_8)
                    raw.split('\n').forEach { line0 ->
                        var line = line0.trimEnd('\r')
                        if (ansiClear.containsMatchIn(line)) {
                            emitLive("clear", "")
                        }
                        line = line.replace(ansiClear, "")
                        if (line.trim().isEmpty()) return@forEach

                        emitLive("log", line)

                        if (doneStr.isNotBlank() && line.contains(doneStr)) {
                            var doEmit = false
                            lock.write {
                                if (status != "running") {
                                    status = "running"
                                    startedAt = System.currentTimeMillis()
                                    doEmit = true
                                }
                            }
                            if (doEmit) {
                                emitLive("status", "Servidor marcado como online...")
                                emitLive("internal", "running")
                            }
                        }
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

            docker.logContainerCmd(cId)
                .withStdOut(true).withStdErr(true)
                .withFollowStream(true)
                .exec(cb)
                .awaitCompletion()
        } catch (e: NotFoundException) {
            // Contêiner foi deletado/morto
        } catch (e: Exception) {
            // Captura outras exceções genéricas
        }
    }
}