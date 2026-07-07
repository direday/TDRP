using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	public class SetupLightingPass : IDisposable
	{
		public GraphicsBuffer PunctualLightBuffer => _lightBuffer;
		public int PunctualLightCount { get; private set; }

		private const int MAX_PUNCTUAL_LIGHTS = 64;
		private const string MAIN_LIGHT_SHADOWS_KEYWORD = "_MAIN_LIGHT_SHADOWS";
		private const string SOFT_SHADOWS_KEYWORD = "_SHADOWS_SOFT";
		private const string PCSS_KEYWORD = "_SHADOWS_PCSS";
		private const string PCSS_PYRAMID_KEYWORD = "_SHADOWS_PCSS_PYRAMID";
		private const string SCREEN_SHADOWS_KEYWORD = "_SCREEN_SPACE_SHADOWS";
		private const string SSAO_KEYWORD = "_SCREEN_SPACE_AO";
		private const string RC_KEYWORD = "_SCREEN_SPACE_RC";
		private const string COOKIE_KEYWORD = "_MAIN_LIGHT_COOKIE";

		private static readonly int _mainLightDirectionId = Shader.PropertyToID("_MainLightDirection");
		private static readonly int _mainLightColorId = Shader.PropertyToID("_MainLightColor");
		private static readonly int _ambientSkyColorId = Shader.PropertyToID("_AmbientSkyColor");
		private static readonly int _ambientGroundColorId = Shader.PropertyToID("_AmbientGroundColor");
		private static readonly int _punctualLightsId = Shader.PropertyToID("_PunctualLights");
		private static readonly int _punctualLightCountId = Shader.PropertyToID("_PunctualLightCount");
		private static readonly int _mainLightShadowMatrixId = Shader.PropertyToID("_MainLightShadowMatrix");
		private static readonly int _mainLightShadowParamsId = Shader.PropertyToID("_MainLightShadowParams");
		private static readonly int _mainLightShadowPcssParamsId = Shader.PropertyToID("_MainLightShadowPcssParams");
		private static readonly int _mainLightShadowMinMaxParamsId = Shader.PropertyToID("_MainLightShadowMinMaxParams");
		private static readonly int _mainLightCookieId = Shader.PropertyToID("_MainLightCookie");
		private static readonly int _mainLightCookieScaleOffsetId = Shader.PropertyToID("_MainLightCookieScaleOffset");
		private static readonly int _mainLightCookieParamsId = Shader.PropertyToID("_MainLightCookieParams");

		[StructLayout(LayoutKind.Sequential)]
		private struct PunctualLightData
		{
			public Vector4 PositionRange; // xyz: world position, w: 1 / range^2
			public Vector4 Color;         // rgb: color * intensity (linear), w: spot cone offset
			public Vector4 SpotDirScale;  // xyz: spot direction (surface->light convention), w: spot cone scale
		}

		private readonly PunctualLightData[] _lightData = new PunctualLightData[MAX_PUNCTUAL_LIGHTS];
		private GraphicsBuffer _lightBuffer;

		private class PassData
		{
			public Vector4 MainLightDirection;
			public Vector4 MainLightColor;
			public Vector4 AmbientSky;
			public Vector4 AmbientGround;
			public GraphicsBuffer PunctualLights;
			public int PunctualLightCount;
			public bool MainLightShadows;
			public bool SoftShadows;
			public bool Pcss;
			public bool PcssPyramid;
			public bool ScreenShadows;
			public bool Ssao;
			public bool Rc;
			public Matrix4x4 ShadowMatrix;
			public Vector4 ShadowParams;
			public Vector4 ShadowPcssParams;
			public Vector4 ShadowPyramidParams;
			public bool Cookie;
			public Texture CookieTexture;
			public Vector4 CookieScaleOffset;
			public Vector4 CookieParams;
		}

		public void Record(RenderGraph rg, in FrameContext frame, in MainLightData mainLight, bool ssaoActive,
			bool rcActive, Texture cookieTexture)
		{
			using var builder = rg.AddUnsafePass<PassData>("Setup Lighting", out var passData);

			passData.MainLightDirection = mainLight.Direction;
			passData.MainLightColor = mainLight.Color;
			// Hemisphere ambient from settings: sRGB swatches -> linear, intensity folded in.
			var settings = frame.Settings;
			passData.AmbientSky = (Vector4)(settings.AmbientSkyColor.linear * settings.AmbientIntensity);
			passData.AmbientGround = (Vector4)(settings.AmbientGroundColor.linear * settings.AmbientIntensity);

			passData.MainLightShadows = mainLight.CastsShadows;
			// The soft-filter paths are mutually exclusive (one keyword set):
			//   _SHADOWS_SOFT          = soft requested, PCSS off
			//   _SHADOWS_PCSS          = PCSS, no blocker pyramid
			//   _SHADOWS_PCSS_PYRAMID  = PCSS accelerated by the min/max pyramid
			passData.SoftShadows = mainLight.SoftShadows && !mainLight.UsePcss;
			passData.Pcss = mainLight.UsePcss && !mainLight.UsePyramid;
			passData.PcssPyramid = mainLight.UsePyramid;
			// Orthogonal to the soft-filter keywords above: when set, the lit shader reads the
			// resolved mask. The soft-filter keywords still apply to the mask-gen pass + to
			// transparents (which stay on the inline path), so they're set regardless.
			passData.ScreenShadows = mainLight.UseScreenSpaceShadows;
			passData.Ssao = ssaoActive;
			passData.Rc = rcActive;
			passData.ShadowMatrix = mainLight.WorldToShadow;
			passData.ShadowParams = mainLight.ShadowParams;
			passData.ShadowPcssParams = mainLight.ShadowPcssParams;
			passData.ShadowPyramidParams = mainLight.ShadowPyramidParams;

			// Directional cookie: only active when the toggle is on AND a texture is assigned (else
			// the keyword stays off and the lit shaders never sample an unbound cookie).
			passData.Cookie = settings.MainLightCookie && cookieTexture != null;
			passData.CookieTexture = cookieTexture;
			if (passData.Cookie)
			{
				// World XZ -> cookie UV. scale = 1/tiling so one tile spans `tiling` world units.
				// Scroll drifts the pattern: worldScroll = speed * time, then / tiling into UV space.
				// Time.time is read here at record (CPU) time — the render func is static and can't.
				float scale = 1f / Mathf.Max(settings.CookieTiling, 0.01f);
				Vector2 offset = settings.CookieScrollSpeed * (Time.time * scale);
				passData.CookieScaleOffset = new Vector4(scale, scale, offset.x, offset.y);
				passData.CookieParams = new Vector4(settings.CookieStrength, 0f, 0f, 0f);
			}

			int count = GatherPunctualLights(frame.CullingResults);
			EnsureBuffer();
			// SetData is an immediate CPU->GPU copy. We do it at record time; the buffer
			// object persists as a field, so the data is valid when the GPU runs this pass.
			_lightBuffer.SetData(_lightData, 0, 0, Mathf.Max(count, 1));
			PunctualLightCount = count;
			passData.PunctualLights = _lightBuffer;
			passData.PunctualLightCount = count;

			// This pass owns no render targets; it only writes global shader state.
			// RG would normally cull it (no output it can see) and is free to reorder
			// passes — both would break the lighting globals the Opaque pass reads.
			builder.AllowPassCulling(false);
			builder.AllowGlobalStateModification(true);

			builder.SetRenderFunc(static (PassData data, UnsafeGraphContext ctx) =>
			{
				var cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
				cmd.SetGlobalVector(_mainLightDirectionId, data.MainLightDirection);
				cmd.SetGlobalVector(_mainLightColorId, data.MainLightColor);
				cmd.SetGlobalVector(_ambientSkyColorId, data.AmbientSky);
				cmd.SetGlobalVector(_ambientGroundColorId, data.AmbientGround);
				cmd.SetGlobalBuffer(_punctualLightsId, data.PunctualLights);
				cmd.SetGlobalInt(_punctualLightCountId, data.PunctualLightCount);

				// The world->shadow matrix and strength are CPU constants; the shadow map
				// texture itself is bound as a global by the DirectionalShadowPass. The
				// keyword compiles shadow sampling in/out of the lit shaders so we never
				// sample an unbound shadow map when shadows are off or absent.
				cmd.SetGlobalMatrix(_mainLightShadowMatrixId, data.ShadowMatrix);
				cmd.SetGlobalVector(_mainLightShadowParamsId, data.ShadowParams);
				cmd.SetGlobalVector(_mainLightShadowPcssParamsId, data.ShadowPcssParams);
				cmd.SetGlobalVector(_mainLightShadowMinMaxParamsId, data.ShadowPyramidParams);
				CoreUtils.SetKeyword(cmd, MAIN_LIGHT_SHADOWS_KEYWORD, data.MainLightShadows);
				CoreUtils.SetKeyword(cmd, SOFT_SHADOWS_KEYWORD, data.SoftShadows);
				CoreUtils.SetKeyword(cmd, PCSS_KEYWORD, data.Pcss);
				CoreUtils.SetKeyword(cmd, PCSS_PYRAMID_KEYWORD, data.PcssPyramid);
				CoreUtils.SetKeyword(cmd, SCREEN_SHADOWS_KEYWORD, data.ScreenShadows);
				CoreUtils.SetKeyword(cmd, SSAO_KEYWORD, data.Ssao);
				CoreUtils.SetKeyword(cmd, RC_KEYWORD, data.Rc);

				// Cookie texture + projection params, bound as globals. The texture is an external
				// (non-RG) resource, so it's set directly like the light buffer — no UseTexture needed.
				if (data.Cookie)
				{
					cmd.SetGlobalTexture(_mainLightCookieId, data.CookieTexture);
					cmd.SetGlobalVector(_mainLightCookieScaleOffsetId, data.CookieScaleOffset);
					cmd.SetGlobalVector(_mainLightCookieParamsId, data.CookieParams);
				}
				CoreUtils.SetKeyword(cmd, COOKIE_KEYWORD, data.Cookie);
			});
		}

		private int GatherPunctualLights(CullingResults cullingResults)
		{
			var lights = cullingResults.visibleLights;
			int count = 0;

			for (int i = 0; i < lights.Length && count < MAX_PUNCTUAL_LIGHTS; i++)
			{
				var light = lights[i];
				if (light.lightType != LightType.Point && light.lightType != LightType.Spot)
					continue;

				// Column 3 of the light-to-world matrix is its world position.
				Vector4 position = light.localToWorldMatrix.GetColumn(3);
				float range = Mathf.Max(light.range, 1e-4f);
				position.w = 1f / (range * range);

				// finalColor folds in intensity (and is linear in a linear project).
				Vector4 color = light.finalColor;

				// Point lights are encoded as spots with scale=0, offset=1 so the cone
				// term collapses to saturate(0 * cosAngle + 1) = 1 (no angular falloff).
				Vector4 spotDirScale = new(0f, 0f, 1f, 0f);
				float spotOffset = 1f;

				if (light.lightType == LightType.Spot)
				{
					// Column 2 is the light's forward (the way it shines). The cone test
					// uses L (surface->light), so store -forward to match the sign.
					Vector4 forward = light.localToWorldMatrix.GetColumn(2);
					spotDirScale = -forward;

					float cosOuter = Mathf.Cos(Mathf.Deg2Rad * light.spotAngle * 0.5f);
					float cosInner = Mathf.Cos(Mathf.Deg2Rad * light.light.innerSpotAngle * 0.5f);
					float invAngleRange = 1f / Mathf.Max(cosInner - cosOuter, 1e-4f);
					spotDirScale.w = invAngleRange;            // scale
					spotOffset = -cosOuter * invAngleRange;    // offset
				}

				color.w = spotOffset;

				_lightData[count] = new PunctualLightData
				{
					PositionRange = position,
					Color = color,
					SpotDirScale = spotDirScale,
				};
				count++;
			}

			return count;
		}

		private void EnsureBuffer()
		{
			if (_lightBuffer != null)
				return;

			int stride = Marshal.SizeOf<PunctualLightData>();
			_lightBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MAX_PUNCTUAL_LIGHTS, stride);
		}

		public void Dispose()
		{
			_lightBuffer?.Dispose();
			_lightBuffer = null;
		}
	}
}
