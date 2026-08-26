using System;
using System.IO;
using System.Linq;
using SpeakUpXR;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SpeakUpXR.Editor
{
    /// <summary>Authors the Fantasy Monster coaching companion directly into Interview.unity.</summary>
    internal static class FeedbackCompanionInstaller
    {
        private const string ScenePath = "Assets/SpeakUpXR/Scenes/Interview.unity";
        private const string PrefabPath = "Assets/Stylized3DMonster/Monster40/Prefab/Monster40_01.prefab";
        private const string MaterialPath = "Assets/Stylized3DMonster/Monster40/ShaderTexture/Monster40_01.mat";
        private const string TexturePath = "Assets/Stylized3DMonster/Monster40/ShaderTexture/Monster40_01.png";
        private const string ClipPath = "Assets/Stylized3DMonster/Monster40/Monster38_Idle01.anim";
        private const string ControllerPath = "Assets/SpeakUpXR/Animations/FeedbackMonster.controller";
        private const string ReportPath = "Assets/SpeakUpXR/UI/feedback-companion-install-v1.txt";
        private const int Revision = 6;

        [InitializeOnLoadMethod]
        private static void Schedule() => EditorApplication.delayCall += InstallIfNeeded;

        [MenuItem("SpeakUpXR/Interview/피드백 몬스터 다시 배치")]
        private static void InstallFromMenu() => Install(true);

        private static void InstallIfNeeded()
        {
            if (File.Exists(ReportPath) && File.ReadAllText(ReportPath).Contains($"Revision: {Revision}")) return;
            Install(false);
        }

        private static void Install(bool force)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.delayCall += InstallIfNeeded;
                return;
            }
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath);
            if (!prefab || !clip || AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
            {
                if (force) Debug.LogWarning("[SpeakUpXR] Fantasy Monster 프리팹/애니메이션 또는 Interview 씬이 없습니다.");
                return;
            }

            EnsureLoop(clip);
            RuntimeAnimatorController controller = EnsureController(clip);
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            bool closeScene = !scene.IsValid() || !scene.isLoaded;
            if (closeScene) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            try
            {
                FirstPersonAvatarController player = FindInScene<FirstPersonAvatarController>(scene);
                InterviewSession session = FindInScene<InterviewSession>(scene);
                XrRealtimeFeedbackController feedback = FindInScene<XrRealtimeFeedbackController>(scene);
                if (!player || !player.HeadCamera || !session || !feedback)
                    throw new MissingReferenceException("Interview의 HeadCamera/InterviewSession/실시간 피드백 구성이 없습니다.");

                FeedbackCompanionController companion = FindInScene<FeedbackCompanionController>(scene);
                if (!companion)
                {
                    var anchor = new GameObject("FeedbackMonster_TOP_LEFT_EDIT_OFFSET");
                    SceneManager.MoveGameObjectToScene(anchor, scene);
                    anchor.transform.SetParent(player.HeadCamera.transform, false);
                    companion = anchor.AddComponent<FeedbackCompanionController>();
                    GameObject visual = (GameObject)PrefabUtility.InstantiatePrefab(prefab, anchor.transform);
                    visual.name = "FantasyMonster_FeedbackCharacter";
                    visual.transform.localPosition = Vector3.zero;
                    visual.transform.localRotation = Quaternion.identity;
                    visual.transform.localScale = Vector3.one;
                    companion.CharacterRoot = visual;
                }

                companion.HeadAnchor = player.HeadCamera.transform;
                companion.Api = session.Api;
                companion.VoiceSource = companion.GetComponent<AudioSource>() ?? companion.gameObject.AddComponent<AudioSource>();
                companion.VoiceSource.playOnAwake = false;
                companion.VoiceSource.spatialBlend = 0f;
                companion.Voice ??= new InterviewerVoice();
                companion.Voice.VoiceName = "ko-KR-InJoonNeural";
                companion.Voice.RatePercent = 3;
                companion.Voice.PitchPercent = 1;
                companion.transform.SetParent(player.HeadCamera.transform, false);
                companion.transform.localPosition = companion.LocalOffset;
                companion.transform.localScale = companion.LocalScale;
                companion.CharacterAnimator = companion.CharacterRoot
                    ? companion.CharacterRoot.GetComponentInChildren<Animator>(true)
                    : companion.GetComponentInChildren<Animator>(true);
                if (!companion.CharacterAnimator && companion.CharacterRoot)
                    companion.CharacterAnimator = companion.CharacterRoot.AddComponent<Animator>();
                if (companion.CharacterAnimator)
                {
                    companion.CharacterAnimator.runtimeAnimatorController = controller;
                    companion.CharacterAnimator.applyRootMotion = false;
                    companion.CharacterAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    EditorUtility.SetDirty(companion.CharacterAnimator);
                }

                ConfigureYetiMaterial(companion.CharacterRoot);
                companion.DisplayName = "예티 코치";
                companion.FaceHeadCamera = true;
                companion.LookAtEulerOffset = Vector3.zero;

                if (!companion.DialogueHud) BuildDialogue(scene, companion);
                ConfigureDialogue(companion);
                int removedOrbitCameras = RemoveConvaiOrbitCameras(scene, player.HeadCamera);
                DisableUnrequestedConvaiUi(scene);
                EnsureCameraVisibilityGuard(player, session);
                session.FeedbackCompanion = companion;
                feedback.Companion = companion;
                foreach (ConvaiInterviewerBridge bridge in Resources.FindObjectsOfTypeAll<ConvaiInterviewerBridge>()
                             .Where(item => item.gameObject.scene == scene))
                {
                    bridge.SpeechStartTimeoutSeconds = 30f;
                    bridge.SpeechStopTimeoutSeconds = 60f;
                    EditorUtility.SetDirty(bridge);
                }
                EditorUtility.SetDirty(companion);
                EditorUtility.SetDirty(session);
                EditorUtility.SetDirty(feedback);

                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath) ?? "Assets/SpeakUpXR/UI");
                File.WriteAllLines(ReportPath, new[]
                {
                    "Fantasy Monster feedback companion installation",
                    $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"Revision: {Revision}",
                    $"Prefab: {PrefabPath}",
                    $"Animator: {(companion.CharacterAnimator ? "BOUND" : "MISSING")}",
                    $"HeadAnchor: {player.HeadCamera.name}",
                    $"LocalOffset: {companion.LocalOffset}",
                    "FeedbackTTS: ENABLED",
                    "TypewriterDialogue: AUDIO_START_SYNCHRONIZED",
                    "DialoguePosition: TOP_LEFT",
                    "DisplayName: 예티 코치",
                    "CameraLookAt: YAW_AND_PITCH",
                    $"Material: {MaterialPath}",
                    $"RemovedConvaiOrbitCameras: {removedOrbitCameras}",
                    "ConvaiSampleTranscriptUI: DISABLED",
                    "CameraOcclusionGuard: ENABLED",
                    "SceneAuthored: YES",
                });
                AssetDatabase.ImportAsset(ReportPath, ImportAssetOptions.ForceUpdate);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                AssetDatabase.SaveAssets();
                Debug.Log("[SpeakUpXR] Fantasy Monster 피드백 캐릭터와 전용 대화창을 Interview 씬에 배치했습니다.");
            }
            catch (Exception exception)
            {
                Debug.LogError("[SpeakUpXR] 피드백 몬스터 배치 실패: " + exception);
            }
            finally
            {
                if (closeScene && scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static int RemoveConvaiOrbitCameras(Scene scene, Camera headCamera)
        {
            Camera[] cameras = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Camera>(true))
                .Where(camera => camera && camera != headCamera && camera.name == "Convai Orbit Camera")
                .ToArray();
            foreach (Camera camera in cameras)
                UnityEngine.Object.DestroyImmediate(camera.gameObject);
            return cameras.Length;
        }

        private static void ConfigureYetiMaterial(GameObject characterRoot)
        {
            if (!characterRoot) return;
            Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
            Shader urpLit = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (!material || !texture || !urpLit)
                throw new MissingReferenceException("예티의 머티리얼, 텍스처 또는 URP Simple Lit 셰이더를 찾지 못했습니다.");

            material.shader = urpLit;
            material.SetTexture("_BaseMap", texture);
            material.SetTexture("_MainTex", texture);
            material.SetColor("_BaseColor", Color.white);
            material.SetColor("_Color", Color.white);
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Smoothness", 0.12f);
            material.SetFloat("_SpecularHighlights", 0f);
            EditorUtility.SetDirty(material);

            foreach (Renderer renderer in characterRoot.GetComponentsInChildren<Renderer>(true))
            {
                Material[] materials = renderer.sharedMaterials;
                if (materials.Length == 0) materials = new Material[1];
                for (int i = 0; i < materials.Length; i++) materials[i] = material;
                renderer.sharedMaterials = materials;
                EditorUtility.SetDirty(renderer);
            }
        }

        private static void BuildDialogue(Scene scene, FeedbackCompanionController companion)
        {
            var canvasObject = new GameObject("FeedbackDialogue_TOP_LEFT", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(CanvasGroup), typeof(AdaptiveWorldCanvas));
            SceneManager.MoveGameObjectToScene(canvasObject, scene);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 1100;
            var adaptive = canvasObject.GetComponent<AdaptiveWorldCanvas>();
            adaptive.DesktopAnchor = new Vector2(0f, 1f);
            adaptive.DesktopPivot = new Vector2(0f, 1f);
            adaptive.DesktopPosition = new Vector2(36f, -36f);
            adaptive.DesktopSize = new Vector2(720f, 190f);
            adaptive.AttachToHeadInXr = true;
            adaptive.XrLocalPosition = new Vector3(-0.24f, 0.16f, 0.76f);
            adaptive.XrLocalEuler = Vector3.zero;
            adaptive.XrWorldScale = 0.00055f;

            GameObject box = CreateUi("DialogueBox_BOTTOM_ONLY", canvasObject.transform,
                new Vector2(0f, 0f), new Vector2(720f, 190f));
            Image background = box.AddComponent<Image>();
            background.color = new Color(0.035f, 0.045f, 0.065f, 0.91f);
            Text speaker = CreateText("Speaker", box.transform, new Vector2(28f, -20f), new Vector2(430f, 34f),
                22, TextAnchor.MiddleLeft, new Color(0.43f, 0.66f, 1f));
            Text status = CreateText("Status", box.transform, new Vector2(458f, -20f), new Vector2(230f, 34f),
                18, TextAnchor.MiddleRight, new Color(0.73f, 0.45f, 0.08f));
            Text question = CreateText("Question", box.transform, new Vector2(28f, -60f), new Vector2(660f, 105f),
                28, TextAnchor.UpperLeft, new Color(0.94f, 0.96f, 1f));

            InterviewHud hud = canvasObject.AddComponent<InterviewHud>();
            hud.SpeakerText = speaker;
            hud.StatusText = status;
            hud.QuestionText = question;
            hud.CompactCompanionLayout = true;
            hud.CharactersPerSecond = 24f;
            companion.DialogueHud = hud;
            companion.DialogueGroup = canvasObject.GetComponent<CanvasGroup>();
            companion.DialogueGroup.alpha = 0f;
        }

        private static void ConfigureDialogue(FeedbackCompanionController companion)
        {
            if (!companion.DialogueHud) return;
            GameObject canvasObject = companion.DialogueHud.gameObject;
            canvasObject.name = "FeedbackDialogue_TOP_LEFT";
            AdaptiveWorldCanvas adaptive = canvasObject.GetComponent<AdaptiveWorldCanvas>();
            if (!adaptive) adaptive = canvasObject.AddComponent<AdaptiveWorldCanvas>();
            adaptive.DesktopAnchor = new Vector2(0f, 1f);
            adaptive.DesktopPivot = new Vector2(0f, 1f);
            adaptive.DesktopPosition = new Vector2(36f, -36f);
            adaptive.DesktopSize = new Vector2(720f, 190f);
            adaptive.AttachToHeadInXr = true;
            adaptive.XrLocalPosition = new Vector3(-0.24f, 0.16f, 0.76f);
            adaptive.XrLocalEuler = Vector3.zero;
            adaptive.XrWorldScale = 0.00055f;
            EditorUtility.SetDirty(adaptive);
            EditorUtility.SetDirty(canvasObject);
        }

        private static void DisableUnrequestedConvaiUi(Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            foreach (Transform item in root.GetComponentsInChildren<Transform>(true))
            {
                if (item.name != "TranscriptUI_Chat") continue;
                item.gameObject.SetActive(false);
                EditorUtility.SetDirty(item.gameObject);
            }
        }

        private static void EnsureCameraVisibilityGuard(
            FirstPersonAvatarController player, InterviewSession session)
        {
            if (!player || !player.HeadCamera) return;
            InterviewCameraOcclusionGuard guard =
                player.HeadCamera.GetComponent<InterviewCameraOcclusionGuard>() ??
                player.HeadCamera.gameObject.AddComponent<InterviewCameraOcclusionGuard>();
            guard.HeadCamera = player.HeadCamera;
            guard.Panel = session ? session.Panel : null;
            player.HeadCamera.nearClipPlane = 0.02f;
            EditorUtility.SetDirty(guard);
            EditorUtility.SetDirty(player.HeadCamera);
        }

        private static GameObject CreateUi(string name, Transform parent, Vector2 position, Vector2 size)
        {
            var gameObject = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)gameObject.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return gameObject;
        }

        private static Text CreateText(string name, Transform parent, Vector2 position, Vector2 size,
            int fontSize, TextAnchor alignment, Color color)
        {
            GameObject gameObject = CreateUi(name, parent, position, size);
            Text text = gameObject.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private static void EnsureLoop(AnimationClip clip)
        {
            SerializedObject serialized = new SerializedObject(clip);
            SerializedProperty settings = serialized.FindProperty("m_AnimationClipSettings");
            SerializedProperty loop = settings?.FindPropertyRelative("m_LoopTime");
            if (loop == null || loop.boolValue) return;
            loop.boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(clip);
        }

        private static RuntimeAnimatorController EnsureController(AnimationClip clip)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ControllerPath) ?? "Assets/SpeakUpXR/Animations");
            AnimatorController existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (existing) return existing;
            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            controller.AddParameter("IsSpeaking", AnimatorControllerParameterType.Bool);
            AnimatorStateMachine machine = controller.layers[0].stateMachine;
            AnimatorState idle = machine.AddState("Companion Idle");
            idle.motion = clip;
            idle.speed = 0.72f;
            AnimatorState talk = machine.AddState("Companion Talking Loop");
            talk.motion = clip;
            talk.speed = 1.28f;
            machine.defaultState = idle;
            AnimatorStateTransition toTalk = idle.AddTransition(talk);
            toTalk.hasExitTime = false;
            toTalk.duration = 0.12f;
            toTalk.AddCondition(AnimatorConditionMode.If, 0f, "IsSpeaking");
            AnimatorStateTransition toIdle = talk.AddTransition(idle);
            toIdle.hasExitTime = false;
            toIdle.duration = 0.18f;
            toIdle.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsSpeaking");
            EditorUtility.SetDirty(controller);
            return controller;
        }

        private static T FindInScene<T>(Scene scene) where T : Component => scene.GetRootGameObjects()
            .Select(root => root.GetComponentInChildren<T>(true)).FirstOrDefault(component => component);
    }
}
