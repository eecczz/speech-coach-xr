using System;
using System.IO;
using System.Linq;
using SpeakUpXR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SpeakUpXR.Editor
{
    internal static class InterviewerPresentationInstaller
    {
        private const string ScenePath = "Assets/SpeakUpXR/Scenes/Interview.unity";
        private const string ReportPath = "Assets/SpeakUpXR/UI/interviewer-presentation-install-v1.txt";

        [InitializeOnLoadMethod]
        private static void Schedule() => EditorApplication.delayCall += InstallIfNeeded;

        [MenuItem("SpeakUpXR/Interview/이름표 정면 정렬 및 캐릭터 광택 보정")]
        private static void InstallFromMenu() => Install(true);

        private static void InstallIfNeeded()
        {
            if (File.Exists(ReportPath)) return;
            Install(false);
        }

        private static void Install(bool force)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.delayCall += InstallIfNeeded;
                return;
            }
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null) return;

            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            bool closeScene = !scene.IsValid() || !scene.isLoaded;
            if (closeScene) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            try
            {
                InterviewerPanel panel = Find<InterviewerPanel>(scene);
                InterviewSession session = Find<InterviewSession>(scene);
                InterviewEntranceSequence entrance = Find<InterviewEntranceSequence>(scene);
                FirstPersonAvatarController player = Find<FirstPersonAvatarController>(scene);
                RectTransform[] plates = Resources.FindObjectsOfTypeAll<Canvas>()
                    .Where(canvas => canvas.gameObject.scene == scene &&
                                     canvas.name.StartsWith("NamePlate_", StringComparison.Ordinal))
                    .Select(canvas => canvas.transform as RectTransform)
                    .Where(value => value != null)
                    .OrderBy(value => value.position.x)
                    .ToArray();
                if (!panel || !session || !entrance || !entrance.SeatPoint || !player || !player.HeadCamera || plates.Length != 3)
                    throw new MissingReferenceException("패널/좌석/플레이어 카메라/이름표 3개를 찾지 못했습니다.");

                var nameplateRig = panel.GetComponent<InterviewerNameplateRig>() ??
                                   panel.gameObject.AddComponent<InterviewerNameplateRig>();
                nameplateRig.Viewer = player.HeadCamera.transform;
                nameplateRig.Session = session;
                nameplateRig.Nameplates = plates;
                nameplateRig.LockDuringEntrance = false;

                float authoredEyeHeight = Mathf.Clamp(
                    player.HeadCamera.transform.position.y - player.transform.position.y, 1.15f, 1.8f);
                Vector3 seatedEyes = entrance.SeatPoint.position + Vector3.up * authoredEyeHeight;
                float commonDistance = plates.Average(plate =>
                {
                    Vector3 delta = plate.position - seatedEyes;
                    delta.y = 0f;
                    return delta.magnitude;
                });
                float commonVerticalOffset = plates.Average(plate => plate.position.y - seatedEyes.y);
                nameplateRig.DistanceFromViewer = Mathf.Max(0.5f, commonDistance);
                nameplateRig.VerticalOffsetFromEyes = commonVerticalOffset;

                foreach (RectTransform plate in plates)
                {
                    Vector3 direction = plate.position - seatedEyes;
                    direction.y = 0f;
                    direction = direction.sqrMagnitude > 0.0001f ? direction.normalized : entrance.SeatPoint.forward;
                    plate.position = seatedEyes + direction * nameplateRig.DistanceFromViewer +
                                     Vector3.up * nameplateRig.VerticalOffsetFromEyes;
                    plate.rotation = Quaternion.LookRotation(direction, Vector3.up);
                    EditorUtility.SetDirty(plate);
                }

                var materialTuner = panel.GetComponent<InterviewerMaterialTuner>() ??
                                    panel.gameObject.AddComponent<InterviewerMaterialTuner>();
                materialTuner.Panel = panel;
                materialTuner.Smoothness = 0.2f;
                materialTuner.Metallic = 0f;
                materialTuner.SpecularLevel = 0.28f;
                materialTuner.DisableClearCoat = true;
                materialTuner.ApplyNow();

                EditorUtility.SetDirty(nameplateRig);
                EditorUtility.SetDirty(materialTuner);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath) ?? "Assets/SpeakUpXR/UI");
                File.WriteAllLines(ReportPath, new[]
                {
                    "Interviewer presentation normalization",
                    $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"Nameplates: {plates.Length}",
                    $"CommonDistance: {nameplateRig.DistanceFromViewer:F3}",
                    $"VerticalOffset: {nameplateRig.VerticalOffsetFromEyes:F3}",
                    "FrontRotationLock: ENABLED",
                    $"CharacterSmoothness: {materialTuner.Smoothness:F2}",
                    $"CharacterSpecular: {materialTuner.SpecularLevel:F2}",
                    "EnvironmentMaterialsChanged: NO",
                });
                AssetDatabase.ImportAsset(ReportPath, ImportAssetOptions.ForceUpdate);
                AssetDatabase.SaveAssets();
                Debug.Log("[SpeakUpXR] 이름표 정면/등거리 고정과 면접관 무광 보정을 적용했습니다.");
            }
            catch (Exception exception)
            {
                Debug.LogError("[SpeakUpXR] 면접관 표시 보정 실패: " + exception);
                if (force) throw;
            }
            finally
            {
                if (closeScene && scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static T Find<T>(Scene scene) where T : Component =>
            Resources.FindObjectsOfTypeAll<T>().FirstOrDefault(value => value.gameObject.scene == scene);
    }
}
