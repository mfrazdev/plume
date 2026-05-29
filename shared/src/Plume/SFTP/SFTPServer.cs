using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Plume.Configuration;
using Plume.Server;

namespace Plume.SFTP;

public static class InternalSftpServer
{
    // ==========================================
    // 1. DEFINIÇÃO DA INTERFACE NATIVA (NativeAOT)
    // ==========================================
    
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int AuthCallback(
        IntPtr usernamePtr, 
        IntPtr passwordPtr,
        IntPtr serverIdPtr,
        out double quota,
        IntPtr outPathBuf,
        int maxPathLen);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void EventCallback(
        int eventCode, 
        IntPtr contextDataPtr);

    // No NativeAOT com biblioteca estática, usamos "*" para indicar
    // que a função está no próprio executável, e não numa DLL externa.
    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern void StartPlumeSFTP(
        int port,
        [MarshalAs(UnmanagedType.LPStr)] string keyPath,
        AuthCallback authCb,
        EventCallback eventCb);

    // Guardar os callbacks estaticamente para o Garbage Collector não destruí-los
    private static readonly AuthCallback _authCallback = HandleAuthentication;
    private static readonly EventCallback _eventCallback = HandleGoEvents;
    
    private static ILogger _logger = null!; 

    // ==========================================
    // 2. INICIALIZAÇÃO DO NÚCLEO NATIVO
    // ==========================================
    
    public static void Start(ILogger logger)
    {
        _logger = logger;

        string keyPath = Path.GetFullPath(Path.Combine(ConfigManager.GlobalConfig.App.Path, "sftp_host_key.pem"));
        int port = ConfigManager.GlobalConfig.Sftp.Port;

        logger.LogInformation("[SFTP] Iniciando núcleo Nativo (AOT Estático) na porta {Port}...", port);

        try
        {
            // Como é AOT estático, não há falha de DllNotFound, se faltar o arquivo, 
            // o erro acontece no momento do "dotnet publish", não em tempo de execução!
            StartPlumeSFTP(port, keyPath, _authCallback, _eventCallback);
            logger.LogInformation("[SFTP] Servidor interno iniciado e escutando conexões com sucesso!");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SFTP] Falha crítica ao inicializar o servidor SFTP.");
        }
    }

    // ==========================================
    // 3. O "CÉREBRO" DE LOGS E EVENTOS
    // ==========================================

    private static void HandleGoEvents(int eventCode, IntPtr contextDataPtr)
    {
        string contextData = Marshal.PtrToStringUTF8(contextDataPtr) ?? "Unknown";

        switch (eventCode)
        {
            case 1:
                _logger.LogInformation("[SFTP] A sessão com o cliente ({ContextData}) foi finalizada com sucesso.", contextData);
                break;

            case 2:
                _logger.LogWarning("[SFTP] A sessão foi interrompida anormalmente. Detalhes: {ContextData}", contextData);
                break;

            case 3:
                var panicEx = new Exception($"Native Core Panic: {contextData}");
                _logger.LogError(panicEx, "[SFTP] Detectada uma falha crítica (Panic) na Goroutine do núcleo SFTP.");
                break;

            case 4:
                var setupEx = new Exception($"SFTP Bind Error: {contextData}");
                _logger.LogError(setupEx, "[SFTP] Falha do sistema operacional ao levantar o Socket do SFTP.");
                break;
        }
    }

    // ==========================================
    // 4. A LÓGICA DE AUTENTICAÇÃO E ROTEAMENTO
    // ==========================================
    
    private static int HandleAuthentication(IntPtr usernamePtr, IntPtr passwordPtr, IntPtr serverIdPtr, out double quota, IntPtr outPathBuf, int maxPathLen)
    {
        quota = 0;

        string username = Marshal.PtrToStringUTF8(usernamePtr) ?? "";
        string password = Marshal.PtrToStringUTF8(passwordPtr) ?? "";
        string serverId = Marshal.PtrToStringUTF8(serverIdPtr) ?? "";

        if (!ConfigManager.ValidateServerID(serverId) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            _logger.LogWarning("[SFTP] Tentativa de login ignorada (Credenciais mal formatadas ou vazias).");
            return 0;
        }

        var server = Manager.GetByShortId(serverId);
        if (server == null)
        {
            _logger.LogWarning("[SFTP] Tentativa de login negada: O servidor com ID '{ServerId}' não foi encontrado.", serverId);
            return 0;
        }

        var (isValid, quotaDouble) = ConfigManager.VerifySFTP(username, password, serverId);

        if (isValid)
        {
            _logger.LogInformation("[SFTP] Usuário '{Username}' autenticado com sucesso no servidor '{ServerId}'.", username, serverId);
            
            quota = quotaDouble;

            string serverFullPath = Path.Combine(ConfigManager.GlobalConfig.App.Path, "servers", server.Id); 
            
            byte[] pathBytes = Encoding.UTF8.GetBytes(serverFullPath);
            int copyLength = Math.Min(pathBytes.Length, maxPathLen - 1);

            Marshal.Copy(pathBytes, 0, outPathBuf, copyLength);
            Marshal.WriteByte(outPathBuf, copyLength, 0); // \0 Null Terminator

            return 1; // Sucesso
        }

        _logger.LogWarning("[SFTP] Tentativa de login negada (Senha incorreta) para o usuário '{Username}' no servidor '{ServerId}'.", username, serverId);
        return 0; // Falha
    }
}