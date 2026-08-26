using System.Collections;
using UnityEngine;

namespace SpeakUpXR
{
    /// <summary>
    /// Head-locked, scene-authored coaching companion. It never speaks TTS: all
    /// multimodal feedback is presented through its own typewriter dialogue UI.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FeedbackCompanionController : MonoBehaviour
    {
        public Transform HeadAnchor;
        public GameObject CharacterRoot;
        public Animator CharacterAnimator;
        public InterviewHud DialogueHud;
        public CanvasGroup DialogueGroup;

        [Header("Head-relative placement")]
        public Vector3 LocalOffset = new(-0.43f, 0.24f, 0.85f);
        public Vector3 LocalEuler = new(0f, 180f, 0f);
        public Vector3 LocalScale = Vector3.one * 0.14f;

        [Header("Dialogue")]
        public string DisplayName = "면접 코치";
        [Range(0.2f, 5f)] public float VisibleTailSeconds = 1.8f;

        private static readonly int IsSpeaking = Animator.StringToHash("IsSpeaking");
        private int _speechGeneration;

        private void Awake()
        {
            if (!HeadAnchor && transform.parent) HeadAnchor = transform.parent;
            if (!CharacterRoot && transform.childCount > 0) CharacterRoot = transform.GetChild(0).gameObject;
            if (!CharacterAnimator && CharacterRoot)
                CharacterAnimator = CharacterRoot.GetComponentInChildren<Animator>(true);
            ApplyPlacement();
            SetTalking(false);
            if (DialogueGroup) DialogueGroup.alpha = 0f;
        }

        private void LateUpdate() => ApplyPlacement();

        private void ApplyPlacement()
        {
            if (HeadAnchor && transform.parent != HeadAnchor) transform.SetParent(HeadAnchor, false);
            transform.localPosition = LocalOffset;
            transform.localRotation = Quaternion.Euler(LocalEuler);
            transform.localScale = LocalScale;
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
                DialogueHud.SetQuestion(message);
            }
            SetTalking(true);

            float typingSeconds = DialogueHud ? DialogueHud.EstimateTypingSeconds(message) : message.Length / 20f;
            float deadline = Time.realtimeSinceStartup + Mathf.Max(1.2f, typingSeconds + 0.25f);
            while (generation == _speechGeneration && Time.realtimeSinceStartup < deadline)
                yield return null;
            if (generation != _speechGeneration) yield break;

            SetTalking(false);
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
