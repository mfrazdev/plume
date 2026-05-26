// The code in this file is a convention plugin - a Gradle mechanism for sharing reusable build logic.
// `plugins` is a Gradle-recognized directory and every plugin there will be easily available in the rest of the build.
package plugins.convention

import org.gradle.api.tasks.testing.logging.TestLogEvent
import org.gradle.accessors.dm.LibrariesForLibs
plugins {
    // Apply the Kotlin JVM plugin to add support for Kotlin in JVM projects.
    kotlin("jvm")
    kotlin("plugin.serialization")
}

val libs = the<LibrariesForLibs>()

kotlin {
    // Use a specific Java version to make it easier to work in different environments.
    jvmToolchain(21)
}


dependencies {

    // ktor server
    implementation(libs.bundles.ktorServer)

    // ktor client
    implementation(libs.bundles.ktorClient)

    implementation(libs.logback)

    implementation(libs.bundles.docker)

    implementation(libs.jackson.yaml)
    implementation(libs.kaml)

    // SSHD
    implementation(libs.sshd.core) {
        exclude(group = "org.bouncycastle")
        exclude(group = "net.i2p.crypto")
    }
    implementation(files("../deps/sshd-sftp-2.12.1.jar"))
}

tasks.withType<Test>().configureEach {
    // Configure all test Gradle tasks to use JUnitPlatform.
    useJUnitPlatform()

    // Log information about all test results, not only the failed ones.
    testLogging {
        events(
            TestLogEvent.FAILED,
            TestLogEvent.PASSED,
            TestLogEvent.SKIPPED
        )
    }
}

sourceSets {
    main {
        kotlin.srcDirs("src/main")
        resources.srcDirs("src/resources")
    }

    test {
        kotlin.srcDirs("src/test")
        resources.srcDirs("src/test-resources")
    }
}
