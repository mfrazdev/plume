package Plume.Server.Extending

import Plume.Server.Server
import Plume.Types.StartData
import com.fasterxml.jackson.databind.ObjectMapper
import com.fasterxml.jackson.dataformat.yaml.YAMLFactory
import com.github.dockerjava.api.async.ResultCallback
import com.github.dockerjava.api.model.Bind
import com.github.dockerjava.api.model.HostConfig
import com.github.dockerjava.api.model.Volume
import com.github.dockerjava.api.model.WaitResponse
import kotlinx.serialization.json.JsonPrimitive
import java.nio.charset.StandardCharsets
import java.nio.file.Files
import java.nio.file.Path
import kotlin.concurrent.read

// Instâncias compartilhadas globalmente no arquivo (melhora a desempenho em vez de criar um por Servidor)
private val json = ObjectMapper()
private val yaml = ObjectMapper(YAMLFactory())

// A MÁGICA TÁ AQUI: Agora ele aceita Any? e sabe desempacotar aspas fantasmas do Ktor
fun clean(v: Any?): String {
    if (v == null) return ""
    // Se for JsonPrimitive, o content pega o valor puro sem forçar as aspas do toString()
    var s = if (v is JsonPrimitive) v.content else v.toString()
    s = s.trim()
    while ((s.startsWith("\"") && s.endsWith("\"")) || (s.startsWith("'") && s.endsWith("'"))) {
        s = s.removeSurrounding("\"").removeSurrounding("'")
    }
    return s
}

// ---- configSystem / startupParser file generation ----
internal fun Server.processConfigFiles(sp: Path, data: StartData) {
    val templates = linkedMapOf<String, Any?>()

    // ConfigSystem
    (data.core.configSystem as? Map<*, *>)?.forEach { (k, v) ->
        if (k != null) {
            val value = if (v is JsonPrimitive) v.content else v
            templates[clean(k)] = value
        }
    }

    // StartupParser (tudo menos "done")
    when (val p = data.core.startupParser) {
        is Map<*, *> -> p.forEach { (k, v) ->
            val key = clean(k)
            if (key != "done") {
                val value = if (v is JsonPrimitive) v.content else v
                templates[key] = value
            }
        }
    }

    for ((filename, content) in templates) {
        lock.read { if (status == "stopped") return }

        val cleanedFilename = clean(filename)
        val path = sp.resolve(cleanedFilename)

        when (content) {
            is String -> {
                Files.createDirectories(path.parent)
                Files.writeString(path, replaceVars(content, data).trim(), StandardCharsets.UTF_8)
            }
            is Map<*, *> -> handleStructuredFile(path, cleanedFilename, content, data)
        }
    }
}

internal fun Server.handleStructuredFile(path: Path, filename: String, cfg: Map<*, *>, data: StartData) {
    val existing = if (Files.exists(path)) Files.readString(path, StandardCharsets.UTF_8) else ""

    when {
        filename.endsWith(".json") -> {
            @Suppress("UNCHECKED_CAST")
            val obj: MutableMap<String, Any?> =
                runCatching { json.readValue(existing.ifBlank { "{}" }, MutableMap::class.java) as MutableMap<String, Any?> }
                    .getOrElse { mutableMapOf() }

            cfg.forEach { (k, v) ->
                if (k == null) return@forEach
                setDot(obj, clean(k), parseVal(v, data))
            }

            Files.createDirectories(path.parent)
            Files.writeString(path, json.writerWithDefaultPrettyPrinter().writeValueAsString(obj), StandardCharsets.UTF_8)
        }

        filename.endsWith(".yml") || filename.endsWith(".yaml") -> {
            @Suppress("UNCHECKED_CAST")
            val obj: MutableMap<String, Any?> =
                runCatching { yaml.readValue(existing.ifBlank { "{}" }, MutableMap::class.java) as MutableMap<String, Any?> }
                    .getOrElse { mutableMapOf() }

            cfg.forEach { (k, v) ->
                if (k == null) return@forEach
                setDot(obj, clean(k), parseVal(v, data))
            }

            Files.createDirectories(path.parent)
            Files.writeString(path, yaml.writeValueAsString(obj), StandardCharsets.UTF_8)
        }

        filename.endsWith(".properties") -> {
            val lines = existing.split("\n").toMutableList()
            val idx = HashMap<String, Int>()
            lines.forEachIndexed { i, l ->
                val parts = l.split("=", limit = 2)
                if (parts.size == 2) idx[clean(parts[0])] = i
            }

            cfg.forEach { (k, v) ->
                if (k == null) return@forEach
                val key = clean(k)
                val line = "$key=${parseVal(v, data)}"
                val at = idx[key]
                if (at != null) lines[at] = line else lines.add(line)
            }

            Files.createDirectories(path.parent)
            Files.writeString(path, lines.joinToString("\n"), StandardCharsets.UTF_8)
        }
    }
}

internal fun Server.parseVal(v: Any?, data: StartData): Any {
    // Se for objeto aninhado (Map/List) deixa passar reto para não estragar a estrutura
    if (v is Map<*, *> || v is List<*>) {
        return v
    }

    // Como v pode ser JsonPrimitive, a gente passa ele no clean() que resolve e puxa a ‘string’
    val p = clean(replaceVars(clean(v), data))

    if (p == "true") return true
    if (p == "false") return false
    return p.toIntOrNull() ?: p
}

internal fun setDot(obj: MutableMap<String, Any?>, path: String, value: Any?) {
    val keys = path.split(".").map { clean(it) }
    var curr: MutableMap<String, Any?> = obj
    for (i in 0 until keys.size - 1) {
        val k = keys[i]
        val next = curr[k]
        if (next !is MutableMap<*, *>) {
            val created = mutableMapOf<String, Any?>()
            curr[k] = created
            curr = created
        } else {
            @Suppress("UNCHECKED_CAST")
            curr = next as MutableMap<String, Any?>
        }
    }
    curr[keys.last()] = value
}

// ---- perms helpers ----
internal fun Server.ensureWritable(dir: Path) {
    if (!Files.exists(dir)) return
    dir.toFile().walkTopDown().forEach { f ->
        runCatching {
            if (f.isDirectory) {
                f.setReadable(true, false)
                f.setWritable(true, false)
                f.setExecutable(true, false)
            } else {
                f.setReadable(true, false)
                f.setWritable(true, false)
            }
        }
    }
}

internal fun Server.fixPerms(hostDir: Path) {
    emitLive("info", "Arrumando as permissões do diretório, isso pode demorar um pouco...")

    val os = clean(System.getProperty("os.name")).lowercase()
    val isWindows = os.contains("win")

    if (isWindows) {
        runCatching { pullImage("alpine", install = true, notifyRaw = false) }
            .onFailure {
                emitLive("error", "Não foi possível ajustar as permissões: ${it.message}")
                return
            }

        val cmd = "chmod -R a+rwX /home/container; chown -R 65534:65534 /home/container"

        val created = docker.createContainerCmd("alpine")
            .withCmd(listOf("/bin/sh", "-c", cmd))
            .withUser("0:0")
            .withHostConfig(
                HostConfig.newHostConfig()
                    .withBinds(Bind(hostDir.toString(), Volume("/home/container")))
            )
            .exec()

        docker.startContainerCmd(created.id).exec()
        docker.waitContainerCmd(created.id).exec(object : ResultCallback.Adapter<WaitResponse>() {}).awaitCompletion()
        runCatching { docker.removeContainerCmd(created.id).withForce(true).exec() }
    } else {
        ensureWritable(hostDir)
    }
}

// ---- misc helpers ----
internal fun directorySize(dir: Path): Long {
    var size = 0L
    Files.walk(dir).use { stream ->
        stream.forEach { p ->
            runCatching {
                if (Files.isRegularFile(p)) size += Files.size(p)
            }
        }
    }
    return size
}

internal fun normalizeScript(script: String): String {
    var s = clean(script).replace("\r\n", "\n")
    if (!s.startsWith("#!")) s = "#!/bin/sh\n$s"
    return s
}

internal fun envToMap(env: Any?): Map<String, String> {
    if (env !is Map<*, *>) return emptyMap()
    val out = LinkedHashMap<String, String>()
    env.forEach { (k, v) ->
        if (k != null) out[clean(k)] = clean(v)
    }
    return out
}

internal fun envToList(env: Any?): List<String> =
    envToMap(env).map { (k, v) ->
        val key = clean(k)
        val value = clean(v)
        "$key=$value"
    }

internal fun Server.replaceVars(input: String, data: StartData): String {
    val vars = LinkedHashMap<String, String>()

    vars["SERVER_MEMORY"] = clean(data.memory)

    data.primaryAllocation?.let {
        vars["SERVER_PORT"] = clean(it.port)
        vars["SERVER_IP"] = clean(it.ip)
    }

    envToMap(data.environment).forEach { (k, v) ->
        vars[clean(k)] = clean(v)
    }

    var res = input

    for ((k, v) in vars) {
        res = res.replace("{{${clean(k)}}}", clean(v))
    }

    return clean(res)
}

internal fun replaceVarsGeneric(input: String, vars: Map<String, String>): String {
    var res = input
    for ((k, v) in vars) res = res.replace("{{${clean(k)}}}", clean(v))
    return clean(res)
}

internal fun parseDoneString(startupParser: Any?): String {
    if (startupParser !is Map<*, *>) return ""
    val v = startupParser["done"] ?: return ""
    return clean(v)
}