// Editor automation for XR + Android player configuration (batchmode-friendly).
//   Configure : OpenXR loader on Android + Meta Quest Support / Touch profile features
//               + Quest-appropriate player settings (IL2CPP, ARM64, Vulkan, ASTC).
//   BuildApk  : builds the Interview scene into Builds/SpeakUpXR.apk.

using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

public static class XrSetup
{
    private const string ConfigurationMarker = "Assets/XR/openxr-quest-pc-configured-v1.txt";

    [InitializeOnLoadMethod]
    private static void AutoConfigureOnce()
    {
        if (!File.Exists(ConfigurationMarker)) EditorApplication.delayCall += ConfigureAndMark;
    }

    private static void ConfigureAndMark()
    {
        if (File.Exists(ConfigurationMarker) || EditorApplication.isCompiling ||
            EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
        Configure();
        File.WriteAllText(ConfigurationMarker,
            "OpenXR loader: Android Quest + Standalone PC VR. Oculus/Meta controller profiles enabled.\n");
        AssetDatabase.ImportAsset(ConfigurationMarker);
    }

    [MenuItem("SpeakUpXR/Configure OpenXR For Quest And PC VR")]
    public static void Configure()
    {
        // ── XR General Settings asset (create when missing, as the UI would) ──
        XRGeneralSettingsPerBuildTarget perTarget = null;
        EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out perTarget);
        if (perTarget == null)
        {
            perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
            if (!AssetDatabase.IsValidFolder("Assets/XR"))
                AssetDatabase.CreateFolder("Assets", "XR");
            AssetDatabase.CreateAsset(perTarget, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
            EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
        }

        ConfigureOpenXrTarget(perTarget, BuildTargetGroup.Android, true);
        ConfigureOpenXrTarget(perTarget, BuildTargetGroup.Standalone, false);

        // ── Player settings for Quest 3 ──
        var nbt = NamedBuildTarget.Android;
        PlayerSettings.SetScriptingBackend(nbt, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)32;
        PlayerSettings.SetApplicationIdentifier(nbt, "com.speakupxr.interview");
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
        PlayerSettings.colorSpace = ColorSpace.Linear; // OpenXR validation rejects Gamma
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android,
            new[] { GraphicsDeviceType.Vulkan }); // Quest 3: Vulkan only
        EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;
        // coach backend is plain http on the LAN — allow cleartext + force INTERNET permission
        PlayerSettings.Android.forceInternetPermission = true;
        PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;

        AssetDatabase.SaveAssets();
        Debug.Log("[XrSetup] Configure done: OpenXR enabled for Quest Android and PC VR Standalone");
    }

    private static void ConfigureOpenXrTarget(
        XRGeneralSettingsPerBuildTarget perTarget, BuildTargetGroup group, bool questAndroid)
    {
        var settings = perTarget.SettingsForBuildTarget(group);
        if (settings == null)
        {
            settings = ScriptableObject.CreateInstance<XRGeneralSettings>();
            settings.name = group + " Settings";
            var manager = ScriptableObject.CreateInstance<XRManagerSettings>();
            manager.name = group + " Providers";
            settings.Manager = manager;
            perTarget.SetSettingsForBuildTarget(group, settings);
            AssetDatabase.AddObjectToAsset(settings, perTarget);
            AssetDatabase.AddObjectToAsset(manager, perTarget);
        }
        // Quest always initializes OpenXR. PC VR is initialized conditionally at
        // runtime only when Windows has an active OpenXR runtime registered, so
        // ordinary desktop preview stays clean instead of logging XR_ERROR_RUNTIME_UNAVAILABLE.
        settings.InitManagerOnStart = questAndroid;
        EditorUtility.SetDirty(settings);

        if (!XRPackageMetadataStore.AssignLoader(settings.Manager, typeof(OpenXRLoader).FullName, group))
        {
            throw new System.InvalidOperationException($"OpenXR loader assign failed for {group}");
        }
        Debug.Log($"[XrSetup] OpenXR loader assigned for {group}");

        // ── OpenXR features: Meta Quest Support + Oculus Touch controller profile ──
        FeatureHelpers.RefreshFeatures(group);
        var openxr = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
        int enabled = 0;
        foreach (var f in openxr.GetFeatures())
        {
            var t = f.GetType().Name;
            bool required = t == "OculusTouchControllerProfile" ||
                            (questAndroid && t == "MetaQuestFeature") ||
                            (!questAndroid && t == "MetaQuestTouchPlusControllerProfile");
            if (required)
            {
                f.enabled = true;
                enabled++;
                Debug.Log($"[XrSetup] feature enabled: {t}");
            }
        }
        int requiredCount = questAndroid ? 2 : 1;
        if (enabled < requiredCount)
        {
            throw new System.InvalidOperationException(
                $"Expected {requiredCount} OpenXR features for {group}, enabled {enabled}");
        }
        EditorUtility.SetDirty(settings.Manager);
        EditorUtility.SetDirty(perTarget);
    }

    public static void BuildApk()
    {
        System.IO.Directory.CreateDirectory("Builds");
        var report = BuildPipeline.BuildPlayer(
            new[] { "Assets/SpeakUpXR/Scenes/Interview.unity" },
            "Builds/SpeakUpXR.apk",
            BuildTarget.Android,
            BuildOptions.None);

        var ok = report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded;
        Debug.Log($"[XrSetup] Build {(ok ? "OK" : "FAILED")} " +
                  $"size={report.summary.totalSize} errors={report.summary.totalErrors}");
        EditorApplication.Exit(ok ? 0 : 1);
    }
}
