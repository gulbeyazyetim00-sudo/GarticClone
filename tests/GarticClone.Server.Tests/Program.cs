using GarticClone.Server.Tests.Framework;
using GarticClone.Server.Utils;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Log.Level = LogLevel.Silent;

return await TestRunner.RunAsync(typeof(Program).Assembly, args.FirstOrDefault());
