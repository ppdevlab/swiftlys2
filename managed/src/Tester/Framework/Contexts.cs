using System.Diagnostics;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;

namespace Tester.Framework;

public sealed class SkipException( string reason ) : Exception(reason);

public abstract class Ctx( ISwiftlyCore core, IPlayer caller )
{
    private readonly List<string> _lines = [];

    public ISwiftlyCore Core { get; } = core;

    public IPlayer Caller { get; } = caller;

    internal IReadOnlyList<string> Lines => _lines;

    protected void Add( string line ) => _lines.Add(line);

    public Task NextTick()
    {
        var tcs = new TaskCompletionSource();
        Core.Scheduler.NextTick(() => tcs.SetResult());
        return tcs.Task;
    }

    public async Task Ticks( int count )
    {
        for (var i = 0; i < count; i++) await NextTick();
    }

    public async Task<bool> Until( Func<bool> condition, int maxTicks = 128 )
    {
        for (var i = 0; i < maxTicks; i++)
        {
            if (condition()) return true;
            await NextTick();
        }
        return condition();
    }
}

public sealed class TestContext( ISwiftlyCore core, IPlayer caller ) : Ctx(core, caller)
{
    public int Passed { get; private set; }
    public int Failed { get; private set; }
    public int Skipped { get; private set; }

    public static void Expect( bool condition, string message = "expectation failed" )
    {
        if (!condition) throw new Exception(message);
    }

    public static void Equal<T>( T expected, T actual, string? what = null )
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what ?? "value"}: expected <{expected}> but was <{actual}>");
    }

    public static void NotNull( object? value, string? what = null )
    {
        if (value is null) throw new Exception($"{what ?? "value"} was null");
    }

    public static void Throws<TEx>( Action body ) where TEx : Exception
    {
        try { body(); }
        catch (TEx) { return; }
        catch (Exception e) { throw new Exception($"expected {typeof(TEx).Name} but got {e.GetType().Name}: {e.Message}"); }
        throw new Exception($"expected {typeof(TEx).Name} but nothing was thrown");
    }

    public static void Skip( string reason ) => throw new SkipException(reason);

    public void Test( string name, Action body )
    {
        var t0 = Stopwatch.GetTimestamp();
        try { body(); Record("PASS", name, t0, null); Passed++; }
        catch (SkipException s) { Record("SKIP", name, t0, s.Message); Skipped++; }
        catch (Exception e) { Record("FAIL", name, t0, e.Message); Failed++; }
    }

    public async Task TestAsync( string name, Func<Task> body )
    {
        var t0 = Stopwatch.GetTimestamp();
        try { await body(); Record("PASS", name, t0, null); Passed++; }
        catch (SkipException s) { Record("SKIP", name, t0, s.Message); Skipped++; }
        catch (Exception e) { Record("FAIL", name, t0, e.Message); Failed++; }
    }

    private void Record( string status, string name, long t0, string? detail )
        => Add($"  {status} {name} ({Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F2} ms){(detail is null ? "" : " - " + detail)}");
}

public sealed class ProfileContext( ISwiftlyCore core, IPlayer caller ) : Ctx(core, caller)
{
    public int Count { get; private set; }

    public void Profile( string name, Action body, int iterations = 10_000 )
    {
        try
        {
            const int batches = 5;
            var per = Math.Max(1, iterations / batches);
            for (var i = 0; i < Math.Min(per, 1000); i++) body();

            var ns = new double[batches];
            var bytes = new double[batches];
            for (var b = 0; b < batches; b++)
            {
                var a0 = GC.GetAllocatedBytesForCurrentThread();
                var t0 = Stopwatch.GetTimestamp();
                for (var i = 0; i < per; i++) body();
                ns[b] = Stopwatch.GetElapsedTime(t0).TotalNanoseconds / per;
                bytes[b] = (double)(GC.GetAllocatedBytesForCurrentThread() - a0) / per;
            }
            Array.Sort(ns);
            Array.Sort(bytes);
            Add($"  {name,-48} {ns[batches / 2],10:F1} ns/op {bytes[batches / 2],8:F1} B/op {per * batches,8} iters");
            Count++;
        }
        catch (Exception e)
        {
            Add($"  {name,-48} ERROR {e.Message}");
        }
    }
}
