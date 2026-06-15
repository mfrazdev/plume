using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Plume.Configuration;
using Plume.Http;

namespace Plume.Http.Routes.Server
{
    public static class FileManagerRoute
    {
        // Constantes de proteção
        private const long MAX_UNZIP_FILE_SIZE = 3L * 1024 * 1024 * 1024; // 3GB por arquivo
        private const long MAX_UNZIP_TOTAL_SIZE = 3L * 1024 * 1024 * 1024; // 3GB total
        private const int MAX_ZIP_FILES = 100000;

        // DTOs
        public record FileBody(
            int UserUuid = 0,
            string ServerId = "",
            double Disk = 0.0,
            string Path = "/", // Default alterado para "/"
            string NewName = "",
            string Content = "",
            string Action = "",
            List<string>? Paths = null,
            string To = "",
            string Destination = ""
        );

        public record FileItem(
            string Name,
            string Type,
            long Size,
            long LastModified,
            string Path
        );

        private static string GlobalBasePath => Path.Combine(ConfigManager.GlobalConfig.App.Path, "servers");

        public static void Register(RouteGroupBuilder group)
        {
            var fileGroup = group.MapGroup("/servers/filemanager");

            fileGroup.MapPost("/upload", async context =>
            {
                var result = await HandleUpload(context);
                await result.ExecuteAsync(context);
            });

            fileGroup.MapGet("/download", async context =>
            {
                var result = await HandleDirectDownload(context);
                await result.ExecuteAsync(context);
            });
            
            fileGroup.MapPost("/{action}", async (string action, HttpContext context) =>
            {
                var result = await FileManagerHandler(action, context);
                await result.ExecuteAsync(context);
            });

            fileGroup.MapGet("/{action}", async (string action, HttpContext context) =>
            {
                var result = await FileManagerHandler(action, context);
                await result.ExecuteAsync(context);
            });
        }

        private static async Task<IResult> FileManagerHandler(string action, HttpContext context)
        {
            FileBody? body = null;

            if (context.Request.ContentLength > 0 && context.Request.HasJsonContentType())
            {
                try
                {
                    body = await context.Request.ReadFromJsonAsync<FileBody>();
                }
                catch(Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    return Reply.Json(new { error = "Invalid JSON" }, statusCode: 400);
                }
            }

            if (body == null)
                return Reply.Json(new { error = "Invalid JSON or missing body" }, statusCode: 400);

            // Garante que se o path vier vazio, seja considerado "/"
            if (string.IsNullOrWhiteSpace(body.Path))
            {
                body = body with { Path = "/" };
            }

            // Validações rigorosas
            if (!ConfigManager.ValidateUUID(body.UserUuid.ToString()))
                return Reply.Json(new { error = "Invalid userUuid" }, statusCode: 400);
    
            if (!ConfigManager.ValidateServerID(body.ServerId))
                return Reply.Json(new { error = "Invalid serverId" }, statusCode: 400);

            if (!ConfigManager.HasPermission(body.UserUuid.ToString(), body.ServerId))
                return Reply.Json(new { error = "Sem permissão" }, statusCode: 403);

            string basePath = Path.GetFullPath(Path.Combine(GlobalBasePath, body.ServerId));
            string relPath = SanitizePath(body.Path);
            string absPath = string.IsNullOrEmpty(relPath) ? basePath : Path.GetFullPath(Path.Combine(basePath, relPath));

            // Segurança contra Path Traversal
            if (!absPath.StartsWith(basePath))
                return Reply.Json(new { error = "Acesso negado (Traversal)" }, statusCode: 403);

            long quotaBytes = (long)(body.Disk * 1024 * 1024);

            // Envolvemos tudo em um try-catch geral para evitar erros 500 brutos por conta de I/O
            try
            {
                switch (action.ToLower())
                {
                    case "list":
                    {
                        if (!Directory.Exists(absPath))
                            return Reply.Json(new { error = "Diretório não encontrado" }, statusCode: 404);

                        var dirInfo = new DirectoryInfo(absPath);
                        var items = new List<FileItem>();

                        // Evitar quebra caso uma pasta exija permissões de ADM do Windows/Linux
                        try
                        {
                            foreach (var dir in dirInfo.GetDirectories())
                            {
                                items.Add(new FileItem(dir.Name, "folder", 0, new DateTimeOffset(dir.LastWriteTimeUtc).ToUnixTimeMilliseconds(), CombinePaths(relPath, dir.Name)));
                            }
                            foreach (var file in dirInfo.GetFiles())
                            {
                                items.Add(new FileItem(file.Name, "file", file.Length, new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(), CombinePaths(relPath, file.Name)));
                            }
                        }
                        catch (Exception ex)
                        {
                            return Reply.Json(new { error = $"Erro ao listar diretório: {ex.Message}" }, statusCode: 500);
                        }

                        return Reply.Json(new { status = "success", items = items, path = "/" + relPath });
                    }

                    case "read":
                    {
                        if (!File.Exists(absPath))
                            return Reply.Json(new { error = "Arquivo não encontrado" }, statusCode: 404);

                        // FileShare.ReadWrite evita crash se um servidor estiver escrevendo no arquivo log
                        using var fs = new FileStream(absPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var sr = new StreamReader(fs);
                        string content = await sr.ReadToEndAsync();
                        return Reply.Json(new { status = "success", content = content, path = "/" + relPath });
                    }

                    case "write":
                    {
                        if (quotaBytes > 0)
                        {
                            long currentSize = GetServerDiskUsage(basePath);
                            long oldSize = File.Exists(absPath) ? new FileInfo(absPath).Length : 0L;
                            long newFileSize = System.Text.Encoding.UTF8.GetByteCount(body.Content ?? "");
                            long sizeDiff = newFileSize - oldSize;

                            if (sizeDiff > 0 && currentSize + sizeDiff > quotaBytes)
                                return Reply.Json(new { error = "Cota de disco excedida!", max = quotaBytes, currentSize }, statusCode: 400);
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);
                        
                        using var fs = new FileStream(absPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                        using var sw = new StreamWriter(fs);
                        await sw.WriteAsync(body.Content ?? "");
                        return Reply.Json(new { status = "success" });
                    }

                    case "rename":
                    {
                        if (string.IsNullOrEmpty(body.NewName) || body.NewName.Contains("/") || body.NewName.Contains("\\"))
                            return Reply.Json(new { error = "Nome inválido" }, statusCode: 400);

                        string newAbs = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(absPath)!, body.NewName));
                        
                        if (File.Exists(newAbs) || Directory.Exists(newAbs))
                            return Reply.Json(new { error = "Já existe um item com esse nome no destino." }, statusCode: 400);

                        if (File.Exists(absPath)) File.Move(absPath, newAbs);
                        else if (Directory.Exists(absPath)) Directory.Move(absPath, newAbs);
                        
                        return Reply.Json(new { status = "success" });
                    }

                    case "mkdir":
                    {
                        Directory.CreateDirectory(absPath);
                        return Reply.Json(new { status = "success" });
                    }

                    case "delete":
                    {
                        DeleteRecursively(absPath);
                        return Reply.Json(new { status = "success" });
                    }

                    case "download":
                    {
                        if (File.Exists(absPath))
                        {
                            // Envia via stream para não dar lock crash
                            var fs = new FileStream(absPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            return Results.File(fs, fileDownloadName: Path.GetFileName(absPath));
                        }
                        return Reply.Json(new { error = "Arquivo não encontrado" }, statusCode: 404);
                    }

                    case "move":
                    {
                        string destRel = SanitizePath(body.To);
                        // Solução definitiva pro move: DirectoryInfo captura o nome corretamente (seja pasta ou arquivo)
                        string itemName = new DirectoryInfo(absPath).Name; 
                        string destAbs = Path.GetFullPath(Path.Combine(basePath, destRel, itemName));

                        if (!destAbs.StartsWith(basePath))
                            return Reply.Json(new { error = "Destino inválido" }, statusCode: 403);

                        if (File.Exists(destAbs) || Directory.Exists(destAbs))
                            return Reply.Json(new { error = "Já existe um item com esse nome no destino." }, statusCode: 400);

                        Directory.CreateDirectory(Path.GetDirectoryName(destAbs)!);
                        
                        if (File.Exists(absPath)) 
                            File.Move(absPath, destAbs);
                        else if (Directory.Exists(absPath)) 
                            Directory.Move(absPath, destAbs);
                        else 
                            return Reply.Json(new { error = "Origem não encontrada" }, statusCode: 404);
                        
                        return Reply.Json(new { status = "success" });
                    }

                    case "unarchive":
                    {
                        if (quotaBytes > 0 && GetServerDiskUsage(basePath) >= quotaBytes)
                            return Reply.Json(new { error = "Cota de disco excedida, limpe espaço antes de extrair." }, statusCode: 400);
                            
                        return await HandleUnarchive(body, absPath, basePath);
                    }

                    case "mass":
                    {
                        var pathsToProcess = body.Paths ?? new List<string>();
                        
                        if (body.Action.Equals("delete", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var p in pathsToProcess)
                        {
                                string target = Path.GetFullPath(Path.Combine(basePath, SanitizePath(p)));
                                if (target.StartsWith(basePath))
                                    DeleteRecursively(target);
                            }
                            return Reply.Json(new { status = "success" });
                        }
                        else if (body.Action.Equals("archive", StringComparison.OrdinalIgnoreCase))
                        {
                            if (quotaBytes > 0 && GetServerDiskUsage(basePath) >= quotaBytes)
                                return Reply.Json(new { error = "Cota de disco cheia, impossível criar arquivo." }, statusCode: 400);
                                
                            // absPath aqui será o diretório atual do usuário, garantindo que o ZIP seja salvo onde ele está!
                            return await HandleMassArchive(pathsToProcess, basePath, absPath);
                        }
                        return Reply.Json(new { error = "Ação de mass desconhecida" }, statusCode: 400);
                    }

                    default:
                        return Reply.Json(new { error = "Ação desconhecida" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Reply.Json(new { error = $"Erro ao executar ação: {ex.Message}" }, statusCode: 500);
            }
        }

        private static async Task<IResult> HandleDirectDownload(HttpContext context)
        {
            try
            {
                string userUuid = context.Request.Query["userUuid"].ToString();
                string serverId = context.Request.Query["serverId"].ToString();
                
                string targetPath = context.Request.Query["path"].ToString();
                if (string.IsNullOrWhiteSpace(targetPath)) targetPath = "/";

                if (!ConfigManager.ValidateUUID(userUuid)) return Reply.Json(new { error = "Invalid userUuid" }, statusCode: 400);
                if (!ConfigManager.ValidateServerID(serverId)) return Reply.Json(new { error = "Invalid serverId" }, statusCode: 400);
                if (!ConfigManager.HasPermission(userUuid, serverId)) return Reply.Json(new { error = "Sem permissão" }, statusCode: 403);

                string basePath = Path.GetFullPath(Path.Combine(GlobalBasePath, serverId));
                string absPath = Path.GetFullPath(Path.Combine(basePath, SanitizePath(targetPath)));

                if (!absPath.StartsWith(basePath)) return Reply.Json(new { error = "Acesso negado (Traversal)" }, statusCode: 403);

                if (!File.Exists(absPath))
                    return Reply.Json(new { error = "Arquivo não encontrado" }, statusCode: 404);

                var fs = new FileStream(absPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return Results.File(fs, fileDownloadName: Path.GetFileName(absPath));
            }
            catch (Exception ex)
            {
                return Reply.Json(new { error = $"Erro ao processar download: {ex.Message}" }, statusCode: 500);
            }
        }

        private static async Task<IResult> HandleUpload(HttpContext context)
        {
            if (!context.Request.HasFormContentType)
                return Reply.Json(new { error = "Esperado form-data" }, statusCode: 400);

            try
            {
                var form = await context.Request.ReadFormAsync();
                
                string userUuid = form["userUuid"].ToString();
                string serverId = form["serverId"].ToString();
                
                string targetPath = form["path"].ToString();
                if (string.IsNullOrWhiteSpace(targetPath)) targetPath = "/";
                
                _ = double.TryParse(form["disk"].ToString(), out double diskFloat);

                if (!ConfigManager.ValidateUUID(userUuid)) return Reply.Json(new { error = "Invalid userUuid" }, statusCode: 400);
                if (!ConfigManager.ValidateServerID(serverId)) return Reply.Json(new { error = "Invalid serverId" }, statusCode: 400);
                if (!ConfigManager.HasPermission(userUuid, serverId)) return Reply.Json(new { error = "Sem permissão" }, statusCode: 403);

                var file = form.Files.FirstOrDefault();
                if (file == null || file.Length == 0)
                    return Reply.Json(new { error = "Arquivo não enviado" }, statusCode: 400);

                string basePath = Path.GetFullPath(Path.Combine(GlobalBasePath, serverId));
                string absPath = Path.GetFullPath(Path.Combine(basePath, SanitizePath(targetPath)));

                if (!absPath.StartsWith(basePath)) return Reply.Json(new { error = "Acesso negado" }, statusCode: 403);

                long quotaBytes = (long)(diskFloat * 1024 * 1024);

                if (quotaBytes > 0)
                {
                    long currentSize = GetServerDiskUsage(basePath);
                    long oldSize = File.Exists(absPath) ? new FileInfo(absPath).Length : 0L;
                    long sizeDiff = file.Length - oldSize;

                    if (sizeDiff > 0 && currentSize + sizeDiff > quotaBytes)
                        return Reply.Json(new { error = $"Upload negado: limite de disco excedido (Cota: {diskFloat} MB)" }, statusCode: 400);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);

                using (var stream = new FileStream(absPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await file.CopyToAsync(stream);
                }

                return Reply.Json(new { status = "success" });
            }
            catch (Exception ex)
            {
                return Reply.Json(new { error = $"Erro no upload: {ex.Message}" }, statusCode: 500);
            }
        }

        private static async Task<IResult> HandleMassArchive(List<string> paths, string basePath, string currentDirAbsPath)
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string zipName = $"archive-{timestamp}.zip";
            // Correção: Agora o zip vai ser salvo no currentDirAbsPath (a pasta atual de ondem chamaram a ação)
            string zipFile = Path.Combine(currentDirAbsPath, zipName); 

            try
            {
                await Task.Run(() =>
                {
                    using var fs = new FileStream(zipFile, FileMode.Create);
                    using var archive = new ZipArchive(fs, ZipArchiveMode.Create);

                    foreach (string p in paths)
                    {
                        string targetPath = Path.GetFullPath(Path.Combine(basePath, SanitizePath(p)));
                        if (!targetPath.StartsWith(basePath)) continue;

                        if (File.Exists(targetPath))
                        {
                            string relPath = Path.GetRelativePath(basePath, targetPath).Replace("\\", "/");
                            AddFileToArchive(archive, targetPath, relPath);
                        }
                        else if (Directory.Exists(targetPath))
                        {
                            var files = Directory.GetFiles(targetPath, "*", SearchOption.AllDirectories);
                            foreach (var f in files)
                            {
                                string relPath = Path.GetRelativePath(basePath, f).Replace("\\", "/");
                                AddFileToArchive(archive, f, relPath);
                            }
                        }
                    }
                });
                return Reply.Json(new { status = "success" });
            }
            catch (Exception e)
            {
                // Limpeza em caso de erro na compactação
                if (File.Exists(zipFile)) File.Delete(zipFile); 
                return Reply.Json(new { error = $"Falha ao criar arquivo zip: {e.Message}" }, statusCode: 500);
            }
        }

        private static void AddFileToArchive(ZipArchive archive, string filePath, string entryName)
        {
            // FileShare.ReadWrite fundamental pra não explodir ao clipar arquivos abertos/log servers
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
            using var entryStream = entry.Open();
            fs.CopyTo(entryStream);
        }

        private static async Task<IResult> HandleUnarchive(FileBody body, string absArchive, string basePath)
        {
            string destRel;
            if (string.IsNullOrEmpty(body.Destination))
            {
                string sanitized = SanitizePath(body.Path);
                int lastSlash = sanitized.LastIndexOf('/');
                destRel = lastSlash >= 0 ? sanitized.Substring(0, lastSlash) : "";
            }
            else
            {
                destRel = SanitizePath(body.Destination);
            }

            string destAbs = Path.GetFullPath(Path.Combine(basePath, destRel));

            if (!destAbs.StartsWith(basePath))
                return Reply.Json(new { error = "Destino inválido" }, statusCode: 403);
                
            Directory.CreateDirectory(destAbs);

            if (absArchive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await Task.Run(() =>
                    {
                        using var fs = new FileStream(absArchive, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var archive = new ZipArchive(fs, ZipArchiveMode.Read);
                        long totalSize = 0;
                        
                        if (archive.Entries.Count > MAX_ZIP_FILES)
                            throw new Exception("Zip contém muitos arquivos");

                        foreach (var entry in archive.Entries)
                        {
                            string newPath = Path.GetFullPath(Path.Combine(destAbs, entry.FullName));
                            
                            if (!newPath.StartsWith(destAbs))
                                throw new Exception("Zip Slip detectado");

                            if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith("/")) 
                            {
                                Directory.CreateDirectory(newPath);
                                continue;
                            }

                            if (entry.Length > MAX_UNZIP_FILE_SIZE)
                                throw new Exception("Arquivo extraído muito grande");
                                
                            totalSize += entry.Length;
                            if (totalSize > MAX_UNZIP_TOTAL_SIZE)
                                throw new Exception("Tamanho total descompactado excedeu o limite");

                            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);

                            // Extração manual via stream para sobrescrever tranquilamente
                            using var entryStream = entry.Open();
                            using var destStream = new FileStream(newPath, FileMode.Create, FileAccess.Write, FileShare.None);
                            entryStream.CopyTo(destStream);
                        }
                    });
                    return Reply.Json(new { status = "success" });
                }
                catch (Exception e)
                {
                    return Reply.Json(new { error = $"Erro ao descompactar zip: {e.Message}" }, statusCode: 500);
                }
            }
            else if (absArchive.EndsWith(".tar.gz") || absArchive.EndsWith(".tgz"))
            {
                return Reply.Json(new { error = "Descompactação de tar.gz requer lib adicional no backend C# (Ex: SharpZipLib)." }, statusCode: 501);
            }

            return Reply.Json(new { error = "Formato não suportado para descompactação" }, statusCode: 400);
        }

        // --- Utils / Helpers ---
        private static string SanitizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            var segments = path.Replace("\\", "/").Split('/')
                               .Where(s => !string.IsNullOrWhiteSpace(s) && s != ".." && s != ".");
            return string.Join("/", segments);
        }

        private static string CombinePaths(string relPath, string name)
        {
            return string.IsNullOrEmpty(relPath) ? name : $"{relPath}/{name}";
        }

        private static long GetServerDiskUsage(string baseFolder)
        {
            if (!Directory.Exists(baseFolder)) return 0L;
            // Catch caso não consiga acessar algo interno no calculo da cota
            try {
                return new DirectoryInfo(baseFolder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            } catch {
                return 0L;
            }
        }

        private static void DeleteRecursively(string path)
        {
            if (Directory.Exists(path))
            {
                try {
                    Directory.Delete(path, true);
                } catch { 
                    // Se estiver em uso, tenta apagar item por item (evitará crash em chain)
                    foreach(var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories)) {
                        try { File.Delete(file); } catch { }
                    }
                    try { Directory.Delete(path, true); } catch { }
                }
            }
            else if (File.Exists(path))
            {
                try { File.Delete(path); } catch { }
            }
        }
    }
}