// Editor automation for XR + Android player configuration (batchmode-friendly).
//   Configure : OpenXR loader on Android + Meta Quest Support / Touch profile features
//               + Quest-appropriate player settings (IL2CPP, ARM64, Vulkan, ASTC).
//   BuildApk  : builds the Interview scene into Builds/SpeakUpXR.apk.

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
    public static void Configure()
    {
        var group = BuildTargetGroup.Android;

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

        var settings = perTarget.SettingsForBuildTarget(group);
        if (settings == null)
        {
            settings = ScriptableObject.CreateInstance<XRGeneralSettings>();
            settings.name = "Android Settings";
            var manager = ScriptableObject.CreateInstance<XRManagerSettings>();
            manager.name = "Android Providers";
            settings.Manager = manager;
            perTarget.SetSettingsForBuildTarget(group, settings);
            AssetDatabase.AddObjectToAsset(settings, perTarget);
            AssetDatabase.AddObjectToAsset(manager, perTarget);
        }
        settings.InitManagerOnStart = true;

        if (!XRPackageMetadataStore.AssignLoader(settings.Manager, typeof(OpenXRLoader).FullName, group))
        {
            Debug.LogError("[XrSetup] OpenXR loader assign FAILED");
            EditorApplication.Exit(1);
            return;
        }
        Debug.Log("[XrSetup] OpenXR loader assigned for Android");

        // ── OpenXR features: Meta Quest Support + Oculus Touch controller profile ──
        FeatureHelpers.RefreshFeatures(group);
        var openxr = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
        int enabled = 0;
        foreach (var f in openxr.GetFeatures())
        {
            var t = f.GetType().Name;
            if (t == "MetaQuestFeature" || t == "OculusTouchControllerProfile")
            {
                f.enabled = true;
                enabled++;
                Debug.Log($"[XrSetup] feature enabled: {t}");
            }
        }
        if (enabled < 2)
        {
            Debug.LogError($"[XrSetup] expected 2 features, enabled {enabled}");
            EditorApplication.Exit(1);
            return;
        }

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
        Debug.Log("[XrSetup] Configure done");
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
