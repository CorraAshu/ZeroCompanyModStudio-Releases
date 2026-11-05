using CUE4Parse.FileProvider;

namespace UEToolkit.Core;

/// <summary>
/// Every customization part the game ships, read once and shared.
///
/// Joins the two readers: <see cref="PartsIndex"/> for what exists and what gates it,
/// <see cref="PartDetails"/> for the name, icon, mesh and stats. Measured at 0.7s for all 3,524,
/// so it is built eagerly in the background rather than lazily per row.
///
/// It lives here, in Core, rather than inside the browser panel that first needed it, because the
/// authoring side needs exactly the same data for a different purpose: the browser asks "what
/// parts are there", the mod editor asks "what meshes could go in a Helmet slot". One build, two
/// readers -- and the alternative was scanning the game twice to answer the same question.
/// </summary>
public sealed class PartsCatalog
{
    public sealed record Entry(PartsIndex.Part Part, PartDetails.Detail Detail)
    {
        public string Label => Detail.Label(Part.Name);

        /// <summary>Lowercased once, because filters run over every entry on every keystroke.</summary>
        public string Haystack { get; } =
            (Detail.Label(Part.Name) + " " + Part.Name + " " + Part.Path).ToLowerInvariant();
    }

    public IReadOnlyList<Entry> Entries { get; private set; } = Array.Empty<Entry>();
    public IReadOnlyList<string> Slots { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> Characters { get; private set; } = Array.Empty<string>();

    public volatile bool Building;
    public volatile string Status = "";
    public bool Ready => Entries.Count > 0;

    /// <summary>Bumped whenever Entries is replaced, so a consumer can notice without polling equality.</summary>
    public int Generation { get; private set; }

    private bool _started;

    /// <summary>
    /// Build once. Safe to call every frame -- the second and later calls do nothing, which is
    /// what lets a panel just ask for the catalogue in its draw loop.
    /// </summary>
    public void EnsureBuilt(IFileProvider provider, string primaryAssetType, Action<string> log)
    {
        if (_started) return;
        _started = true;
        Building = true;
        Status = "Reading the asset registry...";

        _ = Task.Run(() =>
        {
            try
            {
                var parts = PartsIndex.Read(provider, primaryAssetType);
                Status = $"Reading {parts.Count:N0} parts...";

                var entries = new List<Entry>(parts.Count);
                foreach (var p in parts)
                    entries.Add(new Entry(p, PartDetails.Read(provider, p.Path)));

                Slots = entries.Select(e => e.Part.Slot).Distinct().OrderBy(s => s).ToList();
                Characters = entries.SelectMany(e => e.Part.Characters).Distinct()
                                    .OrderBy(s => s).ToList();
                Entries = entries;
                Generation++;
                Status = "";
                log($"Parts: {entries.Count:N0} indexed, " +
                    $"{entries.Count(e => e.Detail.HasName)} named, " +
                    $"{entries.Count(e => e.Detail.IconPath is not null)} with an icon, " +
                    $"{entries.Count(e => e.Detail.Meshes.Count > 0)} with a mesh");
            }
            catch (Exception e)
            {
                Status = $"failed: {e.Message}";
                log($"ERROR building the parts catalogue: {e.Message}");
            }
            finally { Building = false; }
        });
    }

    /// <summary>Reset so the next <see cref="EnsureBuilt"/> rebuilds. For switching games.</summary>
    public void Clear()
    {
        _started = false;
        Entries = Array.Empty<Entry>();
        Slots = Array.Empty<string>();
        Characters = Array.Empty<string>();
        Status = "";
    }

    // ------------------------------------------------------------------ authoring queries

    /// <summary>A mesh a mod could use, and what already wears it.</summary>
    public sealed record MeshChoice(string Path, string Name, string UsedBy, int UsedByCount);

    /// <summary>
    /// Distinct meshes worn in a given slot.
    ///
    /// Keyed by mesh rather than by part on purpose: 165 helmet parts share far fewer meshes --
    /// the same helmet appears once per colourway -- and a list with the same name eleven times
    /// is a list nobody can pick from. The part that wears it is carried along as the label,
    /// because "SK_HAAM_01A_HELM" means nothing and "Headpiece" does.
    ///
    /// <paramref name="friendlySlot"/> null means every slot. Offered deliberately: putting a
    /// torso mesh in a helmet slot is strange but it is the user's call, not ours.
    /// </summary>
    public List<MeshChoice> Meshes(string? friendlySlot)
    {
        var byPath = new Dictionary<string, (string Label, int Count)>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in Entries)
        {
            if (friendlySlot is not null &&
                !string.Equals(ModProject.FromSlotTag(e.Part.Slot), friendlySlot,
                    StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var m in e.Detail.Meshes)
            {
                if (byPath.TryGetValue(m.MeshPath, out var existing))
                    byPath[m.MeshPath] = (existing.Label, existing.Count + 1);
                else
                    byPath[m.MeshPath] = (e.Label, 1);
            }
        }

        return byPath
            .Select(kv => new MeshChoice(kv.Key, Leaf(kv.Key), kv.Value.Label, kv.Value.Count))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>An icon a mod could use.</summary>
    public sealed record IconChoice(string Path, string Name, string UsedBy, int UsedByCount);

    /// <summary>
    /// Distinct menu icons, optionally only those used in one slot.
    ///
    /// Slot-filtered by default in the UI because an icon is a picture of a specific garment:
    /// offering all 1,296 of them when someone is making a helmet is technically complete and
    /// practically useless.
    /// </summary>
    public List<IconChoice> Icons(string? friendlySlot)
    {
        var byPath = new Dictionary<string, (string Label, int Count)>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in Entries)
        {
            if (e.Detail.IconPath is not { } icon) continue;
            if (friendlySlot is not null &&
                !string.Equals(ModProject.FromSlotTag(e.Part.Slot), friendlySlot,
                    StringComparison.OrdinalIgnoreCase))
                continue;

            if (byPath.TryGetValue(icon, out var existing))
                byPath[icon] = (existing.Label, existing.Count + 1);
            else
                byPath[icon] = (e.Label, 1);
        }

        return byPath
            .Select(kv => new IconChoice(kv.Key, Leaf(kv.Key), kv.Value.Label, kv.Value.Count))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>"/Game/X/SK_Y.SK_Y" -> "SK_Y".</summary>
    private static string Leaf(string objectPath)
    {
        var s = objectPath[(objectPath.LastIndexOf('/') + 1)..];
        var dot = s.IndexOf('.');
        return dot > 0 ? s[..dot] : s;
    }
}
