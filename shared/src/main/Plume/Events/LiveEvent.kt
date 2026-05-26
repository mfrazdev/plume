package Plume.Events

data class LiveEvent(
    val category: String,
    val message: String,
    val timestamp: Long = System.currentTimeMillis(),
)