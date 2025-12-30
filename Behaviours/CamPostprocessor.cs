using Camera2.Configuration;
using Camera2.Interfaces;
using Camera2.Managers;
using Camera2.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using Newtonsoft.Json;

namespace Camera2.Behaviours {
	class Settings_Shader {
		public string assetBundlePath = "";
		public string shaderName = "";

		public readonly Dictionary<string, float> properties = new Dictionary<string, float>();
	}

	public class Settings_Outline {
		public bool enabled = false;
		public Color color = Color.cyan;
		public float width = 0.05f;
		public float radius = 0.0f;
		public bool cornerTopLeft = true;
		public bool cornerTopRight = true;
		public bool cornerBottomLeft = true;
		public bool cornerBottomRight = true;
	}

	class Settings_PostProcessing : CameraSubSettings {
		public float transparencyThreshold = 0f;

		private bool _forceDepthTexture = false;
		public bool forceDepthTexture {
			get => _forceDepthTexture;
			set {
				_forceDepthTexture = value;
				settings.cam.UpdateDepthTextureActive();
			}
		}

		public Settings_Shader[] shaders = new Settings_Shader[0];

		[JsonIgnore]
		public Settings_Outline outline = new Settings_Outline();
	}

	class CamPostProcessor : MonoBehaviour {
		private static readonly int Threshold = Shader.PropertyToID("_Threshold");
		private static readonly int HasDepth = Shader.PropertyToID("_HasDepth");
		private static readonly int Width = Shader.PropertyToID("_Width");

		private Material outlineMaterial;

		protected Cam2 cam;
		protected CameraSettings settings => cam.settings;

		public void Init(Cam2 cam) {
			this.cam = cam;
		}

		void OnDisable() {
			
		}

		void OnRenderImage(RenderTexture _src, RenderTexture dest) {
			if(enabled && Plugin.ShaderMat_LuminanceKey) {
				Plugin.ShaderMat_LuminanceKey.SetFloat(Threshold, settings.PostProcessing.transparencyThreshold);
				Plugin.ShaderMat_LuminanceKey.SetFloat(HasDepth, cam.UCamera.depthTextureMode != DepthTextureMode.None ? 1 : 0);

				RenderTexture main = _src;

				void Apply(Material mat) {
					RenderTexture temp = RenderTexture.GetTemporary(main.descriptor);
					Graphics.Blit(main, temp, mat);
					if(main != _src)
						RenderTexture.ReleaseTemporary(main);

					main = temp;
				}

				foreach(var shader in settings.PostProcessing.shaders) {
					var loadedShader = ShaderManager.GetOrLoadShader(shader.assetBundlePath, shader.shaderName);
					var shaderMat = loadedShader.shaderMat;

					foreach(var prop in shader.properties) {
						if(!loadedShader.propIds.TryGetValue(prop.Key, out var propId))
							continue;

						shaderMat.SetFloat(propId, prop.Value);
					}

					Apply(shaderMat);
				}

				Apply(Plugin.ShaderMat_LuminanceKey);

				if(cam.isCurrentlySelectedInSettings)
					Apply(Plugin.ShaderMat_Outline);

				//if(settings.PostProcessing.chromaticAberrationAmount > 0) {
				//	Plugin.ShaderMat_CA.SetFloat(ChromaticAberration, settings.PostProcessing.chromaticAberrationAmount / 1000);
				//	Graphics.Blit(dest, dest, Plugin.ShaderMat_CA);
				//}

				Graphics.Blit(main, dest);
				if(main != _src)
					RenderTexture.ReleaseTemporary(main);

				if(settings.PostProcessing.outline.enabled) {
					Graphics.SetRenderTarget(dest);
					DrawOutline(_src.width, _src.height, settings.PostProcessing.outline);
				}

			} else {
				Graphics.Blit(_src, dest);
				if(settings.PostProcessing.outline.enabled) {
					Graphics.SetRenderTarget(dest);
					DrawOutline(_src.width, _src.height, settings.PostProcessing.outline);
				}
			}

			cam.PostprocessCompleted();
		}

		private void DrawOutline(int viewWidth, int viewHeight, Settings_Outline settings) {
			GL.PushMatrix();
			GL.LoadPixelMatrix(0, viewWidth, viewHeight, 0);

			if(outlineMaterial == null) {
				outlineMaterial = new Material(Shader.Find("Hidden/Internal-Colored"));
				outlineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
				outlineMaterial.SetInt("_ZWrite", 0);
				outlineMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
			}
			outlineMaterial.SetPass(0);

			GL.Begin(GL.TRIANGLES);
			GL.Color(settings.color);

			var w = settings.width * Math.Min(viewWidth, viewHeight);
			var r = settings.radius * Math.Min(viewWidth, viewHeight);

			if(r < 0) r = 0;
			
			float GetR(bool enabled) => enabled ? r : 0;
			
			var rTL = GetR(settings.cornerTopLeft);
			var rTR = GetR(settings.cornerTopRight);
			var rBL = GetR(settings.cornerBottomLeft);
			var rBR = GetR(settings.cornerBottomRight);

			var sTL = w + rTL;
			var sTR = w + rTR;
			var sBL = w + rBL;
			var sBR = w + rBR;

			// Strips
			DrawQuad(sTL, viewWidth - sTR, 0, w);
			DrawQuad(sBL, viewWidth - sBR, viewHeight - w, viewHeight);
			DrawQuad(0, w, sTL, viewHeight - sBL);
			DrawQuad(viewWidth - w, viewWidth, sTR, viewHeight - sBR);

			// Corners
			DrawCorner(0, 0, w, rTL, 0, viewWidth, viewHeight);
			DrawCorner(viewWidth, 0, w, rTR, 1, viewWidth, viewHeight);
			DrawCorner(viewWidth, viewHeight, w, rBR, 2, viewWidth, viewHeight);
			DrawCorner(0, viewHeight, w, rBL, 3, viewWidth, viewHeight);

			GL.End();
			GL.PopMatrix();
		}

		private void DrawQuad(float x1, float x2, float y1, float y2) {
			if(x1 >= x2 || y1 >= y2) return;
			// Tri 1
			GL.Vertex3(x1, y1, 0);
			GL.Vertex3(x2, y1, 0);
			GL.Vertex3(x2, y2, 0);
			// Tri 2
			GL.Vertex3(x1, y1, 0);
			GL.Vertex3(x2, y2, 0);
			GL.Vertex3(x1, y2, 0);
		}

		private void DrawCorner(float refX, float refY, float w, float r, int rotation, float viewWidth, float viewHeight) {
			// Rotation 0: Top-Left (0,0). X+, Y+.
			// Rotation 1: Top-Right (W,0). X-, Y+.
			// Rotation 2: Bottom-Right (W,H). X-, Y-.
			// Rotation 3: Bottom-Left (0,H). X+, Y-.
			
			float mx = (rotation == 1 || rotation == 2) ? -1 : 1;
			float my = (rotation == 2 || rotation == 3) ? -1 : 1;

			// Helper to transform local (0..s) point to world
			void V(float lx, float ly) {
				GL.Vertex3(refX + lx * mx, refY + ly * my, 0);
			}

			if(r <= 0.001f) {
				// Sharp Corner: Simple Square [0,w]x[0,w]
				V(0, 0); V(w, 0); V(w, w);
				V(0, 0); V(w, w); V(0, w);
			} else {
				// Rounded Corner: Annulus Sector
				// Center at (s, s) where s = w + r
				// Inner Arc Radius: r
				// Outer Arc Radius: s
				// Angle: 180 to 270 degrees
				
				float s = w + r;
				int segments = 90;
				
				for(int i = 0; i < segments; i++) {
					float a1 = (180f + (90f * i / segments)) * Mathf.Deg2Rad;
					float a2 = (180f + (90f * (i + 1) / segments)) * Mathf.Deg2Rad;

					float cos1 = Mathf.Cos(a1);
					float sin1 = Mathf.Sin(a1);
					float cos2 = Mathf.Cos(a2);
					float sin2 = Mathf.Sin(a2);

					// Points relative to Center (s,s)
					// Inner points
					float ix1 = s + cos1 * r;
					float iy1 = s + sin1 * r;
					float ix2 = s + cos2 * r;
					float iy2 = s + sin2 * r;

					// Outer points (Radius s)
					float ox1 = s + cos1 * s;
					float oy1 = s + sin1 * s;
					float ox2 = s + cos2 * s;
					float oy2 = s + sin2 * s;

					// Draw Quad (Two Tris)
					// Order: In1, Out1, Out2
					V(ix1, iy1); V(ox1, oy1); V(ox2, oy2);
					
					// Order: In1, Out2, In2
					V(ix1, iy1); V(ox2, oy2); V(ix2, iy2);
				}
			}
		}
	}

	//static class UnityMotionBlur {
	//	static readonly int SID_MainTex_TexelSize = Shader.PropertyToID("_MainTex_TexelSize");
	//	static readonly int SID_CameraMotionVectorsTexture_TexelSize = Shader.PropertyToID("_CameraMotionVectorsTexture_TexelSize");
	//	static readonly int SID_VelocityTex_TexelSize = Shader.PropertyToID("_VelocityTex_TexelSize");
	//	static readonly int SID_NeighborMaxTex_TexelSize = Shader.PropertyToID("_NeighborMaxTex_TexelSize");
	//	static readonly int SID_VelocityScale = Shader.PropertyToID("_VelocityScale");
	//	static readonly int SID_TileMaxLoop = Shader.PropertyToID("_TileMaxLoop");
	//	static readonly int SID_TileMaxOffs = Shader.PropertyToID("_TileMaxOffs");
	//	static readonly int SID_MaxBlurRadius = Shader.PropertyToID("_MaxBlurRadius");
	//	static readonly int SID_RcpMaxBlurRadius = Shader.PropertyToID("_RcpMaxBlurRadius");
	//	static readonly int SID_LoopCount = Shader.PropertyToID("_LoopCount");

	//	enum Pass : int {
	//		VelocitySetup,
	//		TileMax1,
	//		TileMax2,
	//		TileMaxV,
	//		NeighborMax,
	//		Reconstruction
	//	}

	//	static float settings_shutterAngle = 270f;
	//	static float settings_sampleCount = 10;

	//	public static void Apply(RenderTexture src, RenderTexture dest, Camera camera) {
	//		Plugin.ShaderMat_UnityMotionBlur.SetFloatArray(SID_MainTex_TexelSize, new [] { src.texelSize.x, src.texelSize.y });
	//		Plugin.ShaderMat_UnityMotionBlur.SetFloatArray(SID_CameraMotionVectorsTexture_TexelSize, new [] { camera.velocity.x, src.texelSize.y });
	//		Plugin.ShaderMat_UnityMotionBlur.SetFloatArray(SID_VelocityTex_TexelSize, new [] { camera.velocity.x, camera.velocity.y, camera.velocity.z });
	//		Plugin.ShaderMat_UnityMotionBlur.SetFloatArray(SID_NeighborMaxTex_TexelSize, new [] { src.texelSize.x, src.texelSize.y });


	//		const float kMaxBlurRadius = 5f;
	//		var vectorRTFormat = RenderTextureFormat.RGHalf;
	//		var packedRTFormat = RenderTextureFormat.ARGB2101010;

	//		// Calculate the maximum blur radius in pixels.
	//		int maxBlurPixels = (int)(kMaxBlurRadius * src.height / 100);

	//		// Calculate the TileMax size.
	//		// It should be a multiple of 8 and larger than maxBlur.
	//		int tileSize = ((maxBlurPixels - 1) / 8 + 1) * 8;

	//		// Pass 1 - Velocity/depth packing
	//		var velocityScale = settings_shutterAngle / 360f; //settings.shutterAngle
	//		Plugin.ShaderMat_UnityMotionBlur.SetFloat(SID_VelocityScale, velocityScale);
	//		Plugin.ShaderMat_UnityMotionBlur.SetFloat(SID_MaxBlurRadius, maxBlurPixels);
	//		Plugin.ShaderMat_UnityMotionBlur.SetFloat(SID_RcpMaxBlurRadius, 1f / maxBlurPixels);

	//		var vbuffer = RenderTexture.GetTemporary(src.width, src.height, 0, packedRTFormat);
	//		Graphics.Blit(vbuffer, vbuffer, Plugin.ShaderMat_UnityMotionBlur, (int)Pass.VelocitySetup);

	//		// Pass 2 - First TileMax filter (1/2 downsize)
	//		var tile2 = RenderTexture.GetTemporary(src.width / 2, src.height / 2, 0, vectorRTFormat);
	//		Graphics.Blit(vbuffer, tile2, Plugin.ShaderMat_UnityMotionBlur, (int)Pass.TileMax1);

	//		// Pass 3 - Second TileMax filter (1/2 downsize)
	//		var tile4 = RenderTexture.GetTemporary(src.width / 4, src.height / 4, 0, vectorRTFormat);
	//		Graphics.Blit(tile2, tile4, Plugin.ShaderMat_UnityMotionBlur, (int)Pass.TileMax2);
	//		RenderTexture.ReleaseTemporary(tile2);

	//		// Pass 4 - Third TileMax filter (1/2 downsize)
	//		var tile8 = RenderTexture.GetTemporary(src.width / 8, src.height / 8, 0, vectorRTFormat);
	//		Graphics.Blit(tile4, tile8, Plugin.ShaderMat_UnityMotionBlur, (int)Pass.TileMax2);
	//		RenderTexture.ReleaseTemporary(tile4);

	//		// Pass 5 - Fourth TileMax filter (reduce to tileSize)
	//		var tileMaxOffs = Vector2.one * (tileSize / 8f - 1f) * -0.5f;
	//		Plugin.ShaderMat_UnityMotionBlur.SetVector(SID_TileMaxOffs, tileMaxOffs);
	//		Plugin.ShaderMat_UnityMotionBlur.SetFloat(SID_TileMaxLoop, (int)(tileSize / 8f));

	//		var tile = RenderTexture.GetTemporary(src.width / tileSize, src.height / tileSize, 0, vectorRTFormat);
	//		Graphics.Blit(tile8, tile, Plugin.ShaderMat_UnityMotionBlur, (int)Pass.TileMaxV);
	//		RenderTexture.ReleaseTemporary(tile8);

	//		// Pass 6 - NeighborMax filter
	//		int neighborMaxWidth = src.width / tileSize;
	//		int neighborMaxHeight = src.height / tileSize;
	//		var neighborMax = RenderTexture.GetTemporary(neighborMaxWidth, neighborMaxHeight, 0, vectorRTFormat);
	//		Graphics.Blit(tile, neighborMax, Plugin.ShaderMat_UnityMotionBlur, (int)Pass.NeighborMax);
	//		RenderTexture.ReleaseTemporary(tile);

	//		// Pass 7 - Reconstruction pass
	//		Plugin.ShaderMat_UnityMotionBlur.SetFloat(SID_LoopCount, Mathf.Clamp(settings_sampleCount / 2, 1, 64));
	//		Graphics.Blit(src, dest, Plugin.ShaderMat_UnityMotionBlur, (int)Pass.Reconstruction);

	//		RenderTexture.ReleaseTemporary(vbuffer);
	//		RenderTexture.ReleaseTemporary(neighborMax);
	//	}
	//}
}
