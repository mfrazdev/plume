using System.Text;
using Docker.DotNet.Models;
using Plume.Database;
using Plume.Types;

// ReSharper disable once CheckNamespace
namespace Plume.Server.Extending;

public static class ServerLifecycle {
  // ---- kill/delete ----
  extension(Server server)
  {
    public async Task KillAsync() {
      string cId;
      server.Lock.EnterWriteLock();
      try {
        if (server.Status == "installing") return;
        server.Status = "stopped";
        server.IsRestarting = false;
        server.IsStopping = false;
        cId = server.ContainerId;
      } finally {
        server.Lock.ExitWriteLock();
      }

      if (!string.IsNullOrWhiteSpace(cId)) {
        try {
          await server.Docker.Containers.KillContainerAsync(cId, new ContainerKillParameters {
            Signal = "SIGKILL"
          });
        } catch {
          /* Ignorado, assim como no Kotlin (runCatching) */ }
      }
    }

    public async Task DeleteAsync() {
      bool wasInstalling;
      server.Lock.EnterReadLock();
      try {
        wasInstalling = server.Status == "installing";
      } finally {
        server.Lock.ExitReadLock();
      }

      if (wasInstalling) return;

      await server.KillAsync();

      string cId;
      server.Lock.EnterWriteLock();
      try {
        cId = server.ContainerId;
        server.Status = "stopped";
        server.IsRestarting = false;
        server.IsStopping = false;
        server.StartedAt = null;
      } finally {
        server.Lock.ExitWriteLock();
      }

      if (!string.IsNullOrWhiteSpace(cId)) {
        try {
          await server.Docker.Containers.RemoveContainerAsync(cId, new ContainerRemoveParameters {
            Force = true
          });
        } catch (Exception ex) {
          Console.WriteLine($"Falha ao remover container {cId}: {ex.Message}");
        }
      }

      // Assumindo que este método virá no arquivo Monitor.cs correspondente ao Monitor.kt
      server.DestroyMonitor();

      try {
        Directory.Delete(server.ServerPath(), true);
      } catch (Exception ex) {
        Console.WriteLine($"Falha ao remover arquivos do servidor {server.Id}: {ex.Message}");
      }

      string customImageName = $"local_server_{server.Id}:latest";
      try {
        await server.Docker.Images.DeleteImageAsync(customImageName, new ImageDeleteParameters {
          Force = true
        });
      } catch (Exception ex) {
        Console.WriteLine($"Falha ao remover imagem {customImageName}: {ex.Message}");
      }
    }

    internal async Task PullImageAsync(string imageName, bool install, bool notifyRaw) {
      string repo = imageName;
      string tag = "latest";

      if (imageName.Contains(":")) {
        var parts = imageName.Split(new [] {
          ':'
        }, 2);
        repo = parts[0];
        tag = parts[1];
      }

      if (!install) server.EmitLive("info", "Baixando imagem do container Docker, isso pode levar alguns minutos...");

      var progress = new Progress < JSONMessage > (item => {
        if (notifyRaw) {
          string statusMsg = item.Status ?? "";
          string idMsg = item.ID ?? "";

          string msg = "";
          if (!string.IsNullOrWhiteSpace(idMsg)) msg += $"{idMsg}: ";
          if (!string.IsNullOrWhiteSpace(statusMsg)) msg += statusMsg;

          if (string.IsNullOrWhiteSpace(msg)) msg = item.Stream ?? item.ErrorMessage ?? "";

          if (!string.IsNullOrWhiteSpace(msg)) server.EmitLive("log", msg.Trim());
          if (!string.IsNullOrWhiteSpace(item.ErrorMessage)) server.EmitLive("error", item.ErrorMessage ?? "Erro desconhecido ao puxar imagem Docker.");
        }
      });

      try {
        await server.Docker.Images.CreateImageAsync(new ImagesCreateParameters {
          FromImage = repo, Tag = tag
        }, null, progress);
        if (!install) server.EmitLive("info", "Download da imagem Docker concluído");
      } catch (Exception e) {
        server.EmitLive("error", $"Erro ao puxar a imagem: {e.Message}");
        if (notifyRaw) server.EmitLive("log", e.ToString());
        throw;
      }
    }

    public async Task < bool > InstallAsync(StartData data, bool skipPull, bool keepAlive) {
      server.Lock.EnterWriteLock();
      try {
        server.DiskLimitMb = data.Disk;
        server.Status = "installing";
      } finally {
        server.Lock.ExitWriteLock();
      }

      server.EmitLive("status", "Iniciando processo de instalação do servidor...");
      server.EmitLive("internal", "installing");

      string sp = server.ServerPath();
      Directory.CreateDirectory(sp);

      string installImage = string.IsNullOrWhiteSpace(data.Core.InstallImage) ? "alpine" : data.Core.InstallImage;
      string installEntrypoint = string.IsNullOrWhiteSpace(data.Core.InstallEntrypoint) ? "/bin/sh" : data.Core.InstallEntrypoint;

      if (!skipPull) {
        server.EmitLive("info", "Baixando imagem de instalação, isso pode levar alguns minutos...");
        try {
          await server.PullImageAsync(installImage, install: true, notifyRaw: false);
        } catch (Exception ex) {
          server.EmitLive("error", $"Erro ao baixar imagem de instalação: {ex.Message}");
        }
      }

      server.Lock.EnterReadLock();
      try {
        if (server.Status == "stopped") return false;
      } finally {
        server.Lock.ExitReadLock();
      }

      string installCmd = ServerUtils.NormalizeScript(server.ReplaceVars(data.Core.InstallScript, data));
      string installScriptPath = Path.Combine(sp, "install.sh");
    
      // [OTIMIZAÇÃO] Escrita assíncrona para não travar a thread
      await File.WriteAllTextAsync(installScriptPath, installCmd, new UTF8Encoding(false));
      server.EnsureWritable(sp);

      string installerName = $"{server.Id}_installer";
      try {
        await server.Docker.Containers.RemoveContainerAsync(installerName, new ContainerRemoveParameters {
          Force = true
        });
      } catch (Exception ex) {
        Console.WriteLine($"Falha ao remover container instalador {installerName}: {ex.Message}");
      }

      var envs = ServerUtils.EnvToList(data.Environment);

      var fields = installEntrypoint.Trim().Split(new [] {
        ' ',
        '\t'
      }, StringSplitOptions.RemoveEmptyEntries).ToList();
      List < string > cmd;
      if (fields.Count > 0 && fields.Last() == "-c") {
        fields.Add("chmod +x /mnt/server/install.sh && /mnt/server/install.sh");
        cmd = fields;
      } else {
        fields.Add("/mnt/server/install.sh");
        cmd = fields;
      }

      CreateContainerResponse created;
      try {
        created = await server.Docker.Containers.CreateContainerAsync(new CreateContainerParameters {
          Image = installImage,
          Name = installerName,
          Env = envs,
          Cmd = cmd,
          HostConfig = new HostConfig {
            Binds = new List < string > {
              $"{sp}:/mnt/server"
            },
            Memory = data.Memory * 1024L * 1024L
          }
        });
      } catch (Exception e) {
        server.EmitLive("error", $"Falha ao criar container instalador: {e.Message}");
        server.Lock.EnterWriteLock();
        try {
          server.Status = "stopped";
        } finally {
          server.Lock.ExitWriteLock();
        }
        return false;
      }

      await server.Docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters());
      server.EmitLive("status", "Iniciando script de instalação...");

      // Stream assíncrono para os logs
      var logStream = await server.Docker.Containers.GetContainerLogsAsync(created.ID, true, new ContainerLogsParameters {
        ShowStdout = true, ShowStderr = true, Follow = true
      });

      _ = Task.Run(async () => {
        var buffer = new byte[81920];
        try {
          while (true) {
            var result = await logStream.ReadOutputAsync(buffer, 0, buffer.Length, CancellationToken.None);
            if (result.EOF) break;
            string txt = Encoding.UTF8.GetString(buffer, 0, result.Count);
            foreach(var l0 in txt.Split(new [] {
                      '\n'
                    }, StringSplitOptions.None)) {
              string l = l0.TrimEnd('\r');
              if (!string.IsNullOrWhiteSpace(l)) server.EmitLive("log", l);
            }
          }
        } catch (Exception ex) {
          Console.WriteLine($"Falha ao ler logs do instalador {created.ID}: {ex.Message}");
        }
      });

      long code = -1;
      try {
        var waitResponse = await server.Docker.Containers.WaitContainerAsync(created.ID);
        code = waitResponse.StatusCode;
      } catch (Exception ex) {
        Console.WriteLine($"Falha ao aguardar finalizacao do instalador {created.ID}: {ex.Message}");
      }

      try {
        await server.Docker.Containers.RemoveContainerAsync(created.ID, new ContainerRemoveParameters {
          Force = true
        });
      } catch (Exception ex) {
        Console.WriteLine($"Falha ao remover container instalador {created.ID}: {ex.Message}");
      }
      try {
        File.Delete(installScriptPath);
      } catch (Exception ex) {
        Console.WriteLine($"Falha ao remover script de instalação: {ex.Message}");
      }

      server.Lock.EnterReadLock();
      try {
        if (server.Status == "stopped") return false;
      } finally {
        server.Lock.ExitReadLock();
      }

      if (code != 0) {
        server.EmitLive("error", $"A instalação falhou. Código de erro: {code}");
        server.Lock.EnterWriteLock();
        try {
          server.Status = "stopped";
        } finally {
          server.Lock.ExitWriteLock();
        }
        return false;
      }

      server.Lock.EnterWriteLock();
      try {
        server.NeedsInstallFlag = false;
      } finally {
        server.Lock.ExitWriteLock();
      }

      // Salva no banco em background
      _ = Task.Run(() => {
        try {
          var currentServer = DatabaseManager.Db.GetServer(server.Id);
          if (currentServer != null) {
            var updatedServer = currentServer with {
              Installed = 1
            };
            DatabaseManager.Db.SaveServer(updatedServer);
          }
        } catch (Exception ex) {
          Console.WriteLine($"Erro ao salvar no DB pós-instalação: {ex}");
        }
      });

      server.EmitLive("info", "Instalação concluída com sucesso.");

      if (!keepAlive) {
        server.Lock.EnterWriteLock();
        try {
          server.Status = "stopped";
        } finally {
          server.Lock.ExitWriteLock();
        }
      }
      return true;
    }

    public async Task StartAsync(StartData data) {
      string prev;
      server.Lock.EnterWriteLock();
      try {
        server.IsRestarting = false;
        server.DiskLimitMb = data.Disk;
        server.IsStopping = false;
        server.Status = "initializing";
        prev = server.ContainerId;
      } finally {
        server.Lock.ExitWriteLock();
      }

      bool allowRoot = (data.Core.RootAcess == 1L);
      bool maintainable = (data.Core.Maintainable == 1L);

      bool needsRecreate = true;
      string originalBaseImage = data.Image;
      string customImageName = $"local_server_{server.Id}:latest";

      if (!string.IsNullOrWhiteSpace(prev)) {
        if (maintainable) {
          ContainerInspectResponse ? inspect = null;
          try {
            inspect = await server.Docker.Containers.InspectContainerAsync(prev);
          } catch (Exception ex) {
            Console.WriteLine($"Falha ao inspecionar container {prev}: {ex.Message}");
          }

          if (inspect != null) {
            string ? currentBase = server.ExtractEnv(inspect, "PLUME_BASE_IMAGE") ??
                                   (inspect.Config?.Image == customImageName ? originalBaseImage : inspect.Config?.Image);

            bool imageChanged = currentBase != originalBaseImage;
            bool allocChanged = server.AllocationChanged(inspect, data.PrimaryAllocation);

            if (imageChanged || allocChanged) {
              server.EmitLive("warning", "Atenção: Houve alteração na configuração do servidor!");

              if (allocChanged && !imageChanged) {
                server.EmitLive("info", "Alteração de porta detectada. Salvando estado atual do sistema...");
                try {
                  await server.Docker.Containers.StopContainerAsync(prev, new ContainerStopParameters {
                    WaitBeforeKillSeconds = 10
                  });
                } catch (Exception ex) {
                  Console.WriteLine($"Falha ao parar container {prev}: {ex.Message}");
                }

                CommitContainerChangesResponse ? committed = null;
                try {
                  committed = await server.Docker.Images.CommitContainerChangesAsync(
                    new CommitContainerChangesParameters {
                      ContainerID = prev,
                      RepositoryName = $"local_server_{server.Id}",
                      Tag = "latest"
                    },
                    CancellationToken.None
                  );
                } catch (Exception ex) {
                  Console.WriteLine($"Falha ao salvar estado do container {prev}: {ex.Message}");
                }

                if (committed == null) {
                  server.EmitLive("error", "Aviso: Falha ao salvar estado interno.");
                } else {
                  data.Image = customImageName;
                  server.EmitLive("info", "Estado salvo com sucesso! O novo container herdará tudo.");
                }
              } else {
                server.EmitLive("warning", "A imagem base do container foi alterada! Alterações internas antigas serão descartadas.");
                server.EmitLive("warning", "O servidor foi marcado para necessitar de reinstalação na nova imagem.");
                server.Lock.EnterWriteLock();
                try {
                  server.NeedsInstallFlag = true;
                } finally {
                  server.Lock.ExitWriteLock();
                }
              }

              server.EmitLive("warning", "Você tem 10 segundos para clicar em 'Stop' se desejar cancelar a operação.");
              for (int i = 0; i < 10; i++) {
                await Task.Delay(1000);
                string stCheck;
                server.Lock.EnterReadLock();
                try {
                  stCheck = server.Status;
                } finally {
                  server.Lock.ExitReadLock();
                }

                if (stCheck == "stopped" || stCheck == "stopping") {
                  server.EmitLive("info", "Inicialização cancelada pelo usuário.");
                  return;
                }
              }

              string postWaitStatus;
              server.Lock.EnterReadLock();
              try {
                postWaitStatus = server.Status;
              } finally {
                server.Lock.ExitReadLock();
              }
              if (postWaitStatus == "stopped" || postWaitStatus == "stopping") return;

              server.EmitLive("info", "Tempo esgotado. Recriando o container...");
              try {
                await server.Docker.Containers.RemoveContainerAsync(prev, new ContainerRemoveParameters {
                  Force = true
                });
              } catch (Exception ex) {
                Console.WriteLine($"Falha ao remover container {prev}: {ex.Message}");
              }
              if (imageChanged) {
                try {
                  await server.Docker.Images.DeleteImageAsync(customImageName, new ImageDeleteParameters {
                    Force = true
                  });
                } catch (Exception ex) {
                  Console.WriteLine($"Falha ao remover imagem {customImageName}: {ex.Message}");
                }
              }
            } else {
              needsRecreate = false;
              server.EmitLive("info", "Servidor mantível: Iniciando container existente sem recriar...");
            }
          } else {
            try {
              await server.Docker.Containers.RemoveContainerAsync(prev, new ContainerRemoveParameters {
                Force = true
              });
            } catch (Exception ex) {
              Console.WriteLine($"Falha ao remover container {prev}: {ex.Message}");
            }
          }
        } else {
          try {
            await server.Docker.Containers.RemoveContainerAsync(prev, new ContainerRemoveParameters {
              Force = true
            });
          } catch (Exception ex) {
            Console.WriteLine($"Falha ao remover container {prev}: {ex.Message}");
          }
        }
      }

      string sp = server.ServerPath();
      Directory.CreateDirectory(sp);
      server.EmitLive("log", "");
      server.EmitLive("status", "Servidor marcado como iniciando...");
      server.EmitLive("internal", "initializing");

      // [OTIMIZAÇÃO] Processamento de I/O em paralelo com o pull da imagem
      var fileTasks = Task.Run(async () => {
        server.EnsureWritable(sp);
        server.ProcessConfigFiles(sp, data);
        await server.FixPermsAsync(sp);
      });

      Task imageTask = Task.CompletedTask;
      if (data.Image != customImageName) {
        imageTask = Task.Run(async () => {
          try {
            // Mantendo a puxada de imagem pra garantir atualizações (como :latest)
            await server.PullImageAsync(data.Image, install: false, notifyRaw: true);
          } catch (Exception ex) {
            server.EmitLive("error", $"Erro ao baixar imagem principal: {ex.Message}");
          }
        });
      }

      // Espera os dois terminarem ao mesmo tempo
      await Task.WhenAll(fileTasks, imageTask);

      bool needsInstallNow;
      server.Lock.EnterReadLock();
      try {
        needsInstallNow = server.NeedsInstallFlag;
      } finally {
        server.Lock.ExitReadLock();
      }

      if (needsInstallNow && !string.IsNullOrWhiteSpace(data.Core.InstallScript)) {
        if (!await server.InstallAsync(data, skipPull: false, keepAlive: true)) return;
        server.Lock.EnterWriteLock();
        try {
          server.Status = "initializing";
        } finally {
          server.Lock.ExitWriteLock();
        }
      }

      if (maintainable && data.Image != customImageName) {
        bool hasCustom = false;
        try {
          await server.Docker.Images.InspectImageAsync(customImageName);
          hasCustom = true;
        } catch (Exception ex) {
          Console.WriteLine($"Falha ao inspecionar imagem {customImageName}: {ex.Message}");
        }

        if (hasCustom) data.Image = customImageName;
      }

      string doubleCheckStatus;
      server.Lock.EnterReadLock();
      try {
        doubleCheckStatus = server.Status;
      } finally {
        server.Lock.ExitReadLock();
      }
      if (doubleCheckStatus == "stopped" || doubleCheckStatus == "stopping") return;

      server.EmitLive("info", "Container iniciando...");

      if (needsRecreate) {
        var allAllocs = new List < AllocationData > ();
        if (data.PrimaryAllocation != null) allAllocs.Add(data.PrimaryAllocation);
        allAllocs.AddRange(data.AdditionalAllocation);

        var portBindings = new Dictionary < string,
          IList < PortBinding >> ();
        var exposedPorts = new Dictionary < string,
          EmptyStruct > ();

        foreach(var a in allAllocs) {
          string tcpKey = $"{a.Port}/tcp";
          string udpKey = $"{a.Port}/udp";

          portBindings[tcpKey] = new List < PortBinding > {
            new PortBinding {
              HostIP = a.Ip, HostPort = a.Port.ToString()
            }
          };
          portBindings[udpKey] = new List < PortBinding > {
            new PortBinding {
              HostIP = a.Ip, HostPort = a.Port.ToString()
            }
          };

          exposedPorts[tcpKey] =
            default;
          exposedPorts[udpKey] =
            default;
        }

        var varMap = new Dictionary < string,
          string > {
          {
            "SERVER_MEMORY",
            data.Memory.ToString()
          }
        };
        if (data.PrimaryAllocation != null) {
          varMap["SERVER_PORT"] = data.PrimaryAllocation.Port.ToString();
          varMap["SERVER_IP"] = data.PrimaryAllocation.Ip;
        }

        foreach(var kv in ServerUtils.EnvToMap(data.Environment)) {
          varMap[kv.Key] = kv.Value;
        }

        string startupCmd = ServerUtils.ReplaceVarsGeneric(data.Core.StartupCommand, varMap);

        if (!string.IsNullOrWhiteSpace(data.Core.StartupScript)) {
          string script = ServerUtils.NormalizeScript(ServerUtils.ReplaceVarsGeneric(data.Core.StartupScript, varMap));
          string scriptPath = Path.Combine(sp, ".plume_startup.sh");
        
          // [OTIMIZAÇÃO] Escrita assíncrona novamente
          await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(false));
          startupCmd = "chmod +x .plume_startup.sh 2>/dev/null; ./.plume_startup.sh";
        }

        string user = allowRoot ? "0:0" : "65534:65534";

        var envList = ServerUtils.EnvToList(data.Environment);
        envList.Add($"PLUME_BASE_IMAGE={originalBaseImage}");

        var finalCmd = !string.IsNullOrWhiteSpace(data.Core.DockerEntrypoint) ?
          data.Core.DockerEntrypoint.Trim().Split(new [] {
            ' ',
            '\t'
          }, StringSplitOptions.RemoveEmptyEntries).Concat(new [] {
            startupCmd
          }).ToList() :
          new List < string > {
            "/bin/sh",
            "-c",
            startupCmd
          };

        var hostConfig = new HostConfig {
          Binds = new List < string > {
            $"{sp}:/home/container"
          },
          PortBindings = portBindings,
          ExtraHosts = new List < string > {
            "host.docker.internal:host-gateway"
          }
        };

        if (data.Memory > 0) hostConfig.Memory = data.Memory * 1024L * 1024L;
        if (data.Cpu > 0) {
          hostConfig.CPUPeriod = 100_000L;
          hostConfig.CPUQuota = data.Cpu * 1000L;
        }

        var created = await server.Docker.Containers.CreateContainerAsync(new CreateContainerParameters {
          Image = data.Image,
          Name = server.Id,
          Env = envList,
          Cmd = finalCmd,
          ExposedPorts = exposedPorts,
          Tty = true,
          OpenStdin = true,
          WorkingDir = "/home/container",
          User = user,
          HostConfig = hostConfig
        });

        server.Lock.EnterWriteLock();
        try {
          server.ContainerId = created.ID;
        } finally {
          server.Lock.ExitWriteLock();
        }
      }

      string cId;
      server.Lock.EnterReadLock();
      try {
        cId = server.ContainerId;
      } finally {
        server.Lock.ExitReadLock();
      }

      await server.Docker.Containers.StartContainerAsync(cId, new ContainerStartParameters());

      string doneStr = ServerUtils.ParseDoneString(data.Core.StartupParser);

      if (string.IsNullOrWhiteSpace(doneStr)) {
        server.Lock.EnterWriteLock();
        try {
          server.Status = "running";
          server.StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        } finally {
          server.Lock.ExitWriteLock();
        }
        server.EmitLive("status", "Servidor marcado como online...");
        server.EmitLive("internal", "running");
      }

      // Assumindo que essas extensões virão no Monitor.cs e afins
      server.AttachLogStream(doneStr);
      server.WaitContainerExitAsync();
    }

    public void WaitContainerExitAsync() {
      // Dispara sem prender a thread (Fire and forget, como o CoroutineScope)
      _ = Task.Run(async () => {
        string cId;
        server.Lock.EnterReadLock();
        try {
          cId = server.ContainerId;
        } finally {
          server.Lock.ExitReadLock();
        }

        if (string.IsNullOrWhiteSpace(cId)) return;

        try {
          await server.Docker.Containers.WaitContainerAsync(cId);
        } catch (Exception ex) {
          Console.WriteLine($"Falha ao aguardar container {cId}: {ex.Message}");
        }
        server.Lock.EnterWriteLock();
        try {
          server.Status = "stopped";
          server.IsStopping = false;
          server.StartedAt = null;
        } finally {
          server.Lock.ExitWriteLock();
        }

        server.EmitLive("status", "Servidor marcado como offline...");
      });
    }

    internal string ? ExtractEnv(ContainerInspectResponse inspect, string key) {
      var env = inspect.Config?.Env;
      if (env == null) return null;

      string prefix = $"{key}=";
      string ? match = env.FirstOrDefault(e => e.StartsWith(prefix));
      return match?.Substring(prefix.Length);
    }

    internal bool AllocationChanged(ContainerInspectResponse inspect, AllocationData ? primary) {
      if (primary == null) return false;
      string expected = $"{primary.Port}/tcp";

      var pb = inspect.HostConfig?.PortBindings;
      if (pb == null) return true;

      return !pb.ContainsKey(expected);
    }
  }

  // ---- pullImage ----

  // ---- install ----

  // ---- start ----

  // Helpers específicos do Lifecycle
}