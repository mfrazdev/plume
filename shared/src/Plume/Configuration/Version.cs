using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace PlumeSFTP.System;

public class UpdateInfo
{
    public string CurrentVersion { get; set; }
    public string LatestVersion { get; set; }
    public bool UpdateAvailable { get; set; }
}

public static class VersionManager
{
    // Variáveis carregadas via Assembly Metadata (injetadas no build)
    public static string Version { get; }
    public static string Commit { get; }
    public static string BuildDate { get; }
    public static string BuildTime { get; }

    private const string GithubRepo = "mfrazlab/plume"; // Substitua se necessário

    static VersionManager()
    {
        // Tenta buscar os valores injetados no .csproj durante o publish/build.
        // Se não encontrar, usa os valores padrão de desenvolvimento.
        var assembly = Assembly.GetExecutingAssembly();
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>();

        Version = metadata.FirstOrDefault(m => m.Key == "AppVersion")?.Value ?? "dev";
        Commit = metadata.FirstOrDefault(m => m.Key == "AppCommit")?.Value ?? "local";
        BuildDate = metadata.FirstOrDefault(m => m.Key == "AppBuildDate")?.Value ?? "unknown";
        BuildTime = metadata.FirstOrDefault(m => m.Key == "AppBuildTime")?.Value ?? "0";
    }

    public static string GetVersion() => Version;
    public static string GetCommit() => Commit;
    public static string GetBuildDate() => BuildDate;
    public static string GetBuildTime() => BuildTime;

    /// <summary>
    /// Varre o Feed Atom do GitHub em busca da última tag válida e da data de atualização
    /// </summary>
    private static async Task<(string Tag, DateTimeOffset Updated)> FetchLatestGitHubReleaseAsync()
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("daemon-updater");

        bool isCurrentCanary = Version.Contains("canary", StringComparison.OrdinalIgnoreCase);
        string reqUrl = $"https://github.com/{GithubRepo}/releases.atom";

        var response = await client.GetAsync(reqUrl);
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"failed to fetch releases.atom: status {response.StatusCode}");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        var doc = XDocument.Load(stream);
        XNamespace ns = "http://www.w3.org/2005/Atom";

        var re = new Regex(@"/releases/tag/(.+)$");

        foreach (var entry in doc.Descendants(ns + "entry"))
        {
            var href = entry.Element(ns + "link")?.Attribute("href")?.Value;
            var updatedStr = entry.Element(ns + "updated")?.Value;

            if (string.IsNullOrEmpty(href) || string.IsNullOrEmpty(updatedStr))
                continue;

            var match = re.Match(href);
            if (match.Success)
            {
                string tag = match.Groups[1].Value;
                bool isPreRelease = tag.Contains("canary", StringComparison.OrdinalIgnoreCase);

                // Ignora canaries se a versão atual for stable
                if (!isCurrentCanary && isPreRelease)
                    continue;

                // Parse da data do GitHub (RFC3339)
                if (!DateTimeOffset.TryParse(updatedStr, out var remoteUpdated))
                {
                    remoteUpdated = DateTimeOffset.UtcNow; // Fallback
                }

                return (tag, remoteUpdated);
            }
        }

        throw new Exception("no suitable release found in feed");
    }

    public static async Task<UpdateInfo> CheckForUpdatesAsync()
    {
        var (latestTag, remoteUpdated) = await FetchLatestGitHubReleaseAsync();

        string latest = latestTag.TrimStart('v');
        string current = Version.TrimStart('v');
        bool isCurrentCanary = current.Contains("canary", StringComparison.OrdinalIgnoreCase);

        bool updateAvailable = false;

        if (current == "dev")
        {
            updateAvailable = false;
        }
        else if (isCurrentCanary)
        {
            // Lógica Canary: Unix Timestamp
            if (long.TryParse(BuildTime, out long localTime))
            {
                long remoteTime = remoteUpdated.ToUnixTimeSeconds();
                // Tolerância de 5 min (300 segs) do workflow
                if (remoteTime > (localTime + 300))
                {
                    updateAvailable = true;
                }
            }
        }
        else
        {
            // Lógica Stable
            if (latest != current && !string.IsNullOrEmpty(latest))
            {
                updateAvailable = true;
            }
        }

        return new UpdateInfo
        {
            CurrentVersion = current,
            LatestVersion = latest,
            UpdateAvailable = updateAvailable
        };
    }

    /// <summary>
    /// Constrói a URL direta e faz o hot-swap do executável
    /// </summary>
    public static async Task UpdateAsync()
    {
        var (latestTag, remoteUpdated) = await FetchLatestGitHubReleaseAsync();

        string latest = latestTag.TrimStart('v');
        string current = Version.TrimStart('v');
        bool isCurrentCanary = current.Contains("canary", StringComparison.OrdinalIgnoreCase);

        if (current == "dev")
            throw new Exception("cannot update from a 'dev' build");

        bool needsUpdate = false;
        if (isCurrentCanary)
        {
            if (long.TryParse(BuildTime, out long localTime))
            {
                long remoteTime = remoteUpdated.ToUnixTimeSeconds();
                if (remoteTime > (localTime + 300)) needsUpdate = true;
            }
        }
        else
        {
            if (latest != current && !string.IsNullOrEmpty(latest)) needsUpdate = true;
        }

        if (!needsUpdate)
        {
            if (isCurrentCanary)
                throw new Exception("daemon is already up to date (canary - build id match)");
            throw new Exception($"daemon is already up to date (v{current})");
        }

        string tagForLink = latestTag.StartsWith("v") ? latestTag : latestTag;

        // Define OS e Arquitetura para buscar o binário correto
        string os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" :
                    RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" : "darwin";

        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            _ => "unknown"
        };

        string ext = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";

        // Atenção: Seu GH Action do C# zipava o arquivo (plumesftp-{rid}.zip). 
        // Esse código pressupõe que o GitHub Release possui o executável direto, igual ao Go. 
        // Se enviar zipado no release, terá que usar System.IO.Compression.ZipFile aqui.
        string fileName = $"feather-{os}-{arch}{ext}";
        string downloadUrl = $"https://github.com/{GithubRepo}/releases/download/{tagForLink}/{fileName}";

        string exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath))
            throw new Exception("failed to get executable path");

        // No C# moderno não temos symlink nativo fácil cross-platform como `filepath.EvalSymlinks`,
        // mas resolver o MainModule.FileName costuma entregar o path real do binário.

        using var client = new HttpClient();
        using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
            throw new Exception($"failed to download update from {downloadUrl}, server returned: {response.StatusCode}");

        string newExePath = exePath + ".new";
        string oldExePath = exePath + ".old";

        // Baixa o novo binário
        using (var fs = new FileStream(newExePath, FileMode.Create, FileAccess.Write))
        {
            await response.Content.CopyToAsync(fs);
        }

        // Linux/macOS: Restaura permissões de execução do binário baixado
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
#if NET7_0_OR_GREATER
            File.SetUnixFileMode(newExePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute); // Equivalente ao 0755
#endif
        }

        // Remove resíduos antigos caso o último update tenha travado a remoção
        if (File.Exists(oldExePath))
        {
            try { File.Delete(oldExePath); } catch { /* Ignora se estiver travado */ }
        }

        // Renomeia o atual para .old (funciona até no Windows com processos ativos)
        File.Move(exePath, oldExePath, overwrite: true);

        // Coloca o novo no lugar
        File.Move(newExePath, exePath, overwrite: true);

        // Tenta deletar o arquivo antigo. No Windows pode dar erro porque o processo atual 
        // ainda o bloqueia na memória, mas será limpo no próximo update
        try { File.Delete(oldExePath); } catch { }

        RestartAfterUpdate(exePath);
    }

    private static void RestartAfterUpdate(string exePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false
        };

        Process.Start(startInfo);
        Environment.Exit(0);
    }
}
