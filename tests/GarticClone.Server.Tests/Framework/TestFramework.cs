using System.Diagnostics;
using System.Reflection;

namespace GarticClone.Server.Tests.Framework;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute(string? name = null) : Attribute
{
    public string? Name { get; } = name;
}

public sealed class AssertionException(string message) : Exception(message);

public static class Assert
{
    public static void Equal<T>(T expected, T actual, string? because = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"expected <{expected}> but got <{actual}>{Because(because)}");
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? because = null)
    {
        var e = expected.ToArray();
        var a = actual.ToArray();
        if (!e.SequenceEqual(a))
            throw new AssertionException($"expected [{string.Join(", ", e)}] but got [{string.Join(", ", a)}]{Because(because)}");
    }

    public static void True(bool condition, string? because = null)
    {
        if (!condition) throw new AssertionException($"expected true{Because(because)}");
    }

    public static void False(bool condition, string? because = null) => True(!condition, because);

    public static T NotNull<T>(T? value, string? because = null) where T : class =>
        value ?? throw new AssertionException($"expected non-null{Because(because)}");

    public static TException Throws<TException>(Action action, string? because = null) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            throw new AssertionException($"expected {typeof(TException).Name} but got {ex.GetType().Name}: {ex.Message}{Because(because)}");
        }
        throw new AssertionException($"expected {typeof(TException).Name} but nothing was thrown{Because(because)}");
    }

    private static string Because(string? because) => because is null ? "" : $" ({because})";
}

public static class TestRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static async Task<int> RunAsync(Assembly assembly, string? filter)
    {
        var tests = assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(m => (Type: t, Method: m, Attr: m.GetCustomAttribute<TestAttribute>())))
            .Where(x => x.Attr is not null)
            .Select(x => (x.Type, x.Method, Name: $"{x.Type.Name}: {x.Attr!.Name ?? x.Method.Name}"))
            .Where(x => filter is null || x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Type.Namespace).ThenBy(x => x.Type.Name)
            .ToList();

        int passed = 0, failed = 0;
        var total = Stopwatch.StartNew();

        foreach (var (type, method, name) in tests)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                object? instance = method.IsStatic ? null : Activator.CreateInstance(type);
                var result = method.Invoke(instance, null);
                if (result is Task task)
                {
                    var finished = await Task.WhenAny(task, Task.Delay(Timeout));
                    if (finished != task) throw new TimeoutException($"timed out after {Timeout.TotalSeconds}s");
                    await task;
                }
                passed++;
                Write(ConsoleColor.Green, $"  ✓ {name} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
                failed++;
                Write(ConsoleColor.Red, $"  ✗ {name}\n      {inner.GetType().Name}: {inner.Message}");
            }
        }

        Console.WriteLine();
        Write(failed == 0 ? ConsoleColor.Green : ConsoleColor.Red,
            $"tests {tests.Count} | pass {passed} | fail {failed} | {total.ElapsedMilliseconds} ms");
        return failed == 0 ? 0 : 1;
    }

    private static void Write(ConsoleColor color, string text)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }
}
