plugins {
    // Apply the shared build logic from a convention plugin.
    // The shared code is located in `plugins/src/main/kotlin/kotlin-jvm.gradle.kts`.
    id("plugins.convention.kotlin-jvm")
}


dependencies {
    implementation(project(":shared"))
}