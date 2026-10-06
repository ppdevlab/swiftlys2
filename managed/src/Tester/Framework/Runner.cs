using System.Text;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;

namespace Tester.Framework;

public enum Mode { Test, Profile }

public sealed class Runner( ISwiftlyCore core )
{
    public bool IsRunning { get; private set; }

    public IReadOnlyList<Section> Discover()
        => typeof(Runner).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(Section).IsAssignableFrom(t))
            .Select(t => (Section)Activator.CreateInstance(t, core)!)
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

    public async Task Run( IPlayer caller, Mode mode, string? section )
    {
        if (IsRunning) { caller.SendChat("[Tester] a run is already in progress"); return; }

        var sections = Discover().Where(s => section is null || s.Name.Equals(section, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sections.Count == 0)
        {
            caller.SendChat($"[Tester] unknown section '{section}'. Use: tester list");
            return;
        }

        IsRunning = true;
        var started = DateTime.Now;
        var file = Path.Combine(core.PluginDataDirectory, $"{mode.ToString().ToLowerInvariant()}-{section ?? "all"}-{started:yyyyMMdd-HHmmss}.txt");
        var report = new StringBuilder();
        int pass = 0, fail = 0, skip = 0, profiles = 0;
        try
        {
            Emit(report, file, $"=== Tester {mode} run: {sections.Count} section(s), started {started:u} ===");
            caller.SendChat($"[Tester] {mode}: {sections.Count} section(s), see server console");

            foreach (var s in sections)
            {
                Ctx ctx = mode == Mode.Test ? new TestContext(core, caller) : new ProfileContext(core, caller);
                var crashed = (string?)null;
                try
                {
                    await (ctx is TestContext t ? s.Test(t) : s.Profile((ProfileContext)ctx));
                }
                catch (Exception e)
                {
                    crashed = e.ToString();
                }

                var header = ctx switch
                {
                    TestContext t => $"--- [{s.Name}] {t.Passed} pass, {t.Failed} fail, {t.Skipped} skip ---",
                    ProfileContext p => $"--- [{s.Name}] {p.Count} profile(s) ---",
                    _ => s.Name,
                };
                if (ctx is TestContext tc) { pass += tc.Passed; fail += tc.Failed; skip += tc.Skipped; }
                if (ctx is ProfileContext pc) profiles += pc.Count;
                if (crashed is not null) fail++;

                var block = new StringBuilder().AppendLine(header);
                foreach (var line in ctx.Lines) _ = block.AppendLine(line);
                if (crashed is not null) _ = block.AppendLine($"  CRASH section threw: {crashed}");
                Emit(report, file, block.ToString().TrimEnd());

                await ctx.NextTick();
            }

            var total = mode == Mode.Test ? $"{pass} pass, {fail} fail, {skip} skip" : $"{profiles} profile(s)";
            Emit(report, file, $"=== Tester done: {total}. Report: {file} ===");
            if (caller.IsValid) caller.SendChat($"[Tester] done: {total}");
        }
        catch (Exception e)
        {
            core.Logger.LogError(e, "[Tester] runner crashed");
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void Emit( StringBuilder report, string file, string text )
    {
        _ = report.AppendLine(text);
        foreach (var line in text.Split('\n')) core.Logger.LogInformation("{Line}", line.TrimEnd('\r'));
        try { File.WriteAllText(file, report.ToString()); }
        catch (Exception e) { core.Logger.LogError(e, "[Tester] cannot write {File}", file); }
    }
}
