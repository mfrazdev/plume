package Plume.Server.Extending


import Plume.Database.DatabaseManager
import Plume.Logger
import Plume.Server.Server
import com.github.dockerjava.api.async.ResultCallback
import com.github.dockerjava.api.command.InspectContainerResponse
import com.github.dockerjava.api.model.Bind
import com.github.dockerjava.api.model.ExposedPort
import com.github.dockerjava.api.model.Frame
import com.github.dockerjava.api.model.HostConfig
import com.github.dockerjava.api.model.Ports
import com.github.dockerjava.api.model.PullResponseItem
import com.github.dockerjava.api.model.Volume
import com.github.dockerjava.api.model.WaitResponse
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import Plume.Types.AllocationData
import Plume.Types.StartData
import java.nio.charset.StandardCharsets
import java.nio.file.Files
import java.util.concurrent.CompletableFuture
import kotlin.concurrent.read
import kotlin.concurrent.write
import kotlin.time.Duration.Companion.milliseconds

// ---- kill/delete ----
fun Server.kill() {
    val cId = lock.write {
        if (status == "installing") return
        status = "stopped"
        isRestarting = false
        isStopping = false
        containerId
    }

    if (cId.isNotBlank()) {
        runCatching { docker.killContainerCmd(cId).withSignal("SIGKILL").exec() }
    }
}

fun Server.delete() {
    val wasInstalling = lock.read { status == "installing" }
    if (wasInstalling) return

    kill()

    val cId = lock.write {
        val cid = containerId
        status = "stopped"
        isRestarting = false
        isStopping = false
        startedAt = null
        cid
    }

    if (cId.isNotBlank()) runCatching { docker.removeContainerCmd(cId).withForce(true).exec() }

    destroyMonitor()

    runCatching { serverPath().toFile().deleteRecursively() }

    val customImageName = "local_server_${id}:latest"
    runCatching { docker.removeImageCmd(customImageName).withForce(true).exec() }
}

// ---- pullImage ----
internal fun Server.pullImage(imageName: String, install: Boolean, notifyRaw: Boolean) {
    var repo = imageName
    var tag = "latest"
    if (imageName.contains(":")) {
        val parts = imageName.split(":", limit = 2)
        repo = parts[0]
        tag = parts[1]
    }

    if (!install) emitLive("info", "Baixando imagem do container Docker, isso pode levar alguns minutos...")

    val cb = object : ResultCallback.Adapter<PullResponseItem>() {
        override fun onNext(item: PullResponseItem) {
            if(notifyRaw) {
                val statusMsg = item.status
                val idMsg = item.id

                val msg = buildString {
                    if (!idMsg.isNullOrBlank()) append("$idMsg: ")
                    if (!statusMsg.isNullOrBlank()) append(statusMsg)
                }.ifBlank { item.stream ?: item.errorDetail?.message ?: "" }

                if (msg.isNotBlank()) emitLive("log", msg.trim())
                if (item.isErrorIndicated) emitLive("error", item.errorDetail?.message ?: "Erro desconhecido ao puxar imagem Docker.")
            }
        }

        override fun onError(throwable: Throwable) {
            Logger.logger.error("Erro ao puxar a imagem Docker: ${throwable.message}")
            emitLive("error", "Erro ao puxar a imagem: ${throwable.message}")
        }
    }

    try {
        docker.pullImageCmd(repo).withTag(tag).exec(cb).awaitCompletion()
        if (!install) emitLive("info", "Download da imagem Docker concluído")
    } catch (e: Exception) {
        emitLive("error", "Erro ao puxar a imagem: ${e.message}")
        if (notifyRaw) emitLive("log", e.toString())
        throw e
    }
}

// ---- install ----
fun Server.install(data: StartData, skipPull: Boolean, keepAlive: Boolean): Boolean {
    lock.write {
        diskLimitMb = data.disk
        status = "installing"
    }
    emitLive("status", "Iniciando processo de instalação do servidor...")
    emitLive("internal", "installing")

    val sp = serverPath()
    Files.createDirectories(sp)

    val installImage = data.core.installImage.ifBlank { "alpine" }
    val installEntrypoint = data.core.installEntrypoint.ifBlank { "/bin/sh" }

    if (!skipPull) {
        emitLive("info", "Baixando imagem de instalação, isso pode levar alguns minutos...")
        runCatching { pullImage(installImage, install = true, notifyRaw = false) }
            .onFailure { emitLive("error", "Erro ao baixar imagem de instalação: ${it.message}") }
    }

    lock.read { if (status == "stopped") return false }

    val installCmd = normalizeScript(replaceVars(data.core.installScript, data))
    val installScriptPath = sp.resolve("install.sh")
    Files.writeString(installScriptPath, installCmd, StandardCharsets.UTF_8)
    ensureWritable(sp)

    val installerName = "${id}_installer"
    runCatching { docker.removeContainerCmd(installerName).withForce(true).exec() }

    val envs = envToList(data.environment)

    val fields = installEntrypoint.trim().split(Regex("\\s+")).filter { it.isNotBlank() }
    val cmd = if (fields.isNotEmpty() && fields.last() == "-c") {
        fields + "chmod +x /mnt/server/install.sh && /mnt/server/install.sh"
    } else {
        fields + "/mnt/server/install.sh"
    }

    val created = try {
        docker.createContainerCmd(installImage)
            .withName(installerName)
            .withEnv(envs)
            .withCmd(cmd)
            .withHostConfig(
                HostConfig.newHostConfig()
                    .withBinds(Bind(sp.toString(), Volume("/mnt/server")))
                    .withMemory(data.memory.toLong() * 1024L * 1024L)
            )
            .exec()
    } catch (e: Exception) {
        emitLive("error", "Falha ao criar container instalador: ${e.message}")
        lock.write { status = "stopped" }
        return false
    }

    docker.startContainerCmd(created.id).exec()
    emitLive("status", "Iniciando script de instalação...")

    val logCb = object : ResultCallback.Adapter<Frame>() {
        override fun onNext(item: Frame) {
            val txt = String(item.payload, StandardCharsets.UTF_8)
            txt.split('\n').forEach { l0 ->
                val l = l0.trimEnd('\r')
                if (l.isNotBlank()) emitLive("log", l)
            }
        }
    }
    docker.logContainerCmd(created.id)
        .withStdOut(true).withStdErr(true)
        .withFollowStream(true)
        .exec(logCb)

    var exitCode = -1
    val waitCb = object : ResultCallback.Adapter<WaitResponse>() {
        override fun onNext(item: WaitResponse) {
            exitCode = item.statusCode ?: -1
        }
    }
    docker.waitContainerCmd(created.id).exec(waitCb).awaitCompletion()
    val code = exitCode

    runCatching { docker.removeContainerCmd(created.id).withForce(true).exec() }
    runCatching { Files.deleteIfExists(installScriptPath) }

    lock.read { if (status == "stopped") return false }

    if (code != 0) {
        emitLive("error", "A instalação falhou. Código de erro: $code")
        lock.write { status = "stopped" }
        return false
    }

    lock.write {
        needsInstallFlag = false
    }
    CompletableFuture.runAsync {
        DatabaseManager.db.getServer(id)?.let { currentServer ->
            val updatedServer = currentServer.copy(installed = 1)
            DatabaseManager.db.saveServer(updatedServer)
        }
    }.exceptionally {
        it.printStackTrace()
        null
    }
    emitLive("info", "Instalação concluída com sucesso.")

    if (!keepAlive) lock.write { status = "stopped" }
    return true
}

// ---- start ----
fun Server.start(data: StartData) {
    val prev = lock.write {
        isRestarting = false
        diskLimitMb = data.disk
        isStopping = false
        status = "initializing"
        containerId
    }

    val allowRoot = (data.core.rootAcess == 1L)
    val maintainable = (data.core.maintainable == 1L)

    var needsRecreate = true
    val originalBaseImage = data.image
    val customImageName = "local_server_${id}:latest"

    if (prev.isNotBlank()) {
        if (maintainable) {
            val inspect = runCatching { docker.inspectContainerCmd(prev).exec() }.getOrNull()
            if (inspect != null) {
                val currentBase = extractEnv(inspect, "Plume_BASE_IMAGE")
                    ?: if (inspect.config?.image == customImageName) originalBaseImage else inspect.config?.image

                val imageChanged = currentBase != originalBaseImage
                val allocChanged = allocationChanged(inspect, data.primaryAllocation)

                if (imageChanged || allocChanged) {
                    emitLive("warning", "Atenção: Houve alteração na configuração do servidor!")

                    if (allocChanged && !imageChanged) {
                        emitLive("info", "Alteração de porta detectada. Salvando estado atual do sistema...")
                        runCatching { docker.stopContainerCmd(prev).withTimeout(10).exec() }

                        val committed = runCatching {
                            docker.commitCmd(prev)
                                .withRepository("local_server_$id")
                                .withTag("latest")
                                .exec()
                        }.getOrNull()

                        if (committed == null) {
                            emitLive("error", "Aviso: Falha ao salvar estado interno.")
                        } else {
                            data.image = customImageName
                            emitLive("info", "Estado salvo com sucesso! O novo container herdará tudo.")
                        }
                    } else {
                        emitLive("warning", "A imagem base do container foi alterada! Alterações internas antigas serão descartadas.")
                        emitLive("warning", "O servidor foi marcado para necessitar de reinstalação na nova imagem.")
                        lock.write { needsInstallFlag = true }
                    }

                    emitLive("warning", "Você tem 10 segundos para clicar em 'Stop' se desejar cancelar a operação.")
                    runBlocking {
                        repeat(10) {
                            delay(1000.milliseconds)
                            val st = lock.read { status }
                            if (st == "stopped" || st == "stopping") {
                                emitLive("info", "Inicialização cancelada pelo usuário.")
                                return@runBlocking
                            }
                        }
                    }
                    lock.read {
                        if (status == "stopped" || status == "stopping") return
                    }

                    emitLive("info", "Tempo esgotado. Recriando o container...")
                    runCatching { docker.removeContainerCmd(prev).withForce(true).exec() }
                    if (imageChanged) runCatching { docker.removeImageCmd(customImageName).withForce(true).exec() }
                } else {
                    needsRecreate = false
                    emitLive("info", "Servidor mantível: Iniciando container existente sem recriar...")
                }
            } else {
                runCatching { docker.removeContainerCmd(prev).withForce(true).exec() }
            }
        } else {
            runCatching { docker.removeContainerCmd(prev).withForce(true).exec() }
        }
    }

    val sp = serverPath()
    Files.createDirectories(sp)
    emitLive("log", "")
    emitLive("status", "Servidor marcado como iniciando...")
    emitLive("internal", "initializing")

    ensureWritable(sp)
    processConfigFiles(sp, data)
    fixPerms(sp)

    if (data.image != customImageName) {
        runCatching { pullImage(data.image, install = false, notifyRaw = true) }
            .onFailure { emitLive("error", "Erro ao baixar imagem principal: ${it.message}") }
    }

    val needsInstallNow = lock.read { needsInstallFlag }
    if (needsInstallNow && data.core.installScript.isNotBlank()) {
        if (!install(data, skipPull = false, keepAlive = true)) return
        lock.write { status = "initializing" }
    }

    if (maintainable && data.image != customImageName) {
        val hasCustom = runCatching { docker.inspectImageCmd(customImageName).exec() }.isSuccess
        if (hasCustom) data.image = customImageName
    }

    lock.read {
        if (status == "stopped" || status == "stopping") return
    }

    emitLive("info", "Container iniciando...")

    if (needsRecreate) {
        val allAllocs = buildList {
            data.primaryAllocation?.let { add(it) }
            addAll(data.additionalAllocation)
        }

        val ports = Ports()
        for (a in allAllocs) {
            ports.bind(ExposedPort.tcp(a.port), Ports.Binding.bindIpAndPort(a.ip, a.port))
            ports.bind(ExposedPort.udp(a.port), Ports.Binding.bindIpAndPort(a.ip, a.port))
        }

        val varMap = mutableMapOf("SERVER_MEMORY" to data.memory.toString())
        data.primaryAllocation?.let {
            varMap["SERVER_PORT"] = it.port.toString()
            varMap["SERVER_IP"] = it.ip
        }
        envToMap(data.environment).forEach { (k, v) -> varMap[k] = v }

        var startupCmd = replaceVarsGeneric(data.core.startupCommand, varMap)

        if (data.core.startupScript.isNotBlank()) {
            val script = normalizeScript(replaceVarsGeneric(data.core.startupScript, varMap))
            val scriptPath = sp.resolve(".plume_startup.sh")
            Files.writeString(scriptPath, script, StandardCharsets.UTF_8)
            startupCmd = "chmod +x .plume_startup.sh 2>/dev/null; ./.plume_startup.sh"
        }

        val user = if (allowRoot) "0:0" else "65534:65534"

        val envList = envToList(data.environment).toMutableList()
        envList += "PLUME_BASE_IMAGE=$originalBaseImage"

        val finalCmd = if (data.core.dockerEntrypoint.isNotBlank()) {
            data.core.dockerEntrypoint.trim().split(Regex("\\s+")) + startupCmd
        } else {
            listOf("/bin/sh", "-c", startupCmd)
        }

        val hostConfig = HostConfig.newHostConfig()
            .withBinds(Bind(sp.toString(), Volume("/home/container")))
            .withPortBindings(ports)
            .withExtraHosts("host.docker.internal:host-gateway")

        if (data.memory > 0) hostConfig.withMemory(data.memory.toLong() * 1024L * 1024L)
        if (data.cpu > 0) {
            hostConfig.withCpuPeriod(100_000L)
            hostConfig.withCpuQuota(data.cpu.toLong() * 1000L)
        }

        val created = docker.createContainerCmd(data.image)
            .withName(id)
            .withEnv(envList)
            .withCmd(finalCmd)
            .withTty(true)
            .withStdinOpen(true)
            .withWorkingDir("/home/container")
            .withUser(user)
            .withHostConfig(hostConfig)
            .exec()

        lock.write { containerId = created.id }
    }

    val cId = lock.read { containerId }
    docker.startContainerCmd(cId).exec()

    val doneStr = parseDoneString(data.core.startupParser)

    if (doneStr.isBlank()) {
        lock.write {
            status = "running"
            startedAt = System.currentTimeMillis()
        }
        emitLive("status", "Servidor marcado como online...")
        emitLive("internal", "running")
    }

    attachLogStream(doneStr)
    waitContainerExitAsync()
}

fun Server.waitContainerExitAsync() {
    CoroutineScope(Dispatchers.IO).launch {
        val cId = lock.read { containerId }
        if (cId.isBlank()) return@launch

        runCatching { docker.waitContainerCmd(cId).exec(object : ResultCallback.Adapter<WaitResponse>() {}).awaitCompletion() }

        lock.write {
            status = "stopped"
            isStopping = false
            startedAt = null
        }

        emitLive("status", "Servidor marcado como offline...")
    }
}

// Helpers específicos do Lifecycle
internal fun Server.extractEnv(inspect: InspectContainerResponse, key: String): String? {
    val env = inspect.config?.env ?: return null
    val prefix = "$key="
    return env.firstOrNull { it.startsWith(prefix) }?.removePrefix(prefix)
}

internal fun Server.allocationChanged(inspect: InspectContainerResponse, primary: AllocationData?): Boolean {
    if (primary == null) return false
    val expected = "${primary.port}/tcp"

    val pb = inspect.hostConfig?.portBindings ?: return true
    val keys = pb.bindings?.keys?.map { it.toString() } ?: return true
    return expected !in keys
}