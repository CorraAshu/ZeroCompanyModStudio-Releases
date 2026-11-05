using System.Numerics;
using Silk.NET.OpenGL;

namespace UEToolkit.App.Rendering;

public sealed class Shader : IDisposable
{
    private readonly GL _gl;
    private readonly uint _handle;
    private readonly Dictionary<string, int> _uniforms = new();

    public Shader(GL gl, string vertexSrc, string fragmentSrc)
    {
        _gl = gl;
        var vs = Compile(ShaderType.VertexShader, vertexSrc);
        var fs = Compile(ShaderType.FragmentShader, fragmentSrc);

        _handle = _gl.CreateProgram();
        _gl.AttachShader(_handle, vs);
        _gl.AttachShader(_handle, fs);
        _gl.LinkProgram(_handle);

        _gl.GetProgram(_handle, ProgramPropertyARB.LinkStatus, out var ok);
        if (ok == 0) throw new InvalidOperationException($"Shader link failed: {_gl.GetProgramInfoLog(_handle)}");

        _gl.DetachShader(_handle, vs);
        _gl.DetachShader(_handle, fs);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);

        // EVERY SAMPLER GETS ITS OWN UNIT AT LINK TIME, not when a caller remembers.
        //
        // A sampler's default value is 0, so the shadow cascades' sampler2DArrayShadow sits on
        // unit 0 alongside uDiffuse's sampler2D until somebody sets it. Two sampler TYPES on one
        // unit is undefined behaviour: it is a validation failure on some drivers and renders
        // black on others, and it would have happened not here but in the toolkit and the Mod
        // Studio, which share this shader and never heard of a shadow map. Pinning it once, where
        // the program is created, is the version that cannot be forgotten by a third caller.
        Use();
        if (Loc("uShadowMap") >= 0) _gl.Uniform1(Loc("uShadowMap"), 5);
        _gl.UseProgram(0);
    }

    private uint Compile(ShaderType type, string src)
    {
        var handle = _gl.CreateShader(type);
        _gl.ShaderSource(handle, src);
        _gl.CompileShader(handle);
        _gl.GetShader(handle, ShaderParameterName.CompileStatus, out var ok);
        if (ok == 0) throw new InvalidOperationException($"{type} failed: {_gl.GetShaderInfoLog(handle)}");
        return handle;
    }

    public void Use() => _gl.UseProgram(_handle);

    private int Loc(string name)
    {
        if (_uniforms.TryGetValue(name, out var l)) return l;
        l = _gl.GetUniformLocation(_handle, name);
        _uniforms[name] = l;
        return l;
    }

    public void Set(string name, int v) => _gl.Uniform1(Loc(name), v);
    public void Set(string name, float v) => _gl.Uniform1(Loc(name), v);
    public void Set(string name, Vector3 v) => _gl.Uniform3(Loc(name), v.X, v.Y, v.Z);
    public void Set(string name, Vector2 v) => _gl.Uniform2(Loc(name), v.X, v.Y);
    public void Set(string name, Vector4 v) => _gl.Uniform4(Loc(name), v.X, v.Y, v.Z, v.W);

    public unsafe void Set(string name, Matrix4x4 m) =>
        _gl.UniformMatrix4(Loc(name), 1, false, (float*) &m);

    public unsafe void SetArray(string name, Matrix4x4[] m)
    {
        if (m.Length == 0) return;
        fixed (Matrix4x4* p = m)
            _gl.UniformMatrix4(Loc(name), (uint) m.Length, false, (float*) p);
    }

    public void Dispose() => _gl.DeleteProgram(_handle);
}
