using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// FSR 1.0 spatial upscaler (AMD FidelityFX Super Resolution 1). Runs render-res -> display-res as
	// the render->display upscale step, replacing Present's plain bilinear stretch when Upscale = Fsr1.
	// Two fullscreen passes on the post-tonemap/SMAA LDR image:
	//   EASU (pass 0) — edge-adaptive upsample into a display-res buffer.
	//   RCAS (pass 1) — contrast-adaptive sharpen at display-res (recovers EASU's slight softness).
	// Present then blits the RCAS result to CameraTarget (kept separate so the size-tolerant present
	// path still owns the actual backbuffer write).
	//
	// Both passes are UNSAFE passes: a raster render func can't bind a TRANSIENT RG texture as a sampled
	// input (only a producing pass can publish it as a global). Rather than a legacy cmd.Blit (whose
	// implicit fullscreen quad failed to cover the destination when chained inside RenderGraph unsafe
	// passes -> black output), each pass explicitly binds the source as the global _MainTex, sets the
	// destination as the render target, and draws a fullscreen triangle (DrawProcedural, 3 verts) — the
	// same deterministic coverage the SMAA passes use. The per-pass uniforms are set on the shared
	// material at Record time — safe because the graph records AND executes once per camera (see
	// RenderCamera), the same pattern SmaaBlendPass/TonemapPass rely on.
	//
	// Spatial-only: NO motion vectors, jitter, or history (that machinery is FSR2/DLSS territory). The
	// tradeoff is that it magnifies any aliasing already in the source, so it's ordered AFTER SMAA.
	public class FsrUpscalePass
	{
		private static readonly int _mainTexId = Shader.PropertyToID("_MainTex");
		private static readonly int _easuCon0Id = Shader.PropertyToID("_EasuCon0");
		private static readonly int _easuCon1Id = Shader.PropertyToID("_EasuCon1");
		private static readonly int _easuCon2Id = Shader.PropertyToID("_EasuCon2");
		private static readonly int _easuCon3Id = Shader.PropertyToID("_EasuCon3");
		private static readonly int _rcasConfigId = Shader.PropertyToID("_RcasConfig");

		private readonly Material _material;

		// Also require the shader to be SUPPORTED, not just the material non-null: a material wrapping a
		// shader that failed to compile (or a brand-new shader whose variants aren't ready yet) would blit
		// BLACK. When that's the case we return false so the pipeline falls back to the plain bilinear
		// present instead of a black screen. (This bit us once: the FSR shader shipped with a compile
		// error, the pipeline cached the broken material at construction, and every FSR frame went black
		// until a domain reload rebuilt it — graceful degradation is better than black.)
		public bool IsValid => _material != null && _material.shader != null && _material.shader.isSupported;

		public FsrUpscalePass(Material material)
		{
			_material = material;
		}

		private class PassData
		{
			public TextureHandle Source;
			public TextureHandle Dest;
			public Material Material;
			public int Pass;
		}

		// Upscales `source` (render res) to display res and returns the sharpened display-res handle.
		public TextureHandle Record(RenderGraph rg, in FrameContext frame, TextureHandle source,
			int displayWidth, int displayHeight)
		{
			// EASU constants (AMD FsrEasuCon). Our source is EXACTLY the render-res buffer with no guard
			// band, so input-viewport == input-size == render res; output == display res.
			float inW = frame.RenderWidth;
			float inH = frame.RenderHeight;
			float outW = displayWidth;
			float outH = displayHeight;
			// con0: output-pixel -> input-pixel scale (xy) + half-texel bias (zw).
			_material.SetVector(_easuCon0Id, new Vector4(
				inW / outW, inH / outH,
				0.5f * inW / outW - 0.5f, 0.5f * inH / outH - 0.5f));
			// con1..con3: the four gather-centre offsets, in input UV, that tile the 12-tap footprint.
			_material.SetVector(_easuCon1Id, new Vector4(1f / inW, 1f / inH, 1f / inW, -1f / inH));
			_material.SetVector(_easuCon2Id, new Vector4(-1f / inW, 2f / inH, 1f / inW, 2f / inH));
			_material.SetVector(_easuCon3Id, new Vector4(0f, 4f / inH, 0f, 0f));

			// RCAS sharpness is authored in "stops" (0 = sharpest); the shader wants exp2(-stops). yz
			// carry the display size so RCAS can clamp its integer neighbour loads to the buffer.
			float sharpness = Mathf.Pow(2f, -Mathf.Max(0f, frame.Settings.FsrSharpness));
			_material.SetVector(_rcasConfigId, new Vector4(sharpness, displayWidth, displayHeight, 0f));

			// sRGB storage matches CameraColorLdr/CameraColorAA so the linear<->sRGB round-trip (and the
			// present blit) stays identical; FSR itself runs in the decoded linear space (see Fsr.shader).
			var easu = rg.CreateTexture(new TextureDesc(displayWidth, displayHeight)
			{
				format = GraphicsFormat.R8G8B8A8_SRGB,
				name = "FsrEasu",
			});
			RecordBlit(rg, "FSR EASU", source, easu, 0);

			var rcas = rg.CreateTexture(new TextureDesc(displayWidth, displayHeight)
			{
				format = GraphicsFormat.R8G8B8A8_SRGB,
				name = "FsrRcas",
			});
			RecordBlit(rg, "FSR RCAS", easu, rcas, 1);

			return rcas;
		}

		private void RecordBlit(RenderGraph rg, string passName, TextureHandle source, TextureHandle dest, int pass)
		{
			using var builder = rg.AddUnsafePass<PassData>(passName, out var passData);

			passData.Source = source;
			passData.Dest = dest;
			passData.Material = _material;
			passData.Pass = pass;

			builder.UseTexture(source, AccessFlags.Read);
			builder.UseTexture(dest, AccessFlags.Write);
			builder.AllowPassCulling(false);
			// We bind a global (_MainTex) inside the render func — declare that so RG allows it.
			builder.AllowGlobalStateModification(true);

			builder.SetRenderFunc(static (PassData data, UnsafeGraphContext ctx) =>
			{
				var cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
				// Explicit source bind + render target + fullscreen-triangle draw (SetRenderTarget sets the
				// viewport to the destination's full size, so coverage always matches the display-res target).
				cmd.SetGlobalTexture(_mainTexId, data.Source);
				CoreUtils.SetRenderTarget(cmd, data.Dest);
				cmd.DrawProcedural(Matrix4x4.identity, data.Material, data.Pass, MeshTopology.Triangles, 3);
			});
		}
	}
}
