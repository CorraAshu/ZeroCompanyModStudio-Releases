namespace UEToolkit.App.Rendering;

/// <summary>
/// Deliberately simple lighting. The aim is to judge whether the right textures landed on
/// the right mesh, not to match Unreal's shading.
/// </summary>
public static class Shaders
{
    public const string MeshVertex = """
        #version 330 core
        layout (location = 0) in vec3 aPos;
        layout (location = 1) in vec3 aNormal;
        layout (location = 2) in vec2 aUV;
        layout (location = 3) in vec4 aBoneIdx;
        layout (location = 4) in vec4 aBoneWeight;
        layout (location = 5) in vec2 aUV1;

        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProj;
        // (cols, rows, vMinTile, enabled) -- UDIM tile addressing.
        uniform vec4 uUdim;
        uniform int  uSkinned;
        // Bone palette lives in a texture, not uniforms: 256 mat4s is 1024 vec4 registers,
        // past the vertex-shader constant limit, and characters here exceed 128 bones.
        uniform sampler2D uBoneTex;

        mat4 boneMatrix(int i)
        {
            return mat4(
                texelFetch(uBoneTex, ivec2(0, i), 0),
                texelFetch(uBoneTex, ivec2(1, i), 0),
                texelFetch(uBoneTex, ivec2(2, i), 0),
                texelFetch(uBoneTex, ivec2(3, i), 0));
        }

        out vec3 vNormal;
        out vec2 vUV;
        out vec3 vWorld;
        // PASSED THROUGH UNREMAPPED, unlike vUV. The UDIM remap below addresses a tile inside
        // the DIFFUSE's flattened atlas; a mask is its own texture with its own layout, so
        // putting UV1 through that transform would divide it by a grid it is not on.
        out vec2 vUV1;

        void main()
        {
            vec4 pos = vec4(aPos, 1.0);
            vec3 nrm = aNormal;

            if (uSkinned == 1)
            {
                // Linear blend skinning. Weights are normalised on upload, so an
                // unweighted vertex would collapse to the origin -- those keep weight 1
                // on bone 0 instead.
                mat4 skin =
                    boneMatrix(int(aBoneIdx.x)) * aBoneWeight.x +
                    boneMatrix(int(aBoneIdx.y)) * aBoneWeight.y +
                    boneMatrix(int(aBoneIdx.z)) * aBoneWeight.z +
                    boneMatrix(int(aBoneIdx.w)) * aBoneWeight.w;
                pos = skin * pos;
                nrm = mat3(skin) * nrm;
            }

            vec4 world = uModel * pos;
            vWorld = world.xyz;
            vNormal = mat3(uModel) * nrm;
            vUV1 = aUV1;
            vec2 uv = aUV;
            if (uUdim.w > 0.5)
            {
                // The flattened atlas stacks rows OPPOSITE to UE's V direction, so the tile
                // row index is inverted. Within a tile, V still runs top-to-bottom.
                // Clamped to the grid, and the fraction measured from the tile actually chosen:
                // islands bleed past an edge, and an unclamped row index sends V = -0.001 to the
                // far side of the atlas inverted. Must stay identical to UvAtlas.Remap.
                float cols = uUdim.x, rows = uUdim.y, vMinTile = uUdim.z;
                float tileRow = clamp(floor(uv.y) - vMinTile, 0.0, rows - 1.0);
                float fv      = uv.y - (tileRow + vMinTile);
                float imgRow  = (rows - 1.0) - tileRow;
                float imgV    = (imgRow + fv) / rows;
                // No V flip: textures are uploaded top-row-first, so GL v=0 is already the
                // image's TOP row. Flipping here would double-compensate.
                uv = vec2(uv.x / cols, imgV);
            }
            vUV = uv;
            gl_Position = uProj * uView * world;
        }
        """;

    public const string MeshFragment = """
        #version 330 core
        in vec3 vNormal;
        in vec2 vUV;
        in vec3 vWorld;
        in vec2 vUV1;

        uniform sampler2D uDiffuse;
        uniform sampler2D uNormalMap;
        uniform sampler2D uOrme;
        uniform int  uHasDiffuse;
        uniform int  uHasOrme;

        // THE NORMAL MAP, WHICH WAS DECLARED HERE AND NEVER SAMPLED.
        //
        // `uNormalMap` existed as a uniform, the Studio viewport bound it to unit 1 every frame,
        // TextureCache decoded and uploaded the texture per section -- and no line of this shader
        // ever read it. Every surface in both viewports was lit off the interpolated vertex
        // normal alone, which is most of why painted metal reads as flat plastic. The menu even
        // said so out loud: "Rendered adds normals and ORM". The ORM half was true.
        //
        // Same shape as the placement faults: read correctly, uploaded, dropped before use.
        uniform int   uHasNormalMap;
        uniform float uNormalStrength;

        // The DETAIL normal, a second map at its own tiling. `MM_Tiled_RGB` -- 32% of every
        // material instance in the game -- carries `Detail Normal VT` with `Detail Normal Tile`
        // and `Detail Normal Intensity`, behind a `Use Detail Normal?` switch on 39 of 200
        // sampled instances.
        uniform sampler2D uDetailNormal;
        uniform int   uHasDetailNormal;
        uniform float uDetailTile;
        uniform float uDetailIntensity;

        // UV TINT: the quadrant a face's UVs sit in picks one of three colours.
        //
        // `MM_Tiled_RGB` ships `( 1,-1) Color`, `(-1, 1) Color` and `(-1,-1) Color`, each with a
        // strength, behind `Use UVTint?` -- and pointedly NO `( 1, 1) Color`, so the positive
        // quadrant is the untinted base and one material serves four colour variants.
        //
        // MEASURED BEFORE BUILDING, because the reading is worthless if every UV is in 0..1:
        // `--uvrange` found 10 of 29 hangar meshes with negative UVs, UV0 spanning -2.691..4.109
        // and -1.894..2.699, crossing the axes WITHIN a single mesh. That is the feature working
        // as intended -- faces of a modular kit piece placed in different quadrants to be tinted
        // differently by one material.
        uniform int  uHasUvTint;
        uniform vec3 uUvTintPN;      // ( 1,-1)
        uniform vec3 uUvTintNP;      // (-1, 1)
        uniform vec3 uUvTintNN;      // (-1,-1)
        uniform vec3 uUvTintStr;     // strengths, in that same order

        // THE EMISSIVE SHEET: a pattern texture masked by a second texture, tinted and scaled.
        // `Emissive Color` defaults to 000000, so a section that does not set one emits nothing
        // and the default is safe.
        uniform sampler2D uEmSheet;
        uniform sampler2D uEmMask;
        uniform int   uHasEmSheet;
        uniform vec3  uEmSheetColor;
        uniform float uEmSheetTile;
        uniform float uEmSheetStrength;

        // MM_Rocks: A TEXTURE PROJECTED DOWN A WORLD AXIS, gated on how level the surface is.
        //
        // The second-biggest environment family, 396 instances, and this is what it is for: moss
        // or dust laid over rock, projected along `World Projection Axis` and faded in between
        // `Projected Slope Min` and `Max` so it settles on flat faces and not on walls. Every
        // term is a named parameter -- `Projection Size`, `Projected Intensity`,
        // `Projected Tint Color`, `Projected Normal Intensity`, `Projected Roughness Min/Max` --
        // so like the trim sheet this one can be reproduced rather than approximated by feel.
        //
        // The axis arrives PRE-CONVERTED to GL space. It is authored as a colour, 0000FF, which
        // is the vector (0,0,1) -- UE's up. Reading it through the sRGB encoder that the other
        // colours use would gamma-mangle any axis that was not exactly 0 or 1.
        uniform sampler2D uProjColor;
        uniform sampler2D uProjNrm;
        uniform int   uHasProj;
        uniform int   uHasProjNrm;
        uniform vec3  uProjAxis;
        uniform vec3  uProjTint;
        uniform float uProjSize;
        uniform float uProjIntensity;
        uniform float uProjSlopeMin;
        uniform float uProjSlopeMax;
        uniform float uProjNrmIntensity;
        uniform vec2  uProjRough;

        // A SECOND MASK, projected at its OWN size, to break the first one up. 700 against the
        // main projection's 512, so the two never tile together and the moss stops reading as a
        // repeating sheet. `Projected Additional Alpha Contrast` ships 0, which is identity.
        uniform sampler2D uProjAlpha;
        uniform int   uHasProjAlpha;
        uniform float uProjAlphaSize;
        uniform float uProjAlphaContrast;

        // FUZZ: a grazing-angle brightening, for moss and lichen.
        //
        // **`Fuzz Color` is the only parameter exposed** -- A0D295, a pale green. UE's
        // FuzzyShading function also takes a core darkness and a power, and those are graph
        // constants a cooked instance does not carry, so the COLOUR here is a reading and the
        // FALLOFF SHAPE is a convention. Said plainly rather than implied, the same way the trim
        // sheet's channel order is.
        // Below this the fragment is thrown away. Negative disables the test, so an opaque
        // material costs one comparison and never loses a pixel to it.
        uniform float uAlphaCut;

        // A DECAL'S SHAPE FROM A SHARED ATLAS. uDecalChannel picks which of R/G/B/A carries this
        // instance's decal; one grunge sheet serves three of them.
        uniform sampler2D uDecalMask;
        uniform int   uHasDecalMask;
        uniform vec4  uDecalChannel;
        uniform float uDecalMaskPower;
        uniform float uDecalMaskStrength;
        uniform float uDecalAlpha;
        uniform vec3  uDecalTint;
        uniform float uDecalTintStrength;

        uniform int  uHasFuzz;
        uniform vec3 uFuzzColor;
        uniform int  uMode;         // 0 lit, 1 unlit/albedo, 2 normals, 3 UV checker,
                                    // 4 AO, 5 roughness, 6 smoothness, 7 metallic,
                                    // 8 reflectance, 9 flat (wireframe)
        // Is the mask's red an occlusion map, or an unused channel? See RenderSection.MaskHasAo.
        uniform int  uAoFromMask;
        uniform int  uSelected;
        uniform vec3 uCamPos;

        // THE TINT MASK, and one colour per channel.
        //
        // The game keeps a mask whose R, G and B are three separate regions -- measured across
        // 666 of them: 422 use all three. Which region feeds which of the material's "Color NN"
        // parameters is decided by the material GRAPH, which a cooked instance does not expose,
        // so this paints the CHANNELS and says so rather than guessing a wiring.
        //
        // That is also the question an author actually has: a mask drawn for one kit's UVs and
        // used on another's paints the wrong panels, and no amount of knowing the parameter names
        // would show it.
        // THE MATERIAL'S OWN LIGHT. Added to the lit colour, before fog and before the curve --
        // a glowing panel is radiance leaving the surface, so haze sits in front of it and the
        // tonemapper treats it like any other bright thing. Added after the curve it would be a
        // sticker rather than a light.
        uniform sampler2D uEmissive;
        uniform int  uHasEmissive;
        uniform vec3 uEmissiveTint;

        uniform sampler2D uTintMask;
        uniform int  uHasTint;

        // WHAT THE SHADER ACTUALLY SAMPLES, drawn straight out. The same trick uTrimDebug plays
        // for the trim sheet, and for the same reason: a readback of the uploaded texture and a
        // sample of it in the fragment are two different claims, and only the second one is what
        // decides a pixel.
        uniform int  uTintDebug;
        uniform vec3 uTintR;
        uniform vec3 uTintG;
        uniform vec3 uTintB;

        // THE RGB TRIM SHEET, which is a DIFFERENT MECHANISM from the tint mask above.
        //
        // The environment's painted metals wear `MI_*_RGBTrim*`, and those materials name their
        // colours OUTRIGHT -- `R Color`, `G Color`, `B Color`, `A Color` -- so unlike the
        // character tint mask there is no hidden graph wiring to guess: the parameter name IS
        // the channel. They also set `Use tint only? (No DIF)`, which says the diffuse texture
        // is not the surface colour at all.
        //
        // That is the hub's hull. We were binding UNI_HulkMetalPaintedA_DIF and drawing it, which
        // loads correctly and is not what the game shows.
        //
        // uTrimUv1 exists because `RGBMaskUV1` is true on these and the meshes really do ship two
        // UV sets -- measured on SM_Hub_Hull_Roof_00. See RenderMesh.Floats.
        //
        // SUCCESSIVE MIX, NOT A WEIGHTED SUM, and this is a CONVENTION rather than a reading: the
        // compositing order lives in the material graph, which a cooked instance does not expose.
        // Layering is what UE's own trim setups do and what the tint block above does, and it
        // degrades sanely -- an empty mask leaves the base alone instead of dividing by zero.
        uniform sampler2D uTrimMask;
        uniform int  uHasTrim;
        uniform int  uTrimUv1;      // 1 = sample the mask on UV1
        uniform int  uTrimUseA;     // the A channel is a fourth region, not opacity
        uniform int  uTrimNoDif;    // "Use tint only? (No DIF)" -- replace the albedo, not tint it
        uniform vec3 uTrimBase;     // "Tint Color", the ground the channels are laid over
        uniform vec3 uTrimR;
        uniform vec3 uTrimG;
        uniform vec3 uTrimB;
        uniform vec3 uTrimA;
        // PER-CHANNEL STRENGTH, WHICH THE MATERIAL STATES OUTRIGHT.
        //
        // `R/G/B/A Channel Color Strength`, and finding them settled two things at once. B --
        // the grime channel, the near-black one with the widest coverage -- ships at 0.578, so
        // the game itself softens exactly the channel whose flecks looked too heavy. And R, the
        // panel seams, ships at 1.635: applied at 1.0 the seams barely registered, which is why
        // a measured diff of the containers found 6.1% of pixels darkened and NOT ONE lightened.
        //
        // Scaling the mask WEIGHT rather than the colour, and clamped. A strength above one means
        // "reach full colour before the mask does", which is what a contrast control on a soft
        // mask is for; scaling the colour instead would push it past its own value and blow out.
        uniform vec4 uTrimStrength;   // r,g,b,a
        // ROUGHNESS, THROUGH THE SAME MASK, and this is where the panel seams actually live.
        //
        // `R Channel Roughness` is 0.806 at `R Channel Roughness Strength` 2.0, while `R Color`
        // is C4C4C4 -- within a few per cent of the grey it sits on. So the seams were never a
        // colour change: they are a GLOSS BREAK, which is why a measured diff of the colour blend
        // found 6.1% of pixels darkened and not one lightened, and why the seams read as missing.
        //
        // `Roughness Min/Max` (0.091/0.789 on the hull) remaps the ORME roughness before any of
        // this, which is a narrower range than the 0..1 the sampler hands over -- a material
        // saying its surface is never fully matt and never a mirror.
        uniform vec4 uTrimRough;      // per-channel target roughness
        uniform vec4 uTrimRoughStr;   // and how hard each one pulls
        uniform vec2 uTrimRoughRemap; // Roughness Min, Roughness Max
        // METALLIC, through the same mask again. `G Channel Metallic` is 1 and the other three are
        // 0, so on this family the RUST is the metal -- which is the right way round: bare
        // oxidised steel showing through paint, and paint is not a conductor.
        //
        // It matters more than a third channel usually would, because `metal` is what decides
        // whether a highlight keeps the surface's own colour: mix(vec3(0.25), albedo * 2, metal)
        // below. At 0 every specular is grey plastic. So rust reading as metal is the difference
        // between a brown patch and a brown patch that catches the light like iron.
        uniform vec4 uTrimMetal;      // per-channel target
        uniform float uTrimMetalStr;  // `RGB Metallic Strength` -- how hard the mask pulls
        uniform vec2 uTrimMetalRemap; // Metallic Min, Metallic Max
        uniform float uTrimTile;      // `RGB Tile` -- the mask's own UV repeat
        uniform float uTrimBright;    // `Tint Brightness`
        // DRAW THE MASK ITSELF. Four theories about the blend all predict "the picture changes";
        // only looking at the sampled mask says whether we are reading trim regions or noise.
        uniform int  uTrimDebug;

        // WHICH ORDER THE THREE CHANNELS LAYER IN. 0 = R,G,B; 1 = B,G,R.
        //
        // The compositing order lives in the material graph, which cook stripped, so ours is a
        // CONVENTION. "Order is unobservable" was measured once and believed; it needs to stay
        // measurable, and a claim with no switch behind it cannot be re-checked.
        uniform int  uTrimOrder;

        // THE LEVEL'S OWN LIGHT RIG, when there is one.
        //
        // The three-point rig below is right for the thing this shader was written for -- one
        // mesh on a turntable, where the job is to read its shape. A LEVEL has its own lighting
        // and it is the whole point of the look: Umbara's key light is a pale sun at intensity
        // 8.4 coming down at -0.41,-0.59,-0.69, with a violet sky. Lighting that with a studio
        // rig produces a well-presented model of a level rather than the level.
        //
        // So both live here and uLevelLit chooses. The single-mesh viewer never sets it and is
        // bit-for-bit unchanged.
        uniform int  uLevelLit;         // 0 studio three-point, 1 the level's rig
        uniform int  uSunCount;
        uniform vec3 uSunDir[4];        // pointing FROM the light, UE convention, already in GL space
        uniform vec3 uSunRadiance[4];   // colour * intensity, linear, NOT clamped to 1
        uniform vec3 uSkyUp;            // sky radiance overhead
        uniform vec3 uSkyDown;          // bounce from below, the cheapest stand-in for GI
        uniform float uExposure;

        // How hard the glow pushes. UE scales emissive by a graph constant a cooked instance does
        // not expose, so this is a dial rather than a reading -- like the fog's unit bridge, and
        // labelled the same way.
        uniform float uEmissiveStrength;

        // The instance's OWN strength, relative to its family's modal value, so the global above
        // stays the author's exposure control and this carries the data.
        uniform float uEmissiveScale;

        // A glow with no map: the tint IS the surface. Set only where the material provably
        // carries neither emissive artwork nor a base colour for the lit term to shade.
        uniform int   uEmissiveFlat;

        // LOCAL LIGHTS -- the spots and point lights the level hangs off its own fixtures.
        //
        // These are what a corridor actually looks like. MM_02_030_TheDarkGrotto has NO
        // directional light at all and a sky at 0.01: 230 local lights are the entire scene, and
        // without them that map renders black. The hub keeps 179.
        //
        // 32 AT A TIME. GL 3.3 guarantees only 1024 fragment uniform components and each light
        // costs 13, so 32 is what fits with room to spare on the weakest card this could run on.
        // The viewport sends the 32 whose reach the eye is nearest -- which is not the 32 nearest
        // lights, because a 600-metre floodlight matters from further away than a desk lamp.
        //
        // UNITS. uLocalColour carries the light's LUX AT ONE METRE divided by the key sun, so
        // dividing by distance-in-metres squared lands it in the same units as the sun term above.
        // That is the photometric definition of a candela, not a fitted constant: UE's own scale
        // puts an 8-lux sun next to 8-candela bulbs for exactly this reason.
        uniform int   uLocalCount;
        uniform vec3  uLocalPos[32];
        uniform vec3  uLocalDir[32];        // where the cone points, GL space
        uniform vec3  uLocalColour[32];
        // x reach in world units, y cos(outer), z 1/(cos(inner)-cos(outer)), w falloff exponent
        // (0 means inverse-square, which is what most of them use)
        uniform vec4  uLocalShape[32];

        // CASCADED SHADOW MAPS for the sun.
        //
        // Shadows are most of what separates a lit scene from a rendered one: without them every
        // surface facing the sun is equally bright whether or not anything stands between, and the
        // level reads as a model of itself. This is the author's "not even remotely close" in one
        // term -- the game's Umbara has black shadows under a violet haze.
        //
        // CASCADES BECAUSE THE RANGE IS ABSURD. Umbara's geometry spans 65,000 units and the hub
        // over a million with the Ring of Kafrene in the distance. One shadow map over that is
        // metres per texel; three fitted to slices of the view frustum put the resolution where
        // the eye is. The split each fragment uses is chosen by its distance from the camera, the
        // same number the fog already computes.
        uniform int   uShadowOn;
        uniform sampler2DArrayShadow uShadowMap;
        uniform mat4  uShadowMat[3];
        uniform vec3  uShadowSplit;     // far distance of each cascade, in world units
        uniform vec3  uShadowTexel;     // world size of one texel in each cascade

        // NORMAL-OFFSET, not a depth bias. A constant depth bias either leaves acne on surfaces
        // nearly parallel to the sun or peels the shadow away from the object's own feet; moving
        // the SAMPLE along the surface normal by about a texel scales itself with the cascade and
        // does neither. The 3x3 PCF is what stops a 2048-wide map looking like a staircase.
        // WHAT THE CASCADES ACTUALLY CONTAIN, drawn to the screen.
        //
        // The pass ran, culled 16,504 casters, cost 8 ms -- and the picture did not change. Reading
        // the code found nothing: the matrix convention, the NDC range and the sampler unit all
        // check out on paper. So stop reading it. Each mode below answers ONE question, and
        // between them they say whether the map is empty or the sampling is wrong:
        //
        //   1  the shadow factor itself      all white  -> every tap says "lit"
        //   2  which cascade a fragment uses  r/g/b, black -> past the last split
        //   3  uv.xy inside the map           blue       -> sampled outside [0,1] and gave up
        //   4  the reference depth            flat       -> the projection is collapsing z
        uniform int uShadowDebug;

        vec3 ShadowDebug(vec3 world, vec3 n, float viewDist, float nl);

        float SunShadow(vec3 world, vec3 n, float viewDist, float nl)
        {
            if (uShadowOn == 0) return 1.0;

            int c = viewDist < uShadowSplit.x ? 0 : viewDist < uShadowSplit.y ? 1 : 2;
            if (viewDist > uShadowSplit.z) return 1.0;

            float texel = uShadowTexel[c];
            float slope = clamp(1.0 - nl, 0.0, 1.0);
            vec3 offset = world + n * texel * (1.5 + 3.0 * slope);

            vec4 lp = uShadowMat[c] * vec4(offset, 1.0);
            vec3 uv = lp.xyz / lp.w * 0.5 + 0.5;
            if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || uv.z > 1.0) return 1.0;

            // NOT named `step`: that is a GLSL built-in, and shadowing it compiles on this
            // driver and fails on the next one.
            float sum = 0.0;
            vec2 texStep = 1.0 / vec2(textureSize(uShadowMap, 0).xy);
            for (int y = -1; y <= 1; ++y)
                for (int x = -1; x <= 1; ++x)
                    sum += texture(uShadowMap, vec4(uv.xy + vec2(x, y) * texStep, float(c), uv.z));
            return sum / 9.0;
        }

        vec3 ShadowDebug(vec3 world, vec3 n, float viewDist, float nl)
        {
            if (uShadowOn == 0) return vec3(1.0, 0.0, 1.0);          // magenta: never built

            int c = viewDist < uShadowSplit.x ? 0 : viewDist < uShadowSplit.y ? 1 : 2;
            if (viewDist > uShadowSplit.z) return vec3(0.0);

            if (uShadowDebug == 2)
                return c == 0 ? vec3(1.0, 0.2, 0.2)
                     : c == 1 ? vec3(0.2, 1.0, 0.2)
                              : vec3(0.2, 0.2, 1.0);

            float texel = uShadowTexel[c];
            float slope = clamp(1.0 - nl, 0.0, 1.0);
            vec3 offset = world + n * texel * (1.5 + 3.0 * slope);
            vec4 lp = uShadowMat[c] * vec4(offset, 1.0);
            vec3 uv = lp.xyz / lp.w * 0.5 + 0.5;

            bool outside = uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || uv.z > 1.0;
            if (uShadowDebug == 3) return outside ? vec3(0.0, 0.0, 1.0) : vec3(uv.xy, 0.0);

            // The reference depth, stretched. System.Numerics builds DirectX-convention matrices,
            // so ndc.z lands in [0,1] and this in [0.5,1]; showing the raw value would be a flat
            // grey whatever happened. If THIS is flat, the ortho range is swallowing the scene.
            if (uShadowDebug == 4) return outside ? vec3(1.0, 0.0, 0.0) : vec3((uv.z - 0.5) * 2.0);

            // MODE 5 -- IS THERE ANYTHING IN THE MAP AT ALL?
            //
            // Sample with a reference just short of the far plane instead of the fragment's own
            // depth. Where a caster was written the stored depth is less than 0.999, the LEQUAL
            // compare fails and this comes back BLACK; where the layer is still at its cleared 1.0
            // it comes back WHITE. So a white frame means the depth pass wrote nothing -- which no
            // amount of staring at the projection maths can tell you.
            if (uShadowDebug == 5)
            {
                float any = texture(uShadowMap, vec4(uv.xy, float(c), 0.999));
                return vec3(any);
            }

            return vec3(SunShadow(world, n, viewDist, nl));
        }

        // EXPONENTIAL HEIGHT FOG. Umbara's is violet at 1.65,0.63,4.00 and it is most of what
        // that planet looks like -- without it the level reads as an overexposed daylight render
        // however carefully the sun is placed. Density falls off with height above the fog's own
        // anchor, which is why the anchor has to be read rather than assumed to be zero.
        uniform int   uHasFog;
        uniform vec3  uFogColour;       // linear, HDR, not clamped
        uniform float uFogDensity;
        uniform float uFogFalloff;
        uniform float uFogHeight;       // GL space, so the level's Z
        uniform float uFogScale;        // world units are centimetres; this is the unit bridge

        out vec4 FragColor;

        // ACES filmic curve, the cheap fit. A sun at intensity 8.4 against a sky at 1.75 is HDR,
        // and clamping that to 1 blows every lit surface to white and loses exactly the contrast
        // the rig was carrying. Krzysztof Narkowicz's approximation -- close enough that the
        // difference from the real curve is invisible next to having no curve at all.
        vec3 Aces(vec3 x)
        {
            return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
        }

        void main()
        {
            // Two-sided lighting. Back faces keep the normal of the front they belong to, so
            // once culling is off they light as if facing away and come out near-black -- an
            // interior wall then reads as a void just as convincingly as a hole did. Hollow
            // single-sided shells are common, so back faces are seen constantly and must be lit.
            vec3 N = normalize(vNormal);
            if (!gl_FrontFacing) N = -N;

            // A TANGENT BASIS FROM SCREEN-SPACE DERIVATIVES, not from a vertex attribute.
            //
            // The correct way is a tangent per vertex, and the reason not to is bookkeeping: this
            // format went 16 floats to 18 to carry UV1 and that meant three separate packing
            // sites to get right. A fourth would be a fourth chance to write one of them wrong
            // and have it show up as a lighting artefact nobody attributes to packing. The
            // derivative construction needs no format change, works for static and skinned
            // geometry alike, and is only measurably worse across a UV seam.
            //
            // Cotangent frame after Mikkelsen: dp/du and dp/dv recovered from the screen-space
            // derivatives of world position and UV, made perpendicular to the shading normal.
            if (uHasNormalMap == 1 || uHasDetailNormal == 1)
            {
                vec3 dpx = dFdx(vWorld), dpy = dFdy(vWorld);
                vec2 dux = dFdx(vUV),    duy = dFdy(vUV);
                vec3 perpY = cross(dpy, N), perpX = cross(N, dpx);
                vec3 T = perpY * dux.x + perpX * duy.x;
                vec3 B = perpY * dux.y + perpX * duy.y;
                float inv = inversesqrt(max(max(dot(T, T), dot(B, B)), 1e-12));
                mat3 tbn = mat3(T * inv, B * inv, N);

                vec3 nSum = vec3(0.0, 0.0, 1.0);
                if (uHasNormalMap == 1)
                {
                    // Z RECONSTRUCTED rather than sampled. UE cooks most normal maps as BC5,
                    // which stores X and Y only and leaves blue at whatever the decoder filled
                    // in -- trusting that channel tilts every normal towards the viewer on some
                    // textures and not others. Rebuilding it from the length is right for a
                    // two-channel map and within rounding of a three-channel one.
                    vec2 xy = texture(uNormalMap, vUV).rg * 2.0 - 1.0;
                    xy *= uNormalStrength;
                    nSum = vec3(xy, sqrt(max(1.0 - dot(xy, xy), 0.0)));
                }
                if (uHasDetailNormal == 1)
                {
                    vec2 dxy = texture(uDetailNormal, vUV * uDetailTile).rg * 2.0 - 1.0;
                    dxy *= uDetailIntensity;
                    // Whiteout blend: add the tangent-space slopes, keep the base's Z. Cheaper
                    // than reorienting and it does not wash the base map out the way a plain
                    // average does.
                    nSum = normalize(vec3(nSum.xy + dxy, nSum.z));
                }
                N = normalize(tbn * nSum);
            }

            // THE PROJECTION'S WEIGHT, computed from the FINAL normal so the normal maps above
            // decide the slope too -- a rock whose map tilts a face out of the slope band should
            // lose its moss there, which is most of what makes the effect read as settled dust
            // rather than a decal.
            float projW = 0.0;
            vec2 projUv = vec2(0.0);
            if (uHasProj == 1)
            {
                projW = smoothstep(uProjSlopeMin, uProjSlopeMax, dot(N, uProjAxis)) * uProjIntensity;
                projW = clamp(projW, 0.0, 1.0);
                if (projW > 0.0)
                {
                    // A basis perpendicular to the axis, so the projection works for any axis and
                    // not just world up. The guard picks a different seed when the axis IS the
                    // seed, where the cross product would collapse to zero.
                    vec3 seed = abs(uProjAxis.y) > 0.99 ? vec3(0.0, 0.0, 1.0) : vec3(0.0, 1.0, 0.0);
                    vec3 t = normalize(cross(seed, uProjAxis));
                    vec3 b = cross(uProjAxis, t);
                    vec2 flat2 = vec2(dot(vWorld, t), dot(vWorld, b));
                    projUv = flat2 / max(uProjSize, 1.0);

                    // THE BREAK-UP MASK, folded in before the weight is used for anything, so
                    // colour, roughness and normal all agree about where the layer is.
                    if (uHasProjAlpha == 1)
                    {
                        float a = texture(uProjAlpha, flat2 / max(uProjAlphaSize, 1.0)).r;
                        // Contrast about the midpoint; the shipped 0 is identity, which is what a
                        // parameter nobody set has to be.
                        a = clamp((a - 0.5) * (1.0 + uProjAlphaContrast) + 0.5, 0.0, 1.0);
                        projW *= a;
                    }

                    // AND THE PROJECTED NORMAL, WHICH I BOUND AND DID NOT SAMPLE. Declared the
                    // uniform, uploaded the texture to unit 12, spent the decode -- and left no
                    // line reading it, which is the same fault as the base normal map sitting
                    // unsampled for months, reintroduced within the hour of fixing it. In the
                    // PROJECTION's basis rather than the surface's, because that is the space a
                    // planar projection's normal is authored in.
                    if (uHasProjNrm == 1)
                    {
                        vec2 pxy = texture(uProjNrm, projUv).rg * 2.0 - 1.0;
                        pxy *= uProjNrmIntensity;
                        vec3 pn = vec3(pxy, sqrt(max(1.0 - dot(pxy, pxy), 0.0)));
                        N = normalize(mix(N, mat3(t, b, uProjAxis) * pn, projW));
                    }
                }
            }

            vec4 diffuseSample = uHasDiffuse == 1 ? texture(uDiffuse, vUV) : vec4(0.55, 0.55, 0.55, 1.0);

            // THE DECAL'S OWN SHAPE, where its artwork is a stand-in and the mask is all it has.
            // Selected by a dot rather than a branch: uDecalChannel is one-hot, so this IS the
            // channel the material named.
            float decalMask = 1.0;
            if (uHasDecalMask == 1)
            {
                float m = dot(texture(uDecalMask, vUV), uDecalChannel);
                m = pow(clamp(m * uDecalMaskStrength, 0.0, 1.0), uDecalMaskPower);
                decalMask = clamp(m * uDecalAlpha, 0.0, 1.0);
            }

            // Where the decal has no artwork of its own, the mask supplies the alpha -- otherwise
            // the diffuse does, exactly as before.
            float cutAlpha = (uHasDecalMask == 1 && uHasDiffuse == 0) ? decalMask : diffuseSample.a;

            // THE CUTOUT, BEFORE ANY OTHER WORK. Discarding first means a thrown-away fragment
            // costs nothing else -- no trim sample, no projection, no lighting.
            if (uAlphaCut >= 0.0 && cutAlpha < uAlphaCut) discard;

            vec3 srgb = diffuseSample.rgb;
            if (uHasDecalMask == 1 && uDecalTintStrength > 0.0)
                srgb = mix(srgb, uDecalTint, clamp(uDecalTintStrength, 0.0, 1.0));

            // SAMPLED ONCE, USED TWICE -- colour here, roughness after the albedo. Two samples
            // of one texture at one coordinate is a second chance to disagree, and the roughness
            // pass has to see exactly the channels the colour pass did.
            vec4 trimMask = vec4(0.0);
            if (uHasTrim == 1)
            {
                vec4 t = texture(uTrimMask, (uTrimUv1 == 1 ? vUV1 : vUV) * uTrimTile);
                trimMask = t;
                if (uTrimDebug == 1) { FragColor = vec4(t.rgb, 1.0); return; }
                if (uTrimDebug == 2) { FragColor = vec4(vec3(t.a), 1.0); return; }
                // Tint-only replaces the albedo with the material's base colour before layering;
                // otherwise the sheet tints whatever the diffuse supplied.
                vec3 c = uTrimNoDif == 1 ? uTrimBase : srgb;
                if (uTrimOrder == 1)
                {
                    c = mix(c, uTrimB, clamp(t.b * uTrimStrength.z, 0.0, 1.0));
                    c = mix(c, uTrimG, clamp(t.g * uTrimStrength.y, 0.0, 1.0));
                    c = mix(c, uTrimR, clamp(t.r * uTrimStrength.x, 0.0, 1.0));
                }
                else
                {
                    c = mix(c, uTrimR, clamp(t.r * uTrimStrength.x, 0.0, 1.0));
                    c = mix(c, uTrimG, clamp(t.g * uTrimStrength.y, 0.0, 1.0));
                    c = mix(c, uTrimB, clamp(t.b * uTrimStrength.z, 0.0, 1.0));
                }
                if (uTrimUseA == 1)
                    c = mix(c, uTrimA, clamp(t.a * uTrimStrength.w, 0.0, 1.0));
                srgb = clamp(c * uTrimBright, 0.0, 1.0);
            }

            if (uHasUvTint == 1)
            {
                // A MULTIPLY, mixed by strength. Both parameters default to a no-op -- the
                // colours default to FFFFFF and white times anything is itself -- which is the
                // test a reading of an ambiguous switch has to pass before it goes in. Replacing
                // the albedo instead would paint a flat colour over the wear, which is the
                // mistake `Use tint only? (No DIF)` invites.
                bool un = vUV.x < 0.0;
                bool vn = vUV.y < 0.0;
                vec3 tint = vec3(1.0);
                float str = 0.0;
                if (un && vn)  { tint = uUvTintNN; str = uUvTintStr.z; }
                else if (un)   { tint = uUvTintNP; str = uUvTintStr.y; }
                else if (vn)   { tint = uUvTintPN; str = uUvTintStr.x; }
                // (+,+) stays as authored: there is no ( 1, 1) Color parameter to apply.
                if (str > 0.0)
                    srgb = mix(srgb, clamp(srgb * tint, 0.0, 1.0), clamp(str, 0.0, 1.0));
            }

            if (uHasTint == 1)
            {
                vec3 m = texture(uTintMask, vUV).rgb;
                if (uTintDebug == 1) { FragColor = vec4(m, 1.0); return; }
                // MULTIPLY, doubled. The tintable regions of these albedos are painted mid-grey
                // so a colour can be laid over them, so 0.5 * colour * 2 returns the colour
                // exactly while scratches and wear survive. Anything that replaces the albedo
                // outright would show a flat colour and hide the detail an author is judging.
                srgb = mix(srgb, clamp(srgb * uTintR * 2.0, 0.0, 1.0), m.r);
                srgb = mix(srgb, clamp(srgb * uTintG * 2.0, 0.0, 1.0), m.g);
                srgb = mix(srgb, clamp(srgb * uTintB * 2.0, 0.0, 1.0), m.b);
            }
            // THE PROJECTED LAYER SITS ON TOP of everything the surface itself did -- its own
            // diffuse, its trim weathering and its quadrant tint -- because that is what settling
            // on a rock means. Applied last in the albedo chain for that reason.
            if (projW > 0.0)
                srgb = mix(srgb, clamp(texture(uProjColor, projUv).rgb * uProjTint, 0.0, 1.0), projW);

            // Diffuse maps are authored in sRGB. Light in linear space, then encode once
            // at the end -- skipping this brightens everything into near-white.
            vec3 albedo = pow(srgb, vec3(2.2));

            if (uMode == 1) { FragColor = vec4(srgb, 1.0); return; }
            if (uMode == 2) { FragColor = vec4(N * 0.5 + 0.5, 1.0); return; }
            if (uMode == 3)
            {
                vec2 g = floor(vUV * 32.0);
                float c = mod(g.x + g.y, 2.0) * 0.35 + 0.35;
                FragColor = vec4(vec3(c), 1.0);
                return;
            }

            // ORME packs occlusion / roughness / metallic; use roughness for a soft spec.
            float rough = 0.6;
            float ao = 1.0;
            if (uHasOrme == 1)
            {
                vec3 orme = texture(uOrme, vUV).rgb;
                // RED IS ONLY OCCLUSION WHEN THE MASK HAS ONE. Zero Company's `Base Mask 1 VT`
                // measures R=0 across the whole texture, so read as an ORME it says "fully
                // occluded" and kills the ambient term below. ao stays 1 for those, which is the
                // safe direction: the cost of being wrong here is a missing subtlety, and the
                // cost the other way is a black model.
                ao = (uAoFromMask == 1) ? orme.r : 1.0;
                rough = clamp(orme.g, 0.05, 1.0);
            }

            if (uHasTrim == 1)
            {
                // The material's own range first, then each channel pulls towards its own value.
                rough = mix(uTrimRoughRemap.x, uTrimRoughRemap.y, rough);
                rough = mix(rough, uTrimRough.x, clamp(trimMask.r * uTrimRoughStr.x, 0.0, 1.0));
                rough = mix(rough, uTrimRough.y, clamp(trimMask.g * uTrimRoughStr.y, 0.0, 1.0));
                rough = mix(rough, uTrimRough.z, clamp(trimMask.b * uTrimRoughStr.z, 0.0, 1.0));
                if (uTrimUseA == 1)
                    rough = mix(rough, uTrimRough.w, clamp(trimMask.a * uTrimRoughStr.w, 0.0, 1.0));
                rough = clamp(rough, 0.05, 1.0);
            }

            // Moss and rock do not catch light the same way, so the projection carries its own
            // roughness range and pulls towards it by the same weight that laid the colour down.
            if (projW > 0.0)
                rough = clamp(mix(rough, mix(uProjRough.x, uProjRough.y, rough), projW), 0.05, 1.0);

            // HOISTED OUT OF THE LIGHTING BRANCH so the trim pass can reach it, the same way
            // roughness is. It was declared inside `uLevelLit == 1` and read twice within it.
            float metal = 0.0;
            if (uHasOrme == 1) metal = clamp(texture(uOrme, vUV).b, 0.0, 1.0);

            if (uHasTrim == 1)
            {
                // `Metallic Strength` is read but NOT applied to the base -- it ships 0 on the
                // hull, and a literal reading would delete every painted metal's highlight on the
                // strength of one ambiguous scalar. The channels supply the metal here anyway.
                metal = mix(uTrimMetalRemap.x, uTrimMetalRemap.y, metal);
                metal = mix(metal, uTrimMetal.x, clamp(trimMask.r * uTrimMetalStr, 0.0, 1.0));
                metal = mix(metal, uTrimMetal.y, clamp(trimMask.g * uTrimMetalStr, 0.0, 1.0));
                metal = mix(metal, uTrimMetal.z, clamp(trimMask.b * uTrimMetalStr, 0.0, 1.0));
                if (uTrimUseA == 1)
                    metal = mix(metal, uTrimMetal.w, clamp(trimMask.a * uTrimMetalStr, 0.0, 1.0));
                metal = clamp(metal, 0.0, 1.0);
            }

            // ---------------------------------------------- THE SURFACE CHANNEL VIEWS
            //
            // AFTER THE TRIM PASS, DELIBERATELY. These show the value the LIGHTING actually uses,
            // not the raw ORME texel. On a trim-sheet material the sheet is what decides the
            // roughness and the metal -- 71% of one hub level wears one -- so a view of the bare
            // texture would disagree with the surface on screen and send somebody looking for a
            // fault in the wrong map.
            //
            // GREYSCALE, for the same reason the texture viewer draws a lone channel grey: a
            // roughness map judged through a colour is guesswork.
            //
            // Modes 0-3 keep their early returns further up, where they are cheaper; these need
            // ao, rough and metal to exist first. Numbering is append-only because this shader is
            // shared with the level editor and the toolkit's own preview.
            if (uMode == 4) { FragColor = vec4(vec3(ao), 1.0); return; }
            if (uMode == 5) { FragColor = vec4(vec3(rough), 1.0); return; }
            // Smoothness is roughness inverted. Both are offered because the DATA is roughness
            // (ORME green) while many authoring tools show smoothness, and
            // relabelling one as the other would misreport what is in the file.
            if (uMode == 6) { FragColor = vec4(vec3(1.0 - rough), 1.0); return; }
            if (uMode == 7) { FragColor = vec4(vec3(metal), 1.0); return; }
            // REFLECTANCE IS NOT A MAP IN THIS GAME. It is this shader's own specular colour,
            // mix(vec3(0.25), albedo * 2, metal) -- the line that decides whether a highlight
            // keeps the surface's colour or comes out grey plastic. Shown in COLOUR because that
            // is the entire point of it, and encoded back to sRGB because albedo is linear here.
            if (uMode == 8)
            {
                vec3 f0 = clamp(mix(vec3(0.25), albedo * 2.0, metal), 0.0, 1.0);
                FragColor = vec4(pow(f0, vec3(1.0 / 2.2)), 1.0);
                return;
            }
            // FLAT, for wireframe. Lines want one legible colour; a lit wireframe is unreadable
            // where the surface faces away from the key light.
            if (uMode == 9) { FragColor = vec4(vec3(0.85), 1.0); return; }

            vec3 V = normalize(uCamPos - vWorld);
            vec3 lit;

            if (uLevelLit == 1)
            {
                // A HEMISPHERE FOR THE SKY, not a constant. An ambient term that is the same in
                // every direction flattens everything it touches, which is the look that says
                // "viewport" more than any other single thing. Two colours and N.y is nearly free
                // and gives ground bounce -- the cheapest stand-in for the GI we do not have.
                float up = N.y * 0.5 + 0.5;
                vec3 light = mix(uSkyDown, uSkyUp, up) * ao;

                float viewDist = length(vWorld - uCamPos);

                if (uShadowDebug > 0)
                {
                    FragColor = vec4(ShadowDebug(vWorld, N, viewDist,
                                                 uSunCount > 0 ? max(dot(N, -uSunDir[0]), 0.0) : 1.0), 1.0);
                    return;
                }

                for (int i = 0; i < uSunCount && i < 4; ++i)
                {
                    vec3 L = -uSunDir[i];                  // towards the light
                    float nl = max(dot(N, L), 0.0);

                    // ONLY THE KEY LIGHT IS SHADOWED. Umbara's second sun is a dim blue fill at
                    // half the key's intensity and a second set of cascades would double the cost
                    // of the frame to darken something already close to ambient. Which one is
                    // index 0 is not luck: the viewport sorts them by intensity before sending.
                    float shade = i == 0 ? SunShadow(vWorld, N, viewDist, nl) : 1.0;
                    light += uSunRadiance[i] * nl * shade;

                    // Blinn-Phong standing in for a GGX lobe. Roughness drives the exponent and
                    // metals keep their colour in the highlight, which is most of what separates
                    // painted metal from plastic at a glance.
                    vec3 H = normalize(L + V);
                    float e = mix(256.0, 4.0, rough);
                    float sp = pow(max(dot(N, H), 0.0), e) * (1.0 - rough);
                    light += uSunRadiance[i] * sp * shade * mix(vec3(0.25), albedo * 2.0, metal);
                }

                for (int i = 0; i < uLocalCount && i < 32; ++i)
                {
                    vec3 D = uLocalPos[i] - vWorld;
                    float dist = length(D);
                    float reach = uLocalShape[i].x;
                    if (dist > reach) continue;

                    vec3 L = D / max(dist, 0.0001);
                    float nl = max(dot(N, L), 0.0);
                    if (nl <= 0.0) continue;

                    float fall = uLocalShape[i].w;
                    float atten;
                    if (fall <= 0.0)
                    {
                        // INVERSE SQUARE, IN METRES. World units are centimetres, and a candela is
                        // defined against metres -- squaring the wrong unit is a factor of ten
                        // thousand, which is the difference between a lamp and a sun.
                        float dm = max(dist * 0.01, 0.05);
                        // UE's window, so the light reaches exactly zero at its own radius instead
                        // of being cut off mid-falloff with a visible edge.
                        float w = clamp(1.0 - pow(dist / reach, 4.0), 0.0, 1.0);
                        atten = (w * w) / (dm * dm);
                    }
                    else atten = pow(clamp(1.0 - dist / reach, 0.0, 1.0), fall);

                    // THE CONE. A point light is sent with cos(outer) = -1, which makes this 1
                    // everywhere -- the same loop, no branch, and no wedge-shaped point lights.
                    float cd = dot(-L, uLocalDir[i]);
                    float cone = clamp((cd - uLocalShape[i].y) * uLocalShape[i].z, 0.0, 1.0);
                    cone *= cone;

                    vec3 rad = uLocalColour[i] * (atten * cone);
                    light += rad * nl;

                    vec3 Hl = normalize(L + V);
                    float el = mix(256.0, 4.0, rough);
                    light += rad * pow(max(dot(N, Hl), 0.0), el) * (1.0 - rough) *
                             mix(vec3(0.25), albedo * 2.0, metal);
                }

                lit = albedo * light;
                if (uHasEmissive == 1)
                    lit += pow(texture(uEmissive, vUV).rgb, vec3(2.2)) * uEmissiveTint *
                           uEmissiveStrength * uEmissiveScale;
                else if (uEmissiveFlat == 1)
                    lit = uEmissiveTint * uEmissiveStrength * uEmissiveScale;

                // THE TRIM FAMILY'S OWN EMISSIVE, which is a different mechanism from the single
                // `PM_Emissive` map above: a SHEET of patterns, a MASK saying where they show,
                // a colour and a strength. Added rather than replacing, because a section can
                // carry both.
                if (uHasEmSheet == 1)
                {
                    vec2 euv = vUV * uEmSheetTile;
                    vec3 sheet = pow(texture(uEmSheet, euv).rgb, vec3(2.2));
                    float mask = texture(uEmMask, euv).r;
                    lit += sheet * mask * uEmSheetColor * uEmSheetStrength;
                }

                // FUZZ, last. A velvet-style term that lifts the grazing angles, which is how
                // lichen on rock catches light -- the surface brightens where you see it edge-on
                // rather than where a highlight would sit. Scaled by the albedo so it tints the
                // rock rather than painting over it, and by N.L so it cannot light an unlit face.
                if (uHasFuzz == 1)
                {
                    float vn = clamp(dot(N, V), 0.0, 1.0);
                    float rim = pow(1.0 - vn, 3.0);
                    float keyed = uSunCount > 0 ? max(dot(N, -uSunDir[0]), 0.0) : 1.0;
                    lit += albedo * uFuzzColor * rim * keyed;
                }

                // FOG BEFORE THE TONEMAPPER, because that is where it happens: fog is light
                // scattered into the ray, so it belongs in linear radiance and then goes through
                // the same curve as everything else. Mixed in after the curve it looks like a
                // grey wash laid over a photograph, which is exactly what it is at that point.
                if (uHasFog == 1)
                {
                    float dist = viewDist;
                    float dens = uFogDensity * exp(-uFogFalloff * max(vWorld.y - uFogHeight, 0.0) * 0.01);
                    float f = 1.0 - exp(-dist * dens * uFogScale);
                    lit = mix(lit, uFogColour, clamp(f, 0.0, 1.0));
                }

                vec3 outc0 = pow(Aces(lit * uExposure), vec3(1.0 / 2.2));
                FragColor = vec4(uSelected == 1
                    ? mix(outc0, vec3(0.35, 0.70, 1.0), 0.30) +
                      vec3(0.25, 0.50, 0.85) * pow(1.0 - max(dot(N, V), 0.0), 2.0)
                    : outc0, 1.0);
                return;
            }

            // Three-point rig so shapes read without a real environment.
            vec3 key  = normalize(vec3( 0.5,  0.8,  0.6));
            vec3 fill = normalize(vec3(-0.7,  0.2,  0.4));
            vec3 rim  = normalize(vec3( 0.0, -0.4, -1.0));

            float kd = max(dot(N, key), 0.0);
            float fd = max(dot(N, fill), 0.0) * 0.35;
            float rd = pow(max(dot(N, rim), 0.0), 2.0) * 0.25;

            // THE KEY CASTS, AND ONLY THE KEY.
            //
            // The author asked for "any model loaded into it to cast a shadow". The level branch
            // has shadowed its suns all along and this rig never did, because a cascade needs
            // something to fall ONTO and for a long time there was one object here. The floor
            // slab is that something.
            //
            // Fill and rim are deliberately unshadowed: they exist to keep a silhouette readable
            // where the key does not reach, and shadowing them would put the viewport back to one
            // light with black sides, which is the look the three-point rig exists to avoid.
            //
            // The specular is attenuated with it. A highlight burning through a shadow is the
            // giveaway that the two were computed by different rules.
            float keyShade = SunShadow(vWorld, N, length(vWorld - uCamPos), kd);
            kd *= keyShade;

            vec3 H = normalize(key + V);
            float spec = pow(max(dot(N, H), 0.0), mix(96.0, 8.0, rough)) * (1.0 - rough) * 0.12
                       * keyShade;

            // THE STUDIO RIG NEVER READ `metal`, so painted metal lit as plastic here while the
            // level branch two hundred lines up has used mix(vec3(0.25), albedo*2, metal) for its
            // specular colour all along. That is most of what separates metal from plastic at a
            // glance, and this rig is what the model viewport uses.
            //
            // mix FROM WHITE, not from 0.25, deliberately: at metal = 0 this is vec3(1)*spec,
            // byte-for-byte what it was before, so nothing that looks right today changes. The
            // level branch's 0.25 is its own dielectric choice and adopting it here would dim
            // every non-metal highlight in the toolkit's preview as a side effect of a fix that
            // is about metal.
            lit = albedo * (0.16 * ao + kd * 0.80 + fd + rd)
                + mix(vec3(1.0), clamp(albedo * 2.0, 0.0, 1.0), metal) * spec;
            vec3 outc = pow(lit, vec3(1.0 / 2.2));

            // Selection tint plus a rim so the highlighted part reads even when occluded.
            if (uSelected == 1)
            {
                float rim = pow(1.0 - max(dot(N, V), 0.0), 2.0);
                outc = mix(outc, vec3(0.35, 0.70, 1.0), 0.30) + vec3(0.25, 0.50, 0.85) * rim;
            }
            FragColor = vec4(outc, 1.0);
        }
        """;

    /// <summary>
    /// Picking pass: writes a flat colour encoding the mesh index, read back with
    /// glReadPixels under the cursor. Exact, unlike ray-vs-bounds against overlapping parts.
    /// </summary>
    public const string PickFragment = """
        #version 330 core
        uniform vec3 uPickColor;
        out vec4 FragColor;
        void main() { FragColor = vec4(uPickColor, 1.0); }
        """;

    public const string GridVertex = """
        #version 330 core
        layout (location = 0) in vec3 aPos;
        uniform mat4 uView;
        uniform mat4 uProj;
        out float vFade;
        // THE WORLD POSITION, for the floor to look its own shadow up. These vertices ARE world
        // space -- the grid and the slab carry no model matrix -- so this is a pass-through.
        // GridFragment does not declare it and does not have to; an unread vertex output is legal.
        out vec3 vWorld;
        void main()
        {
            vFade = 1.0 - clamp(length(aPos.xz) / 600.0, 0.0, 1.0);
            vWorld = aPos;
            gl_Position = uProj * uView * vec4(aPos, 1.0);
        }
        """;

    public const string GridFragment = """
        #version 330 core
        in float vFade;
        uniform vec3 uColor;
        out vec4 FragColor;
        void main() { FragColor = vec4(uColor, vFade * 0.5); }
        """;

    // OPAQUE, on GridVertex's attributes and uniforms.
    //
    // The grid's own fragment is vec4(uColor, vFade * 0.5) -- half-transparent and fading with
    // distance, which is right for lines drawn over a scene and wrong for anything meant to read
    // as a surface. A floor slab drawn through it came out see-through and dimmer at its far
    // edge. Same vertex shader, so the buffer layout and the uniforms are shared; only the
    // alpha differs.
    public const string SolidFragment = """
        #version 330 core
        in float vFade;
        uniform vec3 uColor;
        out vec4 FragColor;
        void main() { FragColor = vec4(uColor, 1.0); }
        """;

    /// <summary>
    /// THE FLOOR SLAB, AND WHAT FALLS ON IT.
    ///
    /// A separate fragment from <see cref="SolidFragment"/> rather than a branch in it, because
    /// the grid shares that shader and a grid line has no business sampling a shadow map.
    ///
    /// THE LOOKUP IS SIMPLER THAN THE MESH SHADER'S ON PURPOSE, and the difference is justified
    /// rather than convenient: this surface is one plane with a constant +Y normal, so the mesh
    /// shader's slope term -- which scales the normal offset by how edge-on the surface is to the
    /// light -- is a constant here, and the cascade choice is a constant too because the viewport
    /// fits one cascade to the model. What is KEPT is the 3x3 tap, because that is what stops a
    /// 2048 map looking like a staircase, and the normal offset, because without it the slab
    /// shadows itself.
    /// </summary>
    public const string ShadowFloorFragment = """
        #version 330 core
        in float vFade;
        in vec3 vWorld;
        uniform vec3 uColor;
        uniform int  uShadowOn;
        uniform sampler2DArrayShadow uShadowMap;
        uniform mat4 uShadowMat[3];
        uniform vec3 uShadowTexel;
        out vec4 FragColor;
        void main()
        {
            float lit = 1.0;
            if (uShadowOn == 1)
            {
                vec3 offset = vWorld + vec3(0.0, uShadowTexel[0] * 1.5, 0.0);
                vec4 lp = uShadowMat[0] * vec4(offset, 1.0);
                vec3 uv = lp.xyz / lp.w * 0.5 + 0.5;
                if (uv.x >= 0.0 && uv.x <= 1.0 && uv.y >= 0.0 && uv.y <= 1.0 && uv.z <= 1.0)
                {
                    float sum = 0.0;
                    vec2 texStep = 1.0 / vec2(textureSize(uShadowMap, 0).xy);
                    for (int y = -1; y <= 1; ++y)
                        for (int x = -1; x <= 1; ++x)
                            sum += texture(uShadowMap,
                                           vec4(uv.xy + vec2(x, y) * texStep, 0.0, uv.z));
                    lit = sum / 9.0;
                }
            }
            // NOT TO BLACK. A shadow that reaches zero reads as a hole cut in the floor; the
            // slab is a backdrop and what is wanted is that the model sits ON something.
            FragColor = vec4(uColor * mix(0.45, 1.0, lit), 1.0);
        }
        """;

    // A texture on screen, for looking AT a map rather than through a model.
    //
    // uXform is (scaleX, scaleY, panX, panY) mapping the quad's 0..1 UV to image space, so
    // aspect fit, zoom and pan are one multiply-add and the quad never changes.
    public const string TextureVertex = """
        #version 330 core
        layout (location = 0) in vec2 aPos;
        uniform vec4 uXform;
        out vec2 vUV;
        void main()
        {
            vUV = (aPos - uXform.zw) / uXform.xy;
            gl_Position = vec4(aPos * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    public const string TextureFragment = """
        #version 330 core
        in vec2 vUV;
        uniform sampler2D uTex;
        // Which channels reach the screen. (1,1,1,0) is colour; (0,0,0,1) is alpha as grey.
        uniform vec4 uMask;
        uniform int uAlphaOnly;
        uniform float uChecker;
        out vec4 FragColor;

        void main()
        {
            // Outside the image, draw nothing but the background. Sampling would smear the
            // edge texel across the whole viewport and make a zoomed-in map look seamless
            // when it is not.
            vec2 grid = floor(gl_FragCoord.xy / uChecker);
            float odd = mod(grid.x + grid.y, 2.0);
            vec3 bg = mix(vec3(0.13, 0.135, 0.155), vec3(0.17, 0.175, 0.20), odd);

            if (vUV.x < 0.0 || vUV.x > 1.0 || vUV.y < 0.0 || vUV.y > 1.0)
            {
                FragColor = vec4(vec3(0.115, 0.12, 0.14), 1.0);
                return;
            }

            // V flipped: GL samples bottom-up and an image reads top-down.
            vec4 t = texture(uTex, vec2(vUV.x, 1.0 - vUV.y));

            if (uAlphaOnly == 1) { FragColor = vec4(vec3(t.a), 1.0); return; }

            vec3 rgb = t.rgb * uMask.rgb;
            // A single channel shown alone reads as grey, not as a red/green/blue tint --
            // judging a roughness map through a red filter is guesswork.
            float lit = uMask.r + uMask.g + uMask.b;
            if (lit == 1.0) rgb = vec3(rgb.r + rgb.g + rgb.b);

            // Composite over the checkerboard only when alpha is being honoured, so a mask
            // texture's transparency is visible rather than silently premultiplied away.
            float a = uMask.a > 0.5 ? t.a : 1.0;
            FragColor = vec4(mix(bg, rgb, a), 1.0);
        }
        """;
}
