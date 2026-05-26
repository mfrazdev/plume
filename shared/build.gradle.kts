plugins {
    // Apply the shared build logic from a convention plugin.
    // The shared code is located in `plugins/src/main/kotlin/kotlin-jvm.gradle.kts`.
    id("plugins.convention.kotlin-jvm")
    // Apply the Application plugin to add support for building an executable JVM application.
    application
}

val generateVersionInfo by tasks.registering {
    val outputDir = layout.buildDirectory.dir("generated/source/version/main/Plume/Version")
    outputs.dir(outputDir)

    doFirst {
        val dir = outputDir.get().asFile
        dir.mkdirs()

        val ver = System.getenv("TAG_NAME") ?: "dev"
        val commit = System.getenv("GITHUB_SHA") ?: "local"
        val buildDate = System.getenv("BUILD_DATE") ?: "unknown"
        val buildTime = System.getenv("BUILD_TIME") ?: "0"

        val file = File(dir, "Version.kt")
        file.writeText(
            """
            package Plume.Version

            object Version {
                const val VERSION = "$ver"
                const val COMMIT = "$commit"
                const val BUILD_DATE = "$buildDate"
                const val BUILD_TIME = "$buildTime"
            }
            """.trimIndent()
        )
    }
}

tasks.withType<org.jetbrains.kotlin.gradle.tasks.KotlinCompile> {
    dependsOn(generateVersionInfo)
}

sourceSets {
    main {
        kotlin.srcDir(layout.buildDirectory.dir("generated/source/version/main"))
    }
}