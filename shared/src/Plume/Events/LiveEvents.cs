using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Plume.Events;

public record LiveEvent {
    public string Category { get; }
    public string Message { get; }
    public long Timestamp { get; init; }

    public LiveEvent(string category, string message, long? timestamp = null) {
        Category = category;
        Message = message;
        // Se o timestamp não for fornecido, obtém o Unix Timestamp atual em milissegundos
        Timestamp = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}

public class LiveBus {
    // Pub/Sub Real: Mantém uma lista de canais para CADA websocket conectado.
    private readonly ConcurrentDictionary<Guid, ChannelWriter<LiveEvent>> _subscribers = new();

    // A propriedade Events intercepta a chamada e gera um canal exclusivo pra quem tá ouvindo
    public IAsyncEnumerable<LiveEvent> Events => GetEventsAsync();

    private async IAsyncEnumerable<LiveEvent> GetEventsAsync([EnumeratorCancellation] CancellationToken ct = default) {
        // DropOldest: Se lotar com spam de Pull do Docker, dropa a % velha do progress bar 
        // e GARANTE que a linha nova (como Status Offline/Online) entre.
        var channel = Channel.CreateBounded<LiveEvent>(new BoundedChannelOptions(2048) {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var subId = Guid.NewGuid();
        _subscribers.TryAdd(subId, channel.Writer);

        try {
            await foreach (var item in channel.Reader.ReadAllAsync(ct)) {
                yield return item;
            }
        } finally {
            // Previne vazamento de memória: limpa o canal quando o WebSocket desconectar
            _subscribers.TryRemove(subId, out _);
        }
    }

    public void Emit(string category, string message) {
        var evt = new LiveEvent(category, message);
        
        // Broadcast: Emite a mensagem para TODOS os inscritos que estão olhando o console
        foreach (var sub in _subscribers.Values) {
            sub.TryWrite(evt);
        }
    }
}