using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SpeakUpXR.Editor
{
    internal static class ConvaiSandboxInstaller
    {
        private const string SourceScene =
            "Assets/Convai SDK For Unity/Samples/LipSyncSample/Scenes/LipSync Sample.unity";
        private const string TargetScene = "Assets/SpeakUpXR/Scenes/ConvaiSandbox.unity";
        private const string ReportPath = "Assets/SpeakUpXR/UI/convai-sandbox-validation-v1.txt";
        private const string PreviewPath = "Assets/SpeakUpXR/UI/convai-sandbox-preview.png";
        private const string ConvertedMaterialFolder = "Assets/SpeakUpXR/Materials/ConvaiSandbox";
        private const string PlayerTypeName = "Convai.Runtime.Components.ConvaiPlayer";
        private const string CharacterTypeName = "Convai.Runtime.Components.ConvaiCharacter";
        private const string ManagerTypeName = "Convai.Runtime.Components.ConvaiManager";
        private const string RoomManagerTypeName = "Convai.Runtime.Adapters.Networking.ConvaiRoomManager";

        [InitializeOnLoadMethod]
        private static void ScheduleInitialInstall()
        {
            EditorApplication.delayCall += EnsureSandboxIfNeeded;
        }

        [MenuItem("SpeakUpXR/Convai/시험 씬 생성 또는 검증")]
        private static void EnsureSandboxFromMenu()
        {
            EnsureSandbox(forceValidate: true);
        }

        private static void EnsureSandboxIfNeeded()
        {
            EnsureSandbox(forceValidate: true);
        }

        private static void EnsureSandbox(bool forceValidate)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SourceScene) == null) return;

            bool targetExists = AssetDatabase.LoadAssetAtPath<SceneAsset>(TargetScene) != null;
            if (!targetExists)
            {
                if (!AssetDatabase.CopyAsset(SourceScene, TargetScene))
                {
                    Debug.LogError($"[ConvaiSandboxInstaller] 시험 씬 복사 실패: {SourceScene}");
                    return;
                }

                AssetDatabase.ImportAsset(TargetScene, ImportAssetOptions.ForceUpdate);
            }
            else if (!forceValidate)
            {
                return;
            }

            ConfigureSandboxScene(targetExists ? "기존 시험 씬 검증" : "새 시험 씬 생성");
        }

        private static void ConfigureSandboxScene(string operation)
        {
            Scene sandbox = EditorSceneManager.OpenScene(TargetScene, OpenSceneMode.Additive);
            try
            {
                MonoBehaviour player = FindBehaviour(sandbox, PlayerTypeName);
                MonoBehaviour character = FindBehaviour(sandbox, CharacterTypeName);
                MonoBehaviour manager = FindBehaviour(sandbox, ManagerTypeName);
                MonoBehaviour roomManager = FindBehaviour(sandbox, RoomManagerTypeName);

                ConfigureSpeechOnlySandbox(manager, roomManager, sandbox);

                if (character != null)
                {
                    GameObject characterRoot = character.gameObject;
                    characterRoot.name = "Convai_Sofia_Interviewer_SANDBOX";
                    ConfigureSandboxCharacter(character);
                }

                string renderSetupResult = PrepareBuiltInSandboxRendering(sandbox, character);

                GameObject host = FindRoot(sandbox, "SpeakUpXR Convai Sandbox Bridge");
                if (host == null)
                {
                    host = new GameObject("SpeakUpXR Convai Sandbox Bridge");
                    SceneManager.MoveGameObjectToScene(host, sandbox);
                }

                ConvaiSandboxProbe probe = host.GetComponent<ConvaiSandboxProbe>();
                if (probe == null) probe = host.AddComponent<ConvaiSandboxProbe>();

                SerializedObject serializedProbe = new SerializedObject(probe);
                serializedProbe.FindProperty("convaiPlayer").objectReferenceValue = player;
                serializedProbe.FindProperty("convaiCharacter").objectReferenceValue = character;
                serializedProbe.ApplyModifiedPropertiesWithoutUndo();

                EditorSceneManager.MarkSceneDirty(sandbox);
                EditorSceneManager.SaveScene(sandbox);

                string characterId = ReadStringProperty(character, "CharacterId");
                int skinnedMeshCount = 0;
                int blendShapeCount = 0;
                int lipSyncComponentCount = 0;
                int emotionComponentCount = 0;
                int gazeComponentCount = 0;
                CountCharacterFeatures(
                    character,
                    ref skinnedMeshCount,
                    ref blendShapeCount,
                    ref lipSyncComponentCount,
                    ref emotionComponentCount,
                    ref gazeComponentCount);
                string previewResult = RenderScenePreview(sandbox);
                string report =
                    "Convai 4.5.0 Sandbox validation\n" +
                    $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                    $"Operation: {operation}\n" +
                    $"SourceScene: {SourceScene}\n" +
                    $"TargetScene: {TargetScene}\n" +
                    $"ConvaiPlayer: {(player != null ? "FOUND" : "MISSING")}\n" +
                    $"ConvaiCharacter: {(character != null ? "FOUND" : "MISSING")}\n" +
                    $"CharacterId: {characterId}\n" +
                    "IncludedCharacter: Sofia (female only)\n" +
                    $"SkinnedMeshes: {skinnedMeshCount}\n" +
                    $"FacialBlendShapes: {blendShapeCount}\n" +
                    $"LipSyncComponents: {lipSyncComponentCount}\n" +
                    $"EmotionComponents: {emotionComponentCount}\n" +
                    $"GazeComponents: {gazeComponentCount}\n" +
                    $"BuiltInRenderSetup: {renderSetupResult}\n" +
                    $"PreviewRender: {previewResult}\n" +
                    "ExactScriptedSpeech: ConvaiCharacter.SendNarrativeSpeech(string) FOUND\n" +
                    "ExactSpeechResult: dashboard Korean voice + remote audio + transcript + emotion + lip-sync stream\n" +
                    "GeneratedConversationRoute: ConvaiPlayer.SendTextMessage(string)\n" +
                    "RequiredToRun: Convai API key and network connection\n";

                File.WriteAllText(ReportPath, report);
                AssetDatabase.ImportAsset(ReportPath, ImportAssetOptions.ForceUpdate);
                if (File.Exists(PreviewPath))
                    AssetDatabase.ImportAsset(PreviewPath, ImportAssetOptions.ForceUpdate);
                Debug.Log($"[ConvaiSandboxInstaller] {operation} 완료: {TargetScene}");
            }
            finally
            {
                EditorSceneManager.CloseScene(sandbox, removeScene: true);
            }
        }

        private static MonoBehaviour FindBehaviour(Scene scene, string typeName)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                MonoBehaviour[] behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
                foreach (MonoBehaviour behaviour in behaviours)
                {
                    if (behaviour != null && behaviour.GetType().FullName == typeName)
                        return behaviour;
                }
            }

            return null;
        }

        private static GameObject FindRoot(Scene scene, string name)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == name) return root;
            }

            return null;
        }

        private static string ReadStringProperty(MonoBehaviour behaviour, string propertyName)
        {
            if (behaviour == null) return string.Empty;
            PropertyInfo property = behaviour.GetType().GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance);
            return property?.GetValue(behaviour) as string ?? string.Empty;
        }

        private static void ConfigureSandboxCharacter(MonoBehaviour character)
        {
            const string kimMinseoId = "e41f5270-7378-478f-b037-391f0be4a269";
            MethodInfo configure = character.GetType().GetMethod(
                "Configure",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(string) },
                null);
            configure?.Invoke(character, new object[] { kimMinseoId, "Kim Minseo" });

            SerializedObject serialized = new SerializedObject(character);
            SerializedProperty id = serialized.FindProperty("_characterId");
            SerializedProperty name = serialized.FindProperty("_characterName");
            SerializedProperty autoConnect = serialized.FindProperty("_autoConnect");
            SerializedProperty remoteAudio = serialized.FindProperty("_enableRemoteAudio");
            if (id != null) id.stringValue = kimMinseoId;
            if (name != null) name.stringValue = "Kim Minseo";
            if (autoConnect != null) autoConnect.boolValue = true;
            if (remoteAudio != null) remoteAudio.boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureSpeechOnlySandbox(
            MonoBehaviour manager,
            MonoBehaviour roomManager,
            Scene sandbox)
        {
            // Exact authored speech needs downstream audio, transcript, emotion and visemes,
            // but it does not need to publish a local microphone or camera frame. Push-to-talk
            // with OpenOnFirstPress prevents the SDK from prewarming a microphone on connect.
            if (manager != null)
            {
                SerializedObject serialized = new SerializedObject(manager);
                SerializedProperty mode = serialized.FindProperty("_conversationMode");
                if (mode != null) mode.enumValueIndex = 2; // PushToTalk
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            if (roomManager != null)
            {
                SerializedObject serialized = new SerializedObject(roomManager);
                SerializedProperty options = serialized.FindProperty("_turnTakingOptions");
                SerializedProperty mode = options?.FindPropertyRelative("<Mode>k__BackingField");
                if (mode != null) mode.enumValueIndex = 1; // PushToTalk
                SerializedProperty audio = options?.FindPropertyRelative("<LocalAudioPolicy>k__BackingField");
                SerializedProperty startup = audio?.FindPropertyRelative("<PushToTalkStartupMode>k__BackingField");
                if (startup != null) startup.enumValueIndex = 1; // OpenOnFirstPress
                SerializedProperty muted = audio?.FindPropertyRelative("<StartMutedInPushToTalk>k__BackingField");
                if (muted != null) muted.boolValue = true;
                SerializedProperty vision = serialized.FindProperty("_visionContextMode");
                if (vision != null) vision.enumValueIndex = 0;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            foreach (GameObject root in sandbox.GetRootGameObjects())
            {
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null) continue;
                    string type = behaviour.GetType().FullName ?? string.Empty;
                    if (type.IndexOf("VisionFrameSource", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        type.IndexOf("VisionPublisher", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        behaviour.enabled = false;
                        EditorUtility.SetDirty(behaviour);
                    }
                }
            }
        }

        private static void CountCharacterFeatures(
            MonoBehaviour character,
            ref int skinnedMeshCount,
            ref int blendShapeCount,
            ref int lipSyncComponentCount,
            ref int emotionComponentCount,
            ref int gazeComponentCount)
        {
            if (character == null) return;

            SkinnedMeshRenderer[] renderers = character.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            skinnedMeshCount = renderers.Length;
            foreach (SkinnedMeshRenderer renderer in renderers)
            {
                if (renderer != null && renderer.sharedMesh != null)
                    blendShapeCount += renderer.sharedMesh.blendShapeCount;
            }

            MonoBehaviour[] behaviours = character.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour == null) continue;
                string fullName = behaviour.GetType().FullName ?? string.Empty;
                if (fullName.IndexOf("LipSync", StringComparison.OrdinalIgnoreCase) >= 0)
                    lipSyncComponentCount++;
                if (fullName.IndexOf("Emotion", StringComparison.OrdinalIgnoreCase) >= 0)
                    emotionComponentCount++;
                if (fullName.IndexOf("Gaze", StringComparison.OrdinalIgnoreCase) >= 0)
                    gazeComponentCount++;
            }
        }

        private static string RenderScenePreview(Scene scene)
        {
            Camera camera = null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                Camera candidate = root.GetComponentInChildren<Camera>(true);
                if (candidate != null && candidate.name == "SpeakUpXR Sandbox Camera")
                {
                    camera = candidate;
                    break;
                }

                if (camera == null && candidate != null && candidate.enabled)
                    camera = candidate;
            }

            if (camera == null) return "SKIPPED_NO_CAMERA";

            RenderTexture previousActive = RenderTexture.active;
            RenderTexture previousTarget = camera.targetTexture;
            var renderTexture = new RenderTexture(960, 540, 24, RenderTextureFormat.ARGB32);
            var texture = new Texture2D(960, 540, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = renderTexture;
                RenderTexture.active = renderTexture;
                camera.Render();
                texture.ReadPixels(new Rect(0, 0, 960, 540), 0, 0);
                texture.Apply();
                File.WriteAllBytes(PreviewPath, texture.EncodeToPNG());
                return "OK";
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[ConvaiSandboxInstaller] 프리뷰 렌더 실패: {exception.Message}");
                return $"FAILED_{exception.GetType().Name}";
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        private static string PrepareBuiltInSandboxRendering(Scene scene, MonoBehaviour character)
        {
            if (character == null) return "SKIPPED_NO_CHARACTER";

            bool urp = UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline != null;
            Shader standardShader = Shader.Find(urp ? "Universal Render Pipeline/Lit" : "Standard");
            if (standardShader == null) return urp ? "FAILED_URP_LIT_SHADER_MISSING" : "FAILED_STANDARD_SHADER_MISSING";

            EnsureAssetFolder("Assets/SpeakUpXR/Materials");
            EnsureAssetFolder(ConvertedMaterialFolder);

            Transform characterTransform = character.transform;
            var converted = new Dictionary<Material, Material>();
            Renderer[] characterRenderers = character.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in characterRenderers)
            {
                Renderer prefabRenderer = PrefabUtility.GetCorrespondingObjectFromSource(renderer);
                Material[] sourceMaterials = prefabRenderer != null
                    ? prefabRenderer.sharedMaterials
                    : renderer.sharedMaterials;
                var targetMaterials = new Material[sourceMaterials.Length];
                for (int i = 0; i < sourceMaterials.Length; i++)
                    targetMaterials[i] = ConvertMaterial(sourceMaterials[i], standardShader, converted);
                renderer.sharedMaterials = targetMaterials;
                renderer.enabled = true;
            }

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (!renderer.transform.IsChildOf(characterTransform)) renderer.enabled = false;
                }

                foreach (Canvas canvas in root.GetComponentsInChildren<Canvas>(true))
                    canvas.gameObject.SetActive(false);

                foreach (Light light in root.GetComponentsInChildren<Light>(true))
                    light.enabled = false;

                foreach (Camera camera in root.GetComponentsInChildren<Camera>(true))
                    camera.enabled = false;
            }

            Bounds bounds = CalculateBounds(characterRenderers, characterTransform.position);
            Camera sandboxCamera = EnsureSandboxCamera(scene, bounds);
            EnsureSandboxLights(scene, bounds);
            sandboxCamera.enabled = true;

            return $"OK_{converted.Count}_MATERIALS";
        }

        private static Material ConvertMaterial(
            Material source,
            Shader standardShader,
            IDictionary<Material, Material> cache)
        {
            if (source == null) return null;
            if (cache.TryGetValue(source, out Material cached)) return cached;

            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long localId);
            string shortGuid = string.IsNullOrEmpty(guid) ? "runtime" : guid.Substring(0, Math.Min(8, guid.Length));
            string safeName = SanitizeFileName(source.name);
            string path = $"{ConvertedMaterialFolder}/{safeName}_{shortGuid}_{localId}.mat";
            Material target = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (target == null)
            {
                target = new Material(standardShader) { name = $"{source.name}_BuiltIn" };
                AssetDatabase.CreateAsset(target, path);
            }
            else
            {
                target.shader = standardShader;
            }

            Texture albedo = FindTexture(source, "_BaseMap", "_MainTex", "_DiffuseMap", "_BaseColorMap");
            Texture normal = FindTexture(source, "_BumpMap", "_NormalMap", "_NormalTex");
            Texture metallic = FindTexture(source, "_MetallicGlossMap", "_MaskMap");
            Texture emission = FindTexture(source, "_EmissionMap", "_EmissiveColorMap");
            Color color = FindColor(source, Color.white, "_BaseColor", "_Color", "_DiffuseColor");

            target.SetColor("_Color", color);
            if (albedo != null) target.SetTexture("_MainTex", albedo);
            if (normal != null)
            {
                target.SetTexture("_BumpMap", normal);
                target.EnableKeyword("_NORMALMAP");
            }
            if (metallic != null)
            {
                target.SetTexture("_MetallicGlossMap", metallic);
                target.EnableKeyword("_METALLICGLOSSMAP");
            }
            if (emission != null)
            {
                target.SetTexture("_EmissionMap", emission);
                target.SetColor("_EmissionColor", Color.white * 0.2f);
                target.EnableKeyword("_EMISSION");
            }

            if (source.HasProperty("_Smoothness")) target.SetFloat("_Glossiness", source.GetFloat("_Smoothness"));
            else if (source.HasProperty("_Glossiness")) target.SetFloat("_Glossiness", source.GetFloat("_Glossiness"));
            if (source.HasProperty("_Metallic")) target.SetFloat("_Metallic", source.GetFloat("_Metallic"));
            if (source.HasProperty("_Cutoff")) target.SetFloat("_Cutoff", source.GetFloat("_Cutoff"));

            EditorUtility.SetDirty(target);
            cache[source] = target;
            return target;
        }

        private static Texture FindTexture(Material material, params string[] propertyNames)
        {
            foreach (string propertyName in propertyNames)
            {
                if (!material.HasProperty(propertyName)) continue;
                Texture texture = material.GetTexture(propertyName);
                if (texture != null) return texture;
            }

            // Shader Graph/URP properties can be unavailable through Material.HasProperty when the
            // project currently runs the Built-in pipeline. Read the serialized material payload.
            SerializedObject serialized = new SerializedObject(material);
            SerializedProperty textureEnvironments = serialized.FindProperty("m_SavedProperties.m_TexEnvs");
            if (textureEnvironments != null && textureEnvironments.isArray)
            {
                foreach (string propertyName in propertyNames)
                {
                    for (int i = 0; i < textureEnvironments.arraySize; i++)
                    {
                        SerializedProperty entry = textureEnvironments.GetArrayElementAtIndex(i);
                        SerializedProperty key = entry.FindPropertyRelative("first");
                        if (key == null || key.stringValue != propertyName) continue;
                        SerializedProperty value = entry.FindPropertyRelative("second.m_Texture");
                        if (value?.objectReferenceValue is Texture texture) return texture;
                    }
                }
            }

            return null;
        }

        private static Color FindColor(Material material, Color fallback, params string[] propertyNames)
        {
            foreach (string propertyName in propertyNames)
            {
                if (material.HasProperty(propertyName)) return material.GetColor(propertyName);
            }

            SerializedObject serialized = new SerializedObject(material);
            SerializedProperty colors = serialized.FindProperty("m_SavedProperties.m_Colors");
            if (colors != null && colors.isArray)
            {
                foreach (string propertyName in propertyNames)
                {
                    for (int i = 0; i < colors.arraySize; i++)
                    {
                        SerializedProperty entry = colors.GetArrayElementAtIndex(i);
                        SerializedProperty key = entry.FindPropertyRelative("first");
                        if (key == null || key.stringValue != propertyName) continue;
                        SerializedProperty value = entry.FindPropertyRelative("second");
                        if (value != null) return value.colorValue;
                    }
                }
            }

            return fallback;
        }

        private static Bounds CalculateBounds(Renderer[] renderers, Vector3 fallbackCenter)
        {
            bool found = false;
            Bounds bounds = new Bounds(fallbackCenter, Vector3.one * 0.1f);
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                if (!found)
                {
                    bounds = renderer.bounds;
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return bounds;
        }

        private static Camera EnsureSandboxCamera(Scene scene, Bounds bounds)
        {
            GameObject cameraObject = FindRoot(scene, "SpeakUpXR Sandbox Camera");
            if (cameraObject == null)
            {
                cameraObject = new GameObject("SpeakUpXR Sandbox Camera");
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
            }

            Camera camera = cameraObject.GetComponent<Camera>();
            if (camera == null) camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.055f, 0.067f, 0.09f, 1f);
            camera.fieldOfView = 34f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 100f;

            float verticalDistance = bounds.extents.y / Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float horizontalDistance = bounds.extents.x / Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad * (16f / 9f));
            float distance = Mathf.Max(verticalDistance, horizontalDistance) + Mathf.Max(0.35f, bounds.extents.z);
            Vector3 target = bounds.center + Vector3.up * bounds.extents.y * 0.02f;
            camera.transform.position = target - Vector3.forward * distance;
            camera.transform.rotation = Quaternion.LookRotation(target - camera.transform.position, Vector3.up);
            return camera;
        }

        private static void EnsureSandboxLights(Scene scene, Bounds bounds)
        {
            GameObject keyObject = FindRoot(scene, "SpeakUpXR Sandbox Key Light");
            if (keyObject == null)
            {
                keyObject = new GameObject("SpeakUpXR Sandbox Key Light");
                SceneManager.MoveGameObjectToScene(keyObject, scene);
            }

            Light key = keyObject.GetComponent<Light>();
            if (key == null) key = keyObject.AddComponent<Light>();
            key.type = LightType.Directional;
            key.color = new Color(1f, 0.92f, 0.82f);
            key.intensity = 1.25f;
            key.transform.rotation = Quaternion.Euler(38f, -28f, 0f);
            key.enabled = true;

            GameObject fillObject = FindRoot(scene, "SpeakUpXR Sandbox Fill Light");
            if (fillObject == null)
            {
                fillObject = new GameObject("SpeakUpXR Sandbox Fill Light");
                SceneManager.MoveGameObjectToScene(fillObject, scene);
            }

            Light fill = fillObject.GetComponent<Light>();
            if (fill == null) fill = fillObject.AddComponent<Light>();
            fill.type = LightType.Point;
            fill.color = new Color(0.6f, 0.75f, 1f);
            fill.intensity = 1.7f;
            fill.range = Mathf.Max(4f, bounds.size.magnitude * 2f);
            fill.transform.position = bounds.center + new Vector3(-1.5f, 0.7f, 1.3f);
            fill.enabled = true;
        }

        private static void EnsureAssetFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            string name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name)) return;
            EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Material";
            foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
            return value.Replace('/', '_').Replace('\\', '_');
        }
    }
}
