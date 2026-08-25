using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.XR.Management;

namespace SpeakUpXR
{
    /// <summary>
    /// Starts PC OpenXR only when Windows has a runtime (Meta/SteamVR/WMR) selected.
    /// Quest Android uses XR Management's normal automatic initialization.
    /// </summary>
    public static class ConditionalOpenXrBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitializePcVrWhenAvailable()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (!HasWindowsOpenXrRuntime()) return;
            var manager = XRGeneralSettings.Instance ? XRGeneralSettings.Instance.Manager : null;
            if (!manager || manager.activeLoader) return;
            manager.InitializeLoaderSync();
            if (manager.activeLoader) manager.StartSubsystems();
#endif
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        private static bool HasWindowsOpenXrRuntime()
        {
            string explicitRuntime = Environment.GetEnvironmentVariable("XR_RUNTIME_JSON");
            if (!string.IsNullOrWhiteSpace(explicitRuntime) && File.Exists(explicitRuntime)) return true;
            string activeManifest = ReadActiveRuntimeManifest();
            return !string.IsNullOrWhiteSpace(activeManifest) && File.Exists(activeManifest);
        }

        private static string ReadActiveRuntimeManifest()
        {
            const uint registryString = 0x00000002;
            var value = new StringBuilder(1024);
            uint bytes = (uint)(value.Capacity * sizeof(char));
            int result = RegGetValue(new UIntPtr(0x80000002u),
                @"SOFTWARE\Khronos\OpenXR\1", "ActiveRuntime", registryString,
                out _, value, ref bytes);
            return result == 0 ? value.ToString() : null;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegGetValue(UIntPtr hkey, string subKey, string value,
            uint flags, out uint type, StringBuilder data, ref uint dataSize);
#endif
    }
}
