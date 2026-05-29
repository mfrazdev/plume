using System.Text.Json.Serialization;
using Plume.Http.Routes;
using Plume.Http.Routes.Server;
using Plume.Http.Routes.Websocket;

namespace Plume.Http;

// Centralizas tudo aqui. Não precisas de mexer no ficheiro das rotas!
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(ActionsRoute.ActionPayload))]
[JsonSerializable(typeof(ActionsRoute.ActionPayloadCore))]
[JsonSerializable(typeof(CreateRoute.CreatePayload))]
[JsonSerializable(typeof(DeleteRoute.DeletePayload))]
[JsonSerializable(typeof(UsagesRoute.UsagesPayload))]
[JsonSerializable(typeof(FileManagerRoute.FileBody))]
[JsonSerializable(typeof(StatusRoute.StatusResponse))]
[JsonSerializable(typeof(WebsocketConsole.WsLogMessage))]
[JsonSerializable(typeof(WebsocketConsole.WsClearMessage))]
[JsonSerializable(typeof(WebsocketConsole.WsErrorResponse))]
[JsonSerializable(typeof(WebsocketConsole.IncomingCommand))]
[JsonSerializable(typeof(WebsocketUsages.WsUsageData))]
[JsonSerializable(typeof(WebsocketUsages.WsUsagePayload))]
public partial class AppJsonContext : JsonSerializerContext
{
}