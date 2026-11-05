using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace UEToolkit.Core;

/// <summary>
/// Plays a decoded `.wav` through the default output device.
///
/// Deliberately takes WAV rather than `.wem`: decoding is <see cref="WwiseBridge"/>'s job and it
/// already runs vgmstream, so the player never has to know a Wwise codec exists. That also means
/// preview and export go through the SAME decode path — if a sound is wrong in the player it is
/// wrong in the export, and there is no second implementation to disagree.
///
/// One voice, on purpose. Auditioning game audio means clicking through a list, and every click
/// should replace what is playing rather than layer onto it.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private WaveOutEvent? _out;
    private AudioFileReader? _reader;
    private readonly Lock _lock = new();

    /// <summary>What is loaded, for the UI to label the transport.</summary>
    public string? CurrentPath { get; private set; }
    public string? CurrentName { get; private set; }
    public string? Error { get; private set; }

    public bool IsPlaying
    {
        get { lock (_lock) return _out?.PlaybackState == PlaybackState.Playing; }
    }

    public TimeSpan Position
    {
        get { lock (_lock) return _reader?.CurrentTime ?? TimeSpan.Zero; }
        set { lock (_lock) { if (_reader is not null) _reader.CurrentTime = value; } }
    }

    public TimeSpan Duration
    {
        get { lock (_lock) return _reader?.TotalTime ?? TimeSpan.Zero; }
    }

    private float _volume = 0.8f;
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            lock (_lock) { if (_reader is not null) _reader.Volume = _volume; }
        }
    }

    /// <summary>Load and start. Any previous sound is stopped and released first.</summary>
    public void Play(string wavPath, string? displayName = null)
    {
        lock (_lock)
        {
            StopLocked();
            Error = null;
            try
            {
                _reader = new AudioFileReader(wavPath) { Volume = _volume };

                // WAVEOUT REFUSES FORMATS THE DECODER HAPPILY PRODUCES, and it refuses them at
                // the device with "WaveBadFormat calling waveOutOpen" rather than anywhere it
                // can be read as a format problem.
                //
                // AudioFileReader hands out 32-bit FLOAT at the file's own channel count. The
                // game's one-shots are mono or stereo and open fine; its MUSIC is neither, and
                // every music event failed to play while every footstep worked. Measured on
                // Play_Bruno_Music, which decoded to a valid WAV and then would not open.
                //
                // So the provider is converted rather than the device coaxed: 16-bit PCM is what
                // waveOut accepts essentially everywhere, and anything above stereo is folded to
                // two channels first because a 5.1 stream has nowhere to go on a stereo device.
                // The reader stays upstream, so Position, Duration and Volume are unaffected.
                ISampleProvider source = _reader;
                if (source.WaveFormat.Channels > 2)
                    source = new MultiplexingSampleProvider(new[] { source }, 2);

                _out = new WaveOutEvent();
                _out.Init(new SampleToWaveProvider16(source));
                _out.Play();
                CurrentPath = wavPath;
                CurrentName = displayName ?? Path.GetFileNameWithoutExtension(wavPath);
            }
            catch (Exception e)
            {
                Error = e.Message;
                StopLocked();
            }
        }
    }

    public void Pause() { lock (_lock) if (_out?.PlaybackState == PlaybackState.Playing) _out.Pause(); }

    public void Resume() { lock (_lock) if (_out?.PlaybackState == PlaybackState.Paused) _out.Play(); }

    public void Stop() { lock (_lock) StopLocked(); }

    private void StopLocked()
    {
        try { _out?.Stop(); } catch { /* a device that has gone away is not worth throwing over */ }
        _out?.Dispose();
        _reader?.Dispose();
        _out = null;
        _reader = null;
        CurrentPath = null;
        CurrentName = null;
    }

    public void Dispose() => Stop();
}
