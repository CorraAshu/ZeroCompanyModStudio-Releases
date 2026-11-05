using System.Text.Json;
using CUE4Parse.FileProvider;

namespace UEToolkit.Core;

/// <summary>
/// The `SoundBanksInfo` JSON Wwise generates beside every soundbank, which is the authoritative
/// answer to "what is this numbered .wem".
///
/// Walking UE assets gets you the UE folder tree and the EVENT name, which is not what the audio
/// was called: an event named like `Creature_Aggro_Vox` can play three files really called
/// `Creature_Aggro_Vox_01/03/04.wav`, and reconstructing that from events alone produces one
/// name with a media id stuck on the end. This file has the source filename, the language, and
/// the Wwise PROJECT path — `\Events\AI\Creature\Creature_Vox\...` — which is the structure a
/// hand-extraction ends up with and the one the audio team actually organised.
///
/// It also makes language exact. Every media reference carries its own `Language`, so English can
/// be selected rather than inferred from a parenthetical in a filename.
/// </summary>
public sealed class WwiseBankInfo
{
    /// <summary>One media file: what it is called, where it belongs, and in what language.</summary>
    public sealed record Media(
        uint Id,
        string ShortName,
        string Language,
        string EventName,
        string ObjectPath,
        string BankName)
    {
        /// <summary>`SFX` is Wwise's language for anything not localised.</summary>
        public bool IsLocalised => !Language.Equals("SFX", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Output path from the Wwise project tree, not the UE asset tree. `ObjectPath` looks
        /// like `\Events\AI\Creature\Creature_Vox\Creature_Aggro_Vox`; the leading `Events`
        /// segment and the event leaf are dropped, leaving the folders the audio team authored.
        /// </summary>
        public string Folder
        {
            get
            {
                var parts = ObjectPath.Replace('\\', '/')
                    .Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && parts[0].Equals("Events", StringComparison.OrdinalIgnoreCase))
                    parts = parts[1..];
                if (parts.Length > 1) parts = parts[..^1];      // drop the event leaf
                return parts.Length == 0 ? BankName : string.Join('/', parts);
            }
        }

        /// <summary>The authored filename, without the .wav Wwise records for the source.</summary>
        public string Name =>
            ShortName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                ? ShortName[..^4] : ShortName;
    }

    private readonly Dictionary<uint, Media> _byId = new();
    public IReadOnlyDictionary<uint, Media> ById => _byId;
    public int Count => _byId.Count;

    /// <summary>Media count per language, so a filter can be sanity-checked before it runs.</summary>
    public readonly Dictionary<string, int> Languages = new(StringComparer.OrdinalIgnoreCase);

    public Media? Lookup(uint id) => _byId.TryGetValue(id, out var m) ? m : null;

    /// <summary>
    /// Read every `SoundBanksInfo` JSON in the game. One game ships 260 of them; together
    /// they cover the whole sound build, including the localised voice that the event walk
    /// cannot reach because its media lives in per-language folders inside the paks.
    /// </summary>
    public static WwiseBankInfo Build(IFileProvider provider, Action<string>? log = null)
    {
        var info = new WwiseBankInfo();
        var candidates = provider.Files.Keys
            .Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Where(p => p.Contains("wwise", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int read = 0, skipped = 0;
        foreach (var path in candidates)
        {
            try
            {
                if (!provider.TrySaveAsset(path, out var bytes)) { skipped++; continue; }
                using var doc = JsonDocument.Parse(bytes);
                if (!doc.RootElement.TryGetProperty("SoundBanksInfo", out var root)) { skipped++; continue; }
                if (!root.TryGetProperty("SoundBanks", out var banks)) { skipped++; continue; }
                read++;

                foreach (var bank in banks.EnumerateArray())
                {
                    var bankName = Str(bank, "ShortName");
                    if (!bank.TryGetProperty("IncludedEvents", out var events)) continue;

                    foreach (var ev in events.EnumerateArray())
                    {
                        var evName = Str(ev, "Name");
                        var evPath = Str(ev, "ObjectPath");

                        // Streamed and in-memory list the same media; the first wins and the
                        // second is the same file, so this is not a duplicate to worry about.
                        foreach (var key in new[] { "ReferencedStreamedFiles", "IncludedMemoryFiles" })
                        {
                            if (!ev.TryGetProperty(key, out var files)) continue;
                            foreach (var f in files.EnumerateArray())
                            {
                                if (!uint.TryParse(Str(f, "Id"), out var id) || id == 0) continue;
                                if (info._byId.ContainsKey(id)) continue;
                                var media = new Media(
                                    id,
                                    Str(f, "ShortName"),
                                    Str(f, "Language") is { Length: > 0 } l ? l : "SFX",
                                    evName, evPath, bankName);
                                info._byId[id] = media;
                                info.Languages[media.Language] =
                                    info.Languages.GetValueOrDefault(media.Language) + 1;
                            }
                        }
                    }
                }
            }
            catch { skipped++; }
        }

        log?.Invoke($"SoundBanksInfo: {info.Count:N0} media named from {read:N0} bank manifest(s)" +
                    (skipped > 0 ? $", {skipped:N0} unreadable" : ""));
        foreach (var l in info.Languages.OrderByDescending(x => x.Value))
            log?.Invoke($"    {l.Value,8:N0}  {l.Key}");
        return info;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
