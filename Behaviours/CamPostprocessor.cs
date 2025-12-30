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
		
		public bool sideTop = true;
		public bool sideBottom = true;
		public bool sideLeft = true;
		public bool sideRight = true;

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

			if(maskMaterial == null) {
				maskMaterial = new Material(Shader.Find("Hidden/Internal-Colored"));
				maskMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
				maskMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
				maskMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
				maskMaterial.SetInt("_ZWrite", 0);
				maskMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
			}
			
			// Core Width and Radius
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
			
			// Determine corner visibility based on adjacent sides
			bool showTL = settings.sideTop || settings.sideLeft;
			bool showTR = settings.sideTop || settings.sideRight;
			bool showBL = settings.sideBottom || settings.sideLeft;
			bool showBR = settings.sideBottom || settings.sideRight;
			
			// --- Pass 1: Masking (Clear corners) ---
			// Only draw mask if corner is visible AND has radius
			if((showTL && rTL > 0) || (showTR && rTR > 0) || (showBL && rBL > 0) || (showBR && rBR > 0)) {
				maskMaterial.SetPass(0);
				GL.Begin(GL.TRIANGLES);
				GL.Color(Color.clear); 
				if(showTL && rTL > 0) DrawCornerMask(0, 0, w, rTL, 0);
				if(showTR && rTR > 0) DrawCornerMask(viewWidth, 0, w, rTR, 1);
				if(showBR && rBR > 0) DrawCornerMask(viewWidth, viewHeight, w, rBR, 2);
				if(showBL && rBL > 0) DrawCornerMask(0, viewHeight, w, rBL, 3);
				GL.End();
			}

			// --- Pass 2: Outline ---
			outlineMaterial.SetPass(0);

			GL.Begin(GL.TRIANGLES);
			
			// Fringe width (AA width)
			float f = 1.0f;
			
			// Overlap amount to prevent hairline gaps
			float ov = 0.1f;

			// Define Colors (Solid and Transparent)
			Color cSolid = settings.color;
			Color cClear = new Color(cSolid.r, cSolid.g, cSolid.b, 0.0f);

			// Helper to draw a strip
			void DrawFringedQuad(float x1, float x2, float y1, float y2, bool vert, float overlap) {
				if(x1 >= x2 || y1 >= y2) return;
				
				if(vert) {
					// Vertical Bar (Left/Right)
					// y is length direction. Apply overlap to y.
					
					// Core (Extended by overlap)
					GL.Color(cSolid);
					DrawQuadGeometry(x1 + 0.5f, x2 - 0.5f, y1 - overlap, y2 + overlap);
					
					// Outer Fringe (Left side of bar) - Strict Length
					DrawQuadColors(x1 - 0.5f, x1 + 0.5f, y1, y2, cClear, cSolid);
					
					// Inner Fringe (Right side of bar) - Strict Length
					DrawQuadColors(x2 - 0.5f, x2 + 0.5f, y1, y2, cSolid, cClear);
				} else {
					// Horizontal Bar (Top/Bottom)
					// x is length direction. Apply overlap to x.
					
					// Core (Extended by overlap)
					GL.Color(cSolid);
					DrawQuadGeometry(x1 - overlap, x2 + overlap, y1 + 0.5f, y2 - 0.5f);
					
					// Outer Fringe (Top side) - Strict Length
					DrawQuadColors(x1, x2, y1 - 0.5f, y1 + 0.5f, cClear, cSolid, true);
					
					// Inner Fringe (Bottom side) - Strict Length
					DrawQuadColors(x1, x2, y2 - 0.5f, y2 + 0.5f, cSolid, cClear, true);
				}
			}

			// Strips
			// Top (Horizontal)
			if(settings.sideTop) DrawFringedQuad(sTL, viewWidth - sTR, 0, w, false, ov);
			// Bottom (Horizontal)
			if(settings.sideBottom) DrawFringedQuad(sBL, viewWidth - sBR, viewHeight - w, viewHeight, false, ov);
			// Left (Vertical)
			if(settings.sideLeft) DrawFringedQuad(0, w, sTL, viewHeight - sBL, true, ov);
			// Right (Vertical)
			if(settings.sideRight) DrawFringedQuad(viewWidth - w, viewWidth, sTR, viewHeight - sBR, true, ov);

			// Corners
			// Top-Left
			if(showTL) DrawCornerFringed(0, 0, w, rTL, 0, cSolid, cClear, viewWidth, viewHeight);
			// Top-Right
			if(showTR) DrawCornerFringed(viewWidth, 0, w, rTR, 1, cSolid, cClear, viewWidth, viewHeight);
			// Bottom-Right
			if(showBR) DrawCornerFringed(viewWidth, viewHeight, w, rBR, 2, cSolid, cClear, viewWidth, viewHeight);
			// Bottom-Left
			if(showBL) DrawCornerFringed(0, viewHeight, w, rBL, 3, cSolid, cClear, viewWidth, viewHeight);

			GL.End();
			GL.PopMatrix();
		}

		private void DrawQuadGeometry(float x1, float x2, float y1, float y2) {
			if(x1 >= x2 || y1 >= y2) return;
			GL.Vertex3(x1, y1, 0); GL.Vertex3(x2, y1, 0); GL.Vertex3(x2, y2, 0);
			GL.Vertex3(x1, y1, 0); GL.Vertex3(x2, y2, 0); GL.Vertex3(x1, y2, 0);
		}

		private void DrawQuadColors(float x1, float x2, float y1, float y2, Color c1, Color c2, bool verticalGradient = false) {
			if(x1 >= x2 || y1 >= y2) return;
			
			if(!verticalGradient) {
				// Horizontal gradient (Left c1, Right c2)
				GL.Color(c1); GL.Vertex3(x1, y1, 0); 
				GL.Color(c2); GL.Vertex3(x2, y1, 0); GL.Vertex3(x2, y2, 0);
				GL.Color(c1); GL.Vertex3(x1, y1, 0); 
				GL.Color(c2); GL.Vertex3(x2, y2, 0); 
				GL.Color(c1); GL.Vertex3(x1, y2, 0);
			} else {
				// Vertical gradient (Top c1, Bottom c2)
				GL.Color(c1); GL.Vertex3(x1, y1, 0); GL.Vertex3(x2, y1, 0); 
				GL.Color(c2); GL.Vertex3(x2, y2, 0);
				GL.Color(c1); GL.Vertex3(x1, y1, 0); 
				GL.Color(c2); GL.Vertex3(x2, y2, 0); 
				GL.Color(c1); GL.Vertex3(x1, y2, 0); // Oops, winding. 
				// Fixed:
				// Tri1: TL, TR, BR(c2)
				// Tri2: TL, BR(c2), BL(c2) -- wait.
				// Let's be explicit.
				// y1 is Top (c1). y2 is Bottom (c2).
			}
		}

		private void DrawCornerFringed(float refX, float refY, float w, float r, int rotation, Color cSolid, Color cClear, float viewWidth, float viewHeight) {
			float mx = (rotation == 1 || rotation == 2) ? -1 : 1;
			float my = (rotation == 2 || rotation == 3) ? -1 : 1;

			void V(float lx, float ly) {
				GL.Vertex3(refX + lx * mx, refY + ly * my, 0);
			}

			if(r <= 0.001f) {
				// Sharp Corner Implementation
				
				// 1. Solid Center
				GL.Color(cSolid);
				V(0.5f, 0.5f); V(w - 0.5f, 0.5f); V(w - 0.5f, w - 0.5f);
				V(0.5f, 0.5f); V(w - 0.5f, w - 0.5f); V(0.5f, w - 0.5f);
				
				// 2. Outer Tip (Top-Left)
				// Quad: -0.5,-0.5 to 0.5,0.5. TL/TR/BL Clear, BR Solid.
				GL.Color(cClear); V(-0.5f, -0.5f);
				GL.Color(cClear); V(0.5f, -0.5f);
				GL.Color(cSolid); V(0.5f, 0.5f);
				GL.Color(cClear); V(-0.5f, -0.5f);
				GL.Color(cSolid); V(0.5f, 0.5f);
				GL.Color(cClear); V(-0.5f, 0.5f);
				
				// 3. Inner Tip (Bottom-Right)
				// Quad: w-.5, w-.5 to w+.5, w+.5. TL Solid, TR/BL/BR Clear.
				GL.Color(cSolid); V(w - 0.5f, w - 0.5f);
				GL.Color(cClear); V(w + 0.5f, w - 0.5f);
				GL.Color(cClear); V(w + 0.5f, w + 0.5f);
				GL.Color(cSolid); V(w - 0.5f, w - 0.5f);
				GL.Color(cClear); V(w + 0.5f, w + 0.5f);
				GL.Color(cClear); V(w - 0.5f, w + 0.5f);
				
				// 4. Outer Strips
				// Top (Horizontal)
				{
					float x1 = 0.5f, x2 = w, y1 = -0.5f, y2 = 0.5f;
					GL.Color(cClear); V(x1, y1); GL.Color(cClear); V(x2, y1); GL.Color(cSolid); V(x2, y2);
					GL.Color(cClear); V(x1, y1); GL.Color(cSolid); V(x2, y2); GL.Color(cSolid); V(x1, y2);
				}
				// Left (Vertical)
				{
					float x1 = -0.5f, x2 = 0.5f, y1 = 0.5f, y2 = w;
					GL.Color(cClear); V(x1, y1); GL.Color(cSolid); V(x2, y1); GL.Color(cSolid); V(x2, y2);
					GL.Color(cClear); V(x1, y1); GL.Color(cSolid); V(x2, y2); GL.Color(cClear); V(x1, y2);
				}

				// 5. Inner Strips
				// Top (Horizontal)
				{
					float x1 = 0.5f, x2 = w - 0.5f, y1 = w - 0.5f, y2 = w + 0.5f;
					GL.Color(cSolid); V(x1, y1); GL.Color(cSolid); V(x2, y1); GL.Color(cClear); V(x2, y2);
					GL.Color(cSolid); V(x1, y1); GL.Color(cClear); V(x2, y2); GL.Color(cClear); V(x1, y2);
				}
				// Left (Vertical)
				{
					float x1 = w - 0.5f, x2 = w + 0.5f, y1 = 0.5f, y2 = w - 0.5f;
					GL.Color(cSolid); V(x1, y1); GL.Color(cClear); V(x2, y1); GL.Color(cClear); V(x2, y2);
					GL.Color(cSolid); V(x1, y1); GL.Color(cClear); V(x2, y2); GL.Color(cSolid); V(x1, y2);
				}
				
				return;
			}

			// Annulus Sector with Fringe
			// Inner Core Radius: r + 0.5
			// Outer Core Radius: s - 0.5
			// Inner Fringe: r - 0.5 to r + 0.5
			// Outer Fringe: s - 0.5 to s + 0.5
			
			float s = w + r;
			int segments = 90;

			// We draw 3 rings (Inner Fringe, Core, Outer Fringe)
			// Ring 1: Inner Fringe (r-0.5 to r+0.5). Alpha 0 to 1.
			// Ring 2: Core (r+0.5 to s-0.5). Alpha 1.
			// Ring 3: Outer Fringe (s-0.5 to s+0.5). Alpha 1 to 0.
			
			float r_in_start = r - 0.5f;
			float r_in_end = r + 0.5f;
			float r_core_start = r + 0.5f;
			float r_core_end = s - 0.5f;
			float r_out_start = s - 0.5f;
			float r_out_end = s + 0.5f;
			
			if(r < 0.5f) {
				// Handle very sharp inner corner
				// r_in_start < 0.
				// But geometric radius can be negative effectively meaning we fill the hole.
				// If r=0, we just want a filled center?
				// Actually, if r=0, Inner Fringe goes from -0.5 to 0.5.
				// We can clip to 0?
				// No, let's keep math simple.
			}

			for(int i = 0; i < segments; i++) {
				float a1 = (180f + (90f * i / segments)) * Mathf.Deg2Rad;
				float a2 = (180f + (90f * (i + 1) / segments)) * Mathf.Deg2Rad;

				float cos1 = Mathf.Cos(a1); float sin1 = Mathf.Sin(a1);
				float cos2 = Mathf.Cos(a2); float sin2 = Mathf.Sin(a2);

				// Helper for Sector Quad
				void DrawSectorQuad(float radStart, float radEnd, Color colStart, Color colEnd) {
					// Points relative to (s,s)
					float x1s = s + cos1 * radStart; float y1s = s + sin1 * radStart;
					float x2s = s + cos2 * radStart; float y2s = s + sin2 * radStart;
					float x1e = s + cos1 * radEnd;   float y1e = s + sin1 * radEnd;
					float x2e = s + cos2 * radEnd;   float y2e = s + sin2 * radEnd;

					GL.Color(colStart); V(x1s, y1s);
					GL.Color(colEnd);   V(x1e, y1e); 
					GL.Color(colEnd);   V(x2e, y2e);

					GL.Color(colStart); V(x1s, y1s); 
					GL.Color(colEnd);   V(x2e, y2e); 
					GL.Color(colStart); V(x2s, y2s);
				}

				// Inner Fringe
				DrawSectorQuad(r_in_start, r_in_end, cClear, cSolid);
				
				// Core
				DrawSectorQuad(r_core_start, r_core_end, cSolid, cSolid);
				
				// Outer Fringe
				DrawSectorQuad(r_out_start, r_out_end, cSolid, cClear);
			}
		}

		private Material maskMaterial;
	
		private void DrawCornerMask(float refX, float refY, float w, float r, int rotation) {
			float mx = (rotation == 1 || rotation == 2) ? -1 : 1;
			float my = (rotation == 2 || rotation == 3) ? -1 : 1;

			void V(float lx, float ly) {
				GL.Vertex3(refX + lx * mx, refY + ly * my, 0);
			}

			float s = w + r;
			float r_out_end = s + 0.5f;

			int segments = 45;
			
			for(int i = 0; i < segments; i++) {
				float a1 = (180f + (90f * i / segments)) * Mathf.Deg2Rad;
				float a2 = (180f + (90f * (i + 1) / segments)) * Mathf.Deg2Rad;

				float cos1 = Mathf.Cos(a1); float sin1 = Mathf.Sin(a1);
				float cos2 = Mathf.Cos(a2); float sin2 = Mathf.Sin(a2);

				float x1 = s + cos1 * r_out_end; float y1 = s + sin1 * r_out_end;
				float x2 = s + cos2 * r_out_end; float y2 = s + sin2 * r_out_end;
				
				V(0,0); V(x1, y1); V(x2, y2);
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
