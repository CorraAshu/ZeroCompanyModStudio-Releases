using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse.UE4.Wwise;

namespace UEToolkit.Core;

/// <summary>
/// The game's audio, browsable and playable without extracting anything first.
///
/// Two sources, preferred in order.
///
/// **Wwise manifests**, where the game ships them. `SoundBanksInfo` names every media file with
/// its authored filename, language and project path, so the list is real names in the audio
/// team's own folders and covers everything — including localised voice, which no UE asset
/// reaches. Reading all 491 of one game's takes about four seconds.
///
/// **UE audio events**, otherwise. Some games ship no manifests and name their audio assets
/// individually, so event names are good there.
///
/// The first version of this listed any `.uasset` whose PATH contained "audio" or "wwise", which
/// filled the panel with Wwise editor icons — `bus_nor`, `folder_nor`, `acoutex_nor` — because
/// they live under `WwiseTree/Icons`. Matching a path substring is not the same as finding audio.
/// </summary>
public sealed class SoundLibrary
{
    public sealed record Entry(string Name, string Folder, string Language, uint MediaId, string? AssetPath)
    {
        public bool FromManifest => AssetPath is null;
        public string Display => Name;
        public bool IsLocalised => !Language.Equals("SFX", StringComparison.OrdinalIgnoreCase);
    }

    private readonly List<Entry> _entries = new();
    public IReadOnlyList<Entry> Entries => _entries;
    public bool Ready { get; private set; }
    public string Status { get; private set; } = "Sounds not scanned";
    public string Source { get; private set; } = "";

    private readonly Dictionary<uint, string> _wemById = new();
    private WwiseProvider? _wwise;
    private readonly Lock _wwiseLock = new();

    public void Build(IFileProvider provider, Action<string>? log = null)
    {
        _entries.Clear();
        _wemById.Clear();

        foreach (var f in provider.Files.Keys)
            if (f.EndsWith(".wem", StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(Path.GetFileNameWithoutExtension(f), out var id))
                _wemById[id] = f;

        var info = WwiseBankInfo.Build(provider, log);
        if (info.Count > 0)
        {
            foreach (var m in info.ById.Values)
            {
                if (!_wemById.ContainsKey(m.Id)) continue;   // named but not shipped loose
                _entries.Add(new Entry(m.Name, m.Folder, m.Language, m.Id, null));
            }
            Source = "Wwise manifests";
        }
        else
        {
            // Only assets that really are audio events; the class is read, not the path guessed.
            foreach (var path in provider.Files.Keys)
            {
                if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
                if (!path.Contains("audio", StringComparison.OrdinalIgnoreCase) &&
                    !path.Contains("wwise", StringComparison.OrdinalIgnoreCase)) continue;
                if (path.Contains("/Icons/", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("EditorResources", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("EditorMaterials", StringComparison.OrdinalIgnoreCase)) continue;
                if (AssetIndex.RealClass(provider, path) is not { } cls) continue;
                if (!cls.Contains("AkAudioEvent", StringComparison.OrdinalIgnoreCase) &&
                    !cls.Contains("AudioSound", StringComparison.OrdinalIgnoreCase)) continue;

                var dir = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
                var content = dir.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
                if (content >= 0) dir = dir[(content + "/Content/".Length)..];
                _entries.Add(new Entry(Path.GetFileNameWithoutExtension(path), dir, "SFX", 0, path));
            }
            Source = "UE audio events";
        }

        _entries.Sort((a, b) =>
        {
            var f = string.Compare(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase);
            return f != 0 ? f : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        Ready = true;
        Status = $"{_entries.Count:N0} sound(s) - {Source}";
        log?.Invoke($"Sound library: {Status}");
    }

    public List<Entry> Search(string query, int limit = 500)
    {
        if (string.IsNullOrWhiteSpace(query)) return _entries.Take(limit).ToList();
        var results = new List<Entry>();
        foreach (var e in _entries)
        {
            if (!e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !e.Folder.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            results.Add(e);
            if (results.Count >= limit) break;
        }
        return results;
    }

    /// <summary>
    /// Decode one entry to `.wav` in <paramref name="cacheDir"/>, cached so re-auditioning is
    /// instant. Manifest entries go straight to the media file by id — no event walk, no bank
    /// tables — which is why preview from a manifest game is immediate.
    /// </summary>
    public string? Resolve(IFileProvider provider, string paksDirectory, Entry ev,
                           string cacheDir, out string? error)
    {
        error = null;
        try
        {
            var rel = WwiseBridge.Sanitise(Path.Combine(ev.Folder, ev.Name));
            var wav = Path.Combine(cacheDir, rel + ".wav");
            if (File.Exists(wav)) return wav;

            byte[] bytes;
            if (ev.FromManifest)
            {
                if (!_wemById.TryGetValue(ev.MediaId, out var wemPath))
                { error = "media not present in the paks"; return null; }
                if (!provider.TrySaveAsset(wemPath, out bytes!)) { error = "could not read media"; return null; }
            }
            else
            {
                if (provider is not AbstractVfsFileProvider vfs)
                { error = "provider does not support Wwise"; return null; }
                lock (_wwiseLock) _wwise ??= new WwiseProvider(vfs, paksDirectory);

                var pkg = AssetLoader.Read(() => provider.LoadPackage(AssetLoader.PackagePath(ev.AssetPath!)));
                var evt = pkg.GetExports().OfType<UAkAudioEvent>().FirstOrDefault();
                if (evt is null) { error = "not an AkAudioEvent"; return null; }
                List<WwiseExtractedSound> sounds;
                lock (_wwiseLock) sounds = _wwise!.ExtractAudioEventSounds(evt);
                if (sounds.Count == 0) { error = "event reaches no media"; return null; }
                bytes = sounds[0].GetData();
            }

            if (bytes.Length == 0) { error = "media is empty"; return null; }

            Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
            var tmp = Path.Combine(Path.GetTempPath(), $"zcms-preview-{Guid.NewGuid():N}.wem");
            File.WriteAllBytes(tmp, bytes);
            var res = WwiseBridge.ToWav(tmp, wav);
            try { File.Delete(tmp); } catch { }
            if (!res.Success) { error = res.Error ?? "decode failed"; return null; }
            return wav;
        }
        catch (Exception e) { error = e.Message; return null; }
    }
}
