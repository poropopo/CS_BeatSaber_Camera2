using Camera2.Managers;
using HarmonyLib;

namespace Camera2.HarmonyPatches {
	[HarmonyPatch]
	static class HookMultiplayer {
		private static MultiplayerModeSelectionFlowCoordinator _instance;
		public static MultiplayerModeSelectionFlowCoordinator instance => _instance?._lobbyGameStateController == null ? null : _instance;

		[HarmonyPatch(typeof(MultiplayerModeSelectionFlowCoordinator), nameof(MultiplayerModeSelectionFlowCoordinator.TransitionDidFinish))]
		static void Postfix(MultiplayerModeSelectionFlowCoordinator __instance) {
#if DEBUG
			Plugin.Log.Info($"Multiplayer connection state changed. Connected: {__instance._lobbyGameStateController?.state}");
#endif
			_instance = __instance;
			ScenesManager.ActiveSceneChanged();
		}
	}

	[HarmonyPatch(typeof(MultiplayerSpectatorController), nameof(MultiplayerSpectatorController.Start))]
	static class HookMultiplayerSpectatorController {
		private static MultiplayerSpectatorController _instance;
		public static MultiplayerSpectatorController instance => _instance == null || !_instance.isActiveAndEnabled ? null : _instance;
		static void Postfix(MultiplayerSpectatorController __instance) {
#if DEBUG
			Plugin.Log.Info($"MultiplayerSpectatorController.Start()");
#endif
			_instance = __instance;
			ScenesManager.ActiveSceneChanged();
		}
	}
}
