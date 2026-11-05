using Avalonia.Threading;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// HEARING A SOUND, and keeping the file it decoded.
///
/// The author's ask: "Getting sounds to load and play in the new mod studio with a extract button
/// on the sound someone is playing".
///
/// DECODING IS OFF THE UI THREAD AND CAN FAIL. Resolve reads the media out of the paks and hands
/// it to the Wwise decoder, which is a subprocess; on a localised event that reaches no media it
/// returns a reason rather than throwing. So every failure lands on the tab's own note where the
/// person who pressed Play is looking, not only in the log.
/// </summary>
public sealed partial class StudioVm
{
    private DispatcherTimer? _clock;

    /// <summary>
    /// WHAT TO SAY WHEN THE DECODER IS NOT THERE. The Studio does not ship vgmstream (its licence work is not done), and
    /// <see cref="WwiseBridge.Find"/> looks FIRST in <c>tools\vgmstream\</c> beside the program (its line 43, the
    /// base directory itself), then in each folder above it, then on PATH.
    /// </summary>
    internal const string NeedsVgmstream =
        "Playing and extracting sounds needs vgmstream, a free decoder that is not in this download. " +
        "Get the Windows build from https://github.com/vgmstream/vgmstream/releases and unzip it into a folder named " +
        "tools\\vgmstream beside ZeroCompanyModStudio.exe, then press Play again.";

    /// <summary>
    /// Play it, or pause it if it is already playing.
    ///
    /// ONE BUTTON, because the transport has one state and two buttons invite the question of
    /// which one is live. The word on it follows <see cref="SoundTab.Playing"/>.
    /// </summary>
    public async Task PlaySound(SoundTab tab)
    {
        if (_provider is not { } provider) { tab.Note = "the game is not open yet"; return; }

        // ALREADY THIS SOUND AND ALREADY GOING: pause rather than decode it again.
        if (_audio.IsPlaying && _audio.CurrentPath == tab.Wav && tab.Wav.Length > 0)
        {
            _audio.Pause();
            tab.Playing = false;
            tab.Note = "paused";
            return;
        }

        // DECODED ONCE. Resolve returns the cached wav when there is one, so a second press is a
        // file open rather than a subprocess.
        if (tab.Wav.Length == 0 || !File.Exists(tab.Wav))
        {
            tab.Note = "decoding…";
            var paks = _profile?.PaksDirectory ?? "";
            var entry = tab.Entry;
            var cache = SoundCache;

            var (wav, err, noDecoder) = await Task.Run(() =>
            {
                var path = _sounds.Resolve(provider, paks, entry, cache, out var error);
                // ASKED AFTER THE FAILURE, not before: a sound already decoded into the cache plays without it.
                return (path, error, path is null && WwiseBridge.Find() is null);
            });

            if (wav is null)
            {
                // ON THE TAB, not just the log. Whoever pressed Play is looking at the tab.
                // A download without the decoder fails on EVERY first press with the same developer-worded
                // "vgmstream-cli not found"; say what it is and where to put it instead.
                var why = noDecoder ? NeedsVgmstream : $"could not decode: {err}";
                tab.Note = why;
                SayBad($"{entry.Name}: {why}");
                return;
            }
            tab.Wav = wav;
        }

        _audio.Play(tab.Wav, tab.Entry.Name);
        if (_audio.Error is { } playErr)
        {
            tab.Note = $"could not play: {playErr}";
            SayBad($"{tab.Entry.Name}: {playErr}");
            return;
        }

        tab.Playing = true;
        tab.Note = $"playing  ·  {Path.GetFileName(tab.Wav)}";
        StartClock(tab);
    }

    /// <summary>Stop, and forget where it had got to.</summary>
    public void StopSound(SoundTab tab)
    {
        _audio.Stop();
        tab.Playing = false;
        tab.Clock = "";
        tab.Note = "stopped";
        _clock?.Stop();
    }

    /// <summary>
    /// The position readout, ticked four times a second.
    ///
    /// FROM THE PLAYER rather than from a stopwatch this class keeps: the two disagree the moment
    /// anything is paused, and the player is the one that knows.
    /// </summary>
    private void StartClock(SoundTab tab)
    {
        _clock?.Stop();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _clock.Tick += (_, _) =>
        {
            tab.Clock = $"{Show(_audio.Position)} / {Show(_audio.Duration)}";
            if (_audio.IsPlaying) return;

            // ENDED ON ITS OWN. Without this the button still says Pause on a sound that
            // finished, and pressing it pauses something silent.
            tab.Playing = false;
            if (tab.Note.StartsWith("playing", StringComparison.Ordinal)) tab.Note = "finished";
            _clock?.Stop();
        };
        _clock.Start();

        static string Show(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    /// <summary>
    /// KEEP THE DECODED FILE. Copied, not exported: Resolve has already written a WAV into the
    /// preview cache, so by the time a sound is playing the file exists and this is a copy of it.
    /// That is also why the button is dead until it has played once, rather than decoding
    /// silently on the press.
    /// </summary>
    public void ExtractSound(SoundTab tab, string destination)
    {
        if (!tab.CanExtract) { tab.Note = "play it first, then there is a file to keep"; return; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(tab.Wav, destination, overwrite: true);
            tab.Note = $"saved to {destination}";
            Say($"{tab.Entry.Name}: saved to {destination}");
        }
        catch (Exception e)
        {
            tab.Note = $"could not save: {e.Message}";
            SayBad($"{tab.Entry.Name}: could not save — {e.Message}");
        }
    }

    /// <summary>What the library found, for the panel and for a driver flag.</summary>
    public string SoundStatus => _sounds.Ready
        ? $"{_sounds.Entries.Count:N0} sound(s) — {_sounds.Source}"
        : _sounds.Status;

    /// <summary>Find one by name, for a driver flag. First match wins.</summary>
    public SoundLibrary.Entry? FindSound(string fragment) => _sounds.Entries
        .FirstOrDefault(e => e.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
