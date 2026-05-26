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
    description = "Gera executável nativo único com JRE embutida (Warp para Win, SFX para Unix)"
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
        val finalStandalone = File(outputDir, if (isWindows) "Plume-Standalone.exe" else "Plume-Standalone")
        if (finalStandalone.exists()) finalStandalone.delete()

        if (isWindows) {
            println("=== Empacotando com WARP (Windows) ===")
            val warpVersion = "v0.3.0"
            val warpTool = File(layout.buildDirectory.asFile.get(), "warp-packer.exe")

            if (!warpTool.exists()) {
                warpTool.writeBytes(URL("https://github.com/dgiagio/warp/releases/download/$warpVersion/windows-x64.warp-packer.exe").readBytes())
                warpTool.setExecutable(true)
            }

            val rceditTool = File(layout.buildDirectory.asFile.get(), "rcedit-x64.exe")
            if (!rceditTool.exists()) {
                rceditTool.writeBytes(URL("https://github.com/electron/rcedit/releases/download/v2.0.0/rcedit-x64.exe").readBytes())
            }

            val manifestFile = File(layout.buildDirectory.asFile.get(), "manifest.xml")
            manifestFile.writeText("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0"><trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="asInvoker" uiAccess="false"/></requestedPrivileges></security></trustInfo></assembly>""")

            val rceditArgs = mutableListOf(rceditTool.absolutePath, warpTool.absolutePath, "--application-manifest", manifestFile.absolutePath)
            if (iconFile.exists()) { rceditArgs.add("--set-icon"); rceditArgs.add(iconFile.absolutePath) }

            ProcessBuilder(rceditArgs).start().waitFor()
            manifestFile.delete()

            val warpArgs = listOf(warpTool.absolutePath, "--arch", "windows-x64", "--input_dir", finalAppDir.absolutePath, "--exec", "$appName.exe", "--output", finalStandalone.absolutePath)

            if (ProcessBuilder(warpArgs).inheritIO().start().waitFor() == 0) {
                ProcessBuilder("cmd", "/c", "rmdir", "/s", "/q", finalAppDir.absolutePath).start()
                println("✨ VITÓRIA! Arquivo .exe único gerado em: ${finalStandalone.absolutePath}")
            }
        } else {
            println("=== Empacotando com BASH SFX (Linux/macOS - $cleanArch) ===")

            val archiveTar = File(outputDir, "temp_archive.tar.gz")
            ProcessBuilder("tar", "-czf", archiveTar.absolutePath, "-C", outputDir.absolutePath, finalAppDir.name).start().waitFor()

            if (archiveTar.exists()) {
                val scriptContent = """
                #!/bin/bash
                TMPDIR=${'$'}(mktemp -d /tmp/plume.XXXXXX)
                ARCHIVE=`awk '/^__ARCHIVE_BELOW__/ {print NR + 1; exit 0; }' "${'$'}0"`
                tail -n+${'$'}ARCHIVE "${'$'}0" | tar xz -C "${'$'}TMPDIR"
                
                if [ -d "${'$'}TMPDIR/$appName.app" ]; then
                    "${'$'}TMPDIR/$appName.app/Contents/MacOS/$appName"
                else
                    "${'$'}TMPDIR/$appName/bin/$appName"
                fi
                
                EXIT_CODE=${'$'}?
                rm -rf "${'$'}TMPDIR"
                exit ${'$'}EXIT_CODE
                __ARCHIVE_BELOW__
                """.trimIndent() + "\n"

                finalStandalone.outputStream().use { output ->
                    output.write(scriptContent.toByteArray(Charsets.UTF_8))
                    output.write(archiveTar.readBytes())
                }

                ProcessBuilder("chmod", "+x", finalStandalone.absolutePath).start().waitFor()
                archiveTar.delete()
                ProcessBuilder("rm", "-rf", finalAppDir.absolutePath).start()

                println("✨ VITÓRIA! Binário único Unix gerado em: ${finalStandalone.absolutePath}")
            }
        }
    }
}