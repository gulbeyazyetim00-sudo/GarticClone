using GarticClone.Server;

var app = ServerApp.Start(ServerConfig.FromEnvironment());

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; 
    stop.TrySetResult();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();

await stop.Task;
await app.StopAsync();
