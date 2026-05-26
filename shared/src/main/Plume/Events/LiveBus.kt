package Plume.Events

import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow

class LiveBus {
    private val _events = MutableSharedFlow<LiveEvent>(extraBufferCapacity = 512)
    val events = _events.asSharedFlow()

    fun emit(category: String, message: String) {
        _events.tryEmit(LiveEvent(category, message))
    }
}