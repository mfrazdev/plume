package Plume.Http.Routes.Server

import Plume.Configuration.ConfigManager
import Plume.Http.RouteModule
import Plume.Http.Routes.errorJson
import Plume.Http.Routes.successJson

import io.ktor.http.HttpStatusCode
import io.ktor.http.content.*
import io.ktor.server.application.*
import io.ktor.server.request.*
import io.ktor.server.request.receiveNullable
import io.ktor.server.response.*
import io.ktor.server.routing.*
import io.ktor.utils.io.toByteArray
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import java.io.File
import java.io.FileOutputStream
import java.nio.file.Path
import java.nio.file.Paths
import java.util.zip.Deflater
import java.util.zip.ZipEntry
import java.util.zip.ZipInputStream
import java.util.zip.ZipOutputStream
import kotlin.io.path.pathString


object FileManagerRoute : RouteModule {

    // Constantes de proteção
    private const val MAX_UNZIP_FILE_SIZE = 3L * 1024 * 1024 * 1024 // 3GB por arquivo
    private const val MAX_UNZIP_TOTAL_SIZE = 3L * 1024 * 1024 * 1024 // 3GB total
    private const val MAX_ZIP_FILES = 100000

    @Serializable
    data class FileBody(
        val userUuid: Int = 0,
        val serverId: String = "",
        val disk: Double = 0.0, // Cota limite enviada pelo frontend (em MB)
        val path: String = "",
        val newName: String = "",
        val content: String = "",
        val action: String = "",
        val paths: List<String> = emptyList(),
        val to: String = "",
        val destination: String = ""
    )

    @Serializable
    data class FileItem(
        val name: String,
        val type: String,
        val size: Long,
        val lastModified: Long,
        val path: String
    )

    @Serializable
    data class FileListResponse(
        val items: List<FileItem>,
        val path: String
    )

    @Serializable
    data class FileReadResponse(
        val content: String,
        val path: String
    )

    // Helper: Caminho base global (Ajuste para puxar da sua Config Global)
    private val globalBasePath: String = ConfigManager.globalConfig.app.path + "/servers"

    override fun register(route: Route) {
        route.route("/servers/filemanager") {

            // Requer RateLimit Plugin configurado no seu Ktor Server
            // install(RateLimit) { ... }

            post("/upload") { handleUpload(call) }
            get("/download") { handleDirectDownload(call) }
            post("/{action}") { fileManagerHandler(call) }
            get("/{action}") { fileManagerHandler(call) }
        }
    }

    private suspend fun fileManagerHandler(call: ApplicationCall) {
        val action = call.parameters["action"] ?: return call.respondError("Ação desconhecida", 400)

        val bodyString = call.receiveText()

        val body = try {
            call.receiveNullable<FileBody>()
        } catch (_: Exception) {
            null
        }

        if(body == null) {
            return call.respondError("Invalid JSON", 400)
        }
        // Validação rigorosa
        if (!ConfigManager.validateUUID(body.userUuid.toString())) {
            return call.respondError("Invalid userUuid", 400)
        }
        if (!ConfigManager.validateServerID(body.serverId)) {
            return call.respondError("Invalid serverId", 400)
        }

        // Supondo que você crie um HasPermission baseado no antigo Config.HasPermission
        if (!ConfigManager.hasPermission(body.userUuid.toString(), body.serverId)) {
            return call.respondError("Sem permissão", 403)
        }

        val basePath = Paths.get(globalBasePath, body.serverId).normalize()
        val relPath = sanitizePath(body.path)

        val absPath = if(relPath.isEmpty()) basePath else basePath.resolve(relPath).normalize()

        // Segurança contra Path Traversal
        if (!absPath.startsWith(basePath)) {
            return call.respondError("Acesso negado (Traversal)", 403)
        }

        val quotaBytes = (body.disk * 1024 * 1024).toLong()

        when (action) {
            "list" -> {
                val dir = absPath.toFile()
                if (!dir.exists() || !dir.isDirectory) {
                    return call.respondError("Diretório não encontrado", 404)
                }

                val items = dir.listFiles()?.map { file ->
                    FileItem(
                        name = file.name,
                        type = if (file.isDirectory) "folder" else "file",
                        size = file.length(),
                        lastModified = file.lastModified(),
                        path = Paths.get(relPath).resolve(file.name).pathString.replace("\\", "/")
                    )
                }.orEmpty()

                call.respond(
                    FileListResponse(
                        items = items,
                        path = "/$relPath"
                    )
                )
            }

            "read" -> {
                val file = absPath.toFile()
                if (!file.exists() || file.isDirectory) {
                    return call.respondError("Arquivo não encontrado", 404)
                }
                call.respond(
                    FileReadResponse(
                        content = file.readText(),
                        path = "/$relPath"
                    )
                )
            }

            "write" -> {
                if (quotaBytes > 0) {
                    val currentSize = getServerDiskUsage(basePath.toFile())
                    val oldSize = if (absPath.toFile().exists()) absPath.toFile().length() else 0L
                    val newFileSize = body.content.toByteArray().size.toLong()
                    val sizeDiff = newFileSize - oldSize

                    if (sizeDiff > 0 && currentSize + sizeDiff > quotaBytes) {
                        return call.respondError("Cota de disco excedida!", 400)
                    }
                }

                absPath.parent.toFile().mkdirs()
                absPath.toFile().writeText(body.content)
                call.respondSuccess()
            }

            "rename" -> {
                if (body.newName.contains("/") || body.newName.contains("\\")) {
                    return call.respondError("Nome inválido", 400)
                }
                val newAbs = absPath.parent.resolve(body.newName)
                absPath.toFile().renameTo(newAbs.toFile())
                call.respondSuccess()
            }

            "mkdir" -> {
                absPath.toFile().mkdirs()
                call.respondSuccess()
            }

            "delete" -> {
                absPath.toFile().deleteRecursively()
                call.respondSuccess()
            }

            "download" -> {
                val file = absPath.toFile()
                if (file.exists() && file.isFile) {
                    call.respondFile(file)
                } else {
                    call.respondError("Arquivo não encontrado", 404)
                }
            }

            "move" -> {
                val destRel = sanitizePath(body.to)
                val destAbs = basePath.resolve(destRel).resolve(absPath.toFile().name).normalize()

                if (!destAbs.startsWith(basePath)) return call.respondError("Destino inválido", 403)

                destAbs.parent.toFile().mkdirs()
                absPath.toFile().renameTo(destAbs.toFile())
                call.respondSuccess()
            }

            "unarchive" -> {
                if (quotaBytes > 0 && getServerDiskUsage(basePath.toFile()) >= quotaBytes) {
                    return call.respondError("Cota de disco excedida, limpe espaço antes de extrair.", 400)
                }
                handleUnarchive(call, body, absPath, basePath)
            }

            "mass" -> {
                when (body.action) {
                    "delete" -> {
                        body.paths.forEach { p ->
                            val target = basePath.resolve(sanitizePath(p)).normalize()
                            if (target.startsWith(basePath)) {
                                target.toFile().deleteRecursively()
                            }
                        }
                        call.respondSuccess()
                    }
                    "archive" -> {
                        if (quotaBytes > 0 && getServerDiskUsage(basePath.toFile()) >= quotaBytes) {
                            return call.respondError("Cota de disco cheia, impossível criar arquivo.", 400)
                        }
                        handleMassArchive(call, body, basePath)
                    }
                    else -> {
                        call.respondError("Ação de mass desconhecida", 400)
                    }
                }
            }

            else -> call.respondError("Ação desconhecida", 400)
        }
    }

    private suspend fun handleDirectDownload(call: ApplicationCall) {
        val userUuid = call.request.queryParameters["userUuid"] ?: ""
        val serverId = call.request.queryParameters["serverId"] ?: ""
        val targetPath = call.request.queryParameters["path"] ?: ""

        if (!ConfigManager.validateUUID(userUuid)) return call.respondError("Invalid userUuid", 400)
        if (!ConfigManager.validateServerID(serverId)) return call.respondError("Invalid serverId", 400)
        if (!ConfigManager.hasPermission(userUuid, serverId)) return call.respondError("Sem permissão", 403)

        val basePath = Paths.get(globalBasePath, serverId).normalize()
        val absPath = basePath.resolve(sanitizePath(targetPath)).normalize()

        if (!absPath.startsWith(basePath)) return call.respondError("Acesso negado (Traversal)", 403)

        val file = absPath.toFile()
        if (!file.exists() || file.isDirectory) {
            call.respondError("Arquivo não encontrado", 404)
            return
        }

        call.respondFile(file)
    }

    private suspend fun handleUpload(call: ApplicationCall) {
        val multipart = call.receiveMultipart()
        var userUuid = ""
        var serverId = ""
        var targetPath = ""
        var diskStr = "0"

        // Em vez de guardar a referência do arquivo, guardamos os bytes lidos
        var fileBytes: ByteArray? = null

        // Parse do Multipart
        multipart.forEachPart { part ->
            when (part) {
                is PartData.FormItem -> {
                    when (part.name) {
                        "userUuid" -> userUuid = part.value
                        "serverId" -> serverId = part.value
                        "path" -> targetPath = part.value
                        "disk" -> diskStr = part.value
                    }
                }
                is PartData.FileItem -> {
                    // LÊ OS BYTES AQUI DENTRO, enquanto o stream desta parte está aberto
                    fileBytes = part.provider().toByteArray()
                }
                else -> {}
            }
            // É fundamental dar dispose em cada parte após processá-la para liberar memória
            part.dispose()
        }

        if (!ConfigManager.validateUUID(userUuid)) return call.respondError("Invalid userUuid", 400)
        if (!ConfigManager.validateServerID(serverId)) return call.respondError("Invalid serverId", 400)
        if (!ConfigManager.hasPermission(userUuid, serverId)) return call.respondError("Sem permissão", 403)

        // Verifica se conseguimos ler os bytes
        if (fileBytes == null) return call.respondError("Arquivo não enviado", 400)

        val basePath = Paths.get(globalBasePath, serverId).normalize()
        val absPath = basePath.resolve(sanitizePath(targetPath)).normalize()

        if (!absPath.startsWith(basePath)) return call.respondError("Acesso negado", 403)

        val diskFloat = diskStr.toDoubleOrNull() ?: 0.0
        val quotaBytes = (diskFloat * 1024 * 1024).toLong()

        if (quotaBytes > 0) {
            val currentSize = getServerDiskUsage(basePath.toFile())
            val oldSize = if (absPath.toFile().exists()) absPath.toFile().length() else 0L
            val sizeDiff = fileBytes.size - oldSize

            if (sizeDiff > 0 && currentSize + sizeDiff > quotaBytes) {
                return call.respondError("Upload negado: limite de disco excedido (Cota: $diskFloat MB)", 400)
            }
        }

        absPath.parent.toFile().mkdirs()
        // Escreve os bytes que salvamos em memória diretamente no arquivo
        absPath.toFile().writeBytes(fileBytes)

        call.respondSuccess()
    }

    private suspend fun handleMassArchive(call: ApplicationCall, body: FileBody, basePath: Path) {
        val timestamp = System.currentTimeMillis()
        val zipName = "$timestamp.zip"
        val zipFile = basePath.resolve(zipName).toFile()

        try {
            withContext(Dispatchers.IO) {
                ZipOutputStream(FileOutputStream(zipFile)).use { zipOut ->

                    // A MÁGICA ACONTECE AQUI:
                    // Deflater.NO_COMPRESSION: não comprime nada, apenas junta os arquivos. É extremamente rápido (limitado apenas pela velocidade do seu SSD/HD).
                    // Se o arquivo ficar grande demais para você, troque para Deflater.BEST_SPEED.
                    zipOut.setLevel(Deflater.NO_COMPRESSION)

                    for (p in body.paths) {
                        val targetPath = basePath.resolve(sanitizePath(p)).normalize()
                        if (!targetPath.startsWith(basePath)) continue

                        val sourceFile = targetPath.toFile()
                        if (!sourceFile.exists()) continue

                        sourceFile.walkTopDown().forEach { file ->
                            val relPath = basePath.relativize(file.toPath()).pathString.replace("\\", "/")
                            val entryName = if (file.isDirectory) "$relPath/" else relPath
                            val zipEntry = ZipEntry(entryName)
                            zipOut.putNextEntry(zipEntry)

                            if (file.isFile) {
                                file.inputStream().use { it.copyTo(zipOut) }
                            }
                            zipOut.closeEntry()
                        }
                    }
                }
            }
            call.respondSuccess()
        } catch (e: Exception) {
            call.respondError("Falha ao criar arquivo zip: ${e.message}", 500)
        }
    }

    private suspend fun handleUnarchive(call: ApplicationCall, body: FileBody, absArchive: Path, basePath: Path) {
        val destRel = if (body.destination.isEmpty()) {
            val sanitized = sanitizePath(body.path)
            // Se tiver barra, pega a pasta pai. Se não, retorna vazio (raiz)
            if (sanitized.contains("/")) sanitized.substringBeforeLast("/") else ""
        } else {
            sanitizePath(body.destination)
        }

        val destAbs = basePath.resolve(destRel).normalize()

        if (!destAbs.startsWith(basePath)) return call.respondError("Destino inválido", 403)
        destAbs.toFile().mkdirs()

        val archiveFile = absArchive.toFile()

        if (archiveFile.name.endsWith(".zip")) {
            try {
                ZipInputStream(archiveFile.inputStream()).use { zis ->
                    var totalSize = 0L
                    var fileCount = 0

                    var entry: ZipEntry? = zis.nextEntry
                    while (entry != null) {
                        fileCount++
                        if (fileCount > MAX_ZIP_FILES) throw Exception("Zip contém muitos arquivos")

                        val newPath = destAbs.resolve(entry.name).normalize()
                        if (!newPath.startsWith(destAbs)) throw Exception("Zip Slip detectado")

                        if (entry.isDirectory) {
                            newPath.toFile().mkdirs()
                        } else {
                            newPath.parent.toFile().mkdirs()

                            FileOutputStream(newPath.toFile()).use { fos ->
                                val buffer = ByteArray(8192)
                                var len: Int
                                var entrySize = 0L

                                while (zis.read(buffer).also { len = it } > 0) {
                                    entrySize += len
                                    totalSize += len

                                    if (entrySize > MAX_UNZIP_FILE_SIZE) throw Exception("Arquivo extraído muito grande")
                                    if (totalSize > MAX_UNZIP_TOTAL_SIZE) throw Exception("Tamanho total descompactado excedeu o limite")

                                    fos.write(buffer, 0, len)
                                }
                            }
                        }
                        zis.closeEntry()
                        entry = zis.nextEntry
                    }
                }
                call.respondSuccess()
            } catch (e: Exception) {
                call.respondError("Erro ao descompactar zip: ${e.message}", 500)
            }
        } else if (archiveFile.name.endsWith(".tar.gz") || archiveFile.name.endsWith(".tgz")) {
            call.respondError("Descompactação de tar.gz requer integração com Apache Commons Compress no backend.", 501)
        } else {
            call.respondError("Formato não suportado para descompactação", 400)
        }
    }

    // --- Utils / Helpers ---

    suspend fun ApplicationCall.respondError(message: String, status: Int) {
        respond(HttpStatusCode.fromValue(status), errorJson(message))
    }

    private suspend fun ApplicationCall.respondSuccess() {
        respond(successJson())
    }

    private fun sanitizePath(p: String): String {
        return p.replace("\\", "/")
            .split("/")
            .filter { it.isNotBlank() && it != ".." && it != "." }
            .joinToString("/")
    }

    private fun getServerDiskUsage(baseFolder: File): Long {
        if (!baseFolder.exists()) return 0L
        return baseFolder.walkTopDown()
            .filter { it.isFile }.sumOf { it.length() }
    }
}