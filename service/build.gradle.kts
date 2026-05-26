import java.net.URL

plugins {
    id("plugins.convention.kotlin-jvm")
    alias(libs.plugins.shadow)
    application
}

application {
    mainClass.set("Plume.Plume")
}

dependencies {
    implementation(project(":shared"))
    implementation(project(":http"))
}

// ==========================================
// CONFIGURAÇÃO DO SHADOW JAR (UBER JAR)
// ==========================================
tasks.shadowJar {
    mergeServiceFiles() // Essencial para o Ktor mesclar os arquivos de ServiceLoader corretamente
    archiveClassifier.set("all")
}

// ==========================================
// GERAÇÃO DE EXECUTÁVEIS NATIVOS (JPACKAGE + SFX SINGLE EXE)
// ==========================================

val optimizedJvmArgs = listOf(
    "-Xms32M",
    "-Xmx128M",
    "-XX:+UseSerialGC",
    "-XX:+TieredCompilation",
    "-XX:TieredStopAtLevel=1",
    "-XX:MaxMetaspaceSize=128M"
)
tasks.register<Exec>("packageNative") {
    group = "distribution"
    description = "Gera executável nativo único com JRE embutida usando WARP (Com Ícone e Sem Admin)"
    dependsOn("shadowJar")

    val shadowJarTask = tasks.shadowJar.get()
    val originalJarFile = shadowJarTask.archiveFile.get().asFile
    val jarName = shadowJarTask.archiveFileName.get()

    val jpackageInputDir = layout.buildDirectory.dir("jpackage-input").get().asFile

    val osName = System.getProperty("os.name").lowercase()
    val isWindows = osName.contains("win")
    val isMac = osName.contains("mac")
    val osType = if (isWindows) "windows" else if (isMac) "macos" else "linux"

    val arch = System.getProperty("os.arch").lowercase()
    val cleanArch = if (arch == "x86_64" || arch == "amd64") "amd64" else arch

    val outputDir = layout.buildDirectory.dir("builds/$osType-$cleanArch").get().asFile
    val appName = "plume-$osType-$cleanArch"

    val finalAppDir = if (isMac) File(outputDir, "$appName.app") else File(outputDir, appName)

    doFirst {
        if (finalAppDir.exists()) {
            val processBuilder = if (isWindows) {
                ProcessBuilder("cmd", "/c", "rmdir", "/s", "/q", finalAppDir.absolutePath)
            } else {
                ProcessBuilder("rm", "-rf", finalAppDir.absolutePath)
            }
            processBuilder.inheritIO().start().waitFor()
        }

        outputDir.mkdirs()
        jpackageInputDir.mkdirs()
        jpackageInputDir.listFiles()?.forEach { it.delete() }
        originalJarFile.copyTo(File(jpackageInputDir, jarName), overwrite = true)
    }

    val jpackageArgs = mutableListOf(
        "jpackage",
        "--type", "app-image",
        "--name", appName,
        "--input", jpackageInputDir.absolutePath,
        "--main-jar", jarName,
        "--main-class", "Plume.Plume",
        "--dest", outputDir.absolutePath
    )

    if (isWindows) {
        jpackageArgs.add("--win-console")
    }

    optimizedJvmArgs.forEach { arg ->
        jpackageArgs.add("--java-options")
        jpackageArgs.add(arg)
    }

    val iconFile = layout.projectDirectory.file("src/resources/" + if (isWindows) "icon.ico" else "icon.png").asFile

    if (iconFile.exists()) {
        jpackageArgs.add("--icon")
        jpackageArgs.add(iconFile.absolutePath)
    }

    commandLine(jpackageArgs)

    doLast {
        println(iconFile.path)
        println("=== Empacotando com WARP ===")

        val warpVersion = "v0.3.0"
        val warpOs = if (isWindows) "windows-x64" else if (isMac) "macos-x64" else "linux-x64"
        val warpExeName = if (isWindows) "warp-packer.exe" else "warp-packer"
        val warpTool = File(layout.buildDirectory.asFile.get(), warpExeName)

        if (!warpTool.exists()) {
            println("📥 Baixando warp-packer ($warpOs)...")
            val warpDownloadUrl = "https://github.com/dgiagio/warp/releases/download/$warpVersion/$warpOs.warp-packer${if (isWindows) ".exe" else ""}"
            warpTool.writeBytes(URL(warpDownloadUrl).readBytes())
            warpTool.setExecutable(true)
        }

        // --- MÁGICA PARA WINDOWS: ÍCONE E REMOÇÃO DE PRIVILÉGIO ADMINISTRADOR ---
        if (isWindows) {
            val rceditTool = File(layout.buildDirectory.asFile.get(), "rcedit-x64.exe")
            if (!rceditTool.exists()) {
                println("📥 Baixando rcedit para injetar o ícone...")
                rceditTool.writeBytes(URL("https://github.com/electron/rcedit/releases/download/v2.0.0/rcedit-x64.exe").readBytes())
            }

            // Cria o manifesto forçando 'asInvoker' (Sem Admin)
            val manifestFile = File(layout.buildDirectory.asFile.get(), "manifest.xml")
            manifestFile.writeText(
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
                  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
                    <security>
                      <requestedPrivileges>
                        <requestedExecutionLevel level="asInvoker" uiAccess="false"/>
                      </requestedPrivileges>
                    </security>
                  </trustInfo>
                </assembly>
                """.trimIndent()
            )

            println("🔧 Injetando ícone e manifesto no Warp Runner...")
            val rceditArgs = mutableListOf(rceditTool.absolutePath, warpTool.absolutePath, "--application-manifest", manifestFile.absolutePath)

            if (iconFile.exists()) {
                rceditArgs.add("--set-icon")
                rceditArgs.add(iconFile.absolutePath)
            }

            ProcessBuilder(rceditArgs).start().waitFor()
            manifestFile.delete()
        }
        // ------------------------------------------------------------------------

        val execInsideDir = when {
            isWindows -> "$appName.exe"
            isMac -> "Contents/MacOS/$appName"
            else -> "bin/$appName"
        }

        val finalStandalone = File(outputDir, if (isWindows) "Plume-Standalone.exe" else "Plume-Standalone")
        if (finalStandalone.exists()) finalStandalone.delete()

        val warpArgs = listOf(
            warpTool.absolutePath,
            "--arch", warpOs,
            "--input_dir", finalAppDir.absolutePath,
            "--exec", execInsideDir,
            "--output", finalStandalone.absolutePath
        )

        try {
            val exitCode = ProcessBuilder(warpArgs).inheritIO().start().waitFor()
            if (exitCode == 0) {
                if (isWindows) ProcessBuilder("cmd", "/c", "rmdir", "/s", "/q", finalAppDir.absolutePath).start()
                else ProcessBuilder("rm", "-rf", finalAppDir.absolutePath).start()

                println("✨ VITÓRIA! Arquivo ÚNICO (Sem pedir admin e com seu ícone):")
                println("👉 ${finalStandalone.absolutePath}")
            } else {
                throw GradleException("Warp falhou com código $exitCode")
            }
        } catch (e: Exception) {
            throw GradleException("❌ Erro ao rodar o Warp: ${e.message}")
        }
    }
}