using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpeakUpXR
{
    /// <summary>
    /// Keeps a face-rigged interviewer from becoming visually frozen when a turn
    /// temporarily falls back from Convai Narrative Speech to the local TTS route.
    /// Server visemes always win whenever the Convai lip-sync component is actively
    /// changing the mapped facial blendshapes.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    [DisallowMultipleComponent]
    public sealed class ConvaiFacialSpeechFallback : MonoBehaviour
    {
        public InterviewerController Owner;
        public GameObject FaceRoot;
        [Range(0.5f, 2f)] public float Strength = 1f;
        [Range(4f, 30f)] public float Response = 18f;

        private readonly List<VisemeTarget> _targets = new();
        private readonly float[] _samples = new float[128];
        private AudioSource[] _sources = Array.Empty<AudioSource>();
        private float _envelope;
        private bool _localDriving;
        private bool _reported;
        private float _serverVisemeUntil;
        private string _expression = "neutral";
        private float _expressionWeight;
        private float _expressionTarget;

        private sealed class VisemeTarget
        {
            public SkinnedMeshRenderer Renderer;
            public int Open = -1;
            public int Wide = -1;
            public int Tight = -1;
            public int Round = -1;
            public float LastOpen;
            public float LastWide;
            public float LastTight;
            public float LastRound;
            public float ObservedOpen;
            public float ObservedWide;
            public float ObservedTight;
            public float ObservedRound;
            public bool ObservedInitialized;
            public int BrowRaiseInnerL = -1;
            public int BrowRaiseInnerR = -1;
            public int BrowRaiseOuterL = -1;
            public int BrowRaiseOuterR = -1;
            public int BrowDropL = -1;
            public int BrowDropR = -1;
            public int EyeSquintL = -1;
            public int EyeSquintR = -1;
            public int CheekRaiseL = -1;
            public int CheekRaiseR = -1;
            public int SmileL = -1;
            public int SmileR = -1;
            public int FrownL = -1;
            public int FrownR = -1;
        }

        private void Awake() => Rebind();

        private void OnEnable() => Rebind();

        private void OnDisable()
        {
            _expressionTarget = 0f;
            _expressionWeight = 0f;
            _envelope = 0f;
            _localDriving = false;
        }

        [ContextMenu("얼굴 립싱크 대체 드라이버 다시 연결")]
        public void Rebind()
        {
            if (!Owner) Owner = GetComponent<InterviewerController>();
            if (!FaceRoot && Owner) FaceRoot = Owner.AvatarRoot;
            _envelope = 0f;
            _localDriving = false;
            _reported = false;
            _serverVisemeUntil = 0f;
            _expressionWeight = 0f;
            _expressionTarget = 0f;
            _expression = "neutral";
            _targets.Clear();
            if (FaceRoot)
            {
                foreach (SkinnedMeshRenderer renderer in FaceRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    Mesh mesh = renderer.sharedMesh;
                    if (!mesh || mesh.blendShapeCount == 0) continue;
                    var target = new VisemeTarget { Renderer = renderer };
                    for (int i = 0; i < mesh.blendShapeCount; i++)
                    {
                        string key = mesh.GetBlendShapeName(i).Replace(" ", "_").ToLowerInvariant();
                        if (key == "v_open" || key.EndsWith(".v_open")) target.Open = i;
                        else if (key == "v_wide" || key.EndsWith(".v_wide")) target.Wide = i;
                        else if (key == "v_tight" || key.EndsWith(".v_tight")) target.Tight = i;
                        else if (key == "v_tight_o" || key.EndsWith(".v_tight_o")) target.Round = i;
                        else if (EndsWith(key, "brow_raise_inner_l")) target.BrowRaiseInnerL = i;
                        else if (EndsWith(key, "brow_raise_inner_r")) target.BrowRaiseInnerR = i;
                        else if (EndsWith(key, "brow_raise_outer_l")) target.BrowRaiseOuterL = i;
                        else if (EndsWith(key, "brow_raise_outer_r")) target.BrowRaiseOuterR = i;
                        else if (EndsWith(key, "brow_drop_l")) target.BrowDropL = i;
                        else if (EndsWith(key, "brow_drop_r")) target.BrowDropR = i;
                        else if (EndsWith(key, "eye_squint_l")) target.EyeSquintL = i;
                        else if (EndsWith(key, "eye_squint_r")) target.EyeSquintR = i;
                        else if (EndsWith(key, "cheek_raise_l")) target.CheekRaiseL = i;
                        else if (EndsWith(key, "cheek_raise_r")) target.CheekRaiseR = i;
                        else if (EndsWith(key, "mouth_smile_l")) target.SmileL = i;
                        else if (EndsWith(key, "mouth_smile_r")) target.SmileR = i;
                        else if (EndsWith(key, "mouth_frown_l")) target.FrownL = i;
                        else if (EndsWith(key, "mouth_frown_r")) target.FrownR = i;
                    }
                    if (HasViseme(target) || HasExpression(target))
                    {
                        CaptureObserved(target);
                        _targets.Add(target);
                    }
                }
                _sources = FaceRoot.GetComponentsInChildren<AudioSource>(true);
            }
        }

        /// <summary>Contextual local expression used by Coach TTS turns.</summary>
        public void SetDialogueExpression(string tone, InterviewerPersonality personality)
        {
            _expression = tone == "warm" || personality == InterviewerPersonality.Warm
                ? "warm"
                : tone == "challenging" || personality == InterviewerPersonality.Challenging
                    ? "challenging"
                    : "analytical";
            _expressionTarget = 1f;
        }

        public void ClearDialogueExpression() => _expressionTarget = 0f;

        /// <summary>Fallback interpretation when the Convai emotion event is available.</summary>
        public void SetExternalEmotion(string emotion, int intensity)
        {
            if (string.IsNullOrWhiteSpace(emotion)) return;
            _expression = emotion.Contains("joy") || emotion.Contains("happy") || emotion.Contains("approval")
                ? "warm"
                : emotion.Contains("anger") || emotion.Contains("disgust") || emotion.Contains("serious")
                    ? "challenging"
                    : "analytical";
            _expressionTarget = Mathf.Clamp01(Mathf.Max(1, intensity) / 4f);
        }

        private void LateUpdate()
        {
            if (!Owner || !FaceRoot || _targets.Count == 0) return;
            bool speaking = Owner.IsSpeaking;
            bool remoteFace = Owner.ConvaiBridge && Owner.ConvaiBridge.IsRemoteSpeaking;
            _expressionWeight = Mathf.MoveTowards(
                _expressionWeight,
                speaking ? _expressionTarget : 0f,
                Time.unscaledDeltaTime * (speaking ? 3.5f : 5f));
            // Convai's native EmotionController owns the remote face. The local
            // expression layer is reserved for Coach-TTS/offline fallback turns.
            if (!remoteFace) ApplyExpression(_expressionWeight);
            if (!speaking)
            {
                _envelope = Mathf.MoveTowards(_envelope, 0f, Time.unscaledDeltaTime * 8f);
                if (_localDriving) ApplyLocal(_envelope, 0f);
                if (_envelope <= 0.001f) _localDriving = false;
                return;
            }

            if (ServerVisemeChanged())
            {
                _localDriving = false;
                return;
            }

            float amplitude = ReadAmplitude();
            if (amplitude <= 0.002f)
                amplitude = 0.11f + (Mathf.Sin(Time.unscaledTime * 12.7f) * 0.5f + 0.5f) * 0.09f;
            float desired = Mathf.Clamp01(amplitude * 8f) * Strength;
            _envelope = Mathf.Lerp(_envelope, desired, 1f - Mathf.Exp(-Response * Time.unscaledDeltaTime));
            ApplyLocal(_envelope, Time.unscaledTime);
            _localDriving = true;
            if (!_reported)
            {
                _reported = true;
                Debug.Log($"[SpeakUpXR LipSync] {Owner.DisplayName}: local facial-viseme fallback active; targets={_targets.Count}");
            }
        }

        private bool ServerVisemeChanged()
        {
            bool changed = false;
            foreach (VisemeTarget target in _targets)
            {
                if (!HasViseme(target)) continue;
                float open = Get(target.Renderer, target.Open);
                float wide = Get(target.Renderer, target.Wide);
                float tight = Get(target.Renderer, target.Tight);
                float round = Get(target.Renderer, target.Round);
                if (_localDriving)
                {
                    changed |= Mathf.Abs(open - target.LastOpen) > 0.75f
                               || Mathf.Abs(wide - target.LastWide) > 0.75f
                               || Mathf.Abs(tight - target.LastTight) > 0.75f
                               || Mathf.Abs(round - target.LastRound) > 0.75f;
                }
                else if (target.ObservedInitialized)
                {
                    changed |= Mathf.Abs(open - target.ObservedOpen) > 0.75f
                               || Mathf.Abs(wide - target.ObservedWide) > 0.75f
                               || Mathf.Abs(tight - target.ObservedTight) > 0.75f
                               || Mathf.Abs(round - target.ObservedRound) > 0.75f;
                }
                target.ObservedOpen = open;
                target.ObservedWide = wide;
                target.ObservedTight = tight;
                target.ObservedRound = round;
                target.ObservedInitialized = true;
            }
            if (changed) _serverVisemeUntil = Time.realtimeSinceStartup + 0.3f;
            return changed || Time.realtimeSinceStartup < _serverVisemeUntil;
        }

        private float ReadAmplitude()
        {
            float max = 0f;
            if (Owner.VoiceSource) max = Mathf.Max(max, ReadAmplitude(Owner.VoiceSource));
            foreach (AudioSource source in _sources) max = Mathf.Max(max, ReadAmplitude(source));
            return max;
        }

        private float ReadAmplitude(AudioSource source)
        {
            if (!source || !source.isPlaying) return 0f;
            source.GetOutputData(_samples, 0);
            float sum = 0f;
            foreach (float sample in _samples) sum += sample * sample;
            return Mathf.Sqrt(sum / _samples.Length);
        }

        private void ApplyLocal(float open, float time)
        {
            float cadence = Mathf.Sin(time * 9.3f) * 0.5f + 0.5f;
            foreach (VisemeTarget target in _targets)
            {
                target.LastOpen = Mathf.Clamp(open * 88f, 0f, 100f);
                target.LastWide = Mathf.Clamp(open * cadence * 36f, 0f, 60f);
                target.LastTight = Mathf.Clamp(open * (1f - cadence) * 24f, 0f, 45f);
                target.LastRound = Mathf.Clamp(open * (0.35f + (1f - cadence) * 0.35f) * 28f, 0f, 50f);
                Set(target.Renderer, target.Open, target.LastOpen);
                Set(target.Renderer, target.Wide, target.LastWide);
                Set(target.Renderer, target.Tight, target.LastTight);
                Set(target.Renderer, target.Round, target.LastRound);
                target.ObservedOpen = target.LastOpen;
                target.ObservedWide = target.LastWide;
                target.ObservedTight = target.LastTight;
                target.ObservedRound = target.LastRound;
                target.ObservedInitialized = true;
            }
        }

        private void ApplyExpression(float weight)
        {
            foreach (VisemeTarget target in _targets)
            {
                SetWarm(target, 0f);
                SetFirm(target, 0f);
                SetAnalytical(target, 0f);
                if (_expression == "warm")
                {
                    Set(target.Renderer, target.SmileL, weight * 18f);
                    Set(target.Renderer, target.SmileR, weight * 18f);
                    Set(target.Renderer, target.CheekRaiseL, weight * 6f);
                    Set(target.Renderer, target.CheekRaiseR, weight * 6f);
                    Set(target.Renderer, target.BrowRaiseInnerL, weight * 3f);
                    Set(target.Renderer, target.BrowRaiseInnerR, weight * 3f);
                }
                else if (_expression == "challenging")
                {
                    Set(target.Renderer, target.BrowDropL, weight * 14f);
                    Set(target.Renderer, target.BrowDropR, weight * 14f);
                    Set(target.Renderer, target.EyeSquintL, weight * 6f);
                    Set(target.Renderer, target.EyeSquintR, weight * 6f);
                    Set(target.Renderer, target.FrownL, weight * 4f);
                    Set(target.Renderer, target.FrownR, weight * 4f);
                }
                else
                {
                    Set(target.Renderer, target.BrowRaiseInnerL, weight * 7f);
                    Set(target.Renderer, target.BrowRaiseInnerR, weight * 7f);
                    Set(target.Renderer, target.BrowRaiseOuterL, weight * 3f);
                    Set(target.Renderer, target.BrowRaiseOuterR, weight * 3f);
                }
            }
        }

        private static void SetWarm(VisemeTarget target, float value)
        {
            Set(target.Renderer, target.SmileL, value);
            Set(target.Renderer, target.SmileR, value);
            Set(target.Renderer, target.CheekRaiseL, value);
            Set(target.Renderer, target.CheekRaiseR, value);
        }

        private static void SetFirm(VisemeTarget target, float value)
        {
            Set(target.Renderer, target.BrowDropL, value);
            Set(target.Renderer, target.BrowDropR, value);
            Set(target.Renderer, target.EyeSquintL, value);
            Set(target.Renderer, target.EyeSquintR, value);
            Set(target.Renderer, target.FrownL, value);
            Set(target.Renderer, target.FrownR, value);
        }

        private static void SetAnalytical(VisemeTarget target, float value)
        {
            Set(target.Renderer, target.BrowRaiseInnerL, value);
            Set(target.Renderer, target.BrowRaiseInnerR, value);
            Set(target.Renderer, target.BrowRaiseOuterL, value);
            Set(target.Renderer, target.BrowRaiseOuterR, value);
        }

        private static bool EndsWith(string key, string name) => key == name || key.EndsWith("." + name);

        private static bool HasViseme(VisemeTarget target) =>
            target.Open >= 0 || target.Wide >= 0 || target.Tight >= 0 || target.Round >= 0;

        private static bool HasExpression(VisemeTarget target) =>
            target.SmileL >= 0 || target.BrowDropL >= 0 || target.BrowRaiseInnerL >= 0;

        private static void CaptureObserved(VisemeTarget target)
        {
            target.ObservedOpen = Get(target.Renderer, target.Open);
            target.ObservedWide = Get(target.Renderer, target.Wide);
            target.ObservedTight = Get(target.Renderer, target.Tight);
            target.ObservedRound = Get(target.Renderer, target.Round);
            target.ObservedInitialized = true;
        }

        private static float Get(SkinnedMeshRenderer renderer, int index) =>
            renderer && index >= 0 ? renderer.GetBlendShapeWeight(index) : 0f;

        private static void Set(SkinnedMeshRenderer renderer, int index, float value)
        {
            if (renderer && index >= 0) renderer.SetBlendShapeWeight(index, value);
        }
    }
}
