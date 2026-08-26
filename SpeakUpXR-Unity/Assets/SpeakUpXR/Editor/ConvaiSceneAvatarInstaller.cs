using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SpeakUpXR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SpeakUpXR.Editor
{
    /// <summary>
    /// Places three complete, facially-rigged Convai character instances directly in
    /// Interview.unity. The previous Formal Character visuals are retained as inactive
    /// scene children. Nothing is spawned or positioned at gameplay runtime.
    /// </summary>
    // Scene installation revision 9: official Convai CC face rigs (2 male / 1 female).
    internal static class ConvaiSceneAvatarInstaller
    {
        private const string InterviewScene = "Assets/SpeakUpXR/Scenes/Interview.unity";
        private const string SourceScene =
            "Assets/Convai SDK For Unity/Samples/LipSyncSample/Scenes/LipSync Sample.unity";
        private const string ReportPath = "Assets/SpeakUpXR/UI/convai-scene-avatar-install-v1.txt";
        private const string CharacterTypeName = "Convai.Runtime.Components.ConvaiCharacter";
        private const string ManagerTypeName = "Convai.Runtime.Components.ConvaiManager";
        private const string RoomManagerTypeName = "Convai.Runtime.Adapters.Networking.ConvaiRoomManager";
        private const string OfficialMaterialRoot =
            "Assets/ThirdParty/ConvaiOfficialSamples/GeneratedURPMaterials";

        private static readonly string[] CharacterIds =
        {
            "e41f5270-7378-478f-b037-391f0be4a269",
            "07463148-4a9b-42e6-823b-a4eb0950ae65",
            "e25f1f92-4c46-4360-b975-889b0de98e18",
        };

        private static readonly string[] CharacterNames =
        {
            "Kim Minseo",
            "Lee Junho",
            "Park Seongho",
        };

        private static readonly string[] VisualPaths =
        {
            "Assets/ThirdParty/ConvaiOfficialSamples/ChristinaSmith/Christina_Smith.Fbx",
            "Assets/ThirdParty/ConvaiOfficialSamples/KevinShaw/Kevin_Shaw.Fbx",
            "Assets/ThirdParty/ConvaiOfficialSamples/KevinShaw/Kevin_Shaw.Fbx",
        };

        private static readonly string[] TextureRoots =
        {
            "Assets/ThirdParty/ConvaiOfficialSamples/ChristinaSmith/Christina_Smith.fbm",
            "Assets/ThirdParty/ConvaiOfficialSamples/KevinShaw/Kevin_Shaw.fbm",
            "Assets/ThirdParty/ConvaiOfficialSamples/KevinShaw/Kevin_Shaw.fbm",
        };

        [InitializeOnLoadMethod]
        private static void ScheduleInstall()
        {
            EditorApplication.delayCall += InstallIfNeeded;
        }

        [MenuItem("SpeakUpXR/Convai/실제 면접 씬에 얼굴 리그 3인 배치")]
        private static void InstallFromMenu() => Install(force: true);

        private static void InstallIfNeeded()
        {
            if (File.Exists(ReportPath) &&
                File.ReadAllText(ReportPath).Contains("ValidationRevision: 9")) return;
            Install(force: false);
        }

        private static void Install(bool force)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            if (EnsureHumanoidVisualImports())
            {
                EditorApplication.delayCall += InstallIfNeeded;
                return;
            }
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(InterviewScene) == null ||
                AssetDatabase.LoadAssetAtPath<SceneAsset>(SourceScene) == null)
            {
                if (force) Debug.LogWarning("[SpeakUpXR] Interview 또는 Convai LipSync Sample 씬이 없습니다.");
                return;
            }

            Type characterType = ResolveType(CharacterTypeName);
            if (characterType == null)
            {
                if (force) Debug.LogWarning("[SpeakUpXR] ConvaiCharacter 타입을 찾지 못했습니다.");
                return;
            }

            Scene interview = SceneManager.GetSceneByPath(InterviewScene);
            bool closeInterview = !interview.IsValid() || !interview.isLoaded;
            if (closeInterview) interview = EditorSceneManager.OpenScene(InterviewScene, OpenSceneMode.Additive);
            Scene source = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Additive);

            try
            {
                Component sourceCharacter = FindBehaviour(source, CharacterTypeName);
                if (!sourceCharacter)
                    throw new InvalidOperationException("Convai LipSync Sample의 얼굴 리그 캐릭터를 찾지 못했습니다.");

                InterviewerPanel panel = FindComponent<InterviewerPanel>(interview);
                if (!panel || panel.Members == null || panel.Members.Length < 3)
                    throw new InvalidOperationException("Interview 씬의 3인 InterviewerPanel 구성이 없습니다.");

                RuntimeAnimatorController sharedSeatedController = panel.Members
                    .Where(item => item && item.CharacterAnimator && item.CharacterAnimator.runtimeAnimatorController)
                    .Select(item => item.CharacterAnimator.runtimeAnimatorController)
                    .FirstOrDefault();

                var installedCharacters = new List<Component>();
                var report = new List<string>
                {
                    "Convai face-rigged scene avatar installation",
                    $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    "SceneAuthored: YES (no runtime character spawning)",
                    "LegacyFormalCharacters: PRESERVED + INACTIVE",
                    "Speech: Convai Narrative Speech / Korean dashboard voices",
                    "Face: server visemes + Convai emotion blendshape stream",
                    "CurrentLocalFaceModelInventory: official Convai CC4 female + official Convai CC4 male x2",
                    "MaleVariant: Park Seongho uses an independently authored navy clothing material set",
                    "SampleDriversNormalized: YES (Convai body graph + free conversation flow disabled)",
                    "ValidationRevision: 9",
                };

                for (int i = 0; i < 3; i++)
                {
                    InterviewerController member = panel.Members[i];
                    if (!member) throw new InvalidOperationException($"면접관 슬롯 {i + 1}이 비어 있습니다.");

                    RuntimeAnimatorController seatedController = member.CharacterAnimator
                        ? member.CharacterAnimator.runtimeAnimatorController
                        : sharedSeatedController;
                    if (!seatedController) seatedController = sharedSeatedController;
                    PreserveLegacyAvatar(member);
                    RemoveDirectLegacyCharacterComponent(member, characterType);

                    GameObject avatar = UnityEngine.Object.Instantiate(sourceCharacter.gameObject);
                    avatar.name = $"Convai_{CharacterNames[i].Replace(" ", "_")}_FACE_RIG_ACTIVE";
                    avatar.transform.SetParent(member.transform, false);
                    avatar.transform.localPosition = Vector3.zero;
                    avatar.transform.localRotation = Quaternion.identity;
                    avatar.transform.localScale = Vector3.one;
                    avatar.SetActive(true);

                    GameObject visualPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(VisualPaths[i]);
                    if (!visualPrefab)
                        throw new InvalidOperationException($"Convai 얼굴 타깃 모델을 찾지 못했습니다: {VisualPaths[i]}");
                    GameObject visual = (GameObject)PrefabUtility.InstantiatePrefab(visualPrefab, avatar.transform);
                    visual.name = $"{CharacterNames[i].Replace(" ", "_")}_BUSINESS_VISUAL";
                    visual.transform.localPosition = Vector3.zero;
                    visual.transform.localRotation = Quaternion.identity;
                    visual.transform.localScale = Vector3.one;
                    visual.SetActive(true);
                    ApplyOfficialUrpMaterials(visual, i);

                    Renderer[] visualRenderers = visual.GetComponentsInChildren<Renderer>(true);
                    var visualRendererSet = new HashSet<Renderer>(visualRenderers);
                    foreach (Renderer sourceRenderer in avatar.GetComponentsInChildren<Renderer>(true))
                        if (!visualRendererSet.Contains(sourceRenderer)) sourceRenderer.enabled = false;

                    Component character = FindComponentByType(avatar, characterType);
                    if (!character) throw new InvalidOperationException($"{avatar.name}에 ConvaiCharacter가 없습니다.");
                    ConfigureCharacter(character, CharacterIds[i], CharacterNames[i]);
                    installedCharacters.Add(character);
                    DisableConflictingSampleDrivers(avatar);

                    Animator animator = visual.GetComponentInChildren<Animator>(true);
                    if (!animator || !animator.avatar || !animator.avatar.isValid || !animator.avatar.isHuman)
                        throw new InvalidOperationException($"{avatar.name}의 Humanoid Animator가 유효하지 않습니다.");
                    if (seatedController) animator.runtimeAnimatorController = seatedController;
                    animator.applyRootMotion = false;
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    foreach (Animator sourceAnimator in avatar.GetComponentsInChildren<Animator>(true))
                        if (sourceAnimator != animator) sourceAnimator.enabled = false;
                    ConfigureLipSyncTargets(avatar, visualRenderers.OfType<SkinnedMeshRenderer>().ToArray());

                    InterviewerHeadTracker tracker = animator.GetComponent<InterviewerHeadTracker>() ??
                                                     animator.gameObject.AddComponent<InterviewerHeadTracker>();
                    tracker.Animator = animator;
                    tracker.Target = member.LookTarget;
                    tracker.AvatarFacingRoot = avatar.transform;
                    tracker.LockEveryFrame = true;
                    tracker.Rebind();

                    foreach (AudioSource sourceAudio in avatar.GetComponentsInChildren<AudioSource>(true))
                    {
                        sourceAudio.spatialBlend = 1f;
                        sourceAudio.rolloffMode = AudioRolloffMode.Linear;
                        sourceAudio.minDistance = 0.6f;
                        sourceAudio.maxDistance = 8f;
                    }

                    ConvaiInterviewerBridge bridge = member.GetComponent<ConvaiInterviewerBridge>();
                    if (!bridge) bridge = member.gameObject.AddComponent<ConvaiInterviewerBridge>();
                    bridge.CharacterId = CharacterIds[i];
                    bridge.CharacterName = CharacterNames[i];
                    bridge.UseConvaiSpeech = true;
                    SerializedObject bridgeObject = new SerializedObject(bridge);
                    SerializedProperty bridgeCharacter = bridgeObject.FindProperty("convaiCharacter");
                    if (bridgeCharacter != null) bridgeCharacter.objectReferenceValue = character;
                    bridgeObject.ApplyModifiedPropertiesWithoutUndo();

                    member.AvatarRoot = avatar;
                    member.CharacterAnimator = animator;
                    member.PlaceholderMouth = null;
                    member.ConvaiBridge = bridge;
                    ConvaiFacialSpeechFallback facialFallback =
                        member.GetComponent<ConvaiFacialSpeechFallback>() ??
                        member.gameObject.AddComponent<ConvaiFacialSpeechFallback>();
                    facialFallback.Owner = member;
                    facialFallback.FaceRoot = visual;
                    facialFallback.Rebind();
                    EditorUtility.SetDirty(member);
                    EditorUtility.SetDirty(bridge);
                    EditorUtility.SetDirty(animator);
                    EditorUtility.SetDirty(tracker);
                    EditorUtility.SetDirty(facialFallback);

                    int blendShapes = CountBlendShapes(visual);
                    int lipSync = CountNamedComponents(avatar, "LipSync");
                    int emotion = CountNamedComponents(avatar, "Emotion");
                    int gaze = CountNamedComponents(avatar, "Gaze") + CountNamedComponents(avatar, "LookAt");
                    int missingScripts = CountMissingScripts(avatar);
                    int enabledConflicts = CountEnabledSampleDriverConflicts(avatar);
                    report.Add($"[{i + 1}] {member.DisplayName}: {CharacterNames[i]} / {CharacterIds[i]} / " +
                               $"Avatar={visual.name} / BlendShapes={blendShapes} / " +
                               $"LipSync={lipSync} / Emotion={emotion} / Gaze={gaze} / SeatedAnimator={(seatedController ? "BOUND" : "SOURCE")}");
                    report.Add($"    MissingScripts={missingScripts} / EnabledConflictingSampleDrivers={enabledConflicts} / " +
                               "TimestampedServerVisemes=BOUND / LocalExpressions=BOUND");
                }

                DisableUnusedConvaiCharacters(interview, installedCharacters, characterType);
                string managerStatus = ConfigureManager(interview, installedCharacters);
                report.Add(managerStatus);
                report.Add(NormalizeRuntimeCameras(interview));
                report.Add(NormalizeAudioListeners(interview));

                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath) ?? "Assets/SpeakUpXR/UI");
                File.WriteAllLines(ReportPath, report);
                AssetDatabase.ImportAsset(ReportPath, ImportAssetOptions.ForceUpdate);
                EditorSceneManager.MarkSceneDirty(interview);
                EditorSceneManager.SaveScene(interview);
                AssetDatabase.SaveAssets();
                Debug.Log("[SpeakUpXR] Interview 씬에 Convai 얼굴 리그 3인을 직접 배치하고 기존 정장 캐릭터를 비활성 보존했습니다.");
            }
            catch (Exception exception)
            {
                Debug.LogError("[SpeakUpXR] Convai 얼굴 리그 면접관 배치 실패: " + exception);
            }
            finally
            {
                if (source.IsValid() && source.isLoaded) EditorSceneManager.CloseScene(source, true);
                if (closeInterview && interview.IsValid() && interview.isLoaded)
                    EditorSceneManager.CloseScene(interview, true);
            }
        }

        private static bool EnsureHumanoidVisualImports()
        {
            bool changed = false;
            foreach (string path in VisualPaths)
            {
                if (AssetImporter.GetAtPath(path) is not ModelImporter importer) continue;
                bool needsReimport = importer.animationType != ModelImporterAnimationType.Human ||
                                     importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel ||
                                     !importer.importBlendShapes;
                if (!needsReimport) continue;
                importer.animationType = ModelImporterAnimationType.Human;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                importer.importBlendShapes = true;
                importer.SaveAndReimport();
                changed = true;
            }
            return changed;
        }

        private static void ApplyOfficialUrpMaterials(GameObject visual, int characterIndex)
        {
            string textureRoot = TextureRoots[characterIndex];
            bool navyVariant = characterIndex == 2;
            foreach (Renderer renderer in visual.GetComponentsInChildren<Renderer>(true))
            {
                string[] names = GetMaterialNames(renderer.gameObject.name);
                if (names == null || names.Length == 0) continue;
                var materials = new Material[names.Length];
                for (int slot = 0; slot < names.Length; slot++)
                    materials[slot] = GetOrCreateUrpMaterial(
                        names[slot], textureRoot, navyVariant && IsClothing(names[slot]));
                renderer.sharedMaterials = materials;
                EditorUtility.SetDirty(renderer);
            }
        }

        private static string[] GetMaterialNames(string rendererName)
        {
            if (rendererName.StartsWith("Fit_shirts", StringComparison.Ordinal)) return new[] { "Fit_shirts" };
            if (rendererName.StartsWith("Slim_fit_pants", StringComparison.Ordinal)) return new[] { "Slim_fit_pants" };
            if (rendererName.StartsWith("Sport_sneakers", StringComparison.Ordinal)) return new[] { "Sport_sneakers" };
            if (rendererName.StartsWith("Side_part_wavy", StringComparison.Ordinal) ||
                rendererName.StartsWith("Classic_short", StringComparison.Ordinal))
                return new[] { "Scalp_Transparency", "Hair_Transparency" };
            if (rendererName.StartsWith("Camila_Brow", StringComparison.Ordinal))
                return new[] { "Female_Brow_Transparency", "Female_Brow_Base_Transparency" };
            if (rendererName.StartsWith("Male_Brow", StringComparison.Ordinal))
                return new[] { "Male_Brow_Transparency", "Male_Brow_Base_Transparency" };
            if (rendererName.StartsWith("CC_Base_TearLine", StringComparison.Ordinal))
                return new[] { "Std_Tearline_R", "Std_Tearline_L" };
            if (rendererName.StartsWith("CC_Base_Tongue", StringComparison.Ordinal)) return new[] { "Std_Tongue" };
            if (rendererName.StartsWith("CC_Base_Teeth", StringComparison.Ordinal))
                return new[] { "Std_Upper_Teeth", "Std_Lower_Teeth" };
            if (rendererName.StartsWith("CC_Base_Body", StringComparison.Ordinal))
                return new[]
                {
                    "Std_Skin_Head", "Std_Skin_Body", "Std_Skin_Arm", "Std_Skin_Leg", "Std_Nails", "Std_Eyelash",
                };
            if (rendererName.StartsWith("CC_Base_EyeOcclusion", StringComparison.Ordinal))
                return new[] { "Std_Eye_Occlusion_R", "Std_Eye_Occlusion_L" };
            if (rendererName.StartsWith("CC_Base_Eye", StringComparison.Ordinal))
                return new[] { "Std_Eye_R", "Std_Cornea_R", "Std_Eye_L", "Std_Cornea_L" };
            if (rendererName.StartsWith("Sphere01", StringComparison.Ordinal)) return new[] { "_1_Default" };
            return null;
        }

        private static bool IsClothing(string materialName) =>
            materialName == "Fit_shirts" || materialName == "Slim_fit_pants" || materialName == "Sport_sneakers";

        private static Material GetOrCreateUrpMaterial(string materialName, string textureRoot, bool navyVariant)
        {
            string modelFolder = textureRoot.IndexOf("Christina", StringComparison.OrdinalIgnoreCase) >= 0
                ? "ChristinaSmith"
                : navyVariant ? "KevinShawNavy" : "KevinShaw";
            string directory = $"{OfficialMaterialRoot}/{modelFolder}";
            EnsureAssetFolder(directory);
            string path = $"{directory}/{SanitizeAssetName(materialName)}.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (!material)
            {
                material = new Material(shader) { name = materialName + (navyVariant ? " Navy" : string.Empty) };
                AssetDatabase.CreateAsset(material, path);
            }
            else if (material.shader != shader)
            {
                material.shader = shader;
            }

            Texture2D diffuse = LoadTexture(textureRoot, materialName + "_Diffuse");
            Texture2D normal = LoadTexture(textureRoot, materialName + "_Normal") ??
                               LoadTexture(textureRoot, materialName + "_Bump");
            if (diffuse)
            {
                if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", diffuse);
                if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", diffuse);
            }
            if (normal)
            {
                if (material.HasProperty("_BumpMap")) material.SetTexture("_BumpMap", normal);
                material.EnableKeyword("_NORMALMAP");
            }

            Color tint = Color.white;
            if (navyVariant && materialName == "Fit_shirts") tint = new Color(0.28f, 0.36f, 0.55f, 1f);
            if (navyVariant && materialName == "Slim_fit_pants") tint = new Color(0.42f, 0.45f, 0.52f, 1f);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", tint);
            if (material.HasProperty("_Color")) material.SetColor("_Color", tint);

            bool transparent = materialName.IndexOf("Transparency", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               materialName.IndexOf("Eyelash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               materialName.IndexOf("Tearline", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               materialName.IndexOf("Cornea", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               materialName.IndexOf("Occlusion", StringComparison.OrdinalIgnoreCase) >= 0;
            ConfigureSurface(material, transparent);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Texture2D LoadTexture(string root, string stem)
        {
            foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".tga" })
            {
                Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{root}/{stem}{extension}");
                if (texture) return texture;
            }
            return null;
        }

        private static void ConfigureSurface(Material material, bool transparent)
        {
            if (!material.HasProperty("_Surface")) return;
            material.SetFloat("_Surface", transparent ? 1f : 0f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_ZWrite", transparent ? 0f : 1f);
            material.renderQueue = transparent ? 3000 : -1;
            if (transparent) material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            else material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }

        private static void EnsureAssetFolder(string path)
        {
            string current = "Assets";
            foreach (string part in path.Substring("Assets/".Length).Split('/'))
            {
                string next = current + "/" + part;
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, part);
                current = next;
            }
        }

        private static string SanitizeAssetName(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
            return value;
        }

        private static void PreserveLegacyAvatar(InterviewerController member)
        {
            GameObject current = member.AvatarRoot;
            if (!current) return;
            if (current.name.StartsWith("Convai_", StringComparison.Ordinal) &&
                current.name.EndsWith("_FACE_RIG_ACTIVE", StringComparison.Ordinal))
            {
                UnityEngine.Object.DestroyImmediate(current);
                return;
            }

            current.name = StripSuffix(current.name) + "_LEGACY_DISABLED";
            current.transform.SetParent(member.transform, true);
            current.SetActive(false);
            EditorUtility.SetDirty(current);
        }

        private static string StripSuffix(string value)
        {
            const string suffix = "_LEGACY_DISABLED";
            return value.EndsWith(suffix, StringComparison.Ordinal)
                ? value.Substring(0, value.Length - suffix.Length)
                : value;
        }

        private static void RemoveDirectLegacyCharacterComponent(InterviewerController member, Type characterType)
        {
            Component direct = FindDirectComponentByType(member.gameObject, characterType);
            if (direct) UnityEngine.Object.DestroyImmediate(direct);
        }

        private static void DisableUnusedConvaiCharacters(
            Scene scene, IReadOnlyCollection<Component> installed, Type characterType)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Component character in GetComponentsByType(root, characterType))
                {
                    if (!character || installed.Contains(character)) continue;
                    GameObject owner = character.gameObject;
                    if (!owner.name.EndsWith("_UNUSED_DISABLED", StringComparison.Ordinal))
                        owner.name += "_UNUSED_DISABLED";
                    owner.SetActive(false);
                    EditorUtility.SetDirty(owner);
                }
            }
        }

        private static string ConfigureManager(Scene scene, IReadOnlyList<Component> characters)
        {
            Type managerType = ResolveType(ManagerTypeName);
            if (managerType == null) return "RuntimeManagersActive: 0 (TYPE MISSING)";
            List<Component> managers = scene.GetRootGameObjects()
                .SelectMany(root => GetComponentsByType(root, managerType))
                .Where(component => component)
                .ToList();
            Component manager = managers.FirstOrDefault(component =>
                                    component.transform.root.name == "Convai Runtime (Interview)")
                                ?? managers.FirstOrDefault();
            if (!manager) return "RuntimeManagersActive: 0 (MISSING)";

            foreach (Component candidate in managers)
            {
                if (candidate is Behaviour behaviour)
                    behaviour.enabled = candidate == manager;
                EditorUtility.SetDirty(candidate);
            }

            SerializedObject serialized = new SerializedObject(manager);
            SerializedProperty explicitCharacters = serialized.FindProperty("_explicitCharacters");
            if (explicitCharacters != null)
            {
                explicitCharacters.arraySize = characters.Count;
                for (int i = 0; i < characters.Count; i++)
                    explicitCharacters.GetArrayElementAtIndex(i).objectReferenceValue = characters[i];
            }
            SerializedProperty target = serialized.FindProperty("_explicitConversationTarget");
            if (target != null && characters.Count > 0) target.objectReferenceValue = characters[0];
            SerializedProperty mode = serialized.FindProperty("_conversationMode");
            if (mode != null) mode.enumValueIndex = 2; // PushToTalk: no unintended microphone prewarm.
            SerializedProperty sceneSpecific = serialized.FindProperty("_sceneSpecificManager");
            if (sceneSpecific != null) sceneSpecific.boolValue = true;

            Type roomManagerType = ResolveType(RoomManagerTypeName);
            List<Component> roomManagers = roomManagerType == null
                ? new List<Component>()
                : scene.GetRootGameObjects()
                    .SelectMany(root => GetComponentsByType(root, roomManagerType))
                    .Where(component => component)
                    .ToList();
            Component selectedRoom = roomManagerType == null
                ? null
                : manager.GetComponents(roomManagerType).Cast<Component>().FirstOrDefault(component => component);
            SerializedProperty roomReference = serialized.FindProperty("_roomManager");
            if (roomReference != null) roomReference.objectReferenceValue = selectedRoom;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(manager);

            foreach (Component room in roomManagers)
            {
                bool selected = room == selectedRoom;
                // Disabled MonoBehaviours can still receive Awake on an active GameObject.
                // Keep the duplicate setup object inactive so it cannot claim the SDK
                // singleton before the interview room manager initializes.
                room.gameObject.SetActive(selected);
                if (room is Behaviour behaviour) behaviour.enabled = selected;
                EditorUtility.SetDirty(room);
                EditorUtility.SetDirty(room.gameObject);
            }
            int activeManagers = managers.Count(candidate => candidate is Behaviour behaviour && behaviour.enabled);
            return $"RuntimeManagersActive: {activeManagers}; TotalFound={managers.Count}; " +
                   $"RoomManagersActive={roomManagers.Count(room => room is Behaviour behaviour && behaviour.enabled)}; " +
                   $"OwnedCharacters={characters.Count}; DefaultTarget={CharacterNames[0]}";
        }

        private static string NormalizeAudioListeners(Scene scene)
        {
            List<AudioListener> listeners = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<AudioListener>(true))
                .Where(listener => listener)
                .ToList();
            FirstPersonAvatarController player = FindComponent<FirstPersonAvatarController>(scene);
            AudioListener preferred = player && player.HeadCamera
                ? player.HeadCamera.GetComponent<AudioListener>()
                : null;
            preferred ??= listeners.FirstOrDefault(listener => listener.GetComponent<Camera>());
            preferred ??= listeners.FirstOrDefault();
            foreach (AudioListener listener in listeners)
            {
                listener.enabled = listener == preferred;
                EditorUtility.SetDirty(listener);
            }
            return $"AudioListenersActive: {listeners.Count(listener => listener.enabled)}; TotalFound={listeners.Count}";
        }

        private static string NormalizeRuntimeCameras(Scene scene)
        {
            List<Camera> cameras = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Camera>(true))
                .Where(camera => camera)
                .ToList();
            FirstPersonAvatarController player = FindComponent<FirstPersonAvatarController>(scene);
            Camera xrCamera = player ? player.HeadCamera : null;
            int disabledPreviewCameras = 0;

            foreach (Camera camera in cameras)
            {
                if (camera == xrCamera)
                {
                    camera.enabled = true;
                    camera.gameObject.tag = "MainCamera";
                    continue;
                }

                if (camera.name != "Convai Orbit Camera") continue;
                GameObject previewCamera = camera.gameObject;
                UnityEngine.Object.DestroyImmediate(previewCamera);
                disabledPreviewCameras++;
            }

            return $"RuntimeCamera: {(xrCamera ? xrCamera.name : "NOT_FOUND")}; " +
                   $"DisabledConvaiOrbitCameras={disabledPreviewCameras}";
        }

        private static void ConfigureCharacter(Component character, string id, string displayName)
        {
            MethodInfo configure = character.GetType().GetMethod(
                "Configure", BindingFlags.Instance | BindingFlags.Public, null,
                new[] { typeof(string), typeof(string) }, null);
            configure?.Invoke(character, new object[] { id, displayName });

            SerializedObject serialized = new SerializedObject(character);
            SetString(serialized, "_characterId", id);
            SetString(serialized, "_characterName", displayName);
            SetBool(serialized, "_autoConnect", true);
            SetBool(serialized, "_enableRemoteAudio", true);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(character);
        }

        private static void DisableConflictingSampleDrivers(GameObject avatar)
        {
            // The SDK sample's body PlayableGraph intentionally replaces the Animator
            // output. In this interview scene the authored seated controller owns the
            // body, while Convai continues to own visemes, emotion and gaze.
            foreach (MonoBehaviour behaviour in avatar.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!behaviour) continue;
                string fullName = behaviour.GetType().FullName ?? string.Empty;
                bool conflictsWithSeatedBody =
                    fullName == "Convai.Modules.BodyAnimation.Components.ConvaiBodyAnimationController";
                bool conflictsWithExactScriptedTurns =
                    fullName == "Convai.Modules.ConversationFlow.Components.ConvaiConversationFlowController";
                if (!conflictsWithSeatedBody && !conflictsWithExactScriptedTurns) continue;
                behaviour.enabled = false;
                EditorUtility.SetDirty(behaviour);
            }
        }

        private static void ConfigureLipSyncTargets(GameObject avatar, SkinnedMeshRenderer[] renderers)
        {
            MonoBehaviour lipSync = avatar.GetComponentsInChildren<MonoBehaviour>(true)
                .FirstOrDefault(component => component &&
                    component.GetType().FullName == "Convai.Modules.LipSync.ConvaiLipSyncComponent");
            if (!lipSync) throw new InvalidOperationException("ConvaiLipSyncComponent를 찾지 못했습니다.");
            if (renderers == null || renderers.Length == 0)
                throw new InvalidOperationException("커스텀 면접관의 SkinnedMeshRenderer가 없습니다.");

            SerializedObject serialized = new SerializedObject(lipSync);
            SerializedProperty targets = serialized.FindProperty("_targetMeshes");
            if (targets == null) throw new InvalidOperationException("Convai LipSync target 필드를 찾지 못했습니다.");
            targets.arraySize = renderers.Length;
            for (int i = 0; i < renderers.Length; i++)
                targets.GetArrayElementAtIndex(i).objectReferenceValue = renderers[i];
            SerializedProperty smoothing = serialized.FindProperty("_smoothingFactor");
            if (smoothing != null) smoothing.floatValue = 0.12f;
            SerializedProperty fadeIn = serialized.FindProperty("_fadeInDuration");
            if (fadeIn != null) fadeIn.floatValue = 0.035f;
            SerializedProperty offset = serialized.FindProperty("_timeOffset");
            if (offset != null) offset.floatValue = 0f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(lipSync);
        }

        private static Component FindBehaviour(Scene scene, string typeName)
        {
            Type type = ResolveType(typeName);
            if (type == null) return null;
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (Component component in GetComponentsByType(root, type))
                    if (component) return component;
            return null;
        }

        private static T FindComponent<T>(Scene scene) where T : Component
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                T component = root.GetComponentInChildren<T>(true);
                if (component) return component;
            }
            return null;
        }

        private static IEnumerable<Component> GetComponentsByType(GameObject root, Type type)
        {
            return root.GetComponentsInChildren(type, true).Cast<Component>();
        }

        private static Component FindComponentByType(GameObject root, Type type)
        {
            return root.GetComponentsInChildren(type, true).Cast<Component>().FirstOrDefault(component => component);
        }

        private static Component FindDirectComponentByType(GameObject owner, Type type)
        {
            return owner.GetComponents(type).Cast<Component>().FirstOrDefault(component => component);
        }

        private static Type ResolveType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }

        private static int CountBlendShapes(GameObject avatar)
        {
            int count = 0;
            foreach (SkinnedMeshRenderer renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (renderer && renderer.sharedMesh) count += renderer.sharedMesh.blendShapeCount;
            return count;
        }

        private static int CountNamedComponents(GameObject avatar, string fragment)
        {
            return avatar.GetComponentsInChildren<MonoBehaviour>(true).Count(component =>
                component && component.GetType().Name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static int CountMissingScripts(GameObject avatar)
        {
            int count = 0;
            foreach (Transform node in avatar.GetComponentsInChildren<Transform>(true))
                count += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(node.gameObject);
            return count;
        }

        private static int CountEnabledSampleDriverConflicts(GameObject avatar)
        {
            return avatar.GetComponentsInChildren<MonoBehaviour>(true).Count(behaviour =>
            {
                if (!behaviour || !behaviour.enabled) return false;
                string fullName = behaviour.GetType().FullName ?? string.Empty;
                return fullName == "Convai.Modules.BodyAnimation.Components.ConvaiBodyAnimationController" ||
                       fullName == "Convai.Modules.ConversationFlow.Components.ConvaiConversationFlowController";
            });
        }

        private static void SetString(SerializedObject serialized, string name, string value)
        {
            SerializedProperty property = serialized.FindProperty(name);
            if (property != null) property.stringValue = value;
        }

        private static void SetBool(SerializedObject serialized, string name, bool value)
        {
            SerializedProperty property = serialized.FindProperty(name);
            if (property != null) property.boolValue = value;
        }
    }
}
