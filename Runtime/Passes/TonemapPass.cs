using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Tonemaps the HDR scene (or a selected debug view) into an LDR buffer (CameraColorLdr). This was
	// split out of PresentPass so the tonemapped result exists as its OWN sampleable resource: post-
	// tonemap LDR effects (SMAA, and any future LDR post) read that buffer and hand their result to
	// Present. With no such effect wired yet, Tonemap -> Present is a straight passthrough that must
	// produce a byte-identical image to the old single-pass Present (the Phase-0 lossless check).
	//
	// Unsafe pass (a `cmd.Blit` through the tonemap material) rather than a raster pass: the tonemap
	// shader is a legacy _MainTex blit shader, and binding a TRANSIENT RG texture as a fullscreen
	// input inside a raster render func isn't possible (raster passes can't set global/material
	// textures). The unsafe Blit reuses the existing shader unchanged — same code the old Present ran.
	public class TonemapPass
	{
		private const string KEYWORD_REINHARD = "_TONEMAP_REINHARD";
		private const string KEYWORD_ACES = "_TONEMAP_ACES";

		// The LDR result is published under this global so post-tonemap effects (SMAA) can sample it.
		private static readonly int _cameraColorLdrId = Shader.PropertyToID("_CameraColorLdr");

		private readonly Material _tonemapMaterial;

		public bool IsValid => _tonemapMaterial != null;

		private class PassData
		{
			public TextureHandle Source;
			public TextureHandle Dest;
			public Material Material;
		}

		public TonemapPass(Material tonemapMaterial)
		{
			_tonemapMaterial = tonemapMaterial;
		}

		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle source, TextureHandle dest)
		{
			ApplyTonemapKeyword(frame.Settings.Tonemap);

			using var builder = rg.AddUnsafePass<PassData>("Tonemap", out var passData);

			passData.Source = source;
			passData.Dest = dest;
			passData.Material = _tonemapMaterial;

			builder.UseTexture(source, AccessFlags.Read);
			builder.UseTexture(dest, AccessFlags.Write);
			builder.SetGlobalTextureAfterPass(dest, _cameraColorLdrId);
			builder.AllowPassCulling(false);

			builder.SetRenderFunc(static (PassData data, UnsafeGraphContext ctx) =>
			{
				var nativeCmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
				nativeCmd.Blit(data.Source, data.Dest, data.Material, 0);
			});
		}

		private void ApplyTonemapKeyword(TonemapMode mode)
		{
			if (_tonemapMaterial == null) return;

			_tonemapMaterial.DisableKeyword(KEYWORD_REINHARD);
			_tonemapMaterial.DisableKeyword(KEYWORD_ACES);

			switch (mode)
			{
				case TonemapMode.Reinhard: _tonemapMaterial.EnableKeyword(KEYWORD_REINHARD); break;
				case TonemapMode.Aces:     _tonemapMaterial.EnableKeyword(KEYWORD_ACES); break;
			}
		}
	}
}
