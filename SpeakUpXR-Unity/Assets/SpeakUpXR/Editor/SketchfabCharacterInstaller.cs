using System;
using System.Collections.Generic;
using System.Linq;
using SpeakUpXR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// One-time authoring tool for the three downloaded Sketchfab characters.
/// The resulting character instances are saved in Interview.unity; nothing is
/// downloaded, spawned, or replaced while the game is running.
/// </summary>
public static class SketchfabCharacterInstaller
{
    private const string ScenePath = "Assets/SpeakUpXR/Scenes/Interview.unity";
    private const string InstallMarkerPath = "Assets/ThirdParty/Sketchfab/characters-installed-v1.txt";

    private static readonly CharacterSpec[] Characters =
    {
        new(
            "warm",
            "Assets/ThirdParty/Sketchfab/BusinessmanGreySuit/source/Businessman in a Grey Suit.glb",
            "BusinessmanGreySuit_Warm"),
        new(
            "analytical",
            "Assets/ThirdParty/Sketchfab/CorporateWalkGraySuit/source/model.glb",
            "CorporateWalkGraySuit_Analytical"),
        new(
            "challenging",
            "Assets/ThirdParty/Sketchfab/BusinessmanIdBadge/source/Businessman with ID badge.glb",
            "BusinessmanIdBadge_Challenging")
    };

    [InitializeOnLoadMethod]
    private static void QueueFirstInstallation()
    {
        if (System.IO.File.Exists(InstallMarkerPath))
            return;

        EditorApplication.delayCall += () =>
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || System.IO.File.Exists(InstallMarkerPath))
                return;

            try
            {
                PlaceDownloadedCharacters();
                System.IO.File.WriteAllText(
                    InstallMarkerPath,
                    "The three Sketchfab interviewers were placed in Interview.unity at editor import time.\n");
                AssetDatabase.ImportAsset(InstallMarkerPath);
            }
            catch (Exception exception)
            {
                Debug.LogError("[SpeakUpXR] Automatic Sketchfab character placement failed. " + exception);
            }
        };
    }

    [MenuItem("SpeakUpXR/Place Downloaded Sketchfab Interviewers")]
    public static void PlaceDownloadedCharacters()
    {
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var controllers = UnityEngine.Object.FindObjectsByType<InterviewerController>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);

        var failures = new List<string>();
        foreach (var spec in Characters)
        {
            var controller = controllers.FirstOrDefault(value => value.PersonaId == spec.PersonaId);
            var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(spec.AssetPath);
            if (!controller || !modelAsset)
            {
                failures.Add($"{spec.PersonaId}: controller={controller != null}, model={modelAsset != null} ({spec.AssetPath})");
                continue;
            }

            if (controller.AvatarRoot)
                UnityEngine.Object.DestroyImmediate(controller.AvatarRoot);

            var character = PrefabUtility.InstantiatePrefab(modelAsset, controller.transform) as GameObject;
            if (!character)
            {
                failures.Add($"{spec.PersonaId}: could not instantiate {spec.AssetPath}");
                continue;
            }

            character.name = spec.SceneName + "_EDIT_TRANSFORM_HERE";
            character.transform.localPosition = Vector3.zero;
            character.transform.localRotation = Quaternion.identity;
            character.transform.localScale = Vector3.one;

            CreateSeatedMeshAssets(character, spec.SceneName);
            // These source meshes are standing, unrigged scans. Sink the lower legs
            // below the floor/desk so their eye level and silhouette read as seated.
            // A true seated pose still requires a rigged replacement or auto-rigging.
            NormalizeHeightAndFloor(character, 1.76f, -0.28f);
            ConfigureRenderers(character);
            var mouth = CreateMouthProxy(character);

            controller.AvatarRoot = character;
            controller.PlaceholderMouth = mouth;
            EditorUtility.SetDirty(controller);
        }

        if (failures.Count > 0)
            throw new InvalidOperationException("Sketchfab character placement failed:\n" + string.Join("\n", failures));

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("[SpeakUpXR] Three Sketchfab interviewers were placed and saved directly in Interview.unity.");
    }

    private static void CreateSeatedMeshAssets(GameObject character, string sceneName)
    {
        const string generatedFolder = "Assets/ThirdParty/Sketchfab/GeneratedSeated";
        if (!AssetDatabase.IsValidFolder(generatedFolder))
            AssetDatabase.CreateFolder("Assets/ThirdParty/Sketchfab", "GeneratedSeated");

        int index = 0;
        foreach (var filter in character.GetComponentsInChildren<MeshFilter>(true))
        {
            var source = filter.sharedMesh;
            if (!source || !source.isReadable) continue;

            var seated = UnityEngine.Object.Instantiate(source);
            seated.name = sceneName + "_SeatedMesh_" + index;
            var vertices = seated.vertices;
            var bounds = seated.bounds;
            float hipY = bounds.min.y + bounds.size.y * 0.53f;
            float kneeY = bounds.min.y + bounds.size.y * 0.30f;
            float centerX = bounds.center.x;
            float legMaskHalfWidth = bounds.size.x * 0.34f;
            var hipRotation = Quaternion.Euler(-78f, 0f, 0f);
            var hip = new Vector3(0f, hipY, bounds.center.z);
            var knee = new Vector3(0f, kneeY, bounds.center.z);
            var bentKnee = hip + hipRotation * (knee - hip);

            for (int i = 0; i < vertices.Length; i++)
            {
                var value = vertices[i];
                float horizontalMask = 1f - Mathf.SmoothStep(
                    legMaskHalfWidth * 0.78f,
                    legMaskHalfWidth,
                    Mathf.Abs(value.x - centerX));
                if (horizontalMask <= 0f || value.y >= hipY) continue;

                Vector3 bent;
                if (value.y > kneeY)
                {
                    var pivot = new Vector3(value.x, hipY, bounds.center.z);
                    bent = pivot + hipRotation * (value - pivot);
                }
                else
                {
                    // Keep the lower leg mostly vertical after moving the knee forward.
                    bent = new Vector3(value.x, bentKnee.y + (value.y - kneeY), bentKnee.z + (value.z - bounds.center.z));
                }

                float hipBlend = Mathf.InverseLerp(hipY, hipY - bounds.size.y * 0.08f, value.y);
                vertices[i] = Vector3.Lerp(value, bent, horizontalMask * hipBlend);
            }

            seated.vertices = vertices;
            seated.RecalculateBounds();
            seated.RecalculateNormals();
            seated.RecalculateTangents();

            string assetPath = $"{generatedFolder}/{sceneName}_{index}.asset";
            if (AssetDatabase.LoadAssetAtPath<Mesh>(assetPath)) AssetDatabase.DeleteAsset(assetPath);
            AssetDatabase.CreateAsset(seated, assetPath);
            filter.sharedMesh = seated;
            index++;
        }
    }

    private static void NormalizeHeightAndFloor(GameObject root, float targetHeight, float localFloor)
    {
        var bounds = CalculateLocalBounds(root);
        if (bounds.size.y <= 0.0001f)
            return;

        float scale = targetHeight / bounds.size.y;
        root.transform.localScale = Vector3.one * scale;
        root.transform.localPosition = new Vector3(
            -bounds.center.x * scale,
            localFloor - bounds.min.y * scale,
            -bounds.center.z * scale);
    }

    private static Transform CreateMouthProxy(GameObject character)
    {
        var bounds = CalculateLocalBounds(character);
        var mouth = GameObject.CreatePrimitive(PrimitiveType.Cube);
        mouth.name = "Mouth_LipSync_Proxy_EDIT_ME";
        mouth.transform.SetParent(character.transform, false);

        // Sketchfab models face local +Z. The interviewer slot itself is rotated
        // toward the candidate, so this stays editable with the rest of the avatar.
        mouth.transform.localPosition = new Vector3(
            bounds.center.x,
            bounds.min.y + bounds.size.y * 0.865f,
            bounds.max.z + bounds.size.z * 0.006f);
        mouth.transform.localScale = new Vector3(
            bounds.size.x * 0.105f,
            bounds.size.y * 0.010f,
            Mathf.Max(bounds.size.z * 0.008f, bounds.size.x * 0.006f));

        var collider = mouth.GetComponent<Collider>();
        if (collider)
            UnityEngine.Object.DestroyImmediate(collider);

        var renderer = mouth.GetComponent<Renderer>();
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.sharedMaterial = new Material(Shader.Find("Standard"))
        {
            name = "Procedural Lip Sync Mouth",
            color = new Color(0.18f, 0.035f, 0.03f, 1f)
        };
        return mouth.transform;
    }

    private static Bounds CalculateLocalBounds(GameObject root)
    {
        bool initialized = false;
        var result = new Bounds(Vector3.zero, Vector3.zero);
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer.gameObject.name == "Mouth_LipSync_Proxy_EDIT_ME")
                continue;

            var world = renderer.bounds;
            var min = world.min;
            var max = world.max;
            for (int x = 0; x < 2; x++)
            for (int y = 0; y < 2; y++)
            for (int z = 0; z < 2; z++)
            {
                var corner = new Vector3(x == 0 ? min.x : max.x, y == 0 ? min.y : max.y, z == 0 ? min.z : max.z);
                var local = root.transform.InverseTransformPoint(corner);
                if (!initialized)
                {
                    result = new Bounds(local, Vector3.zero);
                    initialized = true;
                }
                else
                {
                    result.Encapsulate(local);
                }
            }
        }
        return result;
    }

    private static void ConfigureRenderers(GameObject root)
    {
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
        }
    }

    private readonly struct CharacterSpec
    {
        public readonly string PersonaId;
        public readonly string AssetPath;
        public readonly string SceneName;

        public CharacterSpec(string personaId, string assetPath, string sceneName)
        {
            PersonaId = personaId;
            AssetPath = assetPath;
            SceneName = sceneName;
        }
    }
}
