package Plume

import Plume.Configuration.ConfigManager
import Plume.Database.DatabaseManager
import Plume.Http.Http
import Plume.Server.Extending.InternalSftpServer
import Plume.Server.Manager
import Plume.Events.LiveBus

object Plume {

    /**
     * O ponto de entrada padrão da aplicação compilada.
     * Mantido dentro de um object real sem funções "soltas" no arquivo.
     */
    @JvmStatic
    fun main(args: Array<String>) {
        CrashHandler.install()

        val RESET = "\u001B[0m"
        val CYAN = "\u001B[36m"
        val WHITE = "\u001B[37m"

        val art = """
$CYAN
   ___  __   __  ____  _______
  / _ \/ /  / / / /  |/  / __/
 / ___/ /__/ /_/ / /|_/ / _/  
/_/  /____/\____/_/  /_/___/  
$WHITE
Copyright © 2026 - MurilloFz

Este software é disponibilizado sob os termos da Licença MIT.
A notificação de direitos autorais acima, bem como este aviso de permissão, devem ser
incluídos em todas as cópias ou partes substanciais deste software.
$RESET
""".trimIndent()

        println(art)
        try {
            Logger.init(this::class.java)
            LiveBus()
            ConfigManager.loadConfig(Logger.logger)
            DatabaseManager.initDB(ConfigManager.globalConfig.app.path + "/db.json")

            Manager.init()
            InternalSftpServer.start()
            Http(Logger.logger).start()
        } catch (e: Throwable) {
            e.printStackTrace()
        }

    }
}