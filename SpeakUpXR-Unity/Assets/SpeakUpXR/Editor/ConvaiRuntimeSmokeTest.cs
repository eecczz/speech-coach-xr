using System;
using System.IO;
using SpeakUpXR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SpeakUpXR.Editor
{
    internal static class ConvaiRuntimeSmokeTest
    {
        private const string SandboxScene = "Assets/SpeakUpXR/Scenes/ConvaiSandbox.unity";
        private const string ReportPath = "Assets/SpeakUpXR/UI/convai-runtime-smoke-v5.txt";
        private const string RunningKey = "SpeakUpXR.ConvaiSmokeV5.Running";
        private const string SentKey = "SpeakUpXR.ConvaiSmokeV5.Sent";
        private const string ResultKey = "SpeakUpXR.ConvaiSmokeV5.Result";

        private static double _enteredAt;
        private static double _sentAt;

        [InitializeOnLoadMethod]
        private static void Schedule()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            if (UnityEditor.SessionState.GetBool(RunningKey, false) && EditorApplication.isPlaying)
            {
                _enteredAt = EditorApplication.timeSinceStartup;
                EditorApplication.update -= Tick;
                EditorApplication.update += Tick;
                return;
            }
            // Runtime smoke tests are explicit menu actions only. Never replace the
            // user's active scene or start Play Mode automatically during normal work.
        }

        [MenuItem("SpeakUpXR/Convai/한국어 음성·Viseme 런타임 시험")]
        private static void StartFromMenu()
        {
            if (File.Exists(ReportPath)) File.Delete(ReportPath);
            StartIfNeeded();
        }

        private static void StartIfNeeded()
        {
            if (File.Exists(ReportPath) || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (UnityEditor.SessionState.GetBool(RunningKey, false)) return;
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SandboxScene) == null) return;

            UnityEditor.SessionState.SetBool(RunningKey, true);
            UnityEditor.SessionState.SetBool(SentKey, false);
            UnityEditor.SessionState.SetString(ResultKey, "");
            EditorSceneManager.OpenScene(SandboxScene, OpenSceneMode.Single);
            EditorApplication.isPlaying = true;
        }

        private static void OnPlayModeChanged(UnityEditor.PlayModeStateChange change)
        {
            if (change == UnityEditor.PlayModeStateChange.EnteredPlayMode)
            {
                _enteredAt = EditorApplication.timeSinceStartup;
                EditorApplication.update += Tick;
            }
            else if (change == UnityEditor.PlayModeStateChange.EnteredEditMode)
            {
                EditorApplication.update -= Tick;
                EditorApplication.playModeStateChanged -= OnPlayModeChanged;
                string result = UnityEditor.SessionState.GetString(ResultKey, "");
                if (string.IsNullOrWhiteSpace(result)) result = "Status: STOPPED_WITHOUT_RESULT";
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath) ?? "Assets/SpeakUpXR/UI");
                File.WriteAllText(ReportPath,
                    "SpeakUpXR Convai runtime Korean speech test\n" +
                    $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" + result + "\n");
                AssetDatabase.ImportAsset(ReportPath, ImportAssetOptions.ForceUpdate);
                UnityEditor.SessionState.SetBool(RunningKey, false);
                Debug.Log("[SpeakUpXR] Convai 한국어 음성·Viseme 런타임 시험이 끝났습니다. " + result);
            }
        }

        private static void Tick()
        {
            ConvaiSandboxProbe probe = UnityEngine.Object.FindFirstObjectByType<ConvaiSandboxProbe>();
            if (!probe) return;

            double now = EditorApplication.timeSinceStartup;
            bool sent = UnityEditor.SessionState.GetBool(SentKey, false);
            // Narrative Speech is ignored until both the room and character are ready.
            // Waiting on the SDK's actual readiness flag removes the startup race from v1.
            if (!sent && probe.ConversationReady)
            {
                probe.SendExactKoreanSpeechProbe();
                _sentAt = now;
                UnityEditor.SessionState.SetBool(SentKey, true);
                return;
            }

            if (!sent)
            {
                if (now - _enteredAt >= 150d)
                {
                    UnityEditor.SessionState.SetString(ResultKey,
                        "Status: CONNECTION_TIMEOUT\n" +
                        $"CharacterId: {probe.CharacterId}\n" +
                        $"Diagnostic: {probe.Diagnostic}\n" +
                        "Convai character did not reach IsInConversation within 150 seconds.");
                    EditorApplication.isPlaying = false;
                }
                return;
            }
            bool transcriptReceived = !string.IsNullOrWhiteSpace(probe.LastTranscript);
            bool remoteSpeechObserved = probe.SpeechObserved;
            if ((transcriptReceived || remoteSpeechObserved) && now - _sentAt >= 2d)
            {
                UnityEditor.SessionState.SetString(ResultKey,
                    "Status: PASS\n" +
                    $"CharacterId: {probe.CharacterId}\n" +
                    $"Transcript: {probe.LastTranscript}\n" +
                    $"RemoteSpeechObserved: {remoteSpeechObserved}\n" +
                    $"Emotion: {probe.LastEmotion}\n" +
                    $"Diagnostic: {probe.Diagnostic}\n" +
                    "Expected visual path: Convai remote audio + server viseme frames + emotion events");
                EditorApplication.isPlaying = false;
            }
            else if (now - _sentAt >= 45d)
            {
                UnityEditor.SessionState.SetString(ResultKey,
                    "Status: TIMEOUT\n" +
                    $"CharacterId: {probe.CharacterId}\n" +
                    $"Transcript: {probe.LastTranscript}\n" +
                    $"Emotion: {probe.LastEmotion}\n" +
                    $"Diagnostic: {probe.Diagnostic}\n" +
                    "Check Convai entitlement, API key, network, and character readiness in Console.");
                EditorApplication.isPlaying = false;
            }
        }
    }
}
