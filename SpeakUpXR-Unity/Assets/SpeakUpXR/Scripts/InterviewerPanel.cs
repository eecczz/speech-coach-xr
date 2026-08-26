using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SpeakUpXR
{
    public class InterviewerPanel : MonoBehaviour
    {
        [Tooltip("Exactly three scene-placed characters: warm, analytical, challenging")]
        public InterviewerController[] Members = new InterviewerController[3];
        public InterviewerController ActiveSpeaker { get; private set; }

        private void Awake()
        {
            var animators = new HashSet<Animator>();
            foreach (var member in Members)
            {
                if (!member) continue;
                if (!member.CharacterAnimator && member.AvatarRoot)
                    member.CharacterAnimator = member.AvatarRoot.GetComponentInChildren<Animator>(true);
                if (member.CharacterAnimator && !animators.Add(member.CharacterAnimator))
                    Debug.LogError("[interview panel] Two interviewers reference the same Animator. Each seat must own an independent Animator.", member);
            }
        }

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

        public IEnumerator SpeakLine(CoachApi api, string line, string speakerId, string kind,
            string tone = "neutral", Action<float> speechStarted = null)
        {
            ActiveSpeaker = Find(speakerId, kind);
            // Explicit turn ownership: no previous/other Animator or AudioSource may
            // remain in a speaking state while this panel member has the floor.
            foreach (var member in Members)
                if (member && member != ActiveSpeaker) member.StopSpeaking();
            if (ActiveSpeaker) yield return ActiveSpeaker.Speak(api, line, tone, speechStarted);
            else
            {
                float fallbackSeconds = Mathf.Clamp(1.3f + line.Length * 0.12f, 1.8f, 12f);
                speechStarted?.Invoke(fallbackSeconds);
                float end = Time.realtimeSinceStartup + fallbackSeconds;
                while (Time.realtimeSinceStartup < end) yield return null;
            }
            ActiveSpeaker = null;
        }

        public void NodRandom()
        {
            if (Members == null || Members.Length == 0) return;
            for (int tries = 0; tries < Members.Length; tries++)
            {
                var member = Members[UnityEngine.Random.Range(0, Members.Length)];
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
