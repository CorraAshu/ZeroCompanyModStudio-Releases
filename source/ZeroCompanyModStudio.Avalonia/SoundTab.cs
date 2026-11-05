using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// ONE SOUND, OPEN: what it is, and the transport to hear it.
///
/// The author's ask: "Getting sounds to load and play in the new mod studio with a extract button
/// on the sound someone is playing".
///
/// NONE OF THE HARD PART IS HERE. <see cref="SoundLibrary"/> indexes the game's events and the
/// Wwise manifests, <see cref="SoundLibrary.Resolve"/> pulls the media out and decodes it, and
/// <see cref="AudioPlayer"/> plays it — all of it built and author-confirmed for both games
/// already. This is a tab over it.
///
/// AND THE EXTRACT BUTTON IS NEARLY FREE because of how Resolve works: it decodes to a WAV in a
/// cache folder and hands back the path, so by the time a sound is playing the file exists. The
/// button is a copy, not an export. That cache is under the SAME DataDir the ImGui Studio uses,
/// so a sound decoded in either is instant in the other.
///
/// ONE PLAYER FOR THE APPLICATION, held by <see cref="StudioVm"/> rather than here. Two tabs each
/// with their own would play over each other, and "stop" would have to find whichever one was
/// making the noise.
/// </summary>
public sealed class SoundTab : StudioTab
{
    public SoundTab(SoundLibrary.Entry entry) => Entry = entry;

    public SoundLibrary.Entry Entry { get; }

    public override string Title => Entry.Name;

    public override string Tooltip =>
        $"{Entry.Folder}/{Entry.Name}" +
        (Entry.IsLocalised ? $"  ({Entry.Language})" : "") +
        (Entry.FromManifest ? "  — from the Wwise manifest" : $"  — {Entry.AssetPath}");

    /// <summary>
    /// Where it came from, for the panel. WHICH OF THE TWO SOURCES MATTERS: an event has a
    /// package somebody can open and re-point, and a manifest entry has only a media id, so the
    /// same row means two different things depending on this.
    /// </summary>
    public string Where => Entry.FromManifest
        ? $"Wwise manifest  ·  media {Entry.MediaId}"
        : Entry.AssetPath ?? "";

    public string Language => Entry.IsLocalised ? Entry.Language : "SFX (not localised)";

    private string _note = "Press Play to decode and hear it.";

    /// <summary>What it is doing, or why it is not. Written by the view model.</summary>
    public string Note { get => _note; set => Set(ref _note, value); }

    private string _clock = "";

    /// <summary>Position over duration, ticked while it plays.</summary>
    public string Clock { get => _clock; set => Set(ref _clock, value); }

    private bool _playing;
    public bool Playing { get => _playing; set { if (Set(ref _playing, value)) Raise(nameof(PlayWord)); } }

    /// <summary>One button, and it says which way it goes rather than making somebody guess.</summary>
    public string PlayWord => _playing ? "Pause" : "Play";

    private string _wav = "";

    /// <summary>
    /// The decoded file, once there is one. EMPTY UNTIL IT HAS PLAYED, which is what the Extract
    /// button is disabled on: offering to save a file that does not exist yet would either have
    /// to decode silently on the press or fail, and the honest version is to say so.
    /// </summary>
    public string Wav
    {
        get => _wav;
        set { if (Set(ref _wav, value)) Raise(nameof(CanExtract)); }
    }

    public bool CanExtract => _wav.Length > 0 && File.Exists(_wav);
}
