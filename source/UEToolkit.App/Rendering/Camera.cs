using System.Numerics;

namespace UEToolkit.App.Rendering;

/// <summary>Orbit camera: drag orbits or looks, shift-drag pans, wheel dollies, WASD flies.</summary>
public sealed class Camera
{
    public Vector3 Target = Vector3.Zero;
    public float Distance = 300f;
    public float Yaw = MathF.PI * 0.25f;
    public float Pitch = 0.35f;
    public float Fov = 50f * MathF.PI / 180f;

    private const float MaxPitch = MathF.PI * 0.49f;

    public Vector3 Position
    {
        get
        {
            var cp = MathF.Cos(Pitch);
            return Target + new Vector3(
                MathF.Sin(Yaw) * cp,
                MathF.Sin(Pitch),
                MathF.Cos(Yaw) * cp) * Distance;
        }
    }

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Target, Vector3.UnitY);

    /// <summary>
    /// HOW FAR THE FAR PLANE MUST REACH, whatever the orbit distance happens to be.
    ///
    /// The planes used to come from Distance alone, which is fine for inspecting one mesh and
    /// backwards for a level: zoom IN and the far plane comes in with you, so the map is cut off
    /// at arm's length and everything past it vanishes. At Distance 79 the far plane was 6,580
    /// units on a level tens of thousands across -- the author hit exactly that, "when i scroll to
    /// try and zoom in the map starts to disappear".
    ///
    /// A viewport showing a scene sets this from the scene's own extent; anything showing a single
    /// object leaves it alone and gets the old behaviour.
    /// </summary>
    public float FarFloor = 5000f;

    /// <summary>The planes <see cref="Projection"/> will use. Exposed so a check reads the real
    /// numbers instead of recomputing the formula and testing its own copy of it.</summary>
    /// <summary>
    /// FORCED CLIP PLANES, for telling one cause of vanishing geometry from another.
    ///
    /// "One second it looks normal and the next, if I move the camera, stuff just disappears."
    /// The draw count says the geometry IS being submitted, so it is lost somewhere after that --
    /// and near-plane clipping and depth-buffer precision produce the same complaint. Nothing
    /// could tell them apart by looking. Overriding the planes can: if the geometry comes back
    /// with near pulled in, it was being clipped, and if it does not, it never was.
    /// </summary>
    public float? NearOverride;
    public float? FarOverride;

    public float FarPlane => FarOverride ?? MathF.Max(Distance * 20f + 5000f, FarFloor);

    /// <summary>
    /// WHAT YOU ZOOMED TO IS ALWAYS IN FRONT OF THE NEAR PLANE.
    ///
    /// Three pulls, and the honest answer is a compromise rather than a free win:
    ///
    ///   * Depth precision wants near as LARGE as possible relative to far. A level spanning a
    ///     million units and a near plane of half a unit is a depth buffer that z-fights.
    ///   * Close inspection wants near SMALL. The hub's vista puts real asteroids 1.7M units out,
    ///     which pushed near to 20.9 -- so zooming to five units away clipped the very thing being
    ///     zoomed at, and the object vanished as you approached it.
    ///   * The far plane has to clear the level, or the map disappears as you zoom IN.
    ///
    /// So the ratio is held as tight as it can be, and then overridden by one invariant: the near
    /// plane never sits past half the orbit distance. That guarantees the target is visible at any
    /// zoom, and spends depth precision only when somebody is right up against something -- which
    /// is exactly when the far half of the scene is a sliver of the screen.
    /// </summary>
    public float NearPlane =>
        NearOverride ??
        MathF.Min(MathF.Max(Distance * 0.005f, FarPlane / 200_000f), MathF.Max(Distance * 0.5f, 0.05f));

    public Matrix4x4 Projection(float aspect)
    {
        var far = FarPlane;

        // AND THE NEAR PLANE HAS TO FOLLOW IT. Depth precision is about the RATIO, not the
        // distances: hold near at Distance*0.005 while far grows to cover a level and a 24-bit
        // buffer runs out of resolution, which trades clipped geometry for z-fighting. Bounding
        // the ratio keeps near as close as it can afford to be.
        return Matrix4x4.CreatePerspectiveFieldOfView(Fov, MathF.Max(aspect, 0.01f), NearPlane, far);
    }

    public void Orbit(float dx, float dy)
    {
        Yaw -= dx * 0.01f;
        Pitch = Math.Clamp(Pitch + dy * 0.01f, -MaxPitch, MaxPitch);
    }

    public void Pan(float dx, float dy)
    {
        var forward = Vector3.Normalize(Target - Position);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var up = Vector3.Cross(right, forward);
        // Scale with distance so panning feels the same however far out you are.
        Target += (-right * dx + up * dy) * Distance * 0.0015f;
    }

    /// <summary>
    /// LOOK AROUND FROM WHERE THE EYE IS, instead of swinging it around the target.
    ///
    /// What a right-drag does in UE: the camera turns on the spot, which is the only rotation
    /// that pairs with flying. Orbit is still the right thing for inspecting one prop, so the two
    /// live on different buttons rather than one replacing the other.
    /// </summary>
    public void Look(float dx, float dy)
    {
        var eye = Position;
        Yaw -= dx * 0.01f;
        Pitch = Math.Clamp(Pitch + dy * 0.01f, -MaxPitch, MaxPitch);

        // Put the target back in front of the eye, so the eye itself did not move.
        var cp = MathF.Cos(Pitch);
        Target = eye - new Vector3(MathF.Sin(Yaw) * cp, MathF.Sin(Pitch), MathF.Cos(Yaw) * cp) * Distance;
    }

    /// <summary>
    /// Fly: carry the whole camera through the world, in world units.
    ///
    /// Moving the TARGET moves the eye with it, because the eye is defined relative to it -- so
    /// the view direction and the orbit radius both survive a flight unchanged.
    /// </summary>
    public void Move(float forward, float right, float up)
    {
        var f = Vector3.Normalize(Target - Position);
        var r = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitY));
        Target += f * forward + r * right + Vector3.UnitY * up;
    }

    /// <summary>
    /// How far out the camera may pull. DERIVED FROM THE LEVEL, not a constant.
    ///
    /// This was 100,000 units, which is a fine ceiling for a character or a skirmish map and far
    /// too small for the hub -- that level spans about a million with the Ring of Kafrene in it,
    /// so the camera hit the clamp long before it could see the place. Fly speed is computed from
    /// Distance, so the clamp also capped how fast anyone could cross it.
    /// </summary>
    public float MaxDistance = 100_000f;

    public void Dolly(float delta) =>
        Distance = Math.Clamp(Distance * MathF.Pow(0.9f, delta), 1f, MathF.Max(MaxDistance, 100f));

    /// <summary>Frame a bounding box so the whole model is visible.</summary>
    public void Frame(Vector3 min, Vector3 max)
    {
        Target = (min + max) * 0.5f;
        var radius = MathF.Max((max - min).Length() * 0.5f, 1f);
        Distance = radius / MathF.Sin(Fov * 0.5f) * 1.25f;
    }
}
