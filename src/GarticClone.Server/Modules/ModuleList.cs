using GarticClone.Server.Modules.Core;
using GarticClone.Server.Protocol;

namespace GarticClone.Server.Modules;


public static class ModuleList
{
    public static IReadOnlyList<IModule> Create() =>
    [
        new SystemModule(),
    ];
}
