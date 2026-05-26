package Plume.Configuration

import com.charleskorn.kaml.Yaml
import io.ktor.client.*
import io.ktor.client.call.*
import io.ktor.client.engine.cio.*
import io.ktor.client.plugins.contentnegotiation.*
import io.ktor.client.request.*
import io.ktor.client.statement.*
import io.ktor.http.*
import io.ktor.serialization.kotlinx.json.*
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.* // <-- Import atualizado para incluir JsonObject, JsonElement, etc.
import org.slf4j.Logger
import java.io.File
import java.security.cert.X509Certificate
import java.util.concurrent.locks.ReentrantReadWriteLock
import javax.net.ssl.X509TrustManager
import kotlin.concurrent.read
import kotlin.concurrent.write

// Estruturas de configuração mapeando o novo YAML organizado
@Serializable
data class AppSection(val id: String, val port: Int, val path: String)

@Serializable
data class SftpSection(val port: Int)

@Serializable
data class SslSection(val enabled: Boolean, val certPath: String, val keyPath: String)

@Serializable
data class RemoteSection(val url: String, val token: String)

@Serializable
data class AppConfig(
    val app: AppSection,
    val sftp: SftpSection,
    val ssl: SslSection,
    val remote: RemoteSection
)

// --- FUNÇÕES DE EXTENSÃO PARA RESOLVER O PROBLEMA DO MAP<STRING, ANY?> ---
private fun Any?.toJsonElement(): JsonElement = when (this) {
    null -> JsonNull
    is JsonElement -> this
    is String -> JsonPrimitive(this)
    is Number -> JsonPrimitive(this)
    is Boolean -> JsonPrimitive(this)
    is Iterable<*> -> JsonArray(this.map { it.toJsonElement() })
    is Map<*, *> -> JsonObject(this.entries.associate { it.key.toString() to it.value.toJsonElement() })
    else -> JsonPrimitive(this.toString())
}

private fun JsonElement.toAnyValue(): Any? = when (this) {
    is JsonNull -> null
    is JsonPrimitive -> {
        if (this.isString) {
            this.content
        } else {
            this.booleanOrNull ?: this.intOrNull ?: this.longOrNull ?: this.doubleOrNull
        }
    }
    is JsonArray -> this.map { it.toAnyValue() }
    is JsonObject -> this.mapValues { it.value.toAnyValue() }
}
// -------------------------------------------------------------------------

object ConfigManager {
    private val lock = ReentrantReadWriteLock()

    lateinit var globalConfig: AppConfig
        private set

    lateinit var httpClient: HttpClient
        private set

    fun loadConfig(logger: Logger) {
        lock.write {
            var configFile = File("config.yml")
            if (!configFile.exists()) {
                // Fallback para o comportamento do build SEA do Go
                configFile = File("/etc/plume/config.yml")
                if (!configFile.exists()) {
                    logger.error("Não foi possível encontrar a configuração do Plume.\nCertifique-se de que tenha configurado o config.yml corretamente.")
                    throw RuntimeException("config.yml missing")
                }
            }

            try {
                val content = configFile.readText()
                globalConfig = Yaml.default.decodeFromString(AppConfig.serializer(), content)
                logger.info("Configurações carregadas com sucesso de ${configFile.absolutePath}")
            } catch (e: Exception) {
                logger.error("Ocorreu um erro ao transformar os dados do config.yml para ser legível ao sistema.")
                throw e
            }

            // Inicializa o cliente HTTP (Ktor) com regras de TLS equivalentes ao Go
            val isDevMode = System.getenv("PLUME_DEV_MODE") == "1"

            httpClient = HttpClient(CIO) {
                install(ContentNegotiation) {
                    json(Json {
                        ignoreUnknownKeys = true
                        prettyPrint = true
                    })
                }

                engine {
                    https {
                        if (isDevMode) {
                            // Equivalente ao InsecureSkipVerify: true (Aceita auto-assinado em Dev)
                            trustManager = object : X509TrustManager {
                                override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {}
                                override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {}
                                override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf()
                            }
                        }
                    }
                }
            }
        }
    }

    // Comunicação com o painel em PHP (Usando runBlocking para manter a assinatura síncrona do Go se necessário)
    fun remoteAPI(endpoint: String, payload: Map<String, Any?>): Map<String, Any?> = runBlocking {
        val (remoteURL, token) = lock.read {
            globalConfig.remote.url + "/api/nodes/helper" + endpoint to globalConfig.remote.token
        }

        // Garante que o payload inclua o token se já não foi passado explicitamente
        val finalPayload = payload.toMutableMap().apply {
            if (!containsKey("token")) put("token", token)
        }

        try {
            // Converte o payload de Map para JsonObject (suportado nativamente pelo Ktor)
            val jsonPayload = finalPayload.toJsonElement() as JsonObject

            val response: HttpResponse = httpClient.post(remoteURL) {
                contentType(ContentType.Application.Json)
                setBody(jsonPayload)
            }

            if (response.status.value >= 400) {
                throw RuntimeException("remote API error: status ${response.status.value}")
            }

            // Deserializa como JsonObject e converte de volta para seu Map dinâmico
            val responseJson = response.body<JsonObject>()

            @Suppress("UNCHECKED_CAST")
            return@runBlocking responseJson.toAnyValue() as Map<String, Any?>
        } catch (e: Exception) {
            throw e
        }
    }

    // Validações estritas de Input
    fun validateServerID(serverId: String): Boolean {
        if (serverId.isEmpty() || serverId.length > 255) return false
        return serverId.all { it in 'a'..'z' || it in 'A'..'Z' || it in '0'..'9' || it == '-' || it == '_' }
    }

    fun validateUUID(id: String): Boolean = validateServerID(id) // Mesma regra do Go

    fun verifySFTP(userName: String, password: Any, serverId: String): Pair<Boolean, Double> {
        if (!validateServerID(serverId) || userName.isEmpty() || password.toString().isEmpty()) {
            return false to 0.0
        }

        return try {
            val res = remoteAPI("/verify-sftp", mapOf(
                "userName" to userName,
                "password" to password,
                "serverUuid" to serverId
            ))
            val perm = res["permission"] as? Boolean ?: false
            val disk = (res["disk"] as? Number)?.toDouble() ?: 0.0
            perm to disk
        } catch (e: Exception) {
            false to 0.0
        }
    }

    fun userIsAdmin(userUUID: String): Boolean {
        if (!validateUUID(userUUID)) return false
        return try {
            val res = remoteAPI("/admin-permission", mapOf("userUuid" to userUUID))
            res["isAdmin"] as? Boolean ?: false
        } catch (e: Exception) {
            e.printStackTrace()
            false
        }
    }

    fun hasPermission(userUUID: String, serverId: String): Boolean {
        if (!validateUUID(userUUID) || !validateServerID(serverId)) return false
        return try {
            val res = remoteAPI("/permission", mapOf(
                "userUuid" to userUUID,
                "serverUuid" to serverId
            ))
            res["permission"] as? Boolean ?: false
        } catch (e: Exception) {
            false
        }
    }
}