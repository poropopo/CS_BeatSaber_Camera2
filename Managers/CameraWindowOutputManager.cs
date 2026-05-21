using Camera2.Behaviours;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Camera2.Managers {
	static class CameraWindowOutputManager {
		private const string NativeDll = "Camera2WindowOutput.Native";

		private class OutputState {
			internal int handle;
			internal string title;
			internal IntPtr texture;
			internal int outputWidth;
			internal int outputHeight;
			internal int width;
			internal int height;
		}

		private static readonly Dictionary<Cam2, OutputState> outputs = new Dictionary<Cam2, OutputState>();
		private static bool nativeAvailable = true;
		private static IntPtr renderEventFunc = IntPtr.Zero;

		[DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
		private static extern int CreateWindowOutput(string title, int width, int height);

		[DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
		private static extern void DestroyWindowOutput(int handle);

		[DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
		private static extern void SetSourceTexture(int handle, IntPtr texture, int width, int height);

		[DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
		private static extern void SetVisible(int handle, [MarshalAs(UnmanagedType.I1)] bool visible);

		[DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
		private static extern void SetWindowTitle(int handle, string title);

		[DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
		[return: MarshalAs(UnmanagedType.I1)]
		private static extern bool IsCloseRequested(int handle);

		[DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
		private static extern IntPtr GetRenderEventFunc();

		internal static void UpdateCamera(Cam2 cam) {
			if(!nativeAvailable || cam?.settings == null)
				return;

			try {
				var enabled = cam.settings.WindowOutput.enabled;

				if(!enabled || cam.destroying) {
					DestroyCamera(cam);
					return;
				}

				var outputWidth = cam.settings.WindowOutput.width;
				var outputHeight = cam.settings.WindowOutput.height;
				var title = GetWindowTitle(cam);

				if(outputs.TryGetValue(cam, out var state) && (state.outputWidth != outputWidth || state.outputHeight != outputHeight)) {
					DestroyCamera(cam);
					state = null;
				}

				if(state == null) {
					var handle = CreateWindowOutput(title, outputWidth, outputHeight);
					if(handle == 0)
						return;
					state = new OutputState {
						handle = handle,
						title = title,
						outputWidth = outputWidth,
						outputHeight = outputHeight
					};
					outputs[cam] = state;
				} else if(state.title != title) {
					SetWindowTitle(state.handle, title);
					state.title = title;
				}

				SetVisible(state.handle, cam.isActiveAndEnabled);

				var sourceTexture = cam.windowOutputTexture;
				if(sourceTexture != null) {
					var texture = sourceTexture.GetNativeTexturePtr();
					if(state.texture != texture || state.width != sourceTexture.width || state.height != sourceTexture.height) {
						SetSourceTexture(state.handle, texture, sourceTexture.width, sourceTexture.height);
						state.texture = texture;
						state.width = sourceTexture.width;
						state.height = sourceTexture.height;
					}
				}
			} catch(DllNotFoundException ex) {
				nativeAvailable = false;
				Plugin.Log.Warn($"Separate window output disabled because {NativeDll}.dll could not be loaded: {ex.Message}");
			} catch(EntryPointNotFoundException ex) {
				nativeAvailable = false;
				Plugin.Log.Warn($"Separate window output disabled because the native DLL is incompatible: {ex.Message}");
			} catch(Exception ex) {
				Plugin.Log.Error($"Failed to update separate window output for {cam.name}:");
				Plugin.Log.Error(ex);
			}
		}

		private static string GetWindowTitle(Cam2 cam) {
			var fpsLimit = cam.settings.FPSLimiter.fpsLimit;
			var fpsText = fpsLimit > 0 ? $"{fpsLimit} FPS" : "FPS uncapped";
			return $"Camera2 - {cam.name} ({cam.settings.WindowOutput.width}x{cam.settings.WindowOutput.height}, {fpsText}, AA {cam.settings.antiAliasing}x)";
		}

		internal static void Present(Cam2 cam) {
			if(!nativeAvailable || cam?.settings == null || !cam.settings.WindowOutput.enabled)
				return;

			if(!outputs.TryGetValue(cam, out var state))
				return;

			try {
				if(IsCloseRequested(state.handle)) {
					cam.settings.WindowOutput.enabled = false;
					cam.settings.Save();
					DestroyCamera(cam);
					return;
				}

				if(renderEventFunc == IntPtr.Zero)
					renderEventFunc = GetRenderEventFunc();

				if(renderEventFunc != IntPtr.Zero)
					GL.IssuePluginEvent(renderEventFunc, state.handle);
			} catch(Exception ex) {
				Plugin.Log.Error($"Failed to present separate window output for {cam.name}:");
				Plugin.Log.Error(ex);
			}
		}

		internal static void DestroyCamera(Cam2 cam) {
			if(cam == null || !outputs.TryGetValue(cam, out var state))
				return;

			outputs.Remove(cam);

			try {
				DestroyWindowOutput(state.handle);
			} catch(Exception ex) {
				Plugin.Log.Error($"Failed to destroy separate window output for {cam.name}:");
				Plugin.Log.Error(ex);
			}
		}

		internal static void Shutdown() {
			foreach(var cam in new List<Cam2>(outputs.Keys))
				DestroyCamera(cam);
		}
	}
}
