using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Plume.Configuration;
using Plume.Http; // Assumindo que Reply esteja aqui

namespace Plume.Http.Routes.Server
{
    public static class FileManagerRoute
    {
        // Constantes de proteção
        private const long MAX_UNZIP_FILE_SIZE = 3L * 1024 * 1024 * 1024; // 3GB por arquivo
        private const long MAX_UNZIP_TOTAL_SIZE = 3L * 1024 * 1024 * 1024; // 3GB total
        private const int MAX_ZIP_FILES = 100000;

        // DTOs Mapeados exatamente como o @Serializable do Kotlin
        public record FileBody(
            [property: JsonPropertyName("userUuid")] int UserUuid = 0,
            [property: JsonPropertyName("serverId")] string ServerId = "",
            [property: JsonPropertyName("disk")] double Disk = 0.0,
            [property: JsonPropertyName("path")] string Path = "",
            [property: JsonPropertyName("newName")] string NewName = "",
            [property: JsonPropertyName("content")] string Content = "",
            [property: JsonPropertyName("action")] string Action = "",
            [property: JsonPropertyName("paths")] List<string>? Paths = null,
            [property: JsonPropertyName("to")] string To = "",
            [property: JsonPropertyName("destination")] string Destination = ""
        );

        public record FileItem(
            [property: JsonPropertyName("name")] string Name,
            [property: JsonPropertyName("type")] string Type,
            [property: JsonPropertyName("size")] long Size,
            [property: JsonPropertyName("lastModified")] long LastModified,
            [property: JsonPropertyName("path")] string Path
        );

        private static string GlobalBasePath => Path.Combine(ConfigManager.GlobalConfig.App.Path, "servers");

        // Helper para emular call.respondError e call.respondSuccess do Kotlin
        private static IResult RespondError(string message, int status) => Reply.Json(new { error = message }, status);
        private static IResult RespondSuccess() => Reply.Json(new { status = "success" });

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
                catch
                {
                    return RespondError("Invalid JSON", 400);
                }
            }

            if (body == null)
                return RespondError("Invalid JSON", 400);

            // Validações rigorosas baseadas no Kotlin
            if (!ConfigManager.ValidateUUID(body.UserUuid.ToString()))
                return RespondError("Invalid userUuid", 400);
    
            if (!ConfigManager.ValidateServerID(body.ServerId))
                return RespondError("Invalid serverId", 400);

            if (!ConfigManager.HasPermission(body.UserUuid.ToString(), body.ServerId))
                return RespondError("Sem permissão", 403);

            string basePath = Path.GetFullPath(Path.Combine(GlobalBasePath, body.ServerId));
            string relPath = SanitizePath(body.Path);
            
            // absPath = if(relPath.isEmpty()) basePath else basePath.resolve(relPath).normalize()
            string absPath = string.IsNullOrEmpty(relPath) ? basePath : Path.GetFullPath(Path.Combine(basePath, relPath));

            // Segurança contra Path Traversal
            if (!absPath.StartsWith(basePath))
                return RespondError("Acesso negado (Traversal)", 403);

            long quotaBytes = (long)(body.Disk * 1024 * 1024);

            try
            {
                switch (action.ToLower())
                {
                    case "list":
                    {
                        if (!Directory.Exists(absPath))
                            return RespondError("Diretório não encontrado", 404);

                        var items = new List<FileItem>();
                        var dirInfo = new DirectoryInfo(absPath);

                        try
                        {
                            foreach (var f in dirInfo.GetFileSystemInfos())
                            {
                                bool isDir = (f.Attributes & FileAttributes.Directory) == FileAttributes.Directory;
                                string type = isDir ? "folder" : "file";
                                long size = isDir ? 0L : ((FileInfo)f).Length;
                                long lastModified = new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                                
                                string itemPath = SanitizePath(string.IsNullOrEmpty(relPath) ? f.Name : $"{relPath}/{f.Name}");
                                items.Add(new FileItem(f.Name, type, size, lastModified, itemPath));
                            }
                        }
                        catch { /* Ignora caso falte permissão em alguma subpasta/arquivo específico */ }

                        // Equivalente ao FileListResponse do Kotlin
                        return Reply.Json(new { items = items, path = "/" + relPath });
                    }

                    case "read":
                    {
                        if (!File.Exists(absPath))
                            return RespondError("Arquivo não encontrado", 404);

                        using var fs = new FileStream(absPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var sr = new StreamReader(fs);
                        string content = await sr.ReadToEndAsync();
                        
                        // Equivalente ao FileReadResponse do Kotlin
                        return Reply.Json(new { content = content, path = "/" + relPath });
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
                                return RespondError("Cota de disco excedida!", 400);
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);
                        await File.WriteAllTextAsync(absPath, body.Content ?? "");
                        return RespondSuccess();
                    }

                    case "rename":
                    {
                        if (string.IsNullOrEmpty(body.NewName) || body.NewName.Contains("/") || body.NewName.Contains("\\"))
                            return RespondError("Nome inválido", 400);

                        string newAbs = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(absPath)!, body.NewName));
                        
                        try 
                        {
                            if (File.Exists(absPath)) File.Move(absPath, newAbs, true);
                            else if (Directory.Exists(absPath)) Directory.Move(absPath, newAbs);
                        } 
                        catch { /* Kotlin's renameTo ignora silenciosamente erros do OS */ }
                        
                        return RespondSuccess();
                    }

                    case "mkdir":
                    {
                        Directory.CreateDirectory(absPath);
                        return RespondSuccess();
                    }

                    case "delete":
                    {
                        DeleteRecursively(absPath);
                        return RespondSuccess();
                    }

                    case "download":
                    {
                        if (File.Exists(absPath))
                        {
                            var fs = new FileStream(absPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            return Results.File(fs, fileDownloadName: Path.GetFileName(absPath));
                        }
                        return RespondError("Arquivo não encontrado", 404);
                    }

                    case "move":
                    {
                        string destRel = SanitizePath(body.To);
                        string itemName = new DirectoryInfo(absPath).Name; 
                        string destAbs = Path.GetFullPath(Path.Combine(basePath, destRel, itemName));

                        if (!destAbs.StartsWith(basePath))
                            return RespondError("Destino inválido", 403);

                        Directory.CreateDirectory(Path.GetDirectoryName(destAbs)!);
                        
                        try 
                        {
                            if (File.Exists(absPath)) File.Move(absPath, destAbs, true);
                            else if (Directory.Exists(absPath)) Directory.Move(absPath, destAbs);
                        } 
                        catch { /* Semelhante ao fail silencioso do kotlin renameTo */ }
                        
                        return RespondSuccess();
                    }

                    case "unarchive":
                    {
                        if (quotaBytes > 0 && GetServerDiskUsage(basePath) >= quotaBytes)
                            return RespondError("Cota de disco excedida, limpe espaço antes de extrair.", 400);
                            
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
                            return RespondSuccess();
                        }
                        else if (body.Action.Equals("archive", StringComparison.OrdinalIgnoreCase))
                        {
                            if (quotaBytes > 0 && GetServerDiskUsage(basePath) >= quotaBytes)
                                return RespondError("Cota de disco cheia, impossível criar arquivo.", 400);
                                
                            // No Kotlin o basePath era usado diretamente pra armazenar e calcular parent relativos
                            return await HandleMassArchive(pathsToProcess, basePath);
                        }
                        return RespondError("Ação de mass desconhecida", 400);
                    }

                    default:
                        return RespondError("Ação desconhecida", 400);
                }
            }
            catch (Exception ex)
            {
                return RespondError($"Erro ao executar ação: {ex.Message}", 500);
            }
        }

        private static async Task<IResult> HandleDirectDownload(HttpContext context)
        {
            try
            {
                string userUuid = context.Request.Query["userUuid"].ToString();
                string serverId = context.Request.Query["serverId"].ToString();
                string targetPath = context.Request.Query["path"].ToString();

                if (!ConfigManager.ValidateUUID(userUuid)) return RespondError("Invalid userUuid", 400);
                if (!ConfigManager.ValidateServerID(serverId)) return RespondError("Invalid serverId", 400);
                if (!ConfigManager.HasPermission(userUuid, serverId)) return RespondError("Sem permissão", 403);

                string basePath = Path.GetFullPath(Path.Combine(GlobalBasePath, serverId));
                string absPath = Path.GetFullPath(Path.Combine(basePath, SanitizePath(targetPath)));

                if (!absPath.StartsWith(basePath)) return RespondError("Acesso negado (Traversal)", 403);

                if (!File.Exists(absPath))
                    return RespondError("Arquivo não encontrado", 404);

                var fs = new FileStream(absPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return Results.File(fs, fileDownloadName: Path.GetFileName(absPath));
            }
            catch (Exception ex)
            {
                return RespondError($"Erro ao processar download: {ex.Message}", 500);
            }
        }

        private static async Task<IResult> HandleUpload(HttpContext context)
        {
            if (!context.Request.HasFormContentType)
                return RespondError("Esperado form-data", 400);

            try
            {
                var form = await context.Request.ReadFormAsync();
                
                string userUuid = form["userUuid"].ToString();
                string serverId = form["serverId"].ToString();
                string targetPath = form["path"].ToString();
                _ = double.TryParse(form["disk"].ToString(), out double diskFloat);

                if (!ConfigManager.ValidateUUID(userUuid)) return RespondError("Invalid userUuid", 400);
                if (!ConfigManager.ValidateServerID(serverId)) return RespondError("Invalid serverId", 400);
                if (!ConfigManager.HasPermission(userUuid, serverId)) return RespondError("Sem permissão", 403);

                var file = form.Files.FirstOrDefault();
                if (file == null || file.Length == 0)
                    return RespondError("Arquivo não enviado", 400);

                string basePath = Path.GetFullPath(Path.Combine(GlobalBasePath, serverId));
                string absPath = Path.GetFullPath(Path.Combine(basePath, SanitizePath(targetPath)));

                if (!absPath.StartsWith(basePath)) return RespondError("Acesso negado", 403);

                long quotaBytes = (long)(diskFloat * 1024 * 1024);

                if (quotaBytes > 0)
                {
                    long currentSize = GetServerDiskUsage(basePath);
                    long oldSize = File.Exists(absPath) ? new FileInfo(absPath).Length : 0L;
                    long sizeDiff = file.Length - oldSize;

                    if (sizeDiff > 0 && currentSize + sizeDiff > quotaBytes)
                        return RespondError($"Upload negado: limite de disco excedido (Cota: {diskFloat} MB)", 400);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);

                // No C# CopyToAsync é melhor em performance e memoria do que ler tudo para array de byte igual no Ktor
                using (var stream = new FileStream(absPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await file.CopyToAsync(stream);
                }

                return RespondSuccess();
            }
            catch (Exception ex)
            {
                return RespondError($"Erro no upload: {ex.Message}", 500);
            }
        }

        private static async Task<IResult> HandleMassArchive(List<string> paths, string basePath)
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string zipName = $"{timestamp}.zip";
            // O Kotlin salvava exatamente no basePath
            string zipFile = Path.Combine(basePath, zipName); 

            try
            {
                await Task.Run(() =>
                {
                    using var fs = new FileStream(zipFile, FileMode.Create);
                    // Equivalente ao Deflater.NO_COMPRESSION do Kotlin (Mágica de Velocidade)
                    using var archive = new ZipArchive(fs, ZipArchiveMode.Create);

                    foreach (string p in paths)
                    {
                        string targetPath = Path.GetFullPath(Path.Combine(basePath, SanitizePath(p)));
                        if (!targetPath.StartsWith(basePath)) continue;

                        if (File.Exists(targetPath))
                        {
                            string relPath = SanitizePath(Path.GetRelativePath(basePath, targetPath));
                            AddFileToArchive(archive, targetPath, relPath);
                        }
                        else if (Directory.Exists(targetPath))
                        {
                            var files = Directory.GetFiles(targetPath, "*", SearchOption.AllDirectories);
                            if (files.Length == 0)
                            {
                                string relPath = SanitizePath(Path.GetRelativePath(basePath, targetPath)) + "/";
                                archive.CreateEntry(relPath, CompressionLevel.NoCompression);
                            }
                            else
                            {
                                foreach (var f in files)
                                {
                                    string relPath = SanitizePath(Path.GetRelativePath(basePath, f));
                                    AddFileToArchive(archive, f, relPath);
                                }
                            }
                        }
                    }
                });
                return RespondSuccess();
            }
            catch (Exception e)
            {
                if (File.Exists(zipFile)) File.Delete(zipFile); 
                return RespondError($"Falha ao criar arquivo zip: {e.Message}", 500);
            }
        }

        private static void AddFileToArchive(ZipArchive archive, string filePath, string entryName)
        {
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
                return RespondError("Destino inválido", 403);
                
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
                        int fileCount = 0;

                        foreach (var entry in archive.Entries)
                        {
                            fileCount++;
                            if (fileCount > MAX_ZIP_FILES) throw new Exception("Zip contém muitos arquivos");

                            string newPath = Path.GetFullPath(Path.Combine(destAbs, entry.FullName));
                            if (!newPath.StartsWith(destAbs)) throw new Exception("Zip Slip detectado");

                            if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith("/")) 
                            {
                                Directory.CreateDirectory(newPath);
                                continue;
                            }

                            if (entry.Length > MAX_UNZIP_FILE_SIZE) throw new Exception("Arquivo extraído muito grande");
                                
                            totalSize += entry.Length;
                            if (totalSize > MAX_UNZIP_TOTAL_SIZE) throw new Exception("Tamanho total descompactado excedeu o limite");

                            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);

                            using var entryStream = entry.Open();
                            using var destStream = new FileStream(newPath, FileMode.Create, FileAccess.Write, FileShare.None);
                            
                            // Lendo os bytes em chunk (Buffer) igual ao InputStream Kotlin 
                            byte[] buffer = new byte[8192];
                            int bytesRead;
                            while ((bytesRead = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                destStream.Write(buffer, 0, bytesRead);
                            }
                        }
                    });
                    return RespondSuccess();
                }
                catch (Exception e)
                {
                    return RespondError($"Erro ao descompactar zip: {e.Message}", 500);
                }
            }
            else if (absArchive.EndsWith(".tar.gz") || absArchive.EndsWith(".tgz"))
            {
                return RespondError("Descompactação de tar.gz requer lib adicional no backend C# (Ex: SharpZipLib).", 501);
            }

            return RespondError("Formato não suportado para descompactação", 400);
        }

        // --- Utils / Helpers ---
        private static string SanitizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            var segments = path.Replace("\\", "/").Split('/')
                               .Where(s => !string.IsNullOrWhiteSpace(s) && s != ".." && s != ".");
            return string.Join("/", segments);
        }

        private static long GetServerDiskUsage(string baseFolder)
        {
            if (!Directory.Exists(baseFolder)) return 0L;
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