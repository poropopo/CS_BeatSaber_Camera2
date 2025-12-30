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

			if(outlineMaterial == null) outlineMaterial = new Material(Shader.Find("Hidden/Internal-Colored"));
			outlineMaterial.SetPass(0);

			GL.Begin(GL.TRIANGLES);
			GL.Color(settings.color);

			var w = settings.width * Math.Min(viewWidth, viewHeight);
			var r = settings.radius * Math.Min(viewWidth, viewHeight);

			// Clamp r to be reasonable
			if(r < 0) r = 0;
			
			// Define the "Corner Size" S.
			// The straight strips will go from S to Width-S.
			// Ideally S = w + r.
			// If a corner is NOT rounded, effectively r=0 for that corner, but for code simplicity we keep S uniform or handle per corner?
			// Let's handle per corner.

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
			// Top: x from sTL to Width-sTR, y from 0 to w
			DrawQuad(sTL, viewWidth - sTR, 0, w);
			// Bottom: x from sBL to Width-sBR, y from Height-w to Height
			DrawQuad(sBL, viewWidth - sBR, viewHeight - w, viewHeight);
			// Left: x from 0 to w, y from sTL to Height-sBL
			DrawQuad(0, w, sTL, viewHeight - sBL);
			// Right: x from Width-w to Width, y from sTR to Height-sBR
			DrawQuad(viewWidth - w, viewWidth, sTR, viewHeight - sBR);

			// Corners
			// Top-Left
			DrawCorner(0, 0, sTL, w, rTL, 0, 0); // Rotation 0: Top-Left logic
			// Top-Right
			DrawCorner(viewWidth - sTR, 0, sTR, w, rTR, 1, viewWidth - sTR);
			// Bottom-Right
			DrawCorner(viewWidth - sBR, viewHeight - sBR, sBR, w, rBR, 2, viewWidth - sBR, viewHeight - sBR);
			// Bottom-Left
			DrawCorner(0, viewHeight - sBL, sBL, w, rBL, 3, 0, viewHeight - sBL);

			GL.End();
			GL.PopMatrix();
		}

		private void DrawQuad(float x1, float x2, float y1, float y2) {
			if(x1 >= x2 || y1 >= y2) return;
			GL.Vertex3(x1, y1, 0);
			GL.Vertex3(x2, y1, 0);
			GL.Vertex3(x2, y2, 0);
			GL.Vertex3(x1, y1, 0);
			GL.Vertex3(x2, y2, 0);
			GL.Vertex3(x1, y2, 0);
		}

		private void DrawCorner(float x, float y, float s, float w, float r, int rotation, float refX, float refY = 0) {
			// Logic is for Top-Left at 0,0 with S.
			// Vertices will be translated by x,y at the end.
			// Actually simpler: Generate relative vertices for Top-Left, then rotate/translate.
			
			// Polygon for Top-Left corner (0,0, size S).
			// Vertices: (S,w) -> (S,0) -> (0,0) -> (0,S) -> (w,S) -> Arc -> (S,w)
			// Arc center is (S,S), radius r.
			// Angles for Top-Left Arc: From 180 to 270 degrees (if 0 is right, 90 is up?).
			// Wait, standard trig: 0 is Right, 90 is Up (screen Y is down usually, but here 0 is top?).
			// PixelMatrix: 0 is Top.
			// Center (S,S). 
			// P1 (S,w). Vector (0, w-S) = (0, -r). Angle -90 (or 270).
			// P2 (w,S). Vector (w-S, 0) = (-r, 0). Angle 180.
			// So Arc is 180 -> 270 degrees.
			
			// If r=0, just (S,w)-(S,0)-(0,0)-(0,S)-(w,S)-(w,w)-(S,w)
            // Or just (w,w)-(w,0)-(0,0)-(0,w) for the missing block.
			
			List<Vector2> verts = new List<Vector2>();
			
			if(r <= 0.001f) {
				// Sharp Corner fill
				// We need to fill the L shape [0,w]x[0,w].
                // But S=w.
				// Rects: [0,w]x[0,w]
				verts.Add(new Vector2(w, w));
				verts.Add(new Vector2(w, 0));
				verts.Add(new Vector2(0, 0));
				verts.Add(new Vector2(0, w));
			} else {
				verts.Add(new Vector2(s, w)); // Start of arc
				verts.Add(new Vector2(s, 0)); // Top edge
				verts.Add(new Vector2(0, 0)); // Corner tip
				verts.Add(new Vector2(0, s)); // Left edge
				verts.Add(new Vector2(w, s)); // End of arc

				// Arc
				int segments = 10;
				for(int i = 0; i <= segments; i++) {
					// 180 to 270
					float angRad = (180f + (90f * i / segments)) * Mathf.Deg2Rad;
					float cx = s + Mathf.Cos(angRad) * r;
					float cy = s + Mathf.Sin(angRad) * r;
					verts.Add(new Vector2(cx, cy));
				}
			}

			// Rotate and Translate
			// Center of 'local' rotation effectively 0,0? No.
			// We defined it in Top-Left orientation.
			// Rotation 0: (x,y) -> (x,y)
			// Rotation 1 (Top-Right): x -> -y, y -> x ? No, easiest to just hardcode mapping.
			// Rotation 1 implies mirroring X?
			
			// Let's just create points relative to corner specific origin?
			// Top-Left: Origin 0,0. Positive X, Positive Y.
			// Top-Right: Origin W,0. Negative X, Positive Y.
			// Bottom-Right: Origin W,H. Negative X, Negative Y.
			// Bottom-Left: Origin 0,H. Positive X, Negative Y.
			
			float mx = (rotation == 1 || rotation == 2) ? -1 : 1;
			float my = (rotation == 2 || rotation == 3) ? -1 : 1;
			
			// Central Point for triangle fan. Use the first point? Or (0,0)?
			// Convex polygon? No, it's L-shaped which is Concave.
			// Must decompose into triangles.
			// Simple fan from (0,0) (Corner tip) works for this shape!
			// (0,0) connects to everything.
			
			// Vertex 2 (0,0) index in list is 2 (Sharp) or 2 (Rounded).
			// Let's reorder to put (0,0) first for Fan.
			// Sharp: (0,0), (0,w), (w,w), (w,0).
			// Rounded: (0,0), (0,s), (w,s), ...arc..., (s,w), (s,0).
			
			// Rebuild verts list for Fan logic
			verts.Clear();
			verts.Add(new Vector2(0, 0));
			if(r <= 0.001f) {
				verts.Add(new Vector2(0, w));
				verts.Add(new Vector2(w, w));
				verts.Add(new Vector2(w, 0));
			} else {
				verts.Add(new Vector2(0, s));
				verts.Add(new Vector2(w, s));
				int segments = 90;
				for(int i = 0; i <= segments; i++) {
					float angRad = (180f + (90f * i / segments)) * Mathf.Deg2Rad;
					float cx = s + Mathf.Cos(angRad) * r;
					float cy = s + Mathf.Sin(angRad) * r;
					verts.Add(new Vector2(cx, cy));
				}
				verts.Add(new Vector2(s, 0));
			}

			// Draw Triangles
			Vector2 center = verts[0];
			for(int i = 1; i < verts.Count - 1; i++) {
				Vector2 p1 = verts[i];
				Vector2 p2 = verts[i+1];

				// Apply Transform
				GL.Vertex3(refX + center.x * mx, refY + center.y * my, 0);
				GL.Vertex3(refX + p1.x * mx, refY + p1.y * my, 0);
				GL.Vertex3(refX + p2.x * mx, refY + p2.y * my, 0);
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
