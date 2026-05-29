using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Docker.DotNet.Models;

namespace Plume; // Ou o namespace que vc usa

// Essa classe existe ÚNICA E EXCLUSIVAMENTE para o compilador NativeAOT ler,
// ver que estamos "usando" essas listas, e não apagar os construtores delas.
// Você NÃO precisa chamar isso em lugar nenhum.
internal static class AotConfig
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(List<ContainerListResponse>))]
    public static void KeepAliveForLinux()
    {
        // Se no futuro der erro de MissingMethodException lendo os Mounts ou Ports do container,
        // é só botar as listas deles aqui também:
        _ = new List<ContainerListResponse>();
        // _ = new List<MountPoint>();
        // _ = new List<Port>();
    }
}