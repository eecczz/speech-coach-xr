using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace SpeakUpXR.Editor
{
    internal static class UrpProjectMigration
    {
        private const string PipelineFolder = "Assets/SpeakUpXR/Rendering";
        private const string PipelinePath = PipelineFolder + "/SpeakUpXR_URP.asset";
        private const string RendererPath = PipelineFolder + "/SpeakUpXR_URP_Renderer.asset";
        private const string ReportPath = "Assets/SpeakUpXR/UI/urp-migration-v1.txt";
        private const string PreviewPath = "Assets/SpeakUpXR/UI/urp-interview-preview.png";
        private const string InterviewScene = "Assets/SpeakUpXR/Scenes/Interview.unity";
        private const string UrpAssetType =
            "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset, Unity.RenderPipelines.Universal.Runtime";
        private const string RendererType =
            "UnityEngine.Rendering.Universal.UniversalRendererData, Unity.RenderPipelines.Universal.Runtime";

        [InitializeOnLoadMethod]
        private static void Schedule()
        {
            EditorApplication.delayCall += MigrateIfReady;
        }

        [MenuItem("SpeakUpXR/Rendering/프로젝트 전체를 URP로 전환")]
        private static void MigrateFromMenu() => Migrate(force: true);

        private static void MigrateIfReady()
        {
            if (File.Exists(ReportPath)) return;
            Migrate(force: false);
        }

        private static void Migrate(bool force)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            Type pipelineType = Type.GetType(UrpAssetType, false);
            Type rendererType = Type.GetType(RendererType, false);
            if (pipelineType == null || rendererType == null)
            {
                if (force) Debug.LogWarning("[SpeakUpXR] URP 17 패키지 로딩 전입니다. Package Manager 완료 후 다시 실행하세요.");
                return;
            }

            EnsureFolder("Assets/SpeakUpXR");
            EnsureFolder(PipelineFolder);
            EnsureFolder("Assets/SpeakUpXR/UI");

            ScriptableObject renderer = AssetDatabase.LoadAssetAtPath<ScriptableObject>(RendererPath);
            if (!renderer)
            {
                renderer = ScriptableObject.CreateInstance(rendererType);
                renderer.name = "SpeakUpXR URP Renderer";
                AssetDatabase.CreateAsset(renderer, RendererPath);
            }

            RenderPipelineAsset pipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(PipelinePath);
            if (!pipeline)
            {
                pipeline = (RenderPipelineAsset)ScriptableObject.CreateInstance(pipelineType);
                pipeline.name = "SpeakUpXR URP";
                AssetDatabase.CreateAsset(pipeline, PipelinePath);
            }

            ConfigurePipeline(pipeline, renderer);
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;

            int converted = ConvertProjectMaterials();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            int errorMaterials;
            string preview = RenderInterviewPreview(out errorMaterials);
            File.WriteAllText(ReportPath,
                "SpeakUpXR URP migration\n" +
                $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                "Package: com.unity.render-pipelines.universal 17.0.4\n" +
                $"PipelineAsset: {PipelinePath}\n" +
                $"RendererAsset: {RendererPath}\n" +
                "XR profile: 4x MSAA, HDR off, depth texture on, opaque texture off, SRP Batcher on\n" +
                $"ConvertedMaterials: {converted}\n" +
                $"ErrorShaderMaterialsInInterview: {errorMaterials}\n" +
                $"Preview: {preview}\n" +
                "Scope: office environment, Formal Character interviewers, SpeakUpXR generated materials\n");
            AssetDatabase.ImportAsset(ReportPath, ImportAssetOptions.ForceUpdate);
            Debug.Log($"[SpeakUpXR] URP 전환 완료. 머티리얼 {converted}개 변환, 오류 셰이더 {errorMaterials}개.");
        }

        private static void ConfigurePipeline(RenderPipelineAsset pipeline, ScriptableObject renderer)
        {
            SerializedObject serialized = new SerializedObject(pipeline);
            SetBool(serialized, "m_RequireDepthTexture", true);
            SetBool(serialized, "m_RequireOpaqueTexture", false);
            SetBool(serialized, "m_SupportsHDR", false);
            SetBool(serialized, "m_UseSRPBatcher", true);
            SetInt(serialized, "m_MSAA", 4);
            SetFloat(serialized, "m_RenderScale", 1f);
            SetInt(serialized, "m_DefaultRendererIndex", 0);
            SerializedProperty list = serialized.FindProperty("m_RendererDataList");
            if (list != null)
            {
                list.arraySize = 1;
                list.GetArrayElementAtIndex(0).objectReferenceValue = renderer;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(pipeline);
            EditorUtility.SetDirty(renderer);
        }

        private static int ConvertProjectMaterials()
        {
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
            if (!lit || !unlit) return 0;

            int converted = 0;
            string[] guids = AssetDatabase.FindAssets("t:Material", new[] { "Assets" });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.StartsWith("Assets/Convai SDK For Unity/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("Assets/TextMesh Pro/", StringComparison.OrdinalIgnoreCase))
                    continue;

                Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (!material || !material.shader) continue;
                string shaderName = material.shader.name ?? string.Empty;
                if (shaderName.StartsWith("Universal Render Pipeline/", StringComparison.OrdinalIgnoreCase)) continue;
                if (shaderName.StartsWith("VRM10/", StringComparison.OrdinalIgnoreCase) ||
                    shaderName.StartsWith("VRM/", StringComparison.OrdinalIgnoreCase))
                    continue;

                Texture mainTexture = material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
                Color color = material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
                Texture normal = material.HasProperty("_BumpMap") ? material.GetTexture("_BumpMap") : null;
                Texture emissionMap = material.HasProperty("_EmissionMap") ? material.GetTexture("_EmissionMap") : null;
                Color emission = material.HasProperty("_EmissionColor") ? material.GetColor("_EmissionColor") : Color.black;
                float metallic = material.HasProperty("_Metallic") ? material.GetFloat("_Metallic") : 0f;
                float smoothness = material.HasProperty("_Glossiness") ? material.GetFloat("_Glossiness") : 0.35f;
                bool useUnlit = shaderName.IndexOf("Unlit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                shaderName.IndexOf("Sprite", StringComparison.OrdinalIgnoreCase) >= 0;

                material.shader = useUnlit ? unlit : lit;
                if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", mainTexture);
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
                if (material.HasProperty("_BumpMap") && normal)
                {
                    material.SetTexture("_BumpMap", normal);
                    material.EnableKeyword("_NORMALMAP");
                }
                if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
                if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
                if (material.HasProperty("_EmissionMap") && emissionMap) material.SetTexture("_EmissionMap", emissionMap);
                if (material.HasProperty("_EmissionColor") && emission.maxColorComponent > 0f)
                {
                    material.SetColor("_EmissionColor", emission);
                    material.EnableKeyword("_EMISSION");
                }
                EditorUtility.SetDirty(material);
                converted++;
            }
            return converted;
        }

        private static string RenderInterviewPreview(out int errorMaterials)
        {
            errorMaterials = 0;
            Scene scene = SceneManager.GetSceneByPath(InterviewScene);
            bool close = !scene.IsValid() || !scene.isLoaded;
            if (close) scene = EditorSceneManager.OpenScene(InterviewScene, OpenSceneMode.Additive);
            try
            {
                Camera camera = null;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (!camera) camera = root.GetComponentInChildren<Camera>(true);
                    foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                        foreach (Material material in renderer.sharedMaterials)
                            if (!material || !material.shader || material.shader.name == "Hidden/InternalErrorShader")
                                errorMaterials++;
                }
                if (!camera) return "NO_CAMERA";
                var target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
                RenderTexture previous = camera.targetTexture;
                RenderTexture active = RenderTexture.active;
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                var texture = new Texture2D(1280, 720, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                texture.Apply();
                File.WriteAllBytes(PreviewPath, texture.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(texture);
                camera.targetTexture = previous;
                RenderTexture.active = active;
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
                AssetDatabase.ImportAsset(PreviewPath, ImportAssetOptions.ForceUpdate);
                return "OK";
            }
            catch (Exception exception)
            {
                return "FAILED: " + exception.GetType().Name + " " + exception.Message;
            }
            finally
            {
                if (close && scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            string name = Path.GetFileName(path);
            if (!string.IsNullOrWhiteSpace(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent ?? "Assets", name);
        }

        private static void SetBool(SerializedObject serialized, string name, bool value)
        {
            SerializedProperty property = serialized.FindProperty(name);
            if (property != null) property.boolValue = value;
        }

        private static void SetInt(SerializedObject serialized, string name, int value)
        {
            SerializedProperty property = serialized.FindProperty(name);
            if (property != null) property.intValue = value;
        }

        private static void SetFloat(SerializedObject serialized, string name, float value)
        {
            SerializedProperty property = serialized.FindProperty(name);
            if (property != null) property.floatValue = value;
        }
    }
}
