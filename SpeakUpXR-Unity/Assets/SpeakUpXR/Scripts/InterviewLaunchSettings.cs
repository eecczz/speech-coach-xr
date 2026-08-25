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

        /// <summary>
        /// Allows Interview.unity to be played directly from the Editor. Reuse the
        /// last menu selection when one exists; otherwise use a neutral, non-job-
        /// specific practice setup instead of redirecting away from the scene.
        /// </summary>
        public static void ApplyDirectSceneDefaults(InterviewSession session)
        {
            if (!session) return;
            InterviewLaunchData value = HasSaved ? LoadSaved() : new InterviewLaunchData
            {
                ProjectName = "면접 연습",
                JobRole = "지원 직무",
                Situation = "실무 면접",
                Topic = "지원자의 경험과 직무 적합성",
                Difficulty = "보통",
                MaxQuestions = 5,
                FocusGoals = new[] { "답변 구조", "시선 처리", "자신감" },
            };
            // Playing Interview.unity directly is an editor/test convenience, not a
            // second launch from the menu. Keep the saved topic, but do not let a
            // previously selected one-question smoke test make every later Play run
            // finish immediately after the motivation question.
            value.MaxQuestions = Mathf.Clamp(Mathf.Max(5, value.MaxQuestions), 5, 12);
            Current = value;
            Apply(session, value);
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
