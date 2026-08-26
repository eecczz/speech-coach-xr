// World-space question/status panel — C# port of interview-ui.ts.
// Uses legacy UGUI Text with a dynamic font so Korean renders without a TMP font atlas.

using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace SpeakUpXR
{
    public enum HudTone { Ask, Listen, Think, Done }

    public class InterviewHud : MonoBehaviour
    {
        public Text QuestionText;
        public Text StatusText;
        public Text InterimText;
        public Text SpeakerText;
        public Image StatusPill;
        [Range(8f, 60f)] public float CharactersPerSecond = 24f;
        public bool CompactCompanionLayout;

        private Coroutine _questionTyping;

        private static readonly Color AskColor = new(0.18f, 0.17f, 0.23f);
        private static readonly Color ListenColor = new(0.02f, 0.59f, 0.41f);
        private static readonly Color ThinkColor = new(0.73f, 0.45f, 0.08f);
        private static readonly Color DoneColor = new(0.42f, 0.41f, 0.50f);

        private void OnEnable()
        {
            // These are authored scene objects, but older scene-repair passes could
            // leave the Canvas in world space or recolor/deactivate its children.
            // Reassert visibility without creating another full-screen container.
            Canvas canvas = GetComponent<Canvas>();
            if (canvas) canvas.enabled = true;

            Transform dialogue = transform.Find("DialogueBox_BOTTOM_ONLY");
            if (dialogue) dialogue.gameObject.SetActive(true);
            RectTransform dialogueRect = dialogue as RectTransform;
            if (dialogueRect && !CompactCompanionLayout)
            {
                dialogueRect.anchorMin = dialogueRect.anchorMax = new Vector2(0.5f, 0f);
                dialogueRect.pivot = new Vector2(0.5f, 0f);
                dialogueRect.anchoredPosition = new Vector2(0f, 18f);
                dialogueRect.sizeDelta = new Vector2(1600f, 250f);
            }
            SetTextVisible(SpeakerText, new Color(0.43f, 0.66f, 1f));
            SetTextVisible(QuestionText, new Color(0.94f, 0.96f, 1f));
            SetTextVisible(InterimText, new Color(0.67f, 0.71f, 0.78f));
            SetTextVisible(StatusText, new Color(0.43f, 0.66f, 1f));
            ResizeText(SpeakerText, CompactCompanionLayout ? 22 : 28, CompactCompanionLayout ? 610f : 1040f);
            ResizeText(QuestionText, CompactCompanionLayout ? 28 : 40, CompactCompanionLayout ? 610f : 1520f);
            ResizeText(InterimText, CompactCompanionLayout ? 18 : 23, CompactCompanionLayout ? 610f : 1520f);
            ResizeText(StatusText, CompactCompanionLayout ? 18 : 23, CompactCompanionLayout ? 240f : 440f);

            if (dialogue)
            {
                Image background = dialogue.GetComponentInChildren<Image>(true);
                if (background)
                {
                    background.gameObject.SetActive(true);
                    background.enabled = true;
                    background.color = new Color(0.035f, 0.045f, 0.065f, 0.92f);
                }
            }
            Canvas.ForceUpdateCanvases();
        }

        private static void SetTextVisible(Text text, Color color)
        {
            if (!text) return;
            text.gameObject.SetActive(true);
            text.enabled = true;
            text.color = color;
        }

        private static void ResizeText(Text text, int fontSize, float width)
        {
            if (!text) return;
            text.fontSize = fontSize;
            text.rectTransform.sizeDelta = new Vector2(width, text.rectTransform.sizeDelta.y);
        }

        public void SetQuestion(string text)
        {
            if (_questionTyping != null)
            {
                StopCoroutine(_questionTyping);
                _questionTyping = null;
            }
            if (QuestionText)
            {
                if (isActiveAndEnabled && gameObject.activeInHierarchy)
                    _questionTyping = StartCoroutine(TypeQuestion(text ?? string.Empty));
                else
                    QuestionText.text = text ?? string.Empty;
            }
            SetInterim("");
        }

        public float EstimateTypingSeconds(string text) =>
            string.IsNullOrEmpty(text) ? 0f : text.Length / Mathf.Max(1f, CharactersPerSecond);

        private IEnumerator TypeQuestion(string fullText)
        {
            QuestionText.text = string.Empty;
            if (string.IsNullOrEmpty(fullText))
            {
                _questionTyping = null;
                yield break;
            }
            float shown = 0f;
            while (shown < fullText.Length)
            {
                shown += CharactersPerSecond * Time.unscaledDeltaTime;
                QuestionText.text = fullText.Substring(0, Mathf.Min(fullText.Length, Mathf.FloorToInt(shown)));
                yield return null;
            }
            QuestionText.text = fullText;
            _questionTyping = null;
        }

        public void SetSpeaker(string text)
        {
            if (SpeakerText) SpeakerText.text = text;
        }

        public void SetStatus(string text, HudTone tone)
        {
            if (StatusText) StatusText.text = text;
            Color toneColor = tone switch
            {
                HudTone.Listen => ListenColor,
                HudTone.Think => ThinkColor,
                HudTone.Done => DoneColor,
                _ => AskColor,
            };
            if (StatusPill)
                StatusPill.color = toneColor;
            else if (StatusText)
                StatusText.color = toneColor;
        }

        public void SetInterim(string text)
        {
            if (InterimText) InterimText.text = string.IsNullOrEmpty(text) ? "" : $"“{text}”";
        }

        /// <summary>Yaw-only billboard toward the user's head. Call from Update.</summary>
        public void FaceCamera(Transform head)
        {
            if (!head) return;
            var p = transform.position;
            var d = head.position - p;
            transform.rotation = Quaternion.Euler(0, Mathf.Atan2(-d.x, -d.z) * Mathf.Rad2Deg, 0);
        }
    }
}
