package Plume.Http

import Plume.Configuration.ConfigManager
import io.ktor.http.*
import io.ktor.serialization.kotlinx.KotlinxWebsocketSerializationConverter
import io.ktor.serialization.kotlinx.json.*
import io.ktor.server.application.*
import io.ktor.server.engine.*
import io.ktor.server.netty.* // Alterado de CIO para Netty
import io.ktor.server.plugins.BadRequestException
import io.ktor.server.plugins.contentnegotiation.*
import io.ktor.server.plugins.cors.routing.CORS
import io.ktor.server.plugins.doublereceive.*
import io.ktor.server.plugins.origin
import io.ktor.server.plugins.ratelimit.RateLimit
import io.ktor.server.plugins.ratelimit.RateLimiter
import io.ktor.server.plugins.statuspages.*
import io.ktor.server.request.*
import io.ktor.server.response.*
import io.ktor.server.routing.*
import io.ktor.server.websocket.WebSockets
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import org.slf4j.Logger
import java.io.File
import java.security.KeyStore
import kotlin.system.exitProcess
import kotlin.time.Duration.Companion.minutes

class Http(
    val logger: Logger
) {

    /**
     * Inicializa e configura a execução do motor do Ktor.
     */
    fun start() {
        logger.info("Iniciando o servidor Lunar Plume na porta ${ConfigManager.globalConfig.app.port}...")

        // Alterado o motor de CIO para Netty para suportar HTTPS
        embeddedServer(Netty, configure = {
            // Lógica Exclusiva: Ou abre HTTPS ou HTTP, sempre na porta do config.
            if (ConfigManager.globalConfig.ssl.enabled) {

                try {
                    val dummyPassword = "plume_internal".toCharArray()
                    val keyStore = loadKeyStoreFromPem(
                        certPath = ConfigManager.globalConfig.ssl.certPath,
                        keyPath = ConfigManager.globalConfig.ssl.keyPath
                    )

                    sslConnector(
                        keyStore = keyStore,
                        keyAlias = "alias",
                        keyStorePassword = { dummyPassword },
                        privateKeyPassword = { dummyPassword }
                    ) {
                        host = "0.0.0.0"
                        // Pode trocar essa porta se quiser, mas mantive a padrão se for a sua preferência
                        port = ConfigManager.globalConfig.app.port
                    }
                } catch (e: Exception) {
                    logger.error("Falha fatal ao iniciar o SSL. Verifique os arquivos .pem e .key.", e)
                    exitProcess(1)
                }
            } else {


                // Conector HTTP Padrão (só sobe se SSL for false)
                connector {
                    host = "0.0.0.0"
                    port = ConfigManager.globalConfig.app.port
                }
            }
        }) {
            install(DoubleReceive)
            install(WebSockets) {
                contentConverter = KotlinxWebsocketSerializationConverter(Json {
                    prettyPrint = false
                    isLenient = true
                    ignoreUnknownKeys = true
                })
            }
            configureContentNegotiation(this)
            configureStatusPages(this)

            install(CORS) {
                val remoteUrl = ConfigManager.globalConfig.remote.url
                if (remoteUrl.isNotBlank()) {
                    allowHost(remoteUrl.removePrefix("http://").removePrefix("https://").substringBefore("/"))
                } else {
                    anyHost()
                }

                allowMethod(HttpMethod.Get)
                allowMethod(HttpMethod.Post)
                allowMethod(HttpMethod.Put)
                allowMethod(HttpMethod.Delete)
                allowMethod(HttpMethod.Options)

                allowHeader(HttpHeaders.Authorization)
                allowHeader(HttpHeaders.ContentType)

                allowCredentials = true
            }
            configureSecurity(this)
            configureRateLimit(this)
            configureRouting(this)
        }.start(wait = true)
    }

    /**
     * Carrega o .pem e o .key direto pra memória e cospe um KeyStore virtual.
     * Fim da frescura de arquivo .p12!
     */
    private fun loadKeyStoreFromPem(certPath: String, keyPath: String): KeyStore {
        val certFile = File(certPath)
        val keyFile = File(keyPath)

        if (!certFile.exists() || !keyFile.exists()) {
            throw IllegalArgumentException("Arquivos SSL não encontrados em: cert=$certPath, key=$keyPath")
        }

        // 1. Carregar Certificado (X.509)
        val certFactory = java.security.cert.CertificateFactory.getInstance("X.509")
        val certs = certFile.inputStream().use { stream ->
            certFactory.generateCertificates(stream).toList()
        }

        // 2. Limpar e Decodificar a Chave Privada (.key)
        val keyContent = keyFile.readText()
            .replace("-----BEGIN PRIVATE KEY-----", "")
            .replace("-----END PRIVATE KEY-----", "")
            .replace("-----BEGIN RSA PRIVATE KEY-----", "")
            .replace("-----END RSA PRIVATE KEY-----", "")
            .replace("-----BEGIN EC PRIVATE KEY-----", "")
            .replace("-----END EC PRIVATE KEY-----", "")
            .replace(Regex("\\s+"), "")

        val keyBytes = java.util.Base64.getDecoder().decode(keyContent)

        // 3. Descobrir o algoritmo da chave (Tenta RSA, se falhar tenta Elliptic Curve)
        val privateKey = try {
            val keySpec = java.security.spec.PKCS8EncodedKeySpec(keyBytes)
            java.security.KeyFactory.getInstance("RSA").generatePrivate(keySpec)
        } catch (e: Exception) {
            try {
                val keySpec = java.security.spec.PKCS8EncodedKeySpec(keyBytes)
                java.security.KeyFactory.getInstance("EC").generatePrivate(keySpec)
            } catch (_: Exception) {
                throw RuntimeException("Falha ao parsear a chave privada. Certifique-se que está no formato PKCS#8 sem senha.", e)
            }
        }

        // 4. Injeta tudo na memória (Virtual KeyStore)
        val dummyPassword = "plume_internal".toCharArray()
        return KeyStore.getInstance(KeyStore.getDefaultType()).apply {
            load(null, null)
            setKeyEntry("alias", privateKey, dummyPassword, certs.toTypedArray())
        }
    }


    private fun configureContentNegotiation(app: Application) {
        app.install(ContentNegotiation) {
            json(Json {
                prettyPrint = false // OTIMIZAÇÃO: Sem quebras de linha desnecessárias, o motor sobbe e responde voando
                isLenient = true
                ignoreUnknownKeys = true
            })
        }
    }

    private fun configureSecurity(app: Application) {
        app.intercept(ApplicationCallPipeline.Plugins) {

            if (call.request.httpMethod == HttpMethod.Options) return@intercept

            val path = call.request.path()
            val method = call.request.httpMethod
            val contentType = call.request.contentType()

            val isWebSocketUpgrade = call.request.headers[HttpHeaders.Upgrade]?.equals("websocket", ignoreCase = true) == true
            if (isWebSocketUpgrade || path.startsWith("/ws") || path.contains("filemanager") || path.contains("/servers/usages")) return@intercept

            if (
                method == HttpMethod.Post &&
                contentType.match(ContentType.MultiPart.FormData)
            ) return@intercept

            val authHeader = call.request.headers["Authorization"]
            var bodyToken: String? = null

            // SEU JEITO CLÁSSICO MANTIDO FIELMENTE AQUI:
            if (authHeader == null && (method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Patch)) {
                val bodyText = try {
                    call.receiveText()
                } catch (_: Exception) {
                    null
                }

                if (bodyText != null && bodyText.contains("token")) {
                    bodyToken = Regex("\"token\"\\s*:\\s*\"(.*?)\"")
                        .find(bodyText)
                        ?.groupValues
                        ?.get(1)
                }
            }

            val expected = "Bearer ${ConfigManager.globalConfig.remote.token}"
            val isValid = authHeader == expected ||
                    bodyToken == ConfigManager.globalConfig.remote.token

            if (!isValid) {
                logger.warn(
                    "Tentativa de acesso não autorizado de IP: ${
                        call.request.origin.remoteHost
                    }, authHeader: $authHeader, bodyToken: $bodyToken"
                )

                call.respond(
                    HttpStatusCode.Forbidden,
                    Responses.ErrorResponse(
                        status = HttpStatusCode.Forbidden.value,
                        error = "Forbidden",
                        message = "Invalid token"
                    )
                )

                finish()
            }
        }
    }

    private fun configureStatusPages(app: Application) {
        app.install(StatusPages) {
            // Ignora ou trata de forma limpa cancelamentos normais de requisição/rede
            exception<CancellationException> { _, _ ->
                // Não faz nada ou apenas dá um logger.debug, pois o cliente só fechou a conexão
            }

            // Retorna respostas em JSON elegantes para qualquer exceção inesperada REAL
            exception<Throwable> { call, cause ->
                logger.error("Erro interno detectado", cause)
                call.respond(
                    HttpStatusCode.InternalServerError,
                    Responses.ErrorResponse(
                        status = HttpStatusCode.InternalServerError.value,
                        error = "Internal Server Error",
                        message = cause.localizedMessage ?: "Ocorreu um erro inesperado."
                    )
                )
            }

            // Erro HTTP 400 em formato JSON estruturado
            exception<BadRequestException> { call, cause ->
                call.respond(
                    HttpStatusCode.BadRequest,
                    Responses.ErrorResponse(
                        status = HttpStatusCode.BadRequest.value,
                        error = "Bad Request",
                        message = cause.localizedMessage ?: "Corpo da requisição inválido."
                    )
                )
            }
        }
    }

    private fun configureRouting(app: Application) {
        app.routing {
            route("/stop") {
                get {
                    exitProcess(0)
                }
            }
            route("/api/v1") {
                RouteRegistry().registerAll(this)
            }
        }
    }

    private fun configureRateLimit(app: Application) {
        app.install(RateLimit) {
            global {
                requestKey { call ->
                    call.request.origin.remoteHost
                }

                rateLimiter(
                    limit = 100,
                    refillPeriod = 1.minutes
                )

                modifyResponse { call, state ->
                    if (state is RateLimiter.State.Exhausted) {
                        runBlocking {
                            call.respond(
                                HttpStatusCode.TooManyRequests,
                                Responses.ErrorResponse(
                                    status = 429,
                                    error = "Too Many Requests",
                                    message = "Rate limit exceeded"
                                )
                            )
                        }
                    }
                }
            }
        }
    }
}