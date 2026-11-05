using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.GameplayTags;
using CUE4Parse.UE4.Objects.UObject;

namespace UEToolkit.Core;

/// <summary>
/// The game's image banks: every icon TAG and the texture it draws.
///
/// A menu icon is not a texture path. An FBitReactorImageReference names its picture by gameplay
/// tag -- ImageBank.Icon.Weapon.IQA11 -- and a BitReactorImageBankSet asset is the table that turns
/// the tag into a texture. Nothing references the banks; the game finds them by scanning for the
/// class, which is what lets a mod ship a bank of its own and have its icons found (proved in game
/// 2026-09-01). This index answers the two questions the Studio has to put on screen: what picture
/// a tag draws, and what tags there are to pick from.
///
/// Measured 2026-09-01: eight banks, 497 entries. Seven sit in Game/UI/Icons/IconBank/ and one,
/// IMB_Customization, in Game/UI/ itself -- the note that said "seven in IconBank" was one short,
/// which is why the candidates are found by leaf name under Game/UI/ and not by folder, and then
/// confirmed by class rather than trusted by name.
/// </summary>
public sealed class ImageBanks
{
    public sealed record Entry(string Tag, string Bank, string? TexturePath, float Width, float Height,
                               string BankPackage = "")
    {
        /// <summary>Width over height. Weapon icons are 512x256; nearly everything else is square.</summary>
        public float Aspect => Width > 0 && Height > 0 ? Width / Height : 1f;

        /// <summary>"ImageBank.Icon.Weapon.IQA11" -> "IQA11".</summary>
        public string Leaf => Tag[(Tag.LastIndexOf('.') + 1)..];

        /// <summary>"ImageBank.Icon.Weapon.IQA11" -> "ImageBank.Icon.Weapon".</summary>
        public string Family
        {
            get { var cut = Tag.LastIndexOf('.'); return cut < 0 ? "" : Tag[..cut]; }
        }

        public bool IsWeapon => Family.Equals(WeaponFamily, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The family every armoury icon belongs to. Nineteen of them, all 512x256.</summary>
    public const string WeaponFamily = "ImageBank.Icon.Weapon";

    /// <summary>The family a specialization's crest belongs to. Fourteen, square.</summary>
    public const string ClassFamily = "ImageBank.Icon.Class";

    /// <summary>"ImageBank.Icon.Weapon" -> "Weapon", for a label.</summary>
    public static string FamilyLeaf(string family) => family[(family.LastIndexOf('.') + 1)..];

    /// <summary>The export class a bank is. Candidates found by name are confirmed by this.</summary>
    public const string BankClass = "BitReactorImageBankSet";

    public IReadOnlyList<Entry> Entries { get; private set; } = Array.Empty<Entry>();
    public IReadOnlyList<string> Banks { get; private set; } = Array.Empty<string>();

    private Dictionary<string, Entry> _byTag = new(StringComparer.OrdinalIgnoreCase);

    public volatile bool Building;
    public volatile string Status = "";
    private volatile bool _done;
    private bool _started;

    /// <summary>
    /// True once the read has FINISHED -- with entries, or having found none. Those are different
    /// states from "not yet", and a picker opened before the read is done photographs as empty,
    /// which is the same picture as a broken one.
    /// </summary>
    public bool Ready => _done;

    /// <summary>The entry a tag names, or null: an unknown tag, or a mod's own that no game bank has.</summary>
    public Entry? Find(string? tag) =>
        tag is not null && _byTag.TryGetValue(tag, out var e) ? e : null;

    /// <summary>Build once, in the background. Safe to call every frame.</summary>
    public void EnsureBuilt(IFileProvider provider, Action<string> log)
    {
        if (_started) return;
        _started = true;
        Building = true;
        Status = "Reading the image banks...";

        _ = Task.Run(() =>
        {
            try
            {
                var entries = Read(provider, Candidates(provider), log);
                _byTag = entries
                    .GroupBy(e => e.Tag, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                Banks = entries.Select(e => e.Bank).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                Entries = entries;
                Status = $"{entries.Count} icons in {Banks.Count} banks";
            }
            catch (Exception ex)
            {
                Status = "image banks: " + ex.Message;
                log(Status);
            }
            finally
            {
                Building = false;
                _done = true;
            }
        });
    }

    public void Clear()
    {
        _started = false;
        _done = false;
        Entries = Array.Empty<Entry>();
        Banks = Array.Empty<string>();
        _byTag = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        Status = "";
    }

    /// <summary>
    /// The files that might be banks: IMB_* under Game/UI/. A name is a hint, not proof, so
    /// <see cref="Read"/> keeps only the exports that ARE the class.
    ///
    /// Game/UI/ and not the game's Mods folder: with a mod mounted, a mod's own bank is a copy of
    /// a game one under new tags, and offering its entries as "the game's" would be wrong.
    /// </summary>
    public static List<string> Candidates(IFileProvider provider) =>
        provider.Files.Keys
            .Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                        k.Contains("/Content/Game/UI/", StringComparison.OrdinalIgnoreCase) &&
                        LeafOf(k).StartsWith("IMB_", StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Every entry of every bank named. A bank's ImageBank property is a TMap from a gameplay tag
    /// to a soft brush; the brush's WeakResourceObject is the cooked soft reference to the texture
    /// (ResourceObject is the uncooked hard one, read second in case a bank carries only that),
    /// and ImageSize is what the menu draws it at.
    /// </summary>
    public static List<Entry> Read(IFileProvider provider, IEnumerable<string> files,
                                   Action<string>? log = null)
    {
        var result = new List<Entry>();
        foreach (var file in files)
        {
            var bank = LeafOf(file);
            // The PACKAGE NAME, "/Game/...", which is what a copy is asked for -- not the container
            // path with its extension dropped, which reads as a name and is not in any container.
            var bankPackage = PackageId.PackageNameFor(file, ModBuild.ProjectName)
                              ?? AssetLoader.PackagePath(file);
            try
            {
                var exports = AssetLoader.Read(() =>
                    provider.LoadPackage(AssetLoader.PackagePath(file)).GetExports().ToArray());
                foreach (var e in exports)
                {
                    if (e.ExportType != BankClass) continue;
                    if (PartDetails.Value(e.Properties, "ImageBank") is not UScriptMap map) continue;

                    foreach (var (k, v) in map.Properties)
                    {
                        if (TagText(k?.GenericValue) is not { } tag) continue;

                        string? path = null;
                        float w = 0, h = 0;
                        if (PartDetails.Unwrap(v?.GenericValue) is FStructFallback brush)
                        {
                            path = PartDetails.SoftPathOf(brush.Properties, "WeakResourceObject")
                                   ?? PartDetails.SoftPathOf(brush.Properties, "ResourceObject");
                            switch (PartDetails.Unwrap(PartDetails.Value(brush.Properties, "ImageSize")))
                            {
                                case FVector2D f: w = f.X; h = f.Y; break;
                                case FVector2d d: w = (float) d.X; h = (float) d.Y; break;
                            }
                        }
                        result.Add(new Entry(tag, bank, path, w, h, bankPackage));
                    }
                }
            }
            catch (Exception ex) { log?.Invoke($"image bank {bank}: {ex.Message}"); }
        }
        return result;
    }

    /// <summary>
    /// A gameplay tag's text from whichever shape it was serialised in: the tag itself, a bare
    /// name, or a struct with a TagName inside -- which is how a TMap KEY comes out, and the shape
    /// <see cref="PartDetails"/> had to learn the hard way.
    /// </summary>
    private static string? TagText(object? v)
    {
        var s = PartDetails.Unwrap(v) switch
        {
            FGameplayTag t => t.TagName.Text,
            FName n => n.Text,
            FStructFallback st => PartDetails.TagOf(st.Properties, "TagName"),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(s) || s == "None" ? null : s;
    }

    private static string LeafOf(string path)
    {
        var s = path[(path.LastIndexOf('/') + 1)..];
        var dot = s.LastIndexOf('.');
        return dot > 0 ? s[..dot] : s;
    }
}
