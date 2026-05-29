using System.Collections;
using System.Text;
using System.Text.Json;
using Docker.DotNet.Models;
using Plume.Configuration; // Para obter acesso ao JsonExtensions.ToAnyValue
using Plume.Types;
using YamlDotNet.Serialization;

namespace Plume.Server.Extending;

public static class ServerUtils {
  // Limpa aspas simples/duplas das pontas de valores strings e trata JsonElements
  public static string Clean(object ? v) {
    if (v == null) return "";

    string s;
    if (v is JsonElement element) {
      s = element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.ToString();
    } else {
      s = v.ToString() ?? "";
    }

    s = s.Trim();
    while ((s.StartsWith("\"") && s.EndsWith("\"")) || (s.StartsWith("'") && s.EndsWith("'"))) {
      if (s.Length >= 2) {
        s = s.Substring(1, s.Length - 2);
      } else {
        break;
      }
    }
    return s;
  }

  // Helper para converter objetos dinâmicos ou JsonElements para Dicionários tipados em C#
  private static IDictionary < string, object ? > ? AsDictionary(object ? obj) {
    if (obj == null) return null;
    if (obj is IDictionary < string, object ? > dict) return dict;

    if (obj is IDictionary objDict) {
      var newDict = new Dictionary < string,
        object ? > ();
      foreach(DictionaryEntry entry in objDict) {
        newDict[entry.Key.ToString() ?? ""] = entry.Value;
      }
      return newDict;
    }

    if (obj is not JsonElement element || element.ValueKind != JsonValueKind.Object) return null;
    var res = new Dictionary < string,
      object ? > ();
    foreach(var prop in element.EnumerateObject()) {
      res[prop.Name] = prop.Value.ToAnyValue();
    }
    return res;

  }

  // ---- configSystem / startupParser file generation ----
  public static void ProcessConfigFiles(this Server server, string sp, StartData data) {
    var templates = new Dictionary < string,
      object ? > ();

    // ConfigSystem
    var configSysDict = AsDictionary(data.Core.ConfigSystem);
    if (configSysDict != null) {
      foreach(var kv in configSysDict) {
        templates[Clean(kv.Key)] = kv.Value;
      }
    }

    // StartupParser (tudo menos "done")
    var startupParserDict = AsDictionary(data.Core.StartupParser);
    if (startupParserDict != null) {
      foreach(var kv in startupParserDict) {
        string key = Clean(kv.Key);
        if (key != "done") {
          templates[key] = kv.Value;
        }
      }
    }

    foreach(var kv in templates) {
      bool isStopped = false;
      server.Lock.EnterReadLock();
      try {
        if (server.Status == "stopped") isStopped = true;
      } finally {
        server.Lock.ExitReadLock();
      }

      if (isStopped) return;

      string filename = Clean(kv.Key);
      string filePath = Path.Combine(sp, filename);
      object ? content = kv.Value;

      if (content is string strContent) {
        string ? parentDir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(parentDir)) {
          Directory.CreateDirectory(parentDir);
        }
        File.WriteAllText(filePath, server.ReplaceVars(strContent, data).Trim(), Encoding.UTF8);
      } else if (content is IDictionary < string, object ? > mapContent) {
        server.HandleStructuredFile(filePath, filename, mapContent, data);
      } else if (content is JsonElement element && element.ValueKind == JsonValueKind.Object) {
        var map = AsDictionary(element);
        if (map != null) {
          server.HandleStructuredFile(filePath, filename, map, data);
        }
      }
    }
  }

  public static void HandleStructuredFile(this Server server, string filePath, string filename, IDictionary < string, object ? > cfg, StartData data) {
    string existing = File.Exists(filePath) ? File.ReadAllText(filePath, Encoding.UTF8) : "";

    if (filename.EndsWith(".json")) {
      Dictionary < string, object ? > obj;
      try {
        if (string.IsNullOrWhiteSpace(existing)) {
          obj = new Dictionary < string, object ? > ();
        } else {
          obj = JsonSerializer.Deserialize < Dictionary < string, object ? >> (existing) ?? new Dictionary < string, object ? > ();
        }
      } catch {
        obj = new Dictionary < string, object ? > ();
      }

      foreach(var kv in cfg) {
        SetDot(obj, Clean(kv.Key), server.ParseVal(kv.Value, data));
      }

      string ? parentDir = Path.GetDirectoryName(filePath);
      if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);

      var options = new JsonSerializerOptions {
        WriteIndented = true
      };
      File.WriteAllText(filePath, JsonSerializer.Serialize(obj, options), Encoding.UTF8);
    } else if (filename.EndsWith(".yml") || filename.EndsWith(".yaml")) {
      Dictionary < string, object ? > obj;
      try {
        if (string.IsNullOrWhiteSpace(existing)) {
          obj = new Dictionary < string, object ? > ();
        } else {
          var deserializer = new DeserializerBuilder().Build();
          obj = deserializer.Deserialize < Dictionary < string, object ? >> (existing);
        }
      } catch {
        obj = new Dictionary < string, object ? > ();
      }

      foreach(var kv in cfg) {
        SetDot(obj, Clean(kv.Key), server.ParseVal(kv.Value, data));
      }

      string ? parentDir = Path.GetDirectoryName(filePath);
      if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);

      var serializer = new SerializerBuilder().Build();
      File.WriteAllText(filePath, serializer.Serialize(obj), Encoding.UTF8);
    } else if (filename.EndsWith(".properties")) {
      var lines = existing.Split([
        "\r\n",
        "\n"
      ], StringSplitOptions.None).ToList();
      var idx = new Dictionary < string,
        int > ();

      for (int i = 0; i < lines.Count; i++) {
        var parts = lines[i].Split([
          '='
        ], 2);
        if (parts.Length == 2) {
          idx[Clean(parts[0])] = i;
        }
      }

      foreach(var kv in cfg) {
        var key = Clean(kv.Key);
        var line = $"{key}={server.ParseVal(kv.Value, data)}";

        if (idx.TryGetValue(key, out int at)) {
          lines[at] = line;
        } else {
          lines.Add(line);
        }
      }

      string ? parentDir = Path.GetDirectoryName(filePath);
      if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);
      File.WriteAllText(filePath, string.Join("\n", lines), Encoding.UTF8);
    }
  }

  public static object ParseVal(this Server server, object ? v, StartData data) {
    // Mantém mapas e listas aninhados sem quebrar a estrutura de dados original
    if (v is IDictionary < string, object ? > || v is IList) {
      return v;
    }

    var p = Clean(server.ReplaceVars(Clean(v), data));

    switch (p)
    {
      case "true":
        return true;
      case "false":
        return false;
    }

    if (int.TryParse(p, out int intVal)) return intVal;

    return p;
  }

  public static void SetDot(Dictionary < string, object ? > obj, string path, object ? value) {
    var keys = path.Split('.').Select(Clean).ToList();
    var curr = obj;

    for (int i = 0; i < keys.Count - 1; i++) {
      string k = keys[i];
      if (!curr.TryGetValue(k, out
          var next) || !(next is Dictionary < string, object ? > )) {
        var created = new Dictionary < string,
          object ? > ();
        curr[k] = created;
        curr = created;
      } else {
        curr = (Dictionary < string, object ? > ) next;
      }
    }
    curr[keys.Last()] = value;
  }

  // ---- perms helpers ----
  public static void EnsureWritable(this Server server, string dir) {
    if (!Directory.Exists(dir)) return;
    try {
      var di = new DirectoryInfo(dir);
      foreach(var file in di.GetFiles("*", SearchOption.AllDirectories)) {
        try {
          file.IsReadOnly = false;
        } catch {
          /* Ignorando falhas de permissão de arquivos individuais */ }
      }
    } catch {
      /* Ignorando acessos inválidos */ }
  }

  public static async Task FixPermsAsync(this Server server, string hostDir) {
    server.EmitLive("info", "Arrumando as permissões do diretório, isso pode demorar um pouco...");

    string os = Clean(System.Runtime.InteropServices.RuntimeInformation.OSDescription).ToLower();
    bool isWindows = os.Contains("win") || os.Contains("windows");

    if (isWindows) {
      try {
        // Pull na imagem do Alpine de forma assíncrona
        await server.Docker.Images.CreateImageAsync(
          new ImagesCreateParameters {
            FromImage = "alpine", Tag = "latest"
          },
          null,
          new Progress < JSONMessage > ()
        );

        string cmd = "chmod -R a+rwX /home/container; chown -R 65534:65534 /home/container";

        var response = await server.Docker.Containers.CreateContainerAsync(new CreateContainerParameters {
          Image = "alpine",
            Cmd = new List < string > {
              "/bin/sh",
              "-c",
              cmd
            },
            User = "0:0",
            HostConfig = new HostConfig {
              Binds = new List < string > {
                $"{hostDir}:/home/container"
              }
            }
        });

        await server.Docker.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());
        await server.Docker.Containers.WaitContainerAsync(response.ID);

        try {
          await server.Docker.Containers.RemoveContainerAsync(response.ID, new ContainerRemoveParameters {
            Force = true
          });
        } catch {
          /* Ignorado */ }
      } catch (Exception ex) {
        server.EmitLive("error", $"Não foi possível ajustar as permissões: {ex.Message}");
      }
    } else {
      server.EnsureWritable(hostDir);
    }
  }

  // ---- misc helpers ----
  public static long DirectorySize(string dir) {
    long size = 0L;
    if (!Directory.Exists(dir)) return size;

    try {
      var di = new DirectoryInfo(dir);
      foreach(var file in di.GetFiles("*", SearchOption.AllDirectories)) {
        try {
          size += file.Length;
        } catch {
          /* Ignorado */ }
      }
    } catch {
      /* Ignorado */ }
    return size;
  }

  public static string NormalizeScript(string script) {
    string s = Clean(script).Replace("\r\n", "\n");
    if (!s.StartsWith("#!")) {
      s = $"#!/bin/sh\n{s}";
    }
    return s;
  }

  public static Dictionary < string, string > EnvToMap(object ? env) {
    var outMap = new Dictionary < string,
      string > ();
    var dict = AsDictionary(env);
    if (dict != null) {
      foreach(var kv in dict) {
        outMap[Clean(kv.Key)] = Clean(kv.Value);
      }
    }
    return outMap;
  }

  public static List < string > EnvToList(object ? env) {
    return EnvToMap(env).Select(kv => $"{Clean(kv.Key)}={Clean(kv.Value)}").ToList();
  }

  public static string ReplaceVars(this Server server, string input, StartData data) {
    var vars = new Dictionary < string,
      string > {
        ["SERVER_MEMORY"] = Clean(data.Memory)
      };

    if (data.PrimaryAllocation != null) {
      vars["SERVER_PORT"] = Clean(data.PrimaryAllocation.Port);
      vars["SERVER_IP"] = Clean(data.PrimaryAllocation.Ip);
    }

    foreach(var kv in EnvToMap(data.Environment)) {
      vars[Clean(kv.Key)] = Clean(kv.Value);
    }

    string res = input;
    foreach(var kv in vars) {
      res = res.Replace($"{{{{{Clean(kv.Key)}}}}}", Clean(kv.Value));
    }

    return Clean(res);
  }

  public static string ReplaceVarsGeneric(string input, IDictionary < string, string > vars) {
    string res = input;
    foreach(var kv in vars) {
      res = res.Replace($"{{{{{Clean(kv.Key)}}}}}", Clean(kv.Value));
    }
    return Clean(res);
  }

  public static string ParseDoneString(object ? startupParser) {
    var map = AsDictionary(startupParser);
    if (map == null) return "";

    if (map.TryGetValue("done", out
        var v)) {
      return Clean(v);
    }
    return "";
  }
}