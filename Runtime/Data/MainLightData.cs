using UnityEngine;
using UnityEngine.Rendering;

namespace TDRP
{
	// CPU-computed per-frame data for the main directional light, shared by the passes
	// that consume it: SetupLightingPass (publishes shader globals) and
	// DirectionalShadowPass (renders the shadow map). Computed once before recording so
	// neither pass has to reach into the other's fields — the passes only depend on this
	// plain data, not on each other.
	public struct MainLightData
	{
		public int Index;          // visibleLights index of the main directional light, or -1
		public Vector4 Direction;  // xyz: surface->light direction (world), w unused
		public Vector4 Color;      // rgb: color * intensity (linear), a unused

		public bool CastsShadows;
		public bool SoftShadows;
		public bool UsePcss;       // soft shadows via PCSS instead of fixed 3x3 PCF
		public bool UsePyramid;    // accelerate the PCSS blocker search with the min/max pyramid
		public bool UseScreenSpaceShadows; // resolve the shadow into a full-screen mask (enables temporal)
		public bool UseTemporalShadows;    // accumulate the mask across frames (needs screen-space)
		public Matrix4x4 ShadowView;
		public Matrix4x4 ShadowProj;
		public ShadowSplitData ShadowSplitData; // culling planes, fed to CullShadowCasters
		public Matrix4x4 WorldToShadow; // world -> shadow-map UV (xy) + compare depth (z)
		public Vector4 ShadowParams;    // x: strength, y: depth bias (world), z: normal bias (world), w: 1/res
		// PCSS sample params. x: blocker-search radius (shadow UV), y: penumbra scale
		// (shadow UV per unit [0,1] depth difference), z: min filter radius (shadow UV), w: unused.
		public Vector4 ShadowPcssParams;
		public Vector4 ShadowPyramidParams; // x: coarse (min/max) resolution
		public int ShadowPyramidCoarseRes;  // ceil(res / ShadowPyramidBlock)

		public static MainLightData Create(in FrameContext frame)
		{
			var data = new MainLightData { WorldToShadow = Matrix4x4.identity };
			data.Index = FindMainLight(frame.CullingResults, out data.Direction, out data.Color);
			SetupShadows(in frame, ref data);
			return data;
		}

		// Returns the visibleLights index of the chosen main directional light, or -1.
		private static int FindMainLight(CullingResults cull, out Vector4 direction, out Vector4 color)
		{
			var lights = cull.visibleLights;
			for (int i = 0; i < lights.Length; i++)
			{
				var light = lights[i];
				if (light.lightType != LightType.Directional)
					continue;

				// Column 2 of the light-to-world matrix is its forward (the way it shines).
				// The shading vector L points from the surface toward the light, so negate it.
				Vector4 forward = light.localToWorldMatrix.GetColumn(2);
				direction = -forward;
				direction.w = 0f;

				// finalColor already folds in intensity (and is linear in a linear project).
				color = light.finalColor;
				return i;
			}

			direction = new Vector4(0f, 1f, 0f, 0f);
			color = Vector4.zero;
			return -1;
		}

		// Bias tuning factors (multiply the Light's Bias / Normal Bias sliders, then texel
		// world size). The Light's default sliders (0.05 / 0.4) are tuned for URP's bias
		// math, so we scale them up to land at a few texels here: 0.05*10=0.5 texel depth,
		// 0.4*7.5=3 texel normal — enough to clear acne without much peter-panning.
		private const float DEPTH_BIAS_SCALE = 10f;
		private const float NORMAL_BIAS_SCALE = 7.5f;

		// Fits the directional shadow's ortho frustum to the patch of GROUND PLANE the camera
		// can see (where the view frustum crosses Y = ShadowPlaneHeight), expanded into a
		// vertical band for casters above/below it — instead of the whole frustum volume.
		// For a top-down game the play area IS the ground, so this spends no resolution on
		// the empty air the frustum spans, and the footprint still shrinks as you zoom in.
		private static void SetupShadows(in FrameContext frame, ref MainLightData data)
		{
			if (!frame.Settings.MainLightShadows || data.Index < 0)
				return;

			var light = frame.CullingResults.visibleLights[data.Index].light;
			if (light == null || light.shadows == LightShadows.None || light.shadowStrength <= 0f)
				return;

			// Skip if this light has no shadow casters in range. Without this, rendering an
			// empty shadow map makes Unity spam "No valid shadow casters ..." every Submit
			// (e.g. for the Scene-view camera or an empty play area).
			if (!frame.CullingResults.GetShadowCasterBounds(data.Index, out _))
				return;

			// Must match the texture the pipeline allocates (it rounds to a power of two).
			int res = Mathf.NextPowerOfTwo(Mathf.Max(16, frame.Settings.ShadowResolution));

			if (!FitDirectionalShadowToPlane(frame.Camera, light.transform, in frame.Settings, res,
				out var view, out var proj, out var split, out float texelWorld, out float depthRangeWorld))
				return;

			data.CastsShadows = true;
			data.SoftShadows = light.shadows == LightShadows.Soft;
			// PCSS is the soft path when enabled; otherwise soft falls back to fixed 3x3 PCF.
			data.UsePcss = data.SoftShadows && frame.Settings.UsePcss;
			// Screen-space resolve works regardless of the inline filter chosen above (it just
			// runs that same filter in a full-screen pass). Required for temporal accumulation.
			data.UseScreenSpaceShadows = data.CastsShadows && frame.Settings.ScreenSpaceShadows;
			data.UseTemporalShadows = data.UseScreenSpaceShadows && frame.Settings.TemporalShadows;
			data.ShadowView = view;
			data.ShadowProj = proj;
			data.ShadowSplitData = split;
			data.WorldToShadow = GetWorldToShadowMatrix(proj, view);

			// Biases come from the Light's own Bias / Normal Bias sliders, scaled by the
			// shadow texel's WORLD size so they stay perceptually constant across zoom /
			// resolution. Applied as world-space offsets in the shader (normal + toward-light).
			float normalBias = light.shadowNormalBias * texelWorld * NORMAL_BIAS_SCALE;
			float depthBias = light.shadowBias * texelWorld * DEPTH_BIAS_SCALE;
			data.ShadowParams = new Vector4(light.shadowStrength, depthBias, normalBias, 1f / res);

			if (data.UsePcss)
				data.ShadowPcssParams = ComputePcssParams(in frame.Settings, res, texelWorld, depthRangeWorld);

			// The min/max pyramid only helps PCSS (it accelerates its blocker search).
			data.UsePyramid = data.UsePcss && frame.Settings.UseShadowBlockerPyramid;
			if (data.UsePyramid)
			{
				int coarse = (res + ShadowPyramidBlock - 1) / ShadowPyramidBlock;
				data.ShadowPyramidCoarseRes = coarse;
				data.ShadowPyramidParams = new Vector4(coarse, 0f, 0f, 0f);
			}
		}

		// Largest blocker search radius we allow, in texels — caps the per-pixel sample
		// footprint (and so the cost) regardless of how wide the penumbra math wants to go.
		public const int PcssMaxSearchTexels = 16;

		// Block size (shadow texels per coarse texel) of the min/max blocker pyramid. Sized to
		// 2x the max search radius so any blocker-search window fits within a 2x2 coarse-texel
		// neighbourhood — which is what lets a SINGLE coarse level replace a full mip chain.
		// If PcssMaxSearchTexels ever grows, this tracks it automatically.
		public const int ShadowPyramidBlock = 2 * PcssMaxSearchTexels;

		// Turns the directional penumbra model into the two shadow-UV scalars the shader needs.
		//
		// For a directional light the penumbra half-width in WORLD units is
		//     penumbraWorld = (d_receiver - d_blocker) * tan(sunAngularRadius)
		// Because our shadow projection is ORTHOGRAPHIC, stored depth is linear in world
		// distance along the light, so a [0,1] depth difference maps to world distance by
		// multiplying by the ortho near-far span (depthRangeWorld). And world distance maps to
		// shadow-UV by dividing by the ortho's lateral world size (texelWorld * res). Folding
		// those together gives penumbraScale = UV per unit [0,1] depth difference.
		private static Vector4 ComputePcssParams(in PipelineSettings settings, int res,
			float texelWorld, float depthRangeWorld)
		{
			float tanHalfAngle = Mathf.Tan(0.5f * Mathf.Deg2Rad * settings.SunAngularDiameter);
			float orthoSize = texelWorld * res; // lateral world size the shadow map covers
			float penumbraScale = orthoSize > 1e-5f
				? depthRangeWorld * tanHalfAngle / orthoSize
				: 0f;

			// Search radius = the widest penumbra the math can produce (depth diff at most 1),
			// clamped to the texel cap so cost stays bounded.
			float searchRadiusUV = Mathf.Min(penumbraScale, PcssMaxSearchTexels / (float)res);
			// Floor so contact points (tiny depth diff) still filter over ~1 texel rather than
			// collapsing to a single aliased tap.
			float minFilterUV = 1f / res;

			return new Vector4(searchRadiusUV, penumbraScale, minFilterUV, 0f);
		}

		// Builds a light's-eye view + ortho projection covering the visible ground footprint
		// (frustum ∩ plane, clamped to ShadowDistance) plus a vertical caster band. Returns
		// the world size of one shadow texel (for bias scaling).
		private static bool FitDirectionalShadowToPlane(Camera camera, Transform lightTransform,
			in PipelineSettings settings, int res, out Matrix4x4 view, out Matrix4x4 proj,
			out ShadowSplitData split, out float texelWorld, out float depthRangeWorld)
		{
			view = Matrix4x4.identity;
			proj = Matrix4x4.identity;
			split = default;
			texelWorld = 0f;
			depthRangeWorld = 0f;

			// Light's-eye view matrix (Unity convention: a camera at the light's orientation
			// looking along its forward; view space is right-handed, so -Z points forward).
			view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) *
				Matrix4x4.TRS(Vector3.zero, lightTransform.rotation, Vector3.one).inverse;

			float far = Mathf.Min(settings.ShadowDistance, camera.farClipPlane);
			if (far <= camera.nearClipPlane)
				return false;

			// The four corner rays of the frustum (camera -> far-plane corners).
			var cornersLocal = new Vector3[4];
			camera.CalculateFrustumCorners(new Rect(0f, 0f, 1f, 1f), far,
				Camera.MonoOrStereoscopicEye.Mono, cornersLocal);
			var camToWorld = camera.transform.localToWorldMatrix;
			Vector3 camPos = camera.transform.position;
			float planeY = settings.ShadowPlaneHeight;

			// LATERAL (xy) extent: from the ground footprint points ONLY. The caster band is
			// kept out of this so the fit isn't bloated (raised points lean in light space and
			// would enlarge every texel). DEPTH (z) extent: from the band, so casters above/
			// below the plane stay within near/far.
			float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
			float zMin = float.MaxValue, zMax = float.MinValue;

			for (int i = 0; i < 4; i++)
			{
				Vector3 corner = camToWorld.MultiplyPoint(cornersLocal[i]);
				Vector3 seg = corner - camPos;
				// Where the camera->corner segment crosses the ground plane (param in [0,1],
				// so it never reaches past the far plane / ShadowDistance).
				float s = Mathf.Abs(seg.y) > 1e-5f ? (planeY - camPos.y) / seg.y : 1f;
				Vector3 ground = camPos + seg * Mathf.Clamp01(s);

				Vector3 lsGround = view.MultiplyPoint(ground);
				xMin = Mathf.Min(xMin, lsGround.x); xMax = Mathf.Max(xMax, lsGround.x);
				yMin = Mathf.Min(yMin, lsGround.y); yMax = Mathf.Max(yMax, lsGround.y);

				for (int h = 0; h < 2; h++)
				{
					float y = planeY + (h == 0 ? settings.ShadowHeightAbove : -settings.ShadowHeightBelow);
					float z = view.MultiplyPoint(new Vector3(ground.x, y, ground.z)).z;
					zMin = Mathf.Min(zMin, z); zMax = Mathf.Max(zMax, z);
				}
			}

			float sizeX = xMax - xMin;
			float sizeY = yMax - yMin;
			if (sizeX <= 0f || sizeY <= 0f)
				return false;
			// Square fit: one size for both axes -> uniform texels, rotation-stable.
			// Rectangular fit: tight to the footprint -> sharper on the shallow axis, but anisotropic texels
			// and shimmer under rotation.
			if (!settings.RectangularShadowFit)
				sizeX = sizeY = Mathf.Max(sizeX, sizeY);

			float texelX = sizeX / res;
			float texelY = sizeY / res;
			// Bias scales by the coarsest axis (worst case for acne).
			texelWorld = Mathf.Max(texelX, texelY);

			float left = (xMin + xMax) * 0.5f - sizeX * 0.5f;
			float bottom = (yMin + yMax) * 0.5f - sizeY * 0.5f;
			// Snap each origin to its own whole-texel increments so the grid stays world-aligned
			// frame to frame — otherwise sub-texel sliding makes shadow edges crawl.
			left = Mathf.Floor(left / texelX) * texelX;
			bottom = Mathf.Floor(bottom / texelY) * texelY;

			// View-space -Z is forward, so distance-along-forward = -z (near = closest band edge).
			proj = Matrix4x4.Ortho(left, left + sizeX, bottom, bottom + sizeY, -zMax, -zMin);
			// World span the ortho depth range covers (near=-zMax .. far=-zMin), so the shader
			// can turn a [0,1] depth difference back into a world distance for the PCSS penumbra.
			depthRangeWorld = zMax - zMin;

			// Culling planes for this ortho box, handed to ScriptableRenderContext.CullShadowCasters
			// so it knows which casters belong to the shadow map (and the split is "valid").
			var planes = GeometryUtility.CalculateFrustumPlanes(proj * view);
			split.cullingPlaneCount = 6;
			for (int i = 0; i < 6; i++)
				split.SetCullingPlane(i, planes[i]);

			return true;
		}

		// Builds world -> shadow-map-UV+depth. Mirrors URP's ShadowUtils.GetShadowTransform:
		// negate the projection's z row on reverse-Z platforms (so the sampled depth matches
		// what SetViewProjectionMatrices writes into the map), then remap clip [-1,1] to the
		// [0,1] texture/depth range with a 0.5 scale + 0.5 bias.
		private static Matrix4x4 GetWorldToShadowMatrix(Matrix4x4 proj, Matrix4x4 view)
		{
			if (SystemInfo.usesReversedZBuffer)
			{
				proj.m20 = -proj.m20;
				proj.m21 = -proj.m21;
				proj.m22 = -proj.m22;
				proj.m23 = -proj.m23;
			}

			var scaleBias = Matrix4x4.identity;
			scaleBias.m00 = 0.5f;
			scaleBias.m11 = 0.5f;
			scaleBias.m22 = 0.5f;
			scaleBias.m03 = 0.5f;
			scaleBias.m13 = 0.5f;
			scaleBias.m23 = 0.5f;

			return scaleBias * (proj * view);
		}
	}
}
