#if DEV
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;
using System.Text;

namespace ZeroCompanyLevelEditor;

/// <summary>
/// DEV ONLY: what the process holds, and what the garbage collector has been doing to it.
///
/// <c>--memreport</c> starts the collection listener before anything else runs (Program.Main) and makes the
/// driver print a report at each point it names. Nothing here is shipped: tools\stagecheck.ps1 looks for the flag.
///
/// WHY AN EVENT LISTENER AND NOT JUST THE COUNTERS. <c>GC.CollectionCount(2)</c> says how many gen-2 collections
/// there were; it cannot say how many of them somebody ASKED for, and "the only GC.Collect in the code we run is
/// CUE4Parse's, once per Nanite mesh" is a claim about who asked. The runtime says why it collected (the Reason
/// of every GCStart event: 0 = an allocation filled a generation, 1 = Induced, 7 = InducedNotForced,
/// 9 = InducedLowMemory, 10 = InducedCompacting), and it says when it stopped the program and let it go again, so
/// the time spent PAUSED is measured here from the runtime's own suspend and restart events rather than inferred.
/// </summary>
internal static class MemReport
{
    private static Listener? _listener;

    /// <summary>Start listening. Idempotent; call it as early as possible so the first load is inside the count.</summary>
    public static void Start() => _listener ??= new Listener();

    public static bool Running => _listener is not null;

    private static readonly bool[] InducedReason = new bool[16];

    static MemReport()
    {
        // GCReason as the runtime numbers it. 1 Induced, 7 InducedNotForced, 9 InducedLowMemory, 10 InducedCompacting.
        foreach (var r in new[] { 1, 7, 9, 10 }) InducedReason[r] = true;
    }

    private sealed class Listener : EventListener
    {
        private readonly object _lock = new();
        private DateTime _suspendBegin;
        private bool _inPause;
        private int _reasonSinceSuspend = -1, _depthSinceSuspend = -1;

        // [0] = not asked for, [1] = asked for (induced); by the generation collected (depth 0, 1, 2)
        public readonly long[,] Count = new long[2, 3];
        public readonly double[,] PauseMs = new double[2, 3];
        public readonly Dictionary<int, long> ByReason = new();
        public double PauseOther;                         // a pause with no GCStart in it (a suspension for something else)

        protected override void OnEventSourceCreated(EventSource source)
        {
            // 0x1 = the GC keyword: GCStart/GCEnd and the suspend/restart pair. Informational is enough.
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, EventLevel.Informational, (EventKeywords) 0x1);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            var name = e.EventName ?? "";
            lock (_lock)
            {
                if (name.StartsWith("GCSuspendEEBegin", StringComparison.Ordinal))
                {
                    _suspendBegin = e.TimeStamp; _inPause = true; _reasonSinceSuspend = -1; _depthSinceSuspend = -1;
                }
                else if (name.StartsWith("GCStart", StringComparison.Ordinal) && e.PayloadNames is { } names && e.Payload is { } p)
                {
                    var reason = Get(names, p, "Reason"); var depth = Get(names, p, "Depth");
                    if (reason >= 0) { _reasonSinceSuspend = (int) reason; ByReason[(int) reason] = ByReason.GetValueOrDefault((int) reason) + 1; }
                    if (depth >= 0) _depthSinceSuspend = (int) Math.Min(2, depth);
                    if (reason >= 0 && depth >= 0)
                        Count[IsInduced((int) reason) ? 1 : 0, (int) Math.Min(2, depth)]++;
                }
                else if (name.StartsWith("GCRestartEEEnd", StringComparison.Ordinal) && _inPause)
                {
                    _inPause = false;
                    var ms = (e.TimeStamp - _suspendBegin).TotalMilliseconds;
                    if (_reasonSinceSuspend >= 0 && _depthSinceSuspend >= 0)
                        PauseMs[IsInduced(_reasonSinceSuspend) ? 1 : 0, _depthSinceSuspend] += ms;
                    else PauseOther += ms;
                }
            }
        }

        private static long Get(System.Collections.ObjectModel.ReadOnlyCollection<string> names,
                                System.Collections.ObjectModel.ReadOnlyCollection<object?> payload, string key)
        {
            var i = names.IndexOf(key);
            return i >= 0 && i < payload.Count && payload[i] is { } v ? Convert.ToInt64(v) : -1;
        }

        public string Summary()
        {
            lock (_lock)
            {
                string Row(int induced)
                {
                    var c = new[] { Count[induced, 0], Count[induced, 1], Count[induced, 2] };
                    var p = new[] { PauseMs[induced, 0], PauseMs[induced, 1], PauseMs[induced, 2] };
                    return $"gen0 {c[0]:N0} ({p[0]:N0} ms)  gen1 {c[1]:N0} ({p[1]:N0} ms)  gen2 {c[2]:N0} ({p[2]:N0} ms)";
                }
                var reasons = string.Join(" ", ByReason.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value:N0}"));
                return $"INDUCED {Row(1)} | OTHER {Row(0)} | pause with no GC in it {PauseOther:N0} ms | reasons {reasons}";
            }
        }

        public (long InducedGen2, double InducedPauseMs, double TotalPauseMs, long InducedAll) Totals()
        {
            lock (_lock)
            {
                double induced = 0, all = 0; long inducedAll = 0;
                for (var g = 0; g < 3; g++) { induced += PauseMs[1, g]; all += PauseMs[0, g] + PauseMs[1, g]; inducedAll += Count[1, g]; }
                return (Count[1, 2], induced, all + PauseOther, inducedAll);
            }
        }

        private static bool IsInduced(int reason) => reason is >= 0 and < 16 && InducedReason[reason];
    }

    // ------------------------------------------------------------------ the numbers

    /// <summary>One line of the process's own memory, the GC's view of its heap, and the GC's activity so far.</summary>
    public static string Line()
    {
        using var proc = Process.GetCurrentProcess();
        var info = GC.GetGCMemoryInfo(GCKind.Any);
        var gens = info.GenerationInfo.ToArray();        // a span cannot be captured by the local function below
        string Gen(int i) => i < gens.Length ? $"{gens[i].SizeAfterBytes / 1048576.0:N0}" : "?";
        var priv = proc.PrivateMemorySize64;
        var committed = info.TotalCommittedBytes;
        var sb = new StringBuilder();
        sb.Append($"private {priv / 1073741824.0:N2} GB | working set {proc.WorkingSet64 / 1073741824.0:N2} GB (peak {proc.PeakWorkingSet64 / 1073741824.0:N2}) | ");
        sb.Append($"GC committed {committed / 1073741824.0:N2} GB, heap {info.HeapSizeBytes / 1073741824.0:N2} GB (gen0 {Gen(0)} gen1 {Gen(1)} gen2 {Gen(2)} LOH {Gen(3)} POH {Gen(4)} MB at the last GC) | ");
        sb.Append($"outside the GC heap {(priv - committed) / 1073741824.0:N2} GB | ");
        sb.Append($"GCs {GC.CollectionCount(0):N0}/{GC.CollectionCount(1):N0}/{GC.CollectionCount(2):N0} (cumulative gen0/1/2), paused {GC.GetTotalPauseDuration().TotalSeconds:N1} s");
        return sb.ToString();
    }

    /// <summary>What the listener saw: collections asked for versus not, by generation, and the pause time each cost.</summary>
    public static string Gc() => _listener?.Summary() ?? "(listener not started)";

    public static (long InducedGen2, double InducedPauseMs, double TotalPauseMs, long InducedAll) GcTotals() =>
        _listener?.Totals() ?? (0, 0, 0, 0);

    // ------------------------------------------------------------------ outside .NET: the address space, by kind

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(IntPtr address, out MemoryBasicInformation info, nuint length);

    /// <summary>
    /// The committed memory of this process by KIND, read from the address space: private (the heap, the GC's
    /// regions, every native allocator), mapped (files and shared sections, and what a driver maps) and image
    /// (the program's own dlls). The biggest private and mapped blocks are listed with where they start, so a
    /// block can be matched against a module or a heap. Not free: it walks every region.
    /// </summary>
    public static IEnumerable<string> AddressSpace(int top = 6)
    {
        const uint MemCommit = 0x1000, MemPrivate = 0x20000, MemMapped = 0x40000, MemImage = 0x1000000;
        long priv = 0, mapped = 0, image = 0, regions = 0;
        var big = new List<(long Size, long Base, string Kind, uint Protect)>();
        var address = (long) 0;
        var size = (nuint) Marshal.SizeOf<MemoryBasicInformation>();
        while (address < 0x7FFFFFFF0000L)
        {
            if (VirtualQuery((IntPtr) address, out var m, size) == 0) break;
            var length = (long) m.RegionSize;
            if (length <= 0) break;
            if (m.State == MemCommit)
            {
                regions++;
                if (m.Type == MemPrivate) priv += length;
                else if (m.Type == MemMapped) mapped += length;
                else if (m.Type == MemImage) image += length;
                if (length >= 64L * 1048576 && m.Type != MemImage)
                    big.Add((length, (long) m.BaseAddress, m.Type == MemPrivate ? "private" : "mapped", m.Protect));
            }
            address = (long) m.BaseAddress + length;
        }
        yield return $"ADDRESS SPACE committed: private {priv / 1073741824.0:N2} GB, mapped {mapped / 1073741824.0:N2} GB, " +
                     $"image {image / 1073741824.0:N2} GB  ({regions:N0} committed regions)";
        foreach (var b in big.OrderByDescending(x => x.Size).Take(top))
            yield return $"ADDRESS SPACE   {b.Size / 1048576.0,9:N0} MB {b.Kind,-7} at 0x{b.Base:X12} protect 0x{b.Protect:X}";
        yield return $"ADDRESS SPACE   {big.Count:N0} block(s) of 64 MB or more, {big.Sum(x => x.Size) / 1073741824.0:N2} GB between them";
    }
}
#endif
