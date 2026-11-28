using GarticClone.Server.Modules.Core;
using GarticClone.Server.Modules.Draw;
using GarticClone.Server.Modules.Session;
using GarticClone.Server.Protocol;

namespace GarticClone.Server.Modules;

public static class ModuleList
{
    public static IReadOnlyList<IModule> Create()
    {
        // DrawModule must share the SessionModule's RoomManager so it sees the same rooms.
        var session = new SessionModule();
        return
        [
            new SystemModule(),
            session,
            new DrawModule(session.Rooms),
        ];
    }
}
