using System.Collections;
using UnityEngine;

namespace SpeakUpXR
{
    public class InterviewerPanel : MonoBehaviour
    {
        [Tooltip("Exactly three scene-placed characters: warm, analytical, challenging")]
        public InterviewerController[] Members = new InterviewerController[3];
        public InterviewerController ActiveSpeaker { get; private set; }

        public InterviewerController Find(string personaId, string kind = null)
        {
            if (!string.IsNullOrWhiteSpace(personaId))
                foreach (var member in Members)
                    if (member && member.PersonaId == personaId) return member;

            var fallback = kind == "pressure" ? InterviewerPersonality.Challenging
                : kind == "followup" ? InterviewerPersonality.Analytical
                : InterviewerPersonality.Warm;
            foreach (var member in Members)
                if (member && member.Personality == fallback) return member;
            foreach (var member in Members) if (member) return member;
            return null;
        }

        public IEnumerator SpeakLine(CoachApi api, string line, string speakerId, string kind, string tone = "neutral")
        {
            ActiveSpeaker = Find(speakerId, kind);
            if (ActiveSpeaker) yield return ActiveSpeaker.Speak(api, line, tone);
            else
            {
                float end = Time.realtimeSinceStartup + Mathf.Clamp(1.3f + line.Length * 0.055f, 1.8f, 12f);
                while (Time.realtimeSinceStartup < end) yield return null;
            }
            ActiveSpeaker = null;
        }

        public void NodRandom()
        {
            if (Members == null || Members.Length == 0) return;
            for (int tries = 0; tries < Members.Length; tries++)
            {
                var member = Members[Random.Range(0, Members.Length)];
                if (member) { member.Nod(); return; }
            }
        }

        public Transform[] GazeTargets()
        {
            var result = new Transform[Members.Length];
            for (int i = 0; i < Members.Length; i++) result[i] = Members[i] ? Members[i].GazePoint : null;
            return result;
        }
    }
}
