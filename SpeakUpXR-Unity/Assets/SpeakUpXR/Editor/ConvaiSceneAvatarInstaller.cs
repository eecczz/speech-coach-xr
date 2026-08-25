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
    // Scene installation revision 2: preserve seated body while Convai drives the face.
    internal static class ConvaiSceneAvatarInstaller
    {
        private const string InterviewScene = "Assets/SpeakUpXR/Scenes/Interview.unity";
        private const string SourceScene =
            "Assets/Convai SDK For Unity/Samples/LipSyncSample/Scenes/LipSync Sample.unity";
        private const string ReportPath = "Assets/SpeakUpXR/UI/convai-scene-avatar-install-v1.txt";
        private const string CharacterTypeName = "Convai.Runtime.Components.ConvaiCharacter";
        private const string ManagerTypeName = "Convai.Runtime.Components.ConvaiManager";
        private const string RoomManagerTypeName = "Convai.Runtime.Adapters.Networking.ConvaiRoomManager";

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
                File.ReadAllText(ReportPath).Contains("ValidationRevision: 6")) return;
            Install(force: false);
        }

        private static void Install(bool force)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode)
                return;
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

                var installedCharacters = new List<Component>();
                var report = new List<string>
                {
                    "Convai face-rigged scene avatar installation",
                    $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    "SceneAuthored: YES (no runtime character spawning)",
                    "LegacyFormalCharacters: PRESERVED + INACTIVE",
                    "Speech: Convai Narrative Speech / Korean dashboard voices",
                    "Face: server visemes + Convai emotion blendshape stream",
                    "CurrentLocalFaceModelInventory: Sofia only (three independent instances)",
                    "SampleDriversNormalized: YES (Convai body graph + free conversation flow disabled)",
                    "ValidationRevision: 6",
                };

                for (int i = 0; i < 3; i++)
                {
                    InterviewerController member = panel.Members[i];
                    if (!member) throw new InvalidOperationException($"면접관 슬롯 {i + 1}이 비어 있습니다.");

                    RuntimeAnimatorController seatedController = member.CharacterAnimator
                        ? member.CharacterAnimator.runtimeAnimatorController
                        : null;
                    PreserveLegacyAvatar(member);
                    RemoveDirectLegacyCharacterComponent(member, characterType);

                    GameObject avatar = UnityEngine.Object.Instantiate(sourceCharacter.gameObject);
                    avatar.name = $"Convai_{CharacterNames[i].Replace(" ", "_")}_FACE_RIG_ACTIVE";
                    avatar.transform.SetParent(member.transform, false);
                    avatar.transform.localPosition = Vector3.zero;
                    avatar.transform.localRotation = Quaternion.identity;
                    avatar.transform.localScale = Vector3.one;
                    avatar.SetActive(true);

                    Component character = FindComponentByType(avatar, characterType);
                    if (!character) throw new InvalidOperationException($"{avatar.name}에 ConvaiCharacter가 없습니다.");
                    ConfigureCharacter(character, CharacterIds[i], CharacterNames[i]);
                    installedCharacters.Add(character);
                    DisableConflictingSampleDrivers(avatar);

                    Animator animator = avatar.GetComponentInChildren<Animator>(true);
                    if (!animator || !animator.avatar || !animator.avatar.isValid || !animator.avatar.isHuman)
                        throw new InvalidOperationException($"{avatar.name}의 Humanoid Animator가 유효하지 않습니다.");
                    if (seatedController) animator.runtimeAnimatorController = seatedController;
                    animator.applyRootMotion = false;
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

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
                    facialFallback.FaceRoot = avatar;
                    facialFallback.Rebind();
                    EditorUtility.SetDirty(member);
                    EditorUtility.SetDirty(bridge);
                    EditorUtility.SetDirty(animator);
                    EditorUtility.SetDirty(tracker);
                    EditorUtility.SetDirty(facialFallback);

                    int blendShapes = CountBlendShapes(avatar);
                    int lipSync = CountNamedComponents(avatar, "LipSync");
                    int emotion = CountNamedComponents(avatar, "Emotion");
                    int gaze = CountNamedComponents(avatar, "Gaze") + CountNamedComponents(avatar, "LookAt");
                    int missingScripts = CountMissingScripts(avatar);
                    int enabledConflicts = CountEnabledSampleDriverConflicts(avatar);
                    report.Add($"[{i + 1}] {member.DisplayName}: {CharacterNames[i]} / {CharacterIds[i]} / " +
                               $"Avatar={avatar.name} / BlendShapes={blendShapes} / " +
                               $"LipSync={lipSync} / Emotion={emotion} / Gaze={gaze} / SeatedAnimator={(seatedController ? "BOUND" : "SOURCE")}");
                    report.Add($"    MissingScripts={missingScripts} / EnabledConflictingSampleDrivers={enabledConflicts} / " +
                               "LocalFacialFallback=READY");
                }

                DisableUnusedConvaiCharacters(interview, installedCharacters, characterType);
                string managerStatus = ConfigureManager(interview, installedCharacters);
                report.Add(managerStatus);
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
