package Plume.Http.Routes.Websocket

import Plume.Configuration.ConfigManager
import Plume.Server.Server
import java.lang.StringBuilder
import java.util.concurrent.ConcurrentHashMap

object WsCommon {
    // --- CACHE DE PERMISSÃO ---
    private val permCache = ConcurrentHashMap<String, Long>()
    private const val TTL_MS = 5 * 60 * 1000L // 5 minutos de cache

    fun checkPermissionCached(userUuid: String, srv: Server): Boolean {
        val cacheKey = "$userUuid:${srv.id}"
        val now = System.currentTimeMillis()

        // Verifica se tá no cache e não expirou
        val expiry = permCache[cacheKey]
        if (expiry != null && now < expiry) {
            return true
        }

        // Lembre de alterar 'hasPermission' pro método exato que você usa na sua ConfigManager
        val hasPerm = ConfigManager.hasPermission(userUuid, srv.id)
        if (hasPerm) {
            permCache[cacheKey] = now + TTL_MS
        }

        return hasPerm
    }

    // --- CORES E GRADIENTE DO CONSOLE ---
    const val PrefixLabel = "container@plume ❯"
    const val PrefixLabel2 = "[Plume Daemon]  ❯" // Espaços adicionados para alinhar perfeitamente com o de cima
    const val ResetColor = "\u001B[0m"
    const val Bold = "\u001B[1m"

    fun gradient(text: String): String {
        // Tons mais frios e modernos para painéis (Light Blue -> Indigo -> Purple)
        // Se quiser o rosa forte de antes: start(168,85,247), middle(217,70,239), end(236,72,153)
        val start = intArrayOf(56, 189, 248)
        val middle = intArrayOf(129, 140, 248)
        val end = intArrayOf(192, 132, 252)

        val result = StringBuilder()
        val length = text.length

        // Aplica negrito no prefixo para dar destaque
        result.append(Bold)

        for (i in text.indices) {
            val t = i.toDouble() / (length - 1).coerceAtLeast(1).toDouble()
            val r: Int; val g: Int; val b: Int

            if (t < 0.5) {
                val t2 = t * 2
                r = (start[0] * (1 - t2) + middle[0] * t2).toInt()
                g = (start[1] * (1 - t2) + middle[1] * t2).toInt()
                b = (start[2] * (1 - t2) + middle[2] * t2).toInt()
            } else {
                val t2 = (t - 0.5) * 2
                r = (middle[0] * (1 - t2) + end[0] * t2).toInt()
                g = (middle[1] * (1 - t2) + end[1] * t2).toInt()
                b = (middle[2] * (1 - t2) + end[2] * t2).toInt()
            }
            result.append("\u001B[38;2;$r;$g;${b}m${text[i]}")
        }
        return result.toString() + ResetColor
    }
    fun getColorForCategory(category: String): String {
        return when (category) {
            "error" -> "\u001B[38;2;255;107;129m"  // Vermelho/Rosa Pastel (destaca o erro, mas na mesma vibe neon)
            "warn" -> "\u001B[38;2;253;224;71m"   // Amarelo Pastel Claro
            "info" -> "\u001B[38;2;186;230;253m"  // Azul Gelo (combina com o início do seu gradiente)
            "status" -> "\u001B[38;2;199;210;254m" // Lavanda Super Claro (substitui o cinza, brilhante no fundo escuro e combina com o final do gradiente)
            else -> "\u001B[0m"
        }
    }
}