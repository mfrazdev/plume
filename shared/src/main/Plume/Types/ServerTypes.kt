package Plume.Types

import kotlinx.serialization.Serializable

@Serializable
data class AllocationData(
    val ip: String,
    val port: Int,
)

data class StartCore(
    val installScript: String = "",
    val installImage: String = "",
    val installEntrypoint: String = "",

    val startupCommand: String = "",
    val startupScript: String = "",
    val dockerEntrypoint: String = "",

    val stopCommand: String = "",

    val configSystem: Any? = null,
    val startupParser: Any? = null,

    val rootAcess: Long? = null,
    val maintainable: Long? = null,
)

data class StartData(
    var image: String,
    val memory: Int,
    val cpu: Int,
    val environment: Any? = null,

    val primaryAllocation: AllocationData? = null,
    val additionalAllocation: List<AllocationData> = emptyList(),

    val disk: Long = 0,
    val core: StartCore,
)

data class UsageMetrics(
    val cpu: Double = 0.0,
    val memory: Long = 0,
    val memoryLimit: Long = 0,
    val disk: Long = 0,
    val networkIn: Long = 0,
    val networkOut: Long = 0,
)