using Microsoft.Diagnostics.Runtime;

namespace SwiftlyS2.Core.Plugins;

internal enum Gen { Gen0, Gen1, Gen2, Loh, Poh, Other }

internal sealed class PluginHeapStats
{
    public readonly long[] Live = new long[6];
    public readonly long[] Dead = new long[6];
    public long LiveTotal => Live.Sum();
    public long DeadTotal => Dead.Sum();
    public long LiveOld => Live[(int)Gen.Gen2] + Live[(int)Gen.Loh];
}

internal static class PluginHeapSnapshot
{
    public const string UnattributedKey = "(core / unattributed)";

    private static Gen Map( Generation g ) => g switch {
        Generation.Generation0 => Gen.Gen0,
        Generation.Generation1 => Gen.Gen1,
        Generation.Generation2 => Gen.Gen2,
        Generation.Large => Gen.Loh,
        Generation.Pinned => Gen.Poh,
        _ => Gen.Other
    };

    public static Dictionary<string, PluginHeapStats> Take( Dictionary<string, string> assemblyNameToPlugin, string tempDirectory )
    {
        _ = Directory.CreateDirectory(tempDirectory);

        var previousTmpDir = Environment.GetEnvironmentVariable("TMPDIR");
        Environment.SetEnvironmentVariable("TMPDIR", tempDirectory);
        try
        {
            using var dt = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
            using var rt = dt.ClrVersions[0].CreateRuntime();
            var heap = rt.Heap;

            var live = new HashSet<ulong>();
            var stack = new Stack<ClrObject>();
            foreach (var root in heap.EnumerateRoots())
            {
                var o = root.Object;
                if (o.IsValid && live.Add(o.Address)) stack.Push(o);
            }
            while (stack.Count > 0)
            {
                var o = stack.Pop();
                foreach (var r in o.EnumerateReferences(carefully: false, considerDependantHandles: true))
                    if (r.IsValid && live.Add(r.Address)) stack.Push(r);
            }

            var result = new Dictionary<string, PluginHeapStats>(StringComparer.OrdinalIgnoreCase);
            foreach (var seg in heap.Segments)
            foreach (var obj in seg.EnumerateObjects())
            {
                if (!obj.IsValid || obj.IsFree) continue;

                var moduleName = obj.Type?.Module?.Name;
                var simpleName = string.IsNullOrEmpty(moduleName) ? null : Path.GetFileNameWithoutExtension(moduleName);
                var owner = simpleName != null && assemblyNameToPlugin.TryGetValue(simpleName, out var p) ? p : UnattributedKey;

                if (!result.TryGetValue(owner, out var s)) result[owner] = s = new PluginHeapStats();

                var gen = (int)Map(seg.GetGeneration(obj.Address));
                if (live.Contains(obj.Address)) s.Live[gen] += (long)obj.Size;
                else s.Dead[gen] += (long)obj.Size;
            }
            return result;
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMPDIR", previousTmpDir);
        }
    }
}
