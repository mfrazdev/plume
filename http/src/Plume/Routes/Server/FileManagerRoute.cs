using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Plume.Configuration;

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
            int UserUuid = 0, // <-- AQUI! Voltamos para int para o JSON não surtar
            string ServerId = "",
            double Disk = 0.0,
            string Path = "",
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

        // O Helper do Ktor correspondente
        private static string GlobalBasePath => Path.Combine(ConfigManager.GlobalConfig.App.Path, "servers");

        public static void Register(RouteGroupBuilder group)
        {
            var fileGroup = group.MapGroup("/servers/filemanager");

            fileGroup.MapPost("/upload", (Delegate)HandleUpload);
            fileGroup.MapGet("/download", (Delegate)HandleDirectDownload);
            
            // Aceita POST ou GET para o handler genérico, lendo o body se houver
            fileGroup.MapPost("/{action}", FileManagerHandler);
            fileGroup.MapGet("/{action}", FileManagerHandler);
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
                    return Results.Json(new { error = "Invalid JSON" }, statusCode: 400);
                }
            }

            if (body == null)
                return Results.Json(new { error = "Invalid JSON or missing body" }, statusCode: 400);

            // Validações rigorosas
            // Validações rigorosas
            if (!ConfigManager.ValidateUUID(body.UserUuid.ToString()))
                return Results.Json(new { error = "Invalid userUuid" }, statusCode: 400);
    
                
            if (!ConfigManager.ValidateServerID(body.ServerId))
                return Results.Json(new { error = "Invalid serverId" }, statusCode: 400);

            if (!ConfigManager.HasPermission(body.UserUuid.ToString(), body.ServerId))
                return Results.Json(new { error = "Sem permissão" }, statusCode: 403);

            string basePath = Path.GetFullPath(Path.Combine(GlobalBasePath, body.ServerId));
            string relPath = SanitizePath(body.Path);
            string absPath = string.IsNullOrEmpty(relPath) ? basePath : Path.GetFullPath(Path.Combine(basePath, relPath));

            // Segurança contra Path Traversal
            if (!absPath.StartsWith(basePath))
                return Results.Json(new { error = "Acesso negado (Traversal)" }, statusCode: 403);

            long quotaBytes = (long)(body.Disk * 1024 * 1024);

            switch (action.ToLower())
            {
                case "list":
                {
                    if (!Directory.Exists(absPath))
                        return Results.Json(new { error = "Diretório não encontrado" }, statusCode: 404);

                    var dirInfo = new DirectoryInfo(absPath);
                    var items = new List<FileItem>();

                    foreach (var dir in dirInfo.GetDirectories())
                    {
                        items.Add(new FileItem(dir.Name, "folder", 0, new DateTimeOffset(dir.LastWriteTimeUtc).ToUnixTimeMilliseconds(), CombinePaths(relPath, dir.Name)));
                    }
                    foreach (var file in dirInfo.GetFiles())
                    {
                        items.Add(new FileItem(file.Name, "file", file.Length, new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(), CombinePaths(relPath, file.Name)));
                    }

                    return Results.Ok(new { status = "success", items = items, path = "/" + relPath });
                }

                case "read":
                {
                    if (!File.Exists(absPath))
                        return Results.Json(new { error = "Arquivo não encontrado" }, statusCode: 404);

                    string content = await File.ReadAllTextAsync(absPath);
                    return Results.Ok(new { status = "success", content = content, path = "/" + relPath });
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
                            return Results.Json(new { error = "Cota de disco excedida!" }, statusCode: 400);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);
                    await File.WriteAllTextAsync(absPath, body.Content ?? "");
                    return Results.Ok(new { status = "success" });
                }

                case "rename":
                {
                    if (string.IsNullOrEmpty(body.NewName) || body.NewName.Contains("/") || body.NewName.Contains("\\"))
                        return Results.Json(new { error = "Nome inválido" }, statusCode: 400);

                    string newAbs = Path.Combine(Path.GetDirectoryName(absPath)!, body.NewName);
                    
                    if (File.Exists(absPath)) File.Move(absPath, newAbs);
                    else if (Directory.Exists(absPath)) Directory.Move(absPath, newAbs);
                    
                    return Results.Ok(new { status = "success" });
                }

                case "mkdir":
                {
                    Directory.CreateDirectory(absPath);
                    return Results.Ok(new { status = "success" });
                }

                case "delete":
                {
                    DeleteRecursively(absPath);
                    return Results.Ok(new { status = "success" });
                }

                case "download":
                {
                    if (File.Exists(absPath))
                        return Results.File(absPath, fileDownloadName: Path.GetFileName(absPath));
                    return Results.Json(new { error = "Arquivo não encontrado" }, statusCode: 404);
                }

                case "move":
                {
                    string destRel = SanitizePath(body.To);
                    string destAbs = Path.GetFullPath(Path.Combine(basePath, destRel, Path.GetFileName(absPath)));

                    if (!destAbs.StartsWith(basePath))
                        return Results.Json(new { error = "Destino inválido" }, statusCode: 403);

                    Directory.CreateDirectory(Path.GetDirectoryName(destAbs)!);
                    
                    if (File.Exists(absPath)) File.Move(absPath, destAbs);
                    else if (Directory.Exists(absPath)) Directory.Move(absPath, destAbs);
                    
                    return Results.Ok(new { status = "success" });
                }

                case "unarchive":
                {
                    if (quotaBytes > 0 && GetServerDiskUsage(basePath) >= quotaBytes)
                        return Results.Json(new { error = "Cota de disco excedida, limpe espaço antes de extrair." }, statusCode: 400);
                        
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
                        return Results.Ok(new { status = "success" });
                    }
                    else if (body.Action.Equals("archive", StringComparison.OrdinalIgnoreCase))
                    {
                        if (quotaBytes > 0 && GetServerDiskUsage(basePath) >= quotaBytes)
                            return Results.Json(new { error = "Cota de disco cheia, impossível criar arquivo." }, statusCode: 400);
                            
                        return await HandleMassArchive(pathsToProcess, basePath);
                    }
                    return Results.Json(new { error = "Ação de mass desconhecida" }, statusCode: 400);
                }

                default:
                    return Results.Json(new { error = "Ação desconhecida" }, statusCode: 400);
            }
        }

        private static async Task<IResult> HandleDirectDownload(HttpContext context)
        {
            string userUuid = context.Request.Query["userUuid"].ToString();
            string serverId = context.Request.Query["serverId"].ToString();
            string targetPath = context.Request.Query["path"].ToString();

            if (!ConfigManager.ValidateUUID(userUuid)) return Results.Json(new { error = "Invalid userUuid" }, statusCode: 400);
            if (!ConfigManager.ValidateServerID(serverId)) return Results.Json(new { error = "Invalid serverId" }, statusCode: 400);
            if (!ConfigManager.HasPermission(userUuid, serverId)) return Results.Json(new { error = "Sem permissão" }, statusCode: 403);

            string basePath = Path.GetFullPath(Path.Combine(GlobalBasePath, serverId));
            string absPath = Path.GetFullPath(Path.Combine(basePath, SanitizePath(targetPath)));

            if (!absPath.StartsWith(basePath)) return Results.Json(new { error = "Acesso negado (Traversal)" }, statusCode: 403);

            if (!File.Exists(absPath))
                return Results.Json(new { error = "Arquivo não encontrado" }, statusCode: 404);

            return Results.File(absPath, fileDownloadName: Path.GetFileName(absPath));
        }

        private static async Task<IResult> HandleUpload(HttpContext context)
        {
            if (!context.Request.HasFormContentType)
                return Results.Json(new { error = "Esperado form-data" }, statusCode: 400);

            var form = await context.Request.ReadFormAsync();
            
            string userUuid = form["userUuid"].ToString();
            string serverId = form["serverId"].ToString();
            string targetPath = form["path"].ToString();
            _ = double.TryParse(form["disk"].ToString(), out double diskFloat);

            if (!ConfigManager.ValidateUUID(userUuid)) return Results.Json(new { error = "Invalid userUuid" }, statusCode: 400);
            if (!ConfigManager.ValidateServerID(serverId)) return Results.Json(new { error = "Invalid serverId" }, statusCode: 400);
            if (!ConfigManager.HasPermission(userUuid, serverId)) return Results.Json(new { error = "Sem permissão" }, statusCode: 403);

            var file = form.Files.FirstOrDefault();
            if (file == null || file.Length == 0)
                return Results.Json(new { error = "Arquivo não enviado" }, statusCode: 400);

            string basePath = Path.GetFullPath(Path.Combine(GlobalBasePath, serverId));
            string absPath = Path.GetFullPath(Path.Combine(basePath, SanitizePath(targetPath)));

            if (!absPath.StartsWith(basePath)) return Results.Json(new { error = "Acesso negado" }, statusCode: 403);

            long quotaBytes = (long)(diskFloat * 1024 * 1024);

            if (quotaBytes > 0)
            {
                long currentSize = GetServerDiskUsage(basePath);
                long oldSize = File.Exists(absPath) ? new FileInfo(absPath).Length : 0L;
                long sizeDiff = file.Length - oldSize;

                if (sizeDiff > 0 && currentSize + sizeDiff > quotaBytes)
                    return Results.Json(new { error = $"Upload negado: limite de disco excedido (Cota: {diskFloat} MB)" }, statusCode: 400);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);

            using (var stream = new FileStream(absPath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            return Results.Ok(new { status = "success" });
        }

        private static async Task<IResult> HandleMassArchive(List<string> paths, string basePath)
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string zipName = $"{timestamp}.zip";
            string zipFile = Path.Combine(basePath, zipName);

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
                            archive.CreateEntryFromFile(targetPath, relPath, CompressionLevel.NoCompression);
                        }
                        else if (Directory.Exists(targetPath))
                        {
                            var files = Directory.GetFiles(targetPath, "*", SearchOption.AllDirectories);
                            foreach (var f in files)
                            {
                                string relPath = Path.GetRelativePath(basePath, f).Replace("\\", "/");
                                archive.CreateEntryFromFile(f, relPath, CompressionLevel.NoCompression);
                            }
                        }
                    }
                });
                return Results.Ok(new { status = "success" });
            }
            catch (Exception e)
            {
                return Results.Json(new { error = $"Falha ao criar arquivo zip: {e.Message}" }, statusCode: 500);
            }
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
                return Results.Json(new { error = "Destino inválido" }, statusCode: 403);
                
            Directory.CreateDirectory(destAbs);

            if (absArchive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await Task.Run(() =>
                    {
                        using var archive = ZipFile.OpenRead(absArchive);
                        long totalSize = 0;
                        
                        if (archive.Entries.Count > MAX_ZIP_FILES)
                            throw new Exception("Zip contém muitos arquivos");

                        foreach (var entry in archive.Entries)
                        {
                            string newPath = Path.GetFullPath(Path.Combine(destAbs, entry.FullName));
                            
                            // Defesa de Zip Slip
                            if (!newPath.StartsWith(destAbs))
                                throw new Exception("Zip Slip detectado");

                            if (string.IsNullOrEmpty(entry.Name)) // É um diretório
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
                            entry.ExtractToFile(newPath, overwrite: true);
                        }
                    });
                    return Results.Ok(new { status = "success" });
                }
                catch (Exception e)
                {
                    return Results.Json(new { error = $"Erro ao descompactar zip: {e.Message}" }, statusCode: 500);
                }
            }
            else if (absArchive.EndsWith(".tar.gz") || absArchive.EndsWith(".tgz"))
            {
                return Results.Json(new { error = "Descompactação de tar.gz requer lib adicional no backend C# (Ex: SharpZipLib)." }, statusCode: 501);
            }

            return Results.Json(new { error = "Formato não suportado para descompactação" }, statusCode: 400);
        }

        // --- Utils / Helpers ---
        private static string SanitizePath(string path)
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
            return new DirectoryInfo(baseFolder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }

        private static void DeleteRecursively(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
            else if (File.Exists(path))
                File.Delete(path);
        }
    }
}