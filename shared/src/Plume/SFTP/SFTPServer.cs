using System.Text;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Plume.Configuration;
using Plume.Server;

namespace Plume.SFTP;

public static partial class InternalSftpServer
{
    
#if DEBUG
    private const string NativeLib = "D:\\panel\\plume-net\\app\\plume_sftp_core.dll";
#else
    private const string NativeLib = "*";
#endif
    
    // ==========================================
    // 1. DEFINIÇÃO DA INTERFACE NATIVA (NativeAOT)
    // ==========================================
    
    [LibraryImport(NativeLib, EntryPoint = "StartPlumeSFTP")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial int StartPlumeSFTP(int port, byte* keyPath, void* authCb, void* eventCb);

    // FIX: Importando a nova função de desligamento seguro do Rust
    [LibraryImport(NativeLib, EntryPoint = "StopPlumeSFTP")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial void StopPlumeSFTP();

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
                    delegate* unmanaged[Cdecl]<int, IntPtr, void> pEvent = &HandleNativeEvents; 
                    
                    byte[] keyPathBytes = Encoding.UTF8.GetBytes(keyPath + "\0");
                    fixed (byte* pKeyPath = keyPathBytes)
                    {
                        StartPlumeSFTP(port, pKeyPath, (void*)pAuth, (void*)pEvent);
                        logger.LogInformation("O motor nativo do SFTP foi encerrado de forma segura.");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha catastrófica ao tentar executar código C embutido.");
            }
        });

        thread.IsBackground = true;
        thread.Name = "SFTP_Native_Core_C";
        thread.Start();
    }

    public static void Stop()
    {
        try { StopPlumeSFTP(); } catch { }
    }

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
            case 10: break; 
            case 11: _logger.LogInformation("Servidor SFTP ouvindo em {Port}", ConfigManager.GlobalConfig.Sftp.Port); break;
            case 12: _logger.LogInformation("Cliente SFTP conectado. IP: {IP}", rawData); break;
            case 100: _logger.LogInformation("Autenticação bem-sucedida para o usuário: {Usuario}", rawData); break;
            case 101: _logger.LogInformation("O subsistema SFTP foi ativado para o IP: {IP}", rawData); break;
            case 2: _logger.LogWarning("Tentativa de login falhou. Alvo: {Dados}", rawData); break;
            case 4: _logger.LogWarning("Comando inválido ou falha de I/O: {Erro}", rawData); break;

            case 20: 
            case 21: 
            case 22: 
            case 23:
                // FIX: Removido o 'throw'. Exceções dentro do P/Invoke geram FailFast corrompendo as finalizações
                _logger.LogCritical("Falha catastrófica no motor nativo do SFTP. Código: {Code}. Detalhe: {Data}", eventCode, rawData);
                break;

            default: 
                _logger.LogDebug("Código desconhecido ({Code}) do C: {Data}", eventCode, rawData); 
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

            // FIX: Bloco de segurança rigorosa anti Path Traversal
            string serversDir = Path.GetFullPath(Path.Combine(ConfigManager.GlobalConfig.App.Path, "servers"));
            string serverFullPath = Path.GetFullPath(Path.Combine(serversDir, server.Id));

            if (!serverFullPath.StartsWith(serversDir + Path.DirectorySeparatorChar))
            {
                _logger.LogWarning("Tentativa de Path Traversal bloqueada no P/Invoke para ServerID: {Id}", server.Id);
                return 0;
            }

            // FIX: GERA A PASTA CASO ELA AINDA NÃO EXISTA - RESOLVE O ERRO DE DIRETÓRIO INEXISTENTE (ACESSO NEGADO)
            if (!Directory.Exists(serverFullPath))
            {
                try { Directory.CreateDirectory(serverFullPath); } catch { }
            }

            byte[] pathBytes = Encoding.UTF8.GetBytes(serverFullPath);

            // FIX: Segurança anti-truncamento e possível buffer-over read (Fuga de Jail)
            if (pathBytes.Length >= maxPathLen)
            {
                _logger.LogError("Buffer nativo muito pequeno para alocar o caminho do servidor {Id}", server.Id);
                return 0;
            }

            Marshal.Copy(pathBytes, 0, outPathBuf, pathBytes.Length);
            Marshal.WriteByte(outPathBuf, pathBytes.Length, 0); // Byte nulo obrigatório
            return 1;
        }
        return 0; 
    }
}