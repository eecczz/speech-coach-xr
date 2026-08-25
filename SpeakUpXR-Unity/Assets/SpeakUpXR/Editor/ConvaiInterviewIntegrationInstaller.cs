using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using SpeakUpXR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SpeakUpXR.Editor
{
    internal static class ConvaiInterviewIntegrationInstaller
    {
        private const string InterviewScene = "Assets/SpeakUpXR/Scenes/Interview.unity";
        private const string ConvaiSourceScene =
            "Assets/Convai SDK For Unity/Samples/LipSyncSample/Scenes/LipSync Sample.unity";
        private const string ReportPath = "Assets/SpeakUpXR/UI/convai-interview-integration-v1.txt";
        private const string ManagerType = "Convai.Runtime.Components.ConvaiManager";
        private const string RoomManagerType = "Convai.Runtime.Adapters.Networking.ConvaiRoomManager";
        private const string PlayerType = "Convai.Runtime.Components.ConvaiPlayer";
        private const string CharacterType = "Convai.Runtime.Components.ConvaiCharacter";
        private const string LipSyncType = "Convai.Modules.LipSync.ConvaiLipSyncComponent";

        private static readonly string[] CharacterIds =
        {
            "e41f5270-7378-478f-b037-391f0be4a269", // Kim Minseo / HR / female Korean voice
            "07463148-4a9b-42e6-823b-a4eb0950ae65", // Lee Junho / Technical / male Korean voice
            "e25f1f92-4c46-4360-b975-889b0de98e18", // Park Seongho / Executive / male Korean voice
        };

        private static readonly string[] CharacterNames = { "Kim Minseo", "Lee Junho", "Park Seongho" };

        [InitializeOnLoadMethod]
        private static void ScheduleInstall()
        {
            EditorApplication.delayCall += InstallIfNeeded;
        }

        [MenuItem("SpeakUpXR/Convai/면접 씬에 3인 패널 연결")]
        private static void InstallFromMenu() => Install(force: true);

        private static void InstallIfNeeded()
        {
            if (File.Exists(ReportPath)) return;
            Install(force: true);
        }

        private static void Install(bool force)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(InterviewScene) == null) return;
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ConvaiSourceScene) == null)
            {
                if (force) Debug.LogWarning("[SpeakUpXR] 로컬 Convai 샘플 씬이 없어 면접 씬 연동을 건너뜁니다.");
                return;
            }

            Type characterType = ResolveType(CharacterType);
            if (characterType == null)
            {
                if (force) Debug.LogWarning("[SpeakUpXR] ConvaiCharacter 타입을 찾지 못했습니다.");
                return;
            }

            Scene interview = SceneManager.GetSceneByPath(InterviewScene);
            bool closeInterview = !interview.IsValid() || !interview.isLoaded;
            if (closeInterview) interview = EditorSceneManager.OpenScene(InterviewScene, OpenSceneMode.Additive);
            Scene source = EditorSceneManager.OpenScene(ConvaiSourceScene, OpenSceneMode.Additive);

            try
            {
                RemoveRoot(interview, "Convai Runtime (Interview)");
                RemoveRoot(interview, "Convai Player (Interview)");

                MonoBehaviour sourceManager = FindBehaviour(source, ManagerType);
                MonoBehaviour sourceRoomManager = FindBehaviour(source, RoomManagerType);
                MonoBehaviour sourcePlayer = FindBehaviour(source, PlayerType);
                GameObject managerClone = CloneRootToScene(
                    (sourceManager ? sourceManager : sourceRoomManager)?.transform.root.gameObject,
                    interview,
                    "Convai Runtime (Interview)");
                GameObject playerClone = CloneRootToScene(
                    sourcePlayer ? sourcePlayer.transform.root.gameObject : null,
                    interview,
                    "Convai Player (Interview)");

                InterviewerPanel panel = FindComponent<InterviewerPanel>(interview);
                var lines = new List<string>
                {
                    "SpeakUpXR Convai 3-person interview integration",
                    $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"ConvaiManager: {(managerClone ? "INSTALLED" : "MISSING")}",
                    $"ConvaiPlayer: {(playerClone ? "INSTALLED" : "MISSING")}",
                    "SpeechRoute: ConvaiCharacter.SendNarrativeSpeech(string) — exact scripted speech",
                    "Fallback: existing Coach TTS when Convai is unavailable/not ready",
                    "VisualPolicy: scene-authored Formal Character business suits remain active",
                };

                if (panel == null || panel.Members == null || panel.Members.Length < 3)
                {
                    lines.Add("Panel: MISSING OR INCOMPLETE");
                }
                else
                {
                    for (int i = 0; i < 3; i++)
                    {
                        InterviewerController member = panel.Members[i];
                        if (!member) continue;
                        Component character = FindComponentByType(member.gameObject, characterType)
                                              ?? member.gameObject.AddComponent(characterType);
                        ConfigureCharacter(character, CharacterIds[i], CharacterNames[i], autoConnect: true);

                        ConvaiInterviewerBridge bridge = member.GetComponent<ConvaiInterviewerBridge>();
                        if (!bridge) bridge = member.gameObject.AddComponent<ConvaiInterviewerBridge>();
                        bridge.CharacterId = CharacterIds[i];
                        bridge.CharacterName = CharacterNames[i];
                        bridge.UseConvaiSpeech = true;
                        member.ConvaiBridge = bridge;

                        SerializedObject serializedBridge = new SerializedObject(bridge);
                        serializedBridge.FindProperty("convaiCharacter").objectReferenceValue = character;
                        serializedBridge.ApplyModifiedPropertiesWithoutUndo();

                        int blendShapes = CountBlendShapes(member.AvatarRoot);
                        if (blendShapes > 0) TryInstallLipSync(member, characterType);
                        string activeAvatar = member.AvatarRoot ? member.AvatarRoot.name : "MISSING";
                        lines.Add($"[{i + 1}] {member.DisplayName}: {CharacterNames[i]} / {CharacterIds[i]} / " +
                                  $"Avatar={activeAvatar} / Animator={(member.CharacterAnimator ? "OWN" : "MISSING")} / " +
                                  $"BlendShapes={blendShapes} / Bridge=READY");
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath) ?? "Assets/SpeakUpXR/UI");
                File.WriteAllLines(ReportPath, lines);
                AssetDatabase.ImportAsset(ReportPath, ImportAssetOptions.ForceUpdate);
                EditorSceneManager.MarkSceneDirty(interview);
                EditorSceneManager.SaveScene(interview);
                Debug.Log("[SpeakUpXR] Convai 한국어 3인 패널을 Interview 씬에 연결했습니다. 정장 캐릭터는 유지됩니다.");
            }
            catch (Exception exception)
            {
                Debug.LogError("[SpeakUpXR] Convai 면접 씬 연동 실패: " + exception);
            }
            finally
            {
                if (source.IsValid() && source.isLoaded) EditorSceneManager.CloseScene(source, true);
                if (closeInterview && interview.IsValid() && interview.isLoaded) EditorSceneManager.CloseScene(interview, true);
            }
        }

        private static void ConfigureCharacter(Component character, string id, string displayName, bool autoConnect)
        {
            MethodInfo configure = character.GetType().GetMethod(
                "Configure",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(string), typeof(string) },
                null);
            configure?.Invoke(character, new object[] { id, displayName });

            var serialized = new SerializedObject(character);
            SetString(serialized, "_characterId", id);
            SetString(serialized, "_characterName", displayName);
            SetBool(serialized, "_autoConnect", autoConnect);
            SetBool(serialized, "_enableRemoteAudio", true);
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void TryInstallLipSync(InterviewerController member, Type characterType)
        {
            Type lipSyncType = ResolveType(LipSyncType);
            if (lipSyncType == null || member.AvatarRoot == null) return;
            Component lipSync = FindComponentByType(member.gameObject, lipSyncType) ?? member.gameObject.AddComponent(lipSyncType);
            SerializedObject serialized = new SerializedObject(lipSync);
            SerializedProperty targets = serialized.FindProperty("_targetMeshes");
            if (targets == null) return;
            SkinnedMeshRenderer[] meshes = member.AvatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var facial = new List<SkinnedMeshRenderer>();
            foreach (SkinnedMeshRenderer mesh in meshes)
                if (mesh && mesh.sharedMesh && mesh.sharedMesh.blendShapeCount > 0) facial.Add(mesh);
            targets.arraySize = facial.Count;
            for (int i = 0; i < facial.Count; i++) targets.GetArrayElementAtIndex(i).objectReferenceValue = facial[i];
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static GameObject CloneRootToScene(GameObject sourceRoot, Scene destination, string name)
        {
            if (!sourceRoot) return null;
            GameObject clone = UnityEngine.Object.Instantiate(sourceRoot);
            clone.name = name;
            SceneManager.MoveGameObjectToScene(clone, destination);
            clone.SetActive(true);
            return clone;
        }

        private static void RemoveRoot(Scene scene, string name)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
                if (root.name == name) UnityEngine.Object.DestroyImmediate(root);
        }

        private static MonoBehaviour FindBehaviour(Scene scene, string typeName)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                    if (behaviour && behaviour.GetType().FullName == typeName) return behaviour;
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

        private static Component FindComponentByType(GameObject owner, Type type)
        {
            foreach (Component component in owner.GetComponents<Component>())
                if (component && component.GetType() == type) return component;
            return null;
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
            if (!avatar) return 0;
            int count = 0;
            foreach (SkinnedMeshRenderer renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (renderer && renderer.sharedMesh) count += renderer.sharedMesh.blendShapeCount;
            return count;
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
