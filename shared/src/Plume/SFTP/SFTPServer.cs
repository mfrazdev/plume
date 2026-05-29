using System.Text;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Plume.Configuration;
using Plume.Server;

namespace Plume.SFTP;

public static partial class InternalSftpServer
{
    // ==========================================
    // 1. DEFINIÇÃO DA INTERFACE NATIVA (NativeAOT)
    // ==========================================
    
    [LibraryImport("*", EntryPoint = "StartPlumeSFTP")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial int StartPlumeSFTP(
        int port,
        byte* keyPath,
        void* authCb,
        void* eventCb);

    private static ILogger _logger = null!; 

    // ==========================================
    // 2. INICIALIZAÇÃO DO NÚCLEO NATIVO
    // ==========================================
    
    public static void Start(ILogger logger)
    {
        _logger = logger;
        string keyPath = Path.GetFullPath(Path.Combine(ConfigManager.GlobalConfig.App.Path, "sftp_host_key.pem"));
        int port = ConfigManager.GlobalConfig.Sftp.Port > 0 ? ConfigManager.GlobalConfig.Sftp.Port : 2022;

        var thread = new Thread(() =>
        {
            try
            {
                unsafe
                {
                    delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, double*, IntPtr, int, int> pAuth = &HandleAuthentication;
                    delegate* unmanaged[Cdecl]<int, IntPtr, void> pEvent = &HandleNativeEvents; // Apontando para o novo gerador de mensagens
                    
                    byte[] keyPathBytes = Encoding.UTF8.GetBytes(keyPath + "\0");
                    fixed (byte* pKeyPath = keyPathBytes)
                    {
                        // O motor C entra em loop infinito aqui
                        StartPlumeSFTP(port, pKeyPath, (void*)pAuth, (void*)pEvent);
                        
                        logger.LogWarning("[SFTP] O servidor nativo foi encerrado inesperadamente.");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[SFTP] Falha catastrófica ao tentar executar código C embutido.");
            }
        });

        thread.IsBackground = true;
        thread.Name = "SFTP_Native_Core_C";
        thread.Start();
    }

    // ==========================================
    // 3. O "CÉREBRO" DE LOGS (CONSTRUTOR DE MENSAGENS)
    // ==========================================

// ==========================================
    // 3. O "CÉREBRO" DE LOGS (CONSTRUTOR DE MENSAGENS)
    // ==========================================

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void HandleNativeEvents(int eventCode, IntPtr contextDataPtr)
    {
        string rawData = "";
        try { rawData = Marshal.PtrToStringUTF8(contextDataPtr) ?? ""; } catch { }

        switch (eventCode)
        {
            // --- INFORMAÇÕES GERAIS ---
            case 10: 
                break; 
            case 11: 
                _logger.LogInformation("Motor de rede iniciado na porta: {Porta}", rawData); 
                break;
            case 12: 
                _logger.LogInformation("Cliente conectado. IP: {IP}", rawData); 
                break;

            // --- AUTENTICAÇÃO E SESSÃO ---
            case 100: 
                _logger.LogInformation("Autenticação bem-sucedida para o usuário: {Usuario}", rawData); 
                break;
            case 101: 
                _logger.LogInformation("O subsistema SFTP foi ativado para o IP: {IP}", rawData); 
                break;

            // --- AVISOS (WARNINGS) ---
            case 2: 
                _logger.LogWarning("Tentativa de login falhou. Alvo: {Dados}", rawData); 
                break;
            case 4:
                _logger.LogWarning("O cliente enviou um comando inválido ou houve falha de I/O: {Erro}", rawData); 
                break;

            // --- ERROS FATAIS (DISPARANDO EXCEPTION) ---
            case 20: 
            case 21: 
            case 22: 
            case 23:
                string criticalMsg = $"Falha catastrófica no motor nativo do SFTP. Código: {eventCode}. Detalhe: {rawData}";
                _logger.LogCritical(criticalMsg);
                
                // Disparar uma exception aqui dentro de um método UnmanagedCallersOnly
                // fará com que o .NET crashe o processo inteiro imediatamente (FailFast).
                throw new InvalidOperationException(criticalMsg);

            // --- NÃO MAPEADOS ---
            default: 
                _logger.LogDebug("Código de evento desconhecido ({Code}) enviado pelo C: {Data}", eventCode, rawData); 
                break;
        }
    }

    // ==========================================
    // 4. A LÓGICA DE AUTENTICAÇÃO E ROTEAMENTO
    // ==========================================
    
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int HandleAuthentication(IntPtr usernamePtr, IntPtr passwordPtr, IntPtr serverIdPtr, double* quota, IntPtr outPathBuf, int maxPathLen)
    {
        *quota = 0;
        string username = ""; string password = ""; string serverId = "";
        
        try 
        {
            username = Marshal.PtrToStringUTF8(usernamePtr) ?? "";
            password = Marshal.PtrToStringUTF8(passwordPtr) ?? "";
            serverId = Marshal.PtrToStringUTF8(serverIdPtr) ?? "";
        }
        catch { return 0; }

        if (!ConfigManager.ValidateServerID(serverId) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return 0;

        var server = Manager.GetByShortId(serverId);
        if (server == null) return 0;

        var (isValid, quotaDouble) = ConfigManager.VerifySFTP(username, password, serverId);

        if (isValid)
        {
            *quota = quotaDouble;
            string serverFullPath = Path.Combine(ConfigManager.GlobalConfig.App.Path, "servers", server.Id); 
            byte[] pathBytes = Encoding.UTF8.GetBytes(serverFullPath);
            int copyLength = Math.Min(pathBytes.Length, maxPathLen - 1);
            Marshal.Copy(pathBytes, 0, outPathBuf, copyLength);
            Marshal.WriteByte(outPathBuf, copyLength, 0);
            return 1;
        }
        return 0; 
    }
}