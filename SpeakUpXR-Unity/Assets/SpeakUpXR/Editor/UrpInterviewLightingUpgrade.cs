using System;
using System.IO;
using SpeakUpXR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace SpeakUpXR.Editor
{
    /// <summary>
    /// Adds a compact, scene-authored URP lighting rig around the user's existing panel layout.
    /// Every light remains visible and editable in the Interview scene hierarchy.
    /// </summary>
    internal static class UrpInterviewLightingUpgrade
    {
        private const string ScenePath = "Assets/SpeakUpXR/Scenes/Interview.unity";
        private const string RootName = "URP_InterviewLighting_EDIT_HERE";
        private const string ReportPath = "Assets/SpeakUpXR/UI/urp-lighting-v3.txt";
        private const string PreviewPath = "Assets/SpeakUpXR/UI/urp-interview-preview-v3.png";
        private static bool running;

        [InitializeOnLoadMethod]
        private static void Schedule()
        {
            EditorApplication.delayCall -= UpgradeIfReady;
            EditorApplication.delayCall += UpgradeIfReady;
        }

        [MenuItem("SpeakUpXR/Rendering/면접실 URP 조명 다시 맞추기")]
        private static void UpgradeFromMenu()
        {
            if (File.Exists(ReportPath)) File.Delete(ReportPath);
            UpgradeIfReady();
        }

        private static void UpgradeIfReady()
        {
            if (running || File.Exists(ReportPath) || EditorApplication.isCompiling ||
                EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            if (GraphicsSettings.defaultRenderPipeline == null ||
                AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
                return;

            running = true;
            Scene previous = SceneManager.GetActiveScene();
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            bool close = !scene.IsValid() || !scene.isLoaded;
            try
            {
                if (close) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
                SceneManager.SetActiveScene(scene);

                InterviewerPanel panel = FindInScene<InterviewerPanel>(scene);
                Camera camera = FindInScene<Camera>(scene);
                if (!panel || !camera) throw new InvalidOperationException("Interview panel or camera is missing.");

                Vector3 panelCenter = ComputePanelCenter(panel);
                Vector3 candidateEye = camera.transform.position;
                Vector3 towardPanel = panelCenter - candidateEye;
                if (towardPanel.sqrMagnitude < 0.01f) towardPanel = camera.transform.forward;
                towardPanel.Normalize();

                GameObject root = FindRoot(scene, RootName) ?? new GameObject(RootName);
                SceneManager.MoveGameObjectToScene(root, scene);
                ClearChildren(root.transform);

                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.21f, 0.23f, 0.27f, 1f);
                RenderSettings.reflectionIntensity = 0.55f;

                Light key = CreateLight(root.transform, "Panel_Key_Directional_EDIT_HERE", LightType.Directional);
                key.transform.rotation = Quaternion.Euler(42f, -32f, 0f);
                key.color = new Color(1f, 0.95f, 0.88f);
                key.intensity = 0.72f;
                key.shadows = LightShadows.Soft;
                key.shadowStrength = 0.7f;

                Light fill = CreateLight(root.transform, "Candidate_FrontFill_EDIT_HERE", LightType.Spot);
                fill.transform.position = candidateEye + Vector3.up * 0.18f - towardPanel * 0.1f;
                fill.transform.rotation = Quaternion.LookRotation(panelCenter + Vector3.up * 0.1f - fill.transform.position);
                fill.color = new Color(0.86f, 0.92f, 1f);
                fill.intensity = 1.35f;
                fill.range = Mathf.Max(7f, Vector3.Distance(candidateEye, panelCenter) + 3f);
                fill.spotAngle = 78f;
                fill.innerSpotAngle = 45f;
                fill.shadows = LightShadows.None;

                Light ceiling = CreateLight(root.transform, "Panel_CeilingFill_EDIT_HERE", LightType.Point);
                ceiling.transform.position = panelCenter + Vector3.up * 1.8f - towardPanel * 0.35f;
                ceiling.color = new Color(1f, 0.93f, 0.84f);
                ceiling.intensity = 0.65f;
                ceiling.range = 5.5f;
                ceiling.shadows = LightShadows.None;

                camera.allowHDR = false;
                camera.allowMSAA = true;
                EditorUtility.SetDirty(camera);
                EditorUtility.SetDirty(root);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);

                string renderResult = Render(scene, camera);
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath) ?? "Assets/SpeakUpXR/UI");
                File.WriteAllText(ReportPath,
                    "SpeakUpXR URP interview lighting\n" +
                    $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                    $"SceneRoot: {RootName}\n" +
                    "Ambient: neutral flat 0.21/0.23/0.27\n" +
                    "Lights: directional key + candidate front fill + panel ceiling fill\n" +
                    $"Preview: {renderResult}\n" +
                    "All lights are scene-authored and inspector-editable.\n");
                AssetDatabase.ImportAsset(ReportPath, ImportAssetOptions.ForceUpdate);
                Debug.Log("[SpeakUpXR] URP 면접실 조명 보정과 미리보기 생성을 완료했습니다.");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                if (close && scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
                running = false;
            }
        }

        private static T FindInScene<T>(Scene scene) where T : Component
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                T found = root.GetComponentInChildren<T>(true);
                if (found) return found;
            }
            return null;
        }

        private static Vector3 ComputePanelCenter(InterviewerPanel panel)
        {
            Vector3 total = Vector3.zero;
            int count = 0;
            if (panel.Members != null)
            {
                foreach (InterviewerController member in panel.Members)
                {
                    if (!member) continue;
                    Transform target = member.GazePoint ? member.GazePoint : member.transform;
                    total += target.position;
                    count++;
                }
            }
            return count > 0 ? total / count : panel.transform.position + Vector3.up * 1.3f;
        }

        private static GameObject FindRoot(Scene scene, string name)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
                if (root.name == name) return root;
            return null;
        }

        private static void ClearChildren(Transform root)
        {
            for (int i = root.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(root.GetChild(i).gameObject);
        }

        private static Light CreateLight(Transform parent, string name, LightType type)
        {
            GameObject go = new GameObject(name, typeof(Light));
            go.transform.SetParent(parent, false);
            Light light = go.GetComponent<Light>();
            light.type = type;
            light.cullingMask = ~0;
            return light;
        }

        private static string Render(Scene scene, Camera camera)
        {
            RenderTexture target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                Texture2D texture = new Texture2D(1280, 720, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                texture.Apply();
                File.WriteAllBytes(PreviewPath, texture.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(texture);
                AssetDatabase.ImportAsset(PreviewPath, ImportAssetOptions.ForceUpdate);
                return "OK";
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }
}
