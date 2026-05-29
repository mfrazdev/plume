namespace Plume.Configuration;

using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

#nullable enable

public class AppSection
{
    public string Id { get; init; } = string.Empty;
    public int Port { get; init; }
    public string Path { get; init; } = string.Empty;

    public AppSection() {}
}

public class SftpSection
{
    public int Port { get; init; }

    public SftpSection() {}
}

public class SslSection
{
    public bool Enabled { get; set; }
    public string CertPath { get; set; } = string.Empty;
    public string KeyPath { get; set; } = string.Empty;

    // Construtor sem parâmetros exigido pelo YamlDotNet
    public SslSection() 
    {
    }

    // Mantém a compatibilidade caso você use a inicialização passando o booleano em outro lugar
    public SslSection(bool enabled)
    {
        Enabled = enabled;
    }
}

public class RemoteSection
{
    public string Url { get; init; } = string.Empty;
    public string Token { get; init; } = string.Empty;

    public RemoteSection() {}
}

public class AppConfig
{
    // Adicionamos os métodos para garantir que o compilador veja que as propriedades são utilizadas
    public AppSection App { get; init; } = new();
    public SftpSection Sftp { get; init; } = new();
    public SslSection Ssl { get; init; } = new(false);
    public RemoteSection Remote { get; init; } = new();

    public AppConfig() {}
}

public static class JsonExtensions
{
    public static object? ToAnyValue(this JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt32(out int i) ? i : (element.TryGetInt64(out long l) ? l : element.GetDouble()),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => element.EnumerateArray().Select(e => e.ToAnyValue()).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToAnyValue()),
        _ => element.GetRawText()
    };
}

public static class ConfigManager
{
    private static readonly ReaderWriterLockSlim _lock = new();
    
    public static AppConfig GlobalConfig { get; private set; } = new();
    public static HttpClient HttpClient { get; private set; } = new(new HttpClientHandler());

    // Avisa ao compilador NativeAOT para NÃO apagar as propriedades e construtores dessas classes!
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(AppConfig))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(AppSection))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SftpSection))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SslSection))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(RemoteSection))]
    public static void LoadConfig(ILogger logger)
    {
        _lock.EnterWriteLock();
        try
        {
            string configFilePath = "config.yml";
            string fallbackConfigFilePath = "/etc/plume/config.yml";

            if (!File.Exists(configFilePath))
            {
                if (!File.Exists(fallbackConfigFilePath))
                {
                    logger.LogError("Configuração do Plume não encontrada.");
                    throw new FileNotFoundException("config.yml missing.");
                }
                configFilePath = fallbackConfigFilePath;
            }

            string content = File.ReadAllText(configFilePath);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();

            GlobalConfig = deserializer.Deserialize<AppConfig>(content) ?? new();
            
            bool isDevMode = Environment.GetEnvironmentVariable("PLUME_DEV_MODE") == "1";
            var handler = new HttpClientHandler();
            if (isDevMode)
            {
                handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }

            HttpClient = new HttpClient(handler);
            HttpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public static Dictionary<string, object?> RemoteAPI(string endpoint, Dictionary<string, object?> payload)
    {
        string remoteURL;
        string token;

        _lock.EnterReadLock();
        try
        {
            remoteURL = $"{GlobalConfig.Remote.Url}/api/nodes/helper{endpoint}";
            token = GlobalConfig.Remote.Token;
        }
        finally
        {
            _lock.ExitReadLock();
        }

        var finalPayload = new Dictionary<string, object?>(payload) { ["token"] = token };

        var response = HttpClient.PostAsJsonAsync(remoteURL, finalPayload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        return doc.RootElement.ToAnyValue() as Dictionary<string, object?> ?? new();
    }

    public static bool ValidateServerID(string? serverId) => 
        !string.IsNullOrEmpty(serverId) && serverId.Length <= 255 && serverId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');

    public static bool ValidateUUID(string id) => ValidateServerID(id);

    public static (bool HasPermission, double Disk) VerifySFTP(string userName, object? password, string serverId)
    {
        if (!ValidateServerID(serverId) || string.IsNullOrEmpty(userName) || password == null) return (false, 0.0);

        try
        {
            var res = RemoteAPI("/verify-sftp", new() { { "userName", userName }, { "password", password }, { "serverUuid", serverId } });
            bool perm = res.TryGetValue("permission", out var p) && p is bool b && b;
            double disk = res.TryGetValue("disk", out var d) ? Convert.ToDouble(d) : 0.0;
            return (perm, disk);
        }
        catch { return (false, 0.0); }
    }

    public static bool UserIsAdmin(string userUUID)
    {
        if (!ValidateUUID(userUUID)) return false;
        try { return RemoteAPI("/admin-permission", new() { { "userUuid", userUUID } }).TryGetValue("isAdmin", out var v) && v is bool b && b; }
        catch { return false; }
    }

    public static bool HasPermission(string userUUID, string serverId)
    {
        if (!ValidateUUID(userUUID) || !ValidateServerID(serverId)) return false;
        try { return RemoteAPI("/permission", new() { { "userUuid", userUUID }, { "serverUuid", serverId } }).TryGetValue("permission", out var v) && v is bool b && b; }
        catch { return false; }
    }
}