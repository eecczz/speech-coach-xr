using System;
using System.Collections;
using UnityEngine;

namespace SpeakUpXR
{
    [Serializable]
    public class VoiceCandidate
    {
        public string Label;
        public InterviewerVoice Voice = new();
    }

    /// <summary>
    /// Auditions natural multilingual voices and applies three distinct male voices.
    /// The custom Inspector exposes one-click sample playback during Play Mode.
    /// </summary>
    public class VoiceCastingController : MonoBehaviour
    {
        public InterviewerPanel Panel;
        [TextArea] public string SampleLine = "안녕하세요. 오늘 면접을 진행할 면접관입니다.";

        public VoiceCandidate[] MaleCandidates =
        {
            Candidate("인준 · 차분한 중저음", "ko-KR-InJoonNeural", -8, -4),
            Candidate("현수 · 또렷한 실무형", "ko-KR-HyunsuMultilingualNeural", 0, 0),
            Candidate("앤드류 · 자연스러운 다국어형", "en-US-AndrewMultilingualNeural", -4, -2),
            Candidate("브라이언 · 차분한 다국어형", "en-US-BrianMultilingualNeural", -6, -3),
        };

        public VoiceCandidate[] FemaleCandidates =
        {
            Candidate("선희 · 따뜻한 안내", "ko-KR-SunHiNeural", -6, 3),
            Candidate("선희 · 차분한 중립", "ko-KR-SunHiNeural", -2, 0),
            Candidate("선희 · 빠르고 명확한 진행", "ko-KR-SunHiNeural", 6, 1),
        };

        [Header("Selected cast")]
        public int HrMaleIndex;
        public int TechnicalMaleIndex = 1;
        public int ExecutiveMaleIndex = 2;

        private CoachApi _api;
        private AudioSource _previewSource;
        private Coroutine _preview;

        private void Awake()
        {
            _api = GetComponent<CoachApi>() ?? FindFirstObjectByType<CoachApi>();
            if (!TryGetComponent(out _previewSource))
                _previewSource = gameObject.AddComponent<AudioSource>();
            _previewSource.spatialBlend = 0f;
            ApplySelection();
        }

        public void ApplySelection()
        {
            if (!Panel) Panel = FindFirstObjectByType<InterviewerPanel>();
            if (!Panel || Panel.Members == null) return;
            foreach (var member in Panel.Members)
            {
                if (!member) continue;
                VoiceCandidate selected = member.PersonaId switch
                {
                    "warm" => At(MaleCandidates, HrMaleIndex),
                    "analytical" => At(MaleCandidates, TechnicalMaleIndex),
                    _ => At(MaleCandidates, ExecutiveMaleIndex),
                };
                if (selected?.Voice == null) continue;
                member.Voice.VoiceName = selected.Voice.VoiceName;
                member.Voice.RatePercent = selected.Voice.RatePercent;
                member.Voice.PitchPercent = selected.Voice.PitchPercent;
            }
        }

        public void PreviewMale(int index) => Preview(At(MaleCandidates, index));
        public void PreviewFemale(int index) => Preview(At(FemaleCandidates, index));

        private void Preview(VoiceCandidate candidate)
        {
            if (!Application.isPlaying || candidate?.Voice == null) return;
            if (_preview != null) StopCoroutine(_preview);
            if (_previewSource) _previewSource.Stop();
            _preview = StartCoroutine(PreviewRoutine(candidate));
        }

        private IEnumerator PreviewRoutine(VoiceCandidate candidate)
        {
            if (!_api) _api = FindFirstObjectByType<CoachApi>();
            AudioClip clip = null;
            string error = null;
            if (_api)
                yield return _api.Synthesize(SampleLine, candidate.Voice, "neutral", value => clip = value, value => error = value);
            if (clip && _previewSource)
            {
                _previewSource.clip = clip;
                _previewSource.Play();
            }
            else if (!string.IsNullOrWhiteSpace(error)) Debug.LogWarning("TTS sample failed: " + error);
        }

        private static VoiceCandidate At(VoiceCandidate[] values, int index)
            => values != null && values.Length > 0 ? values[Mathf.Clamp(index, 0, values.Length - 1)] : null;

        private static VoiceCandidate Candidate(string label, string voice, int rate, int pitch)
        {
            return new VoiceCandidate
            {
                Label = label,
                Voice = new InterviewerVoice { VoiceName = voice, RatePercent = rate, PitchPercent = pitch }
            };
        }
    }
}
