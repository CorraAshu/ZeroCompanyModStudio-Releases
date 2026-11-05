using System.Numerics;
using CUE4Parse.UE4.Assets.Exports.Animation;
using UEToolkit.Core;
using CUE4Parse_Conversion.Animations;
using CUE4Parse_Conversion.Animations.PSA;

namespace UEToolkit.App.Rendering;

/// <summary>Holds the loaded sequence and advances playback time.</summary>
public sealed class AnimPlayer
{
    public CAnimSequence? Sequence { get; private set; }
    public string? Name { get; private set; }
    public string? Error { get; private set; }

    public bool Playing;
    public bool Loop = true;
    public float Speed = 1f;
    public float Frame;

    private int[] _trackMap = [];
    private Skeleton? _skeleton;

    public int FrameCount => External?.FrameCount ?? Sequence?.NumFrames ?? 0;
    public float Fps => External is { Fps: > 0 } b ? b.Fps
        : Sequence is { FramesPerSecond: > 0 } s ? s.FramesPerSecond : 30f;
    public float Duration => FrameCount > 0 ? FrameCount / Fps : 0f;
    public bool HasAnim => (Sequence is not null || External is not null) && _skeleton is not null;

    /// <summary>Set when an animation from another format is playing instead of a UE AnimSequence.</summary>
    public IPoseSource? External { get; private set; }
    private int[] _externalMap = [];

    /// <summary>
    /// Play an animation the host application can read and Core cannot (see
    /// <see cref="IPoseSource"/>): it is matched to the skeleton by bone name and posed through
    /// <see cref="Skeleton.PoseFromDisplacements"/>.
    /// </summary>
    public bool Load(IPoseSource source, Skeleton skeleton, out string message)
    {
        Clear();
        _externalMap = source.BuildMap(skeleton.Names);
        var mapped = _externalMap.Count(x => x >= 0);
        if (mapped == 0)
        {
            message = Error = source.NoMatchMessage;
            return false;
        }

        if (!source.Enabled)
        {
            message = Error = source.DisabledMessage;
            return false;
        }

        External = source;
        Name = source.Name;
        _skeleton = skeleton;
        Frame = 0;
        Playing = true;
        message = $"{source.Name}: {source.FrameCount} frames @ {source.Fps:0.#} fps, " +
                  $"{mapped}/{skeleton.Count} bones mapped";
        return true;
    }

    public bool Load(UAnimSequence anim, Skeleton skeleton, out string message)
    {
        Clear();
        try
        {
            var set = anim.ConvertAnims();
            var seq = set.Sequences.FirstOrDefault();
            if (seq is null)
            {
                message = Error = "Animation converted to zero sequences.";
                return false;
            }

            var withKeys = seq.Tracks.Count(t => t.HasKeys());
            if (withKeys == 0)
            {
                message = Error = "Animation has no bone tracks - data may be in an " +
                                  "unsupported compression codec.";
                return false;
            }

            Sequence = seq;
            Name = anim.Name;
            _skeleton = skeleton;
            _trackMap = skeleton.BuildTrackMap(seq);
            Frame = 0;
            Playing = true;

            var mapped = _trackMap.Count(t => t >= 0);
            message = $"{anim.Name}: {seq.NumFrames} frames @ {Fps:0.#} fps, " +
                      $"{withKeys}/{seq.Tracks.Count} tracks with keys, {mapped}/{skeleton.Count} bones mapped";
            return true;
        }
        catch (Exception e)
        {
            message = Error = e.Message;
            return false;
        }
    }

    /// <summary>
    /// POINT THE LOADED ANIMATION AT A NEW SKELETON, keeping the frame and whether it is running.
    ///
    /// The mod preview reloads its mesh whenever anything about it changes -- a material chosen, a
    /// file saved in Max -- and reloading used to take the animation with it, so the author picked
    /// a walk, changed a material, and the walk stopped. The animation is not tied to the mesh:
    /// it is tied to the RIG, by bone name, and the rig has not changed. Only the maps that index
    /// it need rebuilding.
    /// </summary>
    public bool Rebind(Skeleton skeleton)
    {
        if (Sequence is null && External is null) return false;
        _skeleton = skeleton;
        if (Sequence is not null) _trackMap = skeleton.BuildTrackMap(Sequence);
        else if (External is not null) _externalMap = External.BuildMap(skeleton.Names);
        return true;
    }

    public void Clear()
    {
        Sequence = null;
        External = null;
        _externalMap = [];
        Name = null;
        Error = null;
        Playing = false;
        Frame = 0;
        _trackMap = [];
        _skeleton = null;
    }

    public void Advance(double deltaSeconds)
    {
        if (!Playing || (Sequence is null && External is null) || FrameCount <= 1) return;

        Frame += (float) (deltaSeconds * Fps * Speed);
        if (Frame >= FrameCount - 1)
        {
            if (Loop) Frame %= FrameCount - 1;
            else { Frame = FrameCount - 1; Playing = false; }
        }
        if (Frame < 0) Frame = Loop ? FrameCount - 1 + Frame % (FrameCount - 1) : 0;
    }

    /// <summary>Skinning palette for the current frame, or the rest pose when idle.</summary>
    public Matrix4x4[] Palette(Skeleton skeleton)
    {
        if (_skeleton is null) return skeleton.RestPalette();
        if (External is { } source)
        {
            var frame = Math.Clamp((int) Frame, 0, source.FrameCount - 1);
            return skeleton.PoseFromDisplacements(_externalMap, source.NodeCount,
                node => source.Displacement(node, frame));
        }
        return Sequence is null ? skeleton.RestPalette() : skeleton.Pose(Sequence, Frame, _trackMap);
    }
}
