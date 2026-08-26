using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SpeakUpXR
{
    public class InterviewMenuController : MonoBehaviour
    {
        public CoachApi Api;
        public InputField ProjectInput;
        public InputField JobRoleInput;
        public InputField SituationInput;
        public InputField TopicInput;
        public Dropdown DifficultyDropdown;
        public Dropdown QuestionCountDropdown;
        public Toggle[] FocusToggles;
        public Text ProviderStatusText;
        public Button StartButton;
        public Button LoadPreviousButton;
        public string InterviewSceneName = "Interview";

        private void Start()
        {
            Populate(InterviewLaunchSettings.CreateDraft());
            if (LoadPreviousButton)
            {
                LoadPreviousButton.gameObject.SetActive(InterviewLaunchSettings.HasSaved);
                LoadPreviousButton.onClick.AddListener(LoadPreviousSettings);
            }
            if (StartButton) StartButton.onClick.AddListener(StartInterview);
            StartCoroutine(CheckProvider());
        }

        private void Populate(InterviewLaunchData data)
        {
            if (ProjectInput) ProjectInput.text = data.ProjectName;
            if (JobRoleInput) JobRoleInput.text = data.JobRole;
            if (SituationInput) SituationInput.text = data.Situation;
            if (TopicInput) TopicInput.text = data.Topic;
            if (DifficultyDropdown) DifficultyDropdown.value = Mathf.Max(0, DifficultyDropdown.options.FindIndex(o => o.text == data.Difficulty));
            if (QuestionCountDropdown) QuestionCountDropdown.value = Mathf.Max(0, QuestionCountDropdown.options.FindIndex(o => o.text.StartsWith(data.MaxQuestions.ToString())));
            foreach (var toggle in FocusToggles ?? System.Array.Empty<Toggle>())
                if (toggle) toggle.isOn = System.Array.Exists(data.FocusGoals ?? System.Array.Empty<string>(), value => value == toggle.GetComponentInChildren<Text>()?.text);
        }

        public void LoadPreviousSettings()
        {
            Populate(InterviewLaunchSettings.LoadSaved());
            if (ProviderStatusText)
            {
                ProviderStatusText.text = "이전 설정을 불러왔습니다 · 내용을 확인한 뒤 시작해 주세요";
                ProviderStatusText.color = new Color(0.4196f, 0.4078f, 0.502f);
            }
        }

        private IEnumerator CheckProvider()
        {
            if (ProviderStatusText) ProviderStatusText.text = "AI 면접관 연결 확인 중…";
            CoachHealthResponse health = null;
            string error = null;
            for (int attempt = 0; attempt < 8 && health == null; attempt++)
            {
                error = null;
                if (Api) yield return Api.Health(value => health = value, value => error = value);
                if (health == null) yield return new WaitForSecondsRealtime(0.65f);
            }
            if (!ProviderStatusText) yield break;
            if (health == null)
            {
                ProviderStatusText.text = "AI 서버 연결 안 됨 · 로컬 AI가 준비될 때까지 면접 시작이 대기합니다";
                ProviderStatusText.color = new Color(0.72f, 0.24f, 0.24f);
            }
            else if (health.provider == "mock")
            {
                ProviderStatusText.text = "데모 질문 모드 · API 키 설정 시 실제 LLM 면접으로 전환됩니다";
                ProviderStatusText.color = new Color(0.73f, 0.45f, 0.08f);
            }
            else
            {
                ProviderStatusText.text = $"실시간 AI 면접관 연결됨 · {health.provider} / {health.model}";
                ProviderStatusText.color = new Color(0.05f, 0.56f, 0.38f);
            }
            if (!string.IsNullOrWhiteSpace(error)) Debug.LogWarning("[menu] provider check: " + error);
        }

        public void StartInterview()
        {
            var missing = new List<string>();
            if (!HasValue(JobRoleInput)) missing.Add("지원 직무");
            if (!HasValue(SituationInput)) missing.Add("면접 상황");
            if (!HasValue(TopicInput)) missing.Add("핵심 주제");
            if (missing.Count > 0)
            {
                if (ProviderStatusText)
                {
                    ProviderStatusText.text = string.Join(" · ", missing) + " 항목을 입력해 주세요";
                    ProviderStatusText.color = new Color(0.72f, 0.24f, 0.24f);
                }
                MarkRequired(JobRoleInput, HasValue(JobRoleInput));
                MarkRequired(SituationInput, HasValue(SituationInput));
                MarkRequired(TopicInput, HasValue(TopicInput));
                return;
            }
            var goals = new List<string>();
            foreach (var toggle in FocusToggles ?? System.Array.Empty<Toggle>())
                if (toggle && toggle.isOn) goals.Add(toggle.GetComponentInChildren<Text>()?.text ?? "");
            int count = 5;
            if (QuestionCountDropdown && !int.TryParse(QuestionCountDropdown.options[QuestionCountDropdown.value].text.Split(' ')[0], out count)) count = 5;
            InterviewLaunchSettings.SaveForLaunch(new InterviewLaunchData
            {
                ProjectName = Value(ProjectInput, "면접 준비"),
                JobRole = JobRoleInput.text.Trim(),
                Situation = SituationInput.text.Trim(),
                Topic = TopicInput.text.Trim(),
                Difficulty = DifficultyDropdown ? DifficultyDropdown.options[DifficultyDropdown.value].text : "보통",
                MaxQuestions = count,
                FocusGoals = goals.ToArray(),
            });
            SceneManager.LoadScene(InterviewSceneName);
        }

        private static bool HasValue(InputField input) => input && !string.IsNullOrWhiteSpace(input.text);

        private static void MarkRequired(InputField input, bool valid)
        {
            if (!input) return;
            var image = input.GetComponent<Image>();
            if (image) image.color = valid ? new Color(0.98f, 0.984f, 1f) : new Color(1f, 0.91f, 0.91f);
        }

        private static string Value(InputField input, string fallback) =>
            input && !string.IsNullOrWhiteSpace(input.text) ? input.text.Trim() : fallback;
    }
}
