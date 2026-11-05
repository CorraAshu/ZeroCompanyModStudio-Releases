using System.Numerics;

namespace UEToolkit.Core;

/// <summary>
/// An animation the drawing code can play that is not an Unreal AnimSequence: a format the host
/// application knows how to read and Core does not.
///
/// The player asks it three things - how its own nodes line up with the skeleton on screen (by
/// bone name), how many frames it has, and, for a node and a frame, how far that node has moved
/// from its own bind pose in the skeleton's space - and does the skinning itself. Nothing about
/// the source's file format reaches the drawing code.
/// </summary>
public interface IPoseSource
{
    string Name { get; }
    int FrameCount { get; }
    float Fps { get; }

    /// <summary>How many nodes the source animates; a map entry at or past this poses nothing.</summary>
    int NodeCount { get; }

    /// <summary>False while the host has switched this kind of playback off.</summary>
    bool Enabled { get; }

    /// <summary>What to say when no bone of the skeleton matches a node of this source.</summary>
    string NoMatchMessage { get; }

    /// <summary>What to say when <see cref="Enabled"/> is false.</summary>
    string DisabledMessage { get; }

    /// <summary>For each bone name, the index of this source's node of the same name, or -1.</summary>
    int[] BuildMap(IReadOnlyList<string> boneNames);

    /// <summary>
    /// The node's displacement from its own bind pose at a frame, already restated in the
    /// skeleton's space and units.
    /// </summary>
    Matrix4x4 Displacement(int node, int frame);
}
