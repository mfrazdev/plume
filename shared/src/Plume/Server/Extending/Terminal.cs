using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Plume.Server.Extending;

public static class ServerLogging {
    // Regex compilado estaticamente para alta performance na limpeza de códigos ANSI
    private static readonly Regex AnsiClearRegex = new Regex(@"\x1B(c|\[[0-9;]*[HJ])", RegexOptions.Compiled);
    

    extension(Server server)
    {
        public async Task SendCommandAsync(string input) {
        
            string cId;
            server.Lock.EnterReadLock();
            try {
                cId = server.ContainerId;
            } finally {
                server.Lock.ExitReadLock();
            }

            if (string.IsNullOrWhiteSpace(cId)) return;

            _ = Task.Run(async () => {
                try {
                    Stream socketStream;

                    string? dockerHostEnv = Environment.GetEnvironmentVariable("DOCKER_HOST");
                
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                        var pipe = new NamedPipeClientStream(".", "docker_engine", PipeDirection.InOut, PipeOptions.Asynchronous);
                        await pipe.ConnectAsync(5000);
                        socketStream = pipe;
                    } else {
                        string path = "/var/run/docker.sock";
                        if (!string.IsNullOrEmpty(dockerHostEnv) && dockerHostEnv.StartsWith("unix://")) {
                            path = dockerHostEnv.Substring("unix://".Length);
                        }
                        var endpoint = new UnixDomainSocketEndPoint(path);
                        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                        await socket.ConnectAsync(endpoint);
                        socketStream = new NetworkStream(socket, ownsSocket: true);
                    }

                    using (socketStream) {
                        string request = $"POST /containers/{cId}/attach?stream=1&stdin=1 HTTP/1.1\r\n" +
                                         "Host: localhost\r\n" +
                                         "Connection: Upgrade\r\n" +
                                         "Upgrade: tcp\r\n\r\n";
                    
                        byte[] reqBytes = Encoding.UTF8.GetBytes(request);
                        await socketStream.WriteAsync(reqBytes, 0, reqBytes.Length);
                        await socketStream.FlushAsync();

                        var buffer = new byte[1024];
                        int read = await socketStream.ReadAsync(buffer, 0, buffer.Length);
                        string response = Encoding.UTF8.GetString(buffer, 0, read);

                        if (!response.Contains("101 UPGRADED") && !response.Contains("101 Switching")) {
                            Console.WriteLine($"[RAW-SOCKET] [ERRO] Falha no hijack! Resposta do Docker: {response}");
                            return;
                        }
                        byte[] cmdBytes = Encoding.UTF8.GetBytes(input + "\n");
                        await socketStream.WriteAsync(cmdBytes, 0, cmdBytes.Length);
                        await socketStream.FlushAsync(); 

                        await Task.Delay(200);
                    
                    }
                } catch (Exception ex) {
                    Console.WriteLine($"[RAW-SOCKET] [ERRO CRÍTICO]: {ex.Message}");
                }
            });
        }

        public Action StreamLogs(int tail, Action<string> onLine) {
            string cId;
            server.Lock.EnterReadLock();
            try {
                cId = server.ContainerId;
            } finally {
                server.Lock.ExitReadLock();
            }

            if (string.IsNullOrWhiteSpace(cId)) return () => {};

            var cts = new CancellationTokenSource();

            _ = Task.Run(async () => {
                try {
                    var inspect = await server.Docker.Containers.InspectContainerAsync(cId);
                    bool isTty = inspect.Config.Tty;

                    using var stream = await server.Docker.Containers.GetContainerLogsAsync(cId, isTty, new ContainerLogsParameters {
                        ShowStdout = true,
                        ShowStderr = true,
                        Follow = true,
                        Tail = tail.ToString()
                    }, cts.Token);

                    var buffer = new byte[81920];
                    var leftover = new StringBuilder(); // FIX: Buffer para linhas incompletas

                    while (!cts.Token.IsCancellationRequested) {
                        var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cts.Token);
                        if (result.EOF) break;

                        string txt = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        string fullText = leftover + txt;
                        leftover.Clear();

                        var lines = fullText.Split(new [] { '\n' }, StringSplitOptions.None);
                    
                        // A última string do array é o "resto" da linha, guardamos ela
                        for (int i = 0; i < lines.Length - 1; i++) {
                            string l = lines[i].TrimEnd('\r');
                            if (!string.IsNullOrWhiteSpace(l)) onLine(l);
                        }
                        leftover.Append(lines.Last());
                    }

                    if (leftover.Length > 0) {
                        string l = leftover.ToString().TrimEnd('\r');
                        if (!string.IsNullOrWhiteSpace(l)) onLine(l);
                    }

                } catch (DockerContainerNotFoundException) {
                    // Silencioso
                } catch (OperationCanceledException) {
                    // Silencioso
                } catch (Exception e) {
                    Console.WriteLine($"Erro na stream de logs: {e.Message}");
                }
            }, cts.Token);

            return () => cts.Cancel();
        }

        public void AttachLogStream(string doneStr) {
            string cId;
            server.Lock.EnterReadLock();
            try {
                cId = server.ContainerId;
            } finally {
                server.Lock.ExitReadLock();
            }

            if (string.IsNullOrWhiteSpace(cId)) return;

            _ = Task.Run(async () => {
                try {
                    var inspect = await server.Docker.Containers.InspectContainerAsync(cId);
                    bool isTty = inspect.Config.Tty;

                    using var stream = await server.Docker.Containers.GetContainerLogsAsync(cId, isTty, new ContainerLogsParameters {
                        ShowStdout = true,
                        ShowStderr = true,
                        Follow = true
                    });

                    var buffer = new byte[81920];
                    var leftover = new StringBuilder(); // FIX: Mesma correção do buffer pra cá

                    while (true) {
                        var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, CancellationToken.None);
                        if (result.EOF) break;

                        string rawChunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        string fullText = leftover + rawChunk;
                        leftover.Clear();

                        var lines = fullText.Split(new [] { '\n' }, StringSplitOptions.None);

                        for (int i = 0; i < lines.Length - 1; i++) {
                            string line = lines[i].TrimEnd('\r');

                            if (AnsiClearRegex.IsMatch(line)) {
                                server.EmitLive("clear", "");
                            }

                            line = AnsiClearRegex.Replace(line, "");

                            if (string.IsNullOrWhiteSpace(line.Trim())) continue;

                            server.EmitLive("log", line);

                            if (!string.IsNullOrWhiteSpace(doneStr) && line.Contains(doneStr)) {
                                bool doEmit = false;
                                server.Lock.EnterWriteLock();
                                try {
                                    if (server.Status != "running") {
                                        server.Status = "running";
                                        server.StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                        doEmit = true;
                                    }
                                } finally {
                                    server.Lock.ExitWriteLock();
                                }

                                if (doEmit) {
                                    server.EmitLive("status", "Servidor marcado como online...");
                                    server.EmitLive("internal", "running");
                                }
                            }
                        }
                        leftover.Append(lines.Last());
                    }
                } catch (DockerContainerNotFoundException) {
                }
                catch (Exception)
                {
                    // ignored
                }
            });
        }
    }
}