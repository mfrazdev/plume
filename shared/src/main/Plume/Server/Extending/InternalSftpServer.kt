package Plume.Server.Extending

import Plume.Configuration.ConfigManager
import Plume.Logger
import Plume.Server.Manager
import org.apache.sshd.common.AttributeRepository
import org.apache.sshd.common.file.virtualfs.VirtualFileSystemFactory
import org.apache.sshd.common.session.SessionContext
import org.apache.sshd.server.SshServer
import org.apache.sshd.server.auth.password.PasswordAuthenticator
import org.apache.sshd.server.keyprovider.SimpleGeneratorHostKeyProvider
import org.apache.sshd.server.session.ServerSession
import org.apache.sshd.sftp.server.AbstractSftpEventListenerAdapter
import org.apache.sshd.sftp.server.FileHandle
import org.apache.sshd.sftp.server.Handle
import org.apache.sshd.sftp.server.SftpSubsystemFactory
import java.io.IOException
import java.nio.file.CopyOption
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import kotlin.io.path.exists
import kotlin.io.path.fileSize

object InternalSftpServer {
    val SERVER_ID_KEY = AttributeRepository.AttributeKey<String>()
    val QUOTA_BYTES_KEY = AttributeRepository.AttributeKey<Long>()
    val USED_BYTES_KEY = AttributeRepository.AttributeKey<Long>()



    fun start() {
        System.setProperty("org.apache.sshd.security.provider.BC.enabled", "false")
        System.setProperty("org.apache.sshd.security.provider.EdDSA.enabled", "false")
        System.setProperty("org.apache.sshd.registerBouncyCastle", "false")

        val basePath = Paths.get(ConfigManager.globalConfig.app.path, "servers").normalize()
        val keyPath = Paths.get(ConfigManager.globalConfig.app.path, "sftp_host_key.pem").normalize()

        val sshd = SshServer.setUpDefaultServer()
        sshd.port = ConfigManager.globalConfig.sftp.port

        sshd.keyPairProvider = SimpleGeneratorHostKeyProvider(keyPath)

        sshd.passwordAuthenticator = PasswordAuthenticator { username, password, session ->
            val parts = username.split("_")
            if (parts.size < 2) return@PasswordAuthenticator false

            val serverId = parts.last()
            val actualUsername = parts.dropLast(1).joinToString("_")

            if (!ConfigManager.validateServerID(serverId)) return@PasswordAuthenticator false
            if (actualUsername.isEmpty() || password.isEmpty()) return@PasswordAuthenticator false

            if (Manager.getByShortId(serverId) == null) return@PasswordAuthenticator false

            val (isValid, quotaDouble) = ConfigManager.verifySFTP(actualUsername, password, serverId)

            if (isValid) {
                session.setAttribute(SERVER_ID_KEY, serverId)
                session.setAttribute(QUOTA_BYTES_KEY, (quotaDouble * 1024 * 1024).toLong())
                return@PasswordAuthenticator true
            }
            return@PasswordAuthenticator false
        }

        sshd.keyboardInteractiveAuthenticator = null

        sshd.fileSystemFactory = object : VirtualFileSystemFactory() {
            @Throws(IOException::class)
            override fun getUserHomeDir(session: SessionContext): Path {
                val serverShortId = session.getAttribute(SERVER_ID_KEY)
                val server = Manager.getByShortId(serverShortId)
                    ?: throw IOException("Servidor não encontrado para ID: $serverShortId")

                val path = basePath.resolve(server.id).normalize()
                val file = path.toFile()
                if (!file.exists()) {
                    file.mkdirs()
                }
                return path
            }
        }

        val sftpBuilder = SftpSubsystemFactory.Builder()
        // Passamos o basePath para o listener conseguir achar a pasta sem depender do objeto de sessão do SSHD
        sftpBuilder.addSftpEventListener(QuotaEventListener(basePath))
        sshd.subsystemFactories = listOf(sftpBuilder.build())

        sshd.start()
        Logger.logger.info("Servidor SFTP ouvindo na porta ${sshd.port}.")
    }
}

class QuotaEventListener(private val globalBasePath: Path) : AbstractSftpEventListenerAdapter() {
    private val lock = Any()

    // Inicialização CORRIGIDA: Localiza a pasta real usando o shortId e o Manager
    override fun initialized(session: ServerSession, version: Int) {
        val serverShortId = session.getAttribute(InternalSftpServer.SERVER_ID_KEY) ?: return
        val server = Manager.getByShortId(serverShortId) ?: return
        val serverDir = globalBasePath.resolve(server.id).normalize()

        if (!Files.exists(serverDir)) {
            session.setAttribute(InternalSftpServer.USED_BYTES_KEY, 0L)
            return
        }

        // Caminhada ultra rápida pelo disco usando Streams nativos
        val size = try {
            Files.walk(serverDir).use { stream ->
                stream.filter { Files.isRegularFile(it) }
                    .mapToLong { Files.size(it) }
                    .sum()
            }
        } catch (_: Exception) {
            0L
        }
        session.setAttribute(InternalSftpServer.USED_BYTES_KEY, size)
    }

    override fun writing(
        session: ServerSession, remoteHandle: String, localHandle: FileHandle,
        offset: Long, data: ByteArray, dataOffset: Int, dataLen: Int
    ) {
        val quota = session.getAttribute(InternalSftpServer.QUOTA_BYTES_KEY) ?: 0L
        if (quota <= 0L) return

        synchronized(lock) {
            val currentUsed = session.getAttribute(InternalSftpServer.USED_BYTES_KEY) ?: 0L
            val currentFileSize = if (localHandle.file.exists()) localHandle.file.toFile().length() else 0L

            val endPos = offset + dataLen
            val diff = if (endPos > currentFileSize) endPos - currentFileSize else 0L

            if (diff > 0L) {
                if (currentUsed + diff > quota) {
                    throw IOException("Cota de disco excedida (limite: $quota bytes)")
                }
                session.setAttribute(InternalSftpServer.USED_BYTES_KEY, currentUsed + diff)
            }
        }
    }

    override fun removing(session: ServerSession, remotePath: Path, isDirectory: Boolean) {
        if (!isDirectory && remotePath.exists()) {
            val fileSize = remotePath.fileSize()
            synchronized(lock) {
                val currentUsed = session.getAttribute(InternalSftpServer.USED_BYTES_KEY) ?: 0L
                val newUsed = (currentUsed - fileSize).coerceAtLeast(0L)
                session.setAttribute(InternalSftpServer.USED_BYTES_KEY, newUsed)
            }
        }
    }

    override fun moved(session: ServerSession, srcPath: Path, dstPath: Path, opts: Collection<CopyOption>, thrown: Throwable?) {
        // Nada de I/O pesado aqui
    }

    override fun closed(session: ServerSession, remoteHandle: String, localHandle: Handle, thrown: Throwable?) {
        // Nada de I/O pesado aqui
    }
}