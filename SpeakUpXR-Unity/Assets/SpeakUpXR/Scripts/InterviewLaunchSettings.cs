using System;
using UnityEngine;

namespace SpeakUpXR
{
    [Serializable]
    public class InterviewLaunchData
    {
        public string ProjectName = "면접 준비";
        public string JobRole = "";
        public string Situation = "";
        public string Topic = "";
        public string Difficulty = "보통";
        public int MaxQuestions = 5;
        public string[] FocusGoals = { "답변 구조", "시선 처리", "자신감" };
    }

    public static class InterviewLaunchSettings
    {
        private const string Key = "SpeakUpXR.InterviewLaunchData.v1";
        public static InterviewLaunchData Current { get; private set; }
        private static bool _launchAuthorized;

        public static bool HasSaved => PlayerPrefs.HasKey(Key) && !string.IsNullOrWhiteSpace(PlayerPrefs.GetString(Key, ""));

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRuntimeState()
        {
            Current = null;
            _launchAuthorized = false;
        }

        public static InterviewLaunchData CreateDraft() => new();

        public static InterviewLaunchData LoadSaved()
        {
            string json = PlayerPrefs.GetString(Key, "");
            return string.IsNullOrWhiteSpace(json)
                ? new InterviewLaunchData()
                : JsonUtility.FromJson<InterviewLaunchData>(json) ?? new InterviewLaunchData();
        }

        public static void SaveForLaunch(InterviewLaunchData value)
        {
            Current = value ?? new InterviewLaunchData();
            PlayerPrefs.SetString(Key, JsonUtility.ToJson(Current));
            PlayerPrefs.Save();
            _launchAuthorized = true;
        }

        public static bool TryApplyTo(InterviewSession session)
        {
            if (!session || !_launchAuthorized || Current == null) return false;
            var value = Current;
            _launchAuthorized = false;
            Apply(session, value);
            return true;
        }

        private static void Apply(InterviewSession session, InterviewLaunchData value)
        {
            session.Config.project_name = value.ProjectName;
            session.Config.job_role = value.JobRole;
            session.Config.situation = value.Situation;
            session.Config.topic = value.Topic;
            session.Config.difficulty = value.Difficulty;
            session.Config.focus_goals = value.FocusGoals ?? Array.Empty<string>();
            session.MaxQuestions = Mathf.Clamp(value.MaxQuestions, 1, 12);
        }
    }
}
