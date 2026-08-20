// World-space question/status panel — C# port of interview-ui.ts.
// Uses legacy UGUI Text with a dynamic font so Korean renders without a TMP font atlas.

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

        private static readonly Color AskColor = new(0.18f, 0.17f, 0.23f);
        private static readonly Color ListenColor = new(0.02f, 0.59f, 0.41f);
        private static readonly Color ThinkColor = new(0.73f, 0.45f, 0.08f);
        private static readonly Color DoneColor = new(0.42f, 0.41f, 0.50f);

        public void SetQuestion(string text)
        {
            if (QuestionText) QuestionText.text = text;
            SetInterim("");
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
