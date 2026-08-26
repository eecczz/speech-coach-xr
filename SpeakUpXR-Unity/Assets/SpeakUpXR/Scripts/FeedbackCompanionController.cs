using System.Collections;
using UnityEngine;

namespace SpeakUpXR
{
    /// <summary>
    /// Head-locked, scene-authored coaching companion. Its TTS, typewriter text and
    /// talking animation start together after synthesis has completed.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FeedbackCompanionController : MonoBehaviour
    {
        public Transform HeadAnchor;
        public GameObject CharacterRoot;
        public Animator CharacterAnimator;
        public InterviewHud DialogueHud;
        public CanvasGroup DialogueGroup;
        public CoachApi Api;
        public AudioSource VoiceSource;
        public InterviewerVoice Voice = new()
        {
            VoiceName = "ko-KR-InJoonNeural",
            RatePercent = 3,
            PitchPercent = 1,
        };

        [Header("Head-relative placement")]
        public Vector3 LocalOffset = new(-0.43f, 0.02f, 0.65f);
        public Vector3 LocalEuler = new(0f, 180f, 0f);
        public Vector3 LocalScale = Vector3.one * 0.14f;
        public bool FaceHeadCamera = true;
        [Tooltip("모델의 정면 축이 +Z와 다를 때 사용하는 회전 보정값입니다.")]
        public Vector3 LookAtEulerOffset = Vector3.zero;

        [Header("Dialogue")]
        public string DisplayName = "예티 코치";
        [Range(0.2f, 5f)] public float VisibleTailSeconds = 1.8f;

        private static readonly int IsSpeaking = Animator.StringToHash("IsSpeaking");
        private int _speechGeneration;

        private void Awake()
        {
            if (!HeadAnchor && transform.parent) HeadAnchor = transform.parent;
            if (!CharacterRoot && transform.childCount > 0) CharacterRoot = transform.GetChild(0).gameObject;
            if (!CharacterAnimator && CharacterRoot)
                CharacterAnimator = CharacterRoot.GetComponentInChildren<Animator>(true);
            if (!Api) Api = FindFirstObjectByType<CoachApi>();
            if (!VoiceSource) VoiceSource = GetComponent<AudioSource>() ?? gameObject.AddComponent<AudioSource>();
            Voice ??= new InterviewerVoice();
            VoiceSource.playOnAwake = false;
            VoiceSource.spatialBlend = 0f;
            ApplyPlacement();
            SetTalking(false);
            if (DialogueGroup) DialogueGroup.alpha = 0f;
        }

        private void LateUpdate() => ApplyPlacement();

        private void ApplyPlacement()
        {
            if (HeadAnchor && transform.parent != HeadAnchor) transform.SetParent(HeadAnchor, false);
            transform.localPosition = LocalOffset;
            transform.localScale = LocalScale;
            if (!CharacterRoot) return;
            Transform visual = CharacterRoot.transform;
            if (!FaceHeadCamera || !HeadAnchor)
            {
                visual.localRotation = Quaternion.Euler(LocalEuler);
                return;
            }

            Vector3 toCamera = HeadAnchor.position - visual.position;
            if (toCamera.sqrMagnitude < 0.000001f) return;
            visual.rotation = Quaternion.LookRotation(toCamera.normalized, HeadAnchor.up) *
                              Quaternion.Euler(LookAtEulerOffset);
        }

        public IEnumerator SpeakFeedback(string message, string tone = "neutral")
        {
            if (string.IsNullOrWhiteSpace(message)) yield break;
            int generation = ++_speechGeneration;
            if (DialogueGroup)
            {
                DialogueGroup.alpha = 1f;
                DialogueGroup.interactable = false;
                DialogueGroup.blocksRaycasts = false;
            }
            if (DialogueHud)
            {
                DialogueHud.SetSpeaker(DisplayName);
                DialogueHud.SetStatus(tone == "critique" ? "교정 피드백" : "실시간 코칭", HudTone.Think);
                DialogueHud.PrepareQuestion(message);
            }

            VoiceSource?.Stop();
            SetTalking(false);
            AudioClip clip = null;
            string ttsError = null;
            if (Api)
                yield return Api.Synthesize(message, Voice, tone, value => clip = value, value => ttsError = value);
            if (generation != _speechGeneration)
            {
                if (clip) Destroy(clip);
                yield break;
            }

            float speechSeconds = clip
                ? Mathf.Max(0.1f, clip.length)
                : Mathf.Clamp(0.55f + message.Length / 6.2f, 1.2f, 14f);
            DialogueHud?.PlaySynchronizedQuestion(message, speechSeconds);
            SetTalking(true);
            if (clip && VoiceSource)
            {
                VoiceSource.clip = clip;
                VoiceSource.Play();
                while (generation == _speechGeneration && VoiceSource.isPlaying) yield return null;
                VoiceSource.clip = null;
                Destroy(clip);
            }
            else
            {
                float deadline = Time.realtimeSinceStartup + speechSeconds;
                while (generation == _speechGeneration && Time.realtimeSinceStartup < deadline) yield return null;
            }
            if (generation != _speechGeneration) yield break;

            DialogueHud?.CompleteQuestion(message);
            SetTalking(false);
            if (!string.IsNullOrWhiteSpace(ttsError))
                Debug.LogWarning($"[예티 코치] TTS fallback: {ttsError}");
            float hideAt = Time.realtimeSinceStartup + VisibleTailSeconds;
            while (generation == _speechGeneration && Time.realtimeSinceStartup < hideAt)
                yield return null;
            if (generation == _speechGeneration && DialogueGroup) DialogueGroup.alpha = 0f;
        }

        private void SetTalking(bool talking)
        {
            if (!CharacterAnimator) return;
            CharacterAnimator.speed = 1f;
            foreach (AnimatorControllerParameter parameter in CharacterAnimator.parameters)
            {
                if (parameter.nameHash != IsSpeaking || parameter.type != AnimatorControllerParameterType.Bool) continue;
                CharacterAnimator.SetBool(IsSpeaking, talking);
                return;
            }
            // Imported assets without a generated controller still remain animated.
            CharacterAnimator.speed = talking ? 1.25f : 0.75f;
        }
    }
}
