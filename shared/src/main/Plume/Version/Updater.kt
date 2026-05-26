package Plume.Version

import org.w3c.dom.Element
import java.io.InputStream
import java.net.URI
import java.net.http.HttpClient
import java.net.http.HttpRequest
import java.net.http.HttpResponse
import java.nio.file.Files
import java.nio.file.Paths
import java.nio.file.StandardCopyOption
import java.time.Duration
import java.time.Instant
import javax.xml.parsers.DocumentBuilderFactory
import kotlin.system.exitProcess

const val GITHUB_REPO = "mfrazlab/feather" // Substitua se necessário

data class UpdateInfo(
    val currentVersion: String,
    val latestVersion: String,
    val updateAvailable: Boolean
)

object Updater {

    private val httpClient = HttpClient.newBuilder()
        .connectTimeout(Duration.ofSeconds(10))
        .build()

    private fun fetchLatestGitHubRelease(): Pair<String, Instant> {
        val isCurrentCanary = Version.VERSION.lowercase().contains("canary")
        val reqUrl = "https://github.com/$GITHUB_REPO/releases.atom"

        val request = HttpRequest.newBuilder()
            .uri(URI.create(reqUrl))
            .header("User-Agent", "daemon-updater")
            .GET()
            .build()

        return try {
            val response = httpClient.send(request, HttpResponse.BodyHandlers.ofInputStream())

            if (response.statusCode() != 200) {
                System.err.println("Aviso: Falha ao buscar releases.atom: status ${response.statusCode()}")
                return Pair("dev", Instant.now())
            }

            parseAtomFeed(response.body(), isCurrentCanary)
        } catch (e: Exception) {
            System.err.println("Aviso: Erro de rede ao buscar releases (${e.message})")
            Pair("dev", Instant.now())
        }
    }

    private fun parseAtomFeed(inputStream: InputStream, isCurrentCanary: Boolean): Pair<String, Instant> {
        return try {
            val factory = DocumentBuilderFactory.newInstance()
            val builder = factory.newDocumentBuilder()
            val doc = builder.parse(inputStream)
            val entries = doc.getElementsByTagName("entry")

            val re = Regex("/releases/tag/(.+)$")

            for (i in 0 until entries.length) {
                val entry = entries.item(i) as Element
                val linkNode = entry.getElementsByTagName("link").item(0) as Element
                val href = linkNode.getAttribute("href")
                val updatedNode = entry.getElementsByTagName("updated").item(0)

                val match = re.find(href)
                if (match != null) {
                    val tag = match.groupValues[1]
                    val isPreRelease = tag.lowercase().contains("canary")

                    if (!isCurrentCanary && isPreRelease) {
                        continue
                    }

                    val remoteUpdated = try {
                        Instant.parse(updatedNode.textContent)
                    } catch (e: Exception) {
                        Instant.now() // Fallback
                    }

                    return Pair(tag, remoteUpdated)
                }
            }
            System.err.println("Aviso: Nenhuma release adequada encontrada no feed.")
            Pair("dev", Instant.now())
        } catch (e: Exception) {
            System.err.println("Aviso: Erro ao fazer parse do feed Atom (${e.message})")
            Pair("dev", Instant.now())
        }
    }

    fun checkForUpdates(): UpdateInfo {
        val (latestTag, remoteUpdated) = fetchLatestGitHubRelease()

        // Se a busca falhou e retornou o fallback "dev", garantimos que não tem update disponível
        if (latestTag == "dev") {
            return UpdateInfo("dev", "dev", false)
        }

        val latest = latestTag.removePrefix("v")
        val current = Version.VERSION.removePrefix("v")
        val isCurrentCanary = current.lowercase().contains("canary")

        var updateAvailable = false

        if (current == "dev") {
            updateAvailable = false
        } else if (isCurrentCanary) {
            val localTime = Version.BUILD_TIME.toLongOrNull() ?: 0L
            val remoteTime = remoteUpdated.epochSecond

            // Tolerância de 5 min (300 segs)
            if (remoteTime > (localTime + 300)) {
                updateAvailable = true
            }
        } else {
            if (latest != current && latest.isNotEmpty()) {
                updateAvailable = true
            }
        }

        return UpdateInfo(current, latest, updateAvailable)
    }

    fun update() {
        val (latestTag, remoteUpdated) = fetchLatestGitHubRelease()

        if (latestTag == "dev") {
            System.err.println("Aviso: Falha ao buscar informações da release. Abortando update.")
            return
        }

        val latest = latestTag.removePrefix("v")
        val current = Version.VERSION.removePrefix("v")
        val isCurrentCanary = current.lowercase().contains("canary")

        if (current == "dev") {
            System.err.println("Aviso: Não é possível atualizar a partir de uma build 'dev'")
            return
        }

        var needsUpdate = false
        if (isCurrentCanary) {
            val localTime = Version.BUILD_TIME.toLongOrNull() ?: 0L
            val remoteTime = remoteUpdated.epochSecond
            if (remoteTime > (localTime + 300)) {
                needsUpdate = true
            }
        } else {
            if (latest != current && latest.isNotEmpty()) {
                needsUpdate = true
            }
        }

        if (!needsUpdate) {
            val msg = if (isCurrentCanary) "(canary - build id match)" else "(v$current)"
            System.err.println("Aviso: Daemon já está atualizado $msg")
            return
        }

        // Identificar SO e Arquitetura para baixar o binário correto
        val osName = System.getProperty("os.name").lowercase()
        val goos = when {
            osName.contains("win") -> "windows"
            osName.contains("mac") -> "darwin"
            else -> "linux"
        }

        val osArch = System.getProperty("os.arch").lowercase()
        val goarch = if (osArch.contains("aarch64") || osArch.contains("arm64")) "arm64" else "amd64"

        val ext = if (goos == "windows") ".exe" else ""
        val fileName = "plume-$goos-$goarch$ext"

        val downloadUrl = "https://github.com/$GITHUB_REPO/releases/download/$latestTag/$fileName"

        // Descobrir o caminho do próprio executável (GraalVM Native Image)
        val exePathStr = ProcessHandle.current().info().command().orElse(null)
        if (exePathStr == null) {
            System.err.println("Aviso: Falha ao obter o caminho do executável")
            return
        }

        val exePath = Paths.get(exePathStr).toRealPath()

        try {
            val request = HttpRequest.newBuilder().uri(URI.create(downloadUrl)).GET().build()
            val response = httpClient.send(request, HttpResponse.BodyHandlers.ofInputStream())

            if (response.statusCode() != 200) {
                System.err.println("Aviso: Falha ao baixar update de $downloadUrl, status: ${response.statusCode()}")
                return
            }

            val newExePath = Paths.get("$exePath.new")
            val oldExePath = Paths.get("$exePath.old")

            try {
                // Salva o novo binário
                Files.copy(response.body(), newExePath, StandardCopyOption.REPLACE_EXISTING)

                // Permissão de execução no Linux/Mac
                if (goos != "windows") {
                    newExePath.toFile().setExecutable(true)
                }

                // Remove o .old se existir
                Files.deleteIfExists(oldExePath)

                // Move o atual para .old e o .new para o lugar do atual
                Files.move(exePath, oldExePath, StandardCopyOption.REPLACE_EXISTING)
                Files.move(newExePath, exePath, StandardCopyOption.REPLACE_EXISTING)

                // Tenta deletar o .old imediatamente
                try { Files.deleteIfExists(oldExePath) } catch (_: Exception) {}

                restartAfterUpdate(exePath.toString())

            } catch (e: Exception) {
                Files.deleteIfExists(newExePath)
                System.err.println("Aviso: Falha durante o processo de substituição da atualização: ${e.message}")
                return
            }
        } catch (e: Exception) {
            System.err.println("Aviso: Erro de conexão ao tentar baixar a atualização (${e.message})")
            return
        }
    }

    private fun restartAfterUpdate(exePath: String) {
        ProcessBuilder(exePath).start()
        exitProcess(0)
    }
}