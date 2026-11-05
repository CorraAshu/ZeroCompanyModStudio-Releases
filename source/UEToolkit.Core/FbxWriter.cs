using System.IO.Compression;
using System.Text;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse_Conversion.Meshes.PSK;

namespace UEToolkit.Core;

/// <summary>
/// WRITE A BINARY FBX WITH NOTHING INSTALLED.
///
/// FBX was already reachable from this tool, through <see cref="FbxBridge"/>: write a PSK, then
/// drive `psk2fbx.py` under a Python that can `import fbx`. That works and it is how every FBX in
/// this project has been made -- but it needs the Autodesk FBX SDK on the machine, which is a
/// licence-encumbered install most people who download a mod tool will not have and cannot be
/// shipped for them. So the one format everything reads was the one format an author could not
/// get.
///
/// The format is not a secret. It is a tree of typed records -- the same tree
/// <see cref="FbxReader"/> already parses in this repo, because Max and Maya write it and we read
/// what they write. Writing is that reader run backwards, and every rule it documents is a rule
/// this obeys:
///
///   * records carry the offset where they END, so lengths are back-patched after the children
///   * sizes are 32-bit up to 7400 and 64-bit from 7500 -- this writes 7400 and stays 32-bit
///   * a list of children is terminated by one null record, and so is the top level
///   * array properties are zlib-deflated, flagged per array
///   * object names are stored "Name\0\x01Class"
///
/// What it produces is deliberately the same SHAPE psk2fbx produces, because that pipeline is
/// proven in the user's 3ds Max: control points welded by position exactly as the PSK exporter
/// welds them (CVertexShare, the same class), UVs and normals per polygon vertex, the Max axis
/// system stated rather than left to guess, and bone rotations as Euler XYZ.
///
/// The private `--fbxwrite` verb keeps it honest -- the file is read back and its rig walked, and the
/// bone positions must equal the ones the game's own reference skeleton gives (Y negated, in the
/// right-handed form Dump game assets writes; `--rhfbxcheck` checks that form in full, down to the skin).
/// That check is here because the failure it catches is silent: an Euler convention that is
/// one composition order out leaves the root correct and scrambles everything downstream, which
/// reads as "the importer is odd" rather than as a bug.
/// </summary>
public static class FbxWriter
{
    // ------------------------------------------------------------------ the record tree

    public sealed class Node(string name)
    {
        public readonly string Name = name;
        public readonly List<object> Properties = new();
        public readonly List<Node> Children = new();

        public Node Add(Node child) { Children.Add(child); return child; }
        public Node Add(string name) => Add(new Node(name));

        public Node P(params object[] values) { Properties.AddRange(values); return this; }
    }

    /// <summary>A double array property, distinguished from a long/int one by its own wrapper.</summary>
    private sealed record Doubles(double[] Values);
    private sealed record Ints(int[] Values);
    private sealed record Longs(long[] Values);

    /// <summary>Raw bytes, written as an 'R' property. FileId is the only one that needs it.</summary>
    private sealed record Raw(byte[] Bytes);
    public static object Bytes(byte[] v) => new Raw(v);

    public static object Arr(double[] v) => new Doubles(v);
    public static object Arr(int[] v) => new Ints(v);
    public static object Arr(long[] v) => new Longs(v);

    /// <summary>
    /// An object name as the format stores it: the name, a null, \x01, then the class.
    ///
    /// <see cref="FbxReader"/> turns that separator back into "::" when it reads, which is the
    /// same convention from the other side.
    /// </summary>
    public static string Named(string name, string kind) => name + "\0\u0001" + kind;

    // ------------------------------------------------------------------ serialising

    /// <summary>
    /// The file version, which decides the RECORD WIDTH: 32-bit offsets up to 7400 and 64-bit
    /// from 7500. Both are implemented, because the version is not a free choice -- the footer id
    /// is validated against it along with the FileId, and the only pair proven to pass came out
    /// of a 7700 file.
    ///
    /// 7700 is FBX 2020. It also removes the 2 GB ceiling a 32-bit offset puts on one file, which
    /// a Nanite mesh can genuinely approach.
    /// </summary>
    public const int Version = 7700;

    private static bool Wide => Version >= 7500;

    private static void WriteProperty(BinaryWriter w, object value)
    {
        switch (value)
        {
            case int i: w.Write((byte) 'I'); w.Write(i); break;
            case long l: w.Write((byte) 'L'); w.Write(l); break;
            case float f: w.Write((byte) 'F'); w.Write(f); break;
            case double d: w.Write((byte) 'D'); w.Write(d); break;
            // 'Y' and 'T', which is what the SDK's own files carry -- not 1 and 0.
            case bool b: w.Write((byte) 'C'); w.Write((byte) (b ? 'Y' : 'T')); break;

            case string s:
            {
                w.Write((byte) 'S');
                var bytes = Encoding.UTF8.GetBytes(s);
                w.Write(bytes.Length);
                w.Write(bytes);
                break;
            }

            case Raw r:
            {
                w.Write((byte) 'R');
                w.Write(r.Bytes.Length);
                w.Write(r.Bytes);
                break;
            }

            // An int property written as 0 now and corrected later -- see FbxStream.Slot. It writes
            // exactly what the int it will become writes ('I' and four bytes), which is what keeps
            // a patched file byte-identical to one saved with the final number in the tree.
            case FbxStream.Slot slot:
            {
                if (slot.Position >= 0)
                    throw new InvalidOperationException("a Slot is one property, and this one has already been written");
                w.Write((byte) 'I');
                slot.Position = w.BaseStream.Position;
                w.Write(0);
                break;
            }

            case Doubles a: WriteArray(w, 'd', a.Values.Length, bw => { foreach (var v in a.Values) bw.Write(v); }); break;
            case Ints a: WriteArray(w, 'i', a.Values.Length, bw => { foreach (var v in a.Values) bw.Write(v); }); break;
            case Longs a: WriteArray(w, 'l', a.Values.Length, bw => { foreach (var v in a.Values) bw.Write(v); }); break;

            default: throw new InvalidOperationException($"cannot write property of type {value.GetType().Name}");
        }
    }

    /// <summary>
    /// An array property, always deflated.
    ///
    /// Compression is per array and optional, and a raw array would be simpler -- but a character
    /// mesh is megabytes of doubles and every real exporter deflates, so a file that does not is
    /// both large and unlike anything an importer is used to reading.
    /// </summary>
    private static void WriteArray(BinaryWriter w, char code, int count, Action<BinaryWriter> emit)
    {
        using var raw = new MemoryStream();
        using (var rawWriter = new BinaryWriter(raw, Encoding.UTF8, leaveOpen: true)) emit(rawWriter);

        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            raw.WriteTo(zlib);
        var bytes = packed.ToArray();

        w.Write((byte) code);
        w.Write(count);
        w.Write(1);              // encoding: deflate
        w.Write(bytes.Length);
        w.Write(bytes);
    }

    /// <summary>A record header's three size fields, 4 bytes each up to 7400 and 8 from 7500.</summary>
    private static void Sizes(BinaryWriter w, long end, long props, long propLength)
    {
        if (Wide) { w.Write(end); w.Write(props); w.Write(propLength); }
        else { w.Write((uint) end); w.Write((uint) props); w.Write((uint) propLength); }
    }

    private static int HeaderSize => Wide ? 25 : 13;

    /// <summary>
    /// Where a record's header is and what it holds, kept between <see cref="BeginNode"/> and
    /// <see cref="EndNode"/> so the sizes can be written back once the children are known.
    /// </summary>
    internal readonly record struct Mark(long Start, int PropertyCount, long PropertiesLength);

    internal static void WriteNode(BinaryWriter w, Node node)
    {
        var mark = BeginNode(w, node.Name, node.Properties);
        foreach (var c in node.Children) WriteNode(w, c);
        EndNode(w, mark, node.Children.Count);
    }

    /// <summary>
    /// A record's header, name and properties; its children come next and <see cref="EndNode"/>
    /// closes it. <see cref="WriteNode"/> is these two around a loop, and <see cref="FbxStream"/>
    /// is these two with the loop handed to the caller -- the SAME bytes either way, because it is
    /// the same code.
    /// </summary>
    internal static Mark BeginNode(BinaryWriter w, string name, IReadOnlyList<object> properties)
    {
        var start = w.BaseStream.Position;
        Sizes(w, 0, properties.Count, 0);        // end and length patched below

        var nameBytes = Encoding.UTF8.GetBytes(name);
        w.Write((byte) nameBytes.Length);
        w.Write(nameBytes);

        var propsStart = w.BaseStream.Position;
        foreach (var p in properties) WriteProperty(w, p);
        var propsLength = w.BaseStream.Position - propsStart;
        return new Mark(start, properties.Count, propsLength);
    }

    /// <summary>The terminator, when the rule below asks for one, and the sizes written back into the header.</summary>
    internal static void EndNode(BinaryWriter w, Mark mark, int childCount)
    {
        // THE TERMINATOR RULE, MEASURED FROM A FILE THE SDK WROTE, not reasoned about:
        //
        //     children          -> terminator          (48 nodes)
        //     no properties     -> terminator          (References, and every empty container)
        //     properties, none  -> NO terminator       (238 nodes)
        //
        // Writing it only for nodes with children -- the obvious rule, and the one this had --
        // leaves every EMPTY node unterminated. Our own reader does not care and neither does an
        // independently written parser: both find the same 287 records either way. The Autodesk
        // importer desynchronises at the first one and says "File is corrupted", which is the
        // whole of what it will tell you. In a scene of ours the first such node is `References`,
        // 4,699 bytes in, and everything after it shifts by one record header.
        if (childCount > 0 || mark.PropertyCount == 0) w.Write(new byte[HeaderSize]);

        var end = w.BaseStream.Position;
        w.BaseStream.Position = mark.Start;
        Sizes(w, end, mark.PropertyCount, mark.PropertiesLength);
        w.BaseStream.Position = end;
    }

    /// <summary>
    /// THE FOOTER ID, WHICH IS NOT FREE TO CHOOSE. It is derived from the FileId node, and the
    /// Autodesk importer checks the pair -- a mismatched one is refused with "File is corrupted"
    /// and nothing else. Proved by re-emitting a file the SDK wrote: byte-identical with its own
    /// footer id and it imports, one constant swapped in and it does not.
    ///
    /// So both halves are lifted verbatim from a file the SDK produced, and they travel together.
    /// See <see cref="FileId"/>; changing one without the other breaks every file we write.
    /// </summary>
    public static readonly byte[] FooterId =
    [
        0xfa, 0xbc, 0xaf, 0x0a, 0xd7, 0xc0, 0xd9, 0x65,
        0xb7, 0x7b, 0xf1, 0x81, 0x1a, 0xf0, 0x20, 0x7f,
    ];

    /// <summary>
    /// The creation time from the same captured file. Under test: the derivation may take this
    /// as well as the FileId, which would be why a portable pair alone was not enough.
    /// </summary>
    public const string CreationTime = "2026-09-04 01:05:47:235";

    /// <summary>The other half of that pair. Not decoration -- see <see cref="FooterId"/>.</summary>
    public static readonly byte[] FileId =
    [
        0x2c, 0xb0, 0x29, 0xe0, 0xbc, 0x2f, 0xc7, 0xcc,
        0xb3, 0xc7, 0xbc, 0x2f, 0xa9, 0x2d, 0xfa, 0xf2,
    ];

    private static readonly byte[] FooterMagic =
    [
        0xf8, 0x5a, 0x8c, 0x6a, 0xde, 0xf5, 0xd9, 0x7e,
        0xec, 0xe9, 0x0c, 0xe3, 0x75, 0x8f, 0x29, 0x0b,
    ];

    /// <summary>
    /// Write a whole tree. This is <see cref="FbxStream"/> run over the root's children: create,
    /// one <see cref="FbxStream.Write"/> each, finish -- so the file a tree saves and the file the
    /// same tree streams are the same bytes by construction, and the check that says so
    /// (`--fbxstreamcheck`) compares them anyway.
    /// </summary>
    public static void Save(Node root, string path)
    {
        using var stream = FbxStream.Create(path);
        foreach (var c in root.Children) stream.Write(c);
        stream.Finish();
    }

    /// <summary>The 27 bytes every file starts with.</summary>
    internal static void WriteHeader(BinaryWriter w)
    {
        w.Write(Encoding.ASCII.GetBytes("Kaydara FBX Binary  "));
        w.Write((byte) 0x00);
        w.Write((byte) 0x1a);
        w.Write((byte) 0x00);
        w.Write((uint) Version);
    }

    /// <summary>The top-level terminator and the footer, which close a file after its last top-level node.</summary>
    internal static void WriteFooter(BinaryWriter w)
    {
        w.Write(new byte[HeaderSize]);                // the top-level list terminator

        // THE FOOTER, IN THE ONE ARRANGEMENT PROVEN TO IMPORT.
        //
        // The footer id goes IMMEDIATELY after the top-level terminator -- no alignment before it
        // -- and the padding goes AFTER, sized so the version field lands 140 bytes from the end
        // and the whole file is a multiple of 16. An earlier arrangement padded first instead,
        // which produced a file whose record region was byte-identical to one the Autodesk
        // importer accepts and which it nonetheless refused. That was the last of four framing
        // faults; the others were the terminator rule, the name separator, and this footer's
        // missing version stamp.
        w.Write(FooterId);
        var pad = (16 - (int) ((w.BaseStream.Position + 140) % 16)) % 16;
        if (pad > 0) w.Write(new byte[pad]);
        w.Write((uint) Version);
        w.Write(new byte[120]);
        w.Write(FooterMagic);
    }

    // ------------------------------------------------------------------ maths

    /// <summary>
    /// A quaternion as the rotation matrix FBX means, COLUMN-vector convention.
    ///
    /// Written out rather than borrowed from System.Numerics, whose matrices are row-vector and
    /// therefore the transpose of this. Mixing the two is invisible on any identity rotation --
    /// which is the root of every rig -- and scrambles everything below it.
    /// </summary>
    private static double[,] Rotation(FQuat q)
    {
        double x = q.X, y = q.Y, z = q.Z, w = q.W;
        var n = Math.Sqrt(x * x + y * y + z * z + w * w);
        if (n > 0) { x /= n; y /= n; z /= n; w /= n; }

        return new[,]
        {
            { 1 - 2 * (y * y + z * z), 2 * (x * y - z * w),     2 * (x * z + y * w) },
            { 2 * (x * y + z * w),     1 - 2 * (x * x + z * z), 2 * (y * z - x * w) },
            { 2 * (x * z - y * w),     2 * (y * z + x * w),     1 - 2 * (x * x + y * y) },
        };
    }

    /// <summary>
    /// A bone's local matrix straight from its QUATERNION, never touching Euler angles.
    ///
    /// The independent half of the check. Comparing a rig FK'd this way against the same rig FK'd
    /// from the angles actually written to the file is what proves the Euler conversion, because
    /// the two share no code -- a check built on the conversion it is checking proves only that it
    /// is self-consistent.
    /// </summary>
    public static double[] MatrixFromQuat(FQuat rotation, FVector translation)
    {
        var r = Rotation(rotation);
        return
        [
            r[0, 0], r[1, 0], r[2, 0], 0,
            r[0, 1], r[1, 1], r[2, 1], 0,
            r[0, 2], r[1, 2], r[2, 2], 0,
            translation.X, translation.Y, translation.Z, 1,
        ];
    }

    /// <summary>
    /// Euler angles in DEGREES, in the order `Lcl Rotation` is read back in: XYZ, meaning the
    /// composition Rz * Ry * Rx.
    ///
    /// This is the single most dangerous number in the file. A ZYX decomposition -- the aerospace
    /// roll/pitch/yaw one, which is what a person writes from memory -- produces angles that are
    /// correct for every bone whose rotation is identity and wrong for the rest, compounding down
    /// the chain. The same mistake in psk2fbx put 488 of a vehicle's 522 bones in the wrong place
    /// and read as an importer quirk. The private `--fbxwrite` verb exists to catch exactly this.
    /// </summary>
    public static (double X, double Y, double Z) EulerXyz(FQuat q) => EulerXyz(Rotation(q));

    /// <summary>
    /// The same decomposition from a 3x3 rotation matrix indexed <c>r[row, column]</c> in the
    /// COLUMN-vector convention (the layout the private <c>Rotation(FQuat)</c> builds), for a caller
    /// that already has the matrix and would otherwise round it through a four-float quaternion
    /// and back. The quaternion overload is this one called on <c>Rotation(q)</c>, so the two
    /// cannot disagree.
    ///
    /// The matrix must be a PROPER rotation (determinant +1). A reflection has no Euler angles;
    /// what this returns for one is not a description of it, and nothing here would say so.
    /// </summary>
    public static (double X, double Y, double Z) EulerXyz(double[,] r)
    {
        const double deg = 180.0 / Math.PI;

        var sy = Math.Clamp(-r[2, 0], -1.0, 1.0);
        var y = Math.Asin(sy);

        double x, z;
        if (Math.Abs(r[2, 0]) < 0.999999)
        {
            x = Math.Atan2(r[2, 1], r[2, 2]);
            z = Math.Atan2(r[1, 0], r[0, 0]);
        }
        else
        {
            // Gimbal: cos(y) is zero, X and Z become the same axis. Put it all in Z.
            x = 0;
            z = Math.Atan2(-r[0, 1], r[1, 1]);
        }
        return (x * deg, y * deg, z * deg);
    }
}

/// <summary>
/// A BINARY FBX WRITTEN ONE NODE AT A TIME, so a scene never has to exist as one tree.
///
/// <see cref="FbxWriter.Save"/> takes a finished tree, and a finished tree of a whole map is the
/// thing that cannot be afforded: one sublevel is 5,233 placements, a whole map tens of thousands,
/// and each is a node with a dozen properties that would all sit in memory until the last one was
/// built. The format does not need that -- a record only has to know where it ENDS, and the writer
/// already comes back to fill that in (<see cref="FbxWriter.EndNode"/>) -- so this hands the loop
/// to the caller:
///
///     using var fbx = FbxStream.Create(path);
///     fbx.Write(header);                       // a whole top-level node
///     fbx.Open("Objects");                     // a top-level node whose children arrive one at a time
///     foreach (...) fbx.Child(Build(...));     // each written now, its tree free to be dropped
///     fbx.Close();
///     fbx.Finish();                            // the top-level terminator and the footer
///
/// <see cref="FbxWriter.Save"/> is this same class run over a tree, and shares every line that
/// decides a byte -- the terminator rule, the sizes, the footer arrangement, all of whose hard-won
/// comments live on those methods and apply here unchanged.
///
/// THE COUNTS THAT COME LAST. A file's Definitions section states how many objects there are,
/// and sits BEFORE the objects. Written from a tree that is no problem; streamed, the count is not
/// known until the end. So <see cref="ReserveInt"/> gives an int property to write as 0 now and
/// <see cref="Patch"/> corrects it when the number is known -- the file is seekable and the property
/// is four bytes of fixed width, so nothing after it moves.
///
/// WHAT THIS DOES NOT DO: nest (one node is open at a time -- a scene's streamed section is a
/// top-level node with a flat list of children), or protect an unfinished file. Disposing without
/// <see cref="Finish"/> leaves a file with no footer, which is incomplete on purpose; a caller that
/// must not leave one behind writes to a temporary name and renames after Finish.
/// </summary>
public sealed class FbxStream : IDisposable
{
    /// <summary>
    /// An int property whose value is not known yet. Put it where the int goes -- in a node's
    /// properties (<c>node.P(slot)</c>) or in <see cref="Open"/>'s -- and it is written as
    /// <c>I</c> and 0 when that node is; <see cref="Patch"/> writes the real number over it.
    /// One slot is one property: it can be written once.
    /// </summary>
    public sealed class Slot
    {
        /// <summary>Where the four value bytes are in the file, or -1 before the property has been written.</summary>
        internal long Position = -1;
    }

    private readonly FileStream _file;
    private readonly BinaryWriter _w;
    private FbxWriter.Mark _mark;
    private bool _isOpen;
    private string _openName = "";
    private int _openChildren;
    private bool _finished;
    private bool _disposed;

    private FbxStream(FileStream file, BinaryWriter writer)
    {
        _file = file;
        _w = writer;
    }

    /// <summary>Create the file, replacing any there, and write the file header.</summary>
    public static FbxStream Create(string path)
    {
        // A bare filename has no directory, and CreateDirectory("") throws rather than doing
        // nothing -- which turned a one-word probe argument into a stack trace.
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is { Length: > 0 }) Directory.CreateDirectory(folder);

        // A 64 KB buffer rather than the default 4 KB: every node ends by seeking back to its own
        // header, and each seek flushes, so the buffer sets how often a streamed map hits the disk.
        var file = File.Create(path, 1 << 16);
        var writer = new BinaryWriter(file, Encoding.UTF8);
        try { FbxWriter.WriteHeader(writer); }
        catch { writer.Dispose(); throw; }
        return new FbxStream(file, writer);
    }

    private void Writable(string what)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) throw new InvalidOperationException($"{what}: the file is already finished");
    }

    /// <summary>One whole top-level node, children and all.</summary>
    public void Write(FbxWriter.Node node)
    {
        Writable("Write");
        if (_isOpen)
            throw new InvalidOperationException($"Write: '{_openName}' is still open; Close it first (nodes do not nest here)");
        ArgumentNullException.ThrowIfNull(node);
        FbxWriter.WriteNode(_w, node);
    }

    /// <summary>
    /// Begin a top-level node whose children are written one at a time with <see cref="Child"/>
    /// and which <see cref="Close"/> ends. Its properties are written now.
    /// </summary>
    public void Open(string name, params object[] properties)
    {
        Writable("Open");
        if (_isOpen)
            throw new InvalidOperationException($"Open '{name}': '{_openName}' is still open; Close it first (nodes do not nest here)");
        _mark = FbxWriter.BeginNode(_w, name, properties);
        _isOpen = true;
        _openName = name;
        _openChildren = 0;
    }

    /// <summary>One child of the open node, written now; the caller may drop its tree.</summary>
    public void Child(FbxWriter.Node node)
    {
        Writable("Child");
        if (!_isOpen) throw new InvalidOperationException("Child: no node is open; Open one first");
        ArgumentNullException.ThrowIfNull(node);
        FbxWriter.WriteNode(_w, node);
        _openChildren++;
    }

    /// <summary>
    /// End the open node: its terminator if the format's rule wants one (it does for any node with
    /// children and for a node with no properties, and does not for properties and no children --
    /// see <see cref="FbxWriter.EndNode"/>) and its size fields.
    /// </summary>
    public void Close()
    {
        Writable("Close");
        if (!_isOpen) throw new InvalidOperationException("Close: no node is open");
        FbxWriter.EndNode(_w, _mark, _openChildren);
        _isOpen = false;
    }

    /// <summary>A new int property to put in a node and correct later with <see cref="Patch"/>.</summary>
    public Slot ReserveInt() => new();

    /// <summary>
    /// Write <paramref name="value"/> into the slot's four bytes. The slot must already have been
    /// written (its node passed to <see cref="Write"/>, <see cref="Child"/> or <see cref="Open"/>);
    /// it may be patched again, and the last value stands.
    /// </summary>
    public void Patch(Slot slot, int value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(slot);
        if (slot.Position < 0)
            throw new InvalidOperationException("Patch: this slot has not been written yet; pass the node holding it to Write, Child or Open first");
        var end = _file.Position;
        _file.Position = slot.Position;
        _w.Write(value);
        _file.Position = end;
    }

    /// <summary>
    /// The top-level terminator and the footer, exactly as <see cref="FbxWriter.Save"/> ends a
    /// file, and the data is flushed. No node may be open. Slots may still be patched afterwards,
    /// until the stream is disposed.
    /// </summary>
    public void Finish()
    {
        Writable("Finish");
        if (_isOpen) throw new InvalidOperationException($"Finish: '{_openName}' is still open; Close it first");
        FbxWriter.WriteFooter(_w);
        _w.Flush();
        _finished = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _w.Dispose();
    }
}
