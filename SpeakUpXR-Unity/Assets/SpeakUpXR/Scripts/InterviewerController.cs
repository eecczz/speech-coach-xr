using System;
using System.Collections;
using UnityEngine;
using UniVRM10;

namespace SpeakUpXR
{
    public enum InterviewerPersonality { Warm, Analytical, Challenging }

    [Serializable]
    public class InterviewerVoice
    {
        [Tooltip("Azure Korean neural voice name")]
        public string VoiceName = "ko-KR-SunHiNeural";
        [Range(-30, 30)] public int RatePercent;
        [Range(-20, 20)] public int PitchPercent;
    }

    /// <summary>
    /// Controls a character already placed in the scene. It never loads or creates an
    /// avatar at runtime. Replace AvatarRoot in the Inspector to swap a character while
    /// preserving the seat, personality, TTS voice and session wiring.
    /// </summary>
    [DisallowMultipleComponent]
    public class InterviewerController : MonoBehaviour
    {
        [Header("Identity / routing")]
        public string PersonaId = "warm";
        public string DisplayName = "인사 면접관";
        public InterviewerPersonality Personality = InterviewerPersonality.Warm;
        public InterviewerVoice Voice = new();

        [Header("Scene references (all author-time objects)")]
        public GameObject AvatarRoot;
        [Tooltip("This interviewer's own Animator component. It must not be shared with another panel member.")]
        public Animator CharacterAnimator;
        public Transform LookTarget;
        [Tooltip("Optional mouth transform for non-VRM placeholder characters")]
        public Transform PlaceholderMouth;
        public AudioSource VoiceSource;
        [Tooltip("Optional Convai exact-speech bridge. When the locally installed Convai package is ready, its Korean voice is used before the existing TTS fallback.")]
        public ConvaiInterviewerBridge ConvaiBridge;

        [Header("TTS / gesture synchronization — editable during Play Mode")]
        [Tooltip("Speaking gesture state, start delay, source start frame and playback speed.")]
        public AnimationCueTiming SpeakingGesture = new() { CrossFadeSeconds = 0.16f };
        [Tooltip("Delay between the gesture start and audible TTS. Increase when the hands should lead the voice.")]
        [Range(0f, 1.5f)] public float AudioStartDelaySeconds = 0.08f;
        [Tooltip("How long the gesture continues after TTS ends before returning to seated idle.")]
        [Range(0f, 1.5f)] public float GestureTailSeconds = 0.12f;
        [Tooltip("Off keeps the seated idle pose and adds only a restrained neck nod while speaking.")]
        public bool UseFullBodySpeakingGesture;
        [Range(0f, 6f)] public float SubtleSpeakingNodDegrees = 1.8f;
        [Range(0.2f, 4f)] public float SubtleSpeakingNodHz = 0.72f;

        private Vrm10Instance _vrm;
        private Animator _animator;
        private Transform _neck;
        private Transform _jaw;
        private Transform _chest;
        private Quaternion _neckBase;
        private Quaternion _jawBase;
        private Quaternion _chestBase;
        private Vector3 _mouthBaseScale;
        private Vector3 _staticAvatarBasePosition;
        private Quaternion _staticAvatarBaseRotation;
        private bool _isStaticAvatar;
        private bool _speaking;
        private float _nodTime = -1f;
        private float _blinkTime;
        private float _nextBlink;
        private float _clock;
        private readonly float[] _samples = new float[128];
        private bool _hasRestrainedHeadLayer;
        private ConvaiFacialSpeechFallback _facialFallback;

        private static readonly int IsSpeakingParameter = Animator.StringToHash("IsSpeaking");
        private static readonly int GestureStyleParameter = Animator.StringToHash("GestureStyle");
        private static readonly int RestrainedHeadState = Animator.StringToHash("Speaking Head.Restrained Head Nod");

        public Transform GazePoint => _neck ? _neck : (AvatarRoot ? AvatarRoot.transform : transform);
        public bool IsSpeaking => _speaking;

        private void Awake()
        {
            if (!AvatarRoot && transform.childCount > 0) AvatarRoot = transform.GetChild(0).gameObject;
            if (!VoiceSource) VoiceSource = GetComponent<AudioSource>() ?? gameObject.AddComponent<AudioSource>();
            if (!ConvaiBridge) ConvaiBridge = GetComponent<ConvaiInterviewerBridge>();
            _facialFallback = GetComponent<ConvaiFacialSpeechFallback>();
            VoiceSource.spatialBlend = 1f;
            VoiceSource.rolloffMode = AudioRolloffMode.Linear;
            VoiceSource.minDistance = 0.6f;
            VoiceSource.maxDistance = 8f;

            _vrm = AvatarRoot ? AvatarRoot.GetComponentInChildren<Vrm10Instance>(true) : null;
            if (!CharacterAnimator && AvatarRoot)
                CharacterAnimator = AvatarRoot.GetComponentInChildren<Animator>(true);
            _animator = CharacterAnimator;
            _isStaticAvatar = AvatarRoot && !_vrm && !_animator;
            if (_isStaticAvatar)
            {
                _staticAvatarBasePosition = AvatarRoot.transform.localPosition;
                _staticAvatarBaseRotation = AvatarRoot.transform.localRotation;
            }
            if (_animator)
            {
                _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                _animator.applyRootMotion = false;
                _neck = _animator.GetBoneTransform(HumanBodyBones.Neck);
                _jaw = _animator.GetBoneTransform(HumanBodyBones.Jaw);
                _chest = _animator.GetBoneTransform(HumanBodyBones.Chest) ?? _animator.GetBoneTransform(HumanBodyBones.Spine);
                if (HasAnimatorParameter(GestureStyleParameter, AnimatorControllerParameterType.Int))
                {
                    int gestureStyle = Personality == InterviewerPersonality.Challenging
                        ? 2
                        : Personality == InterviewerPersonality.Analytical ? 1 : 0;
                    _animator.SetInteger(GestureStyleParameter, gestureStyle);
                }
                if (HasAnimatorParameter(IsSpeakingParameter, AnimatorControllerParameterType.Bool))
                    _animator.SetBool(IsSpeakingParameter, false);
                _hasRestrainedHeadLayer = _animator.layerCount > 1 && _animator.HasState(1, RestrainedHeadState);
                if (string.IsNullOrWhiteSpace(SpeakingGesture.StateName))
                    SpeakingGesture.StateName = Personality == InterviewerPersonality.Challenging
                        ? "Challenging - Angry Gesture"
                        : Personality == InterviewerPersonality.Analytical
                            ? "Analytical - Asking Question"
                            : "Warm - Sitting Talking";
            }
            if (_neck) _neckBase = _neck.localRotation;
            if (_jaw) _jawBase = _jaw.localRotation;
            if (_chest) _chestBase = _chest.localRotation;
            if (PlaceholderMouth) _mouthBaseScale = PlaceholderMouth.localScale;
            _nextBlink = UnityEngine.Random.Range(1.5f, 4.5f);

            if (_vrm && LookTarget)
            {
                _vrm.LookAtTargetType = VRM10ObjectLookAt.LookAtTargetTypes.SpecifiedTransform;
                _vrm.LookAtTarget = LookTarget;
            }
        }

        public IEnumerator Speak(CoachApi api, string text, string tone)
        {
            if (!_facialFallback) _facialFallback = GetComponent<ConvaiFacialSpeechFallback>();
            _facialFallback?.SetDialogueExpression(tone, Personality);
            if (ConvaiBridge && ConvaiBridge.UseConvaiSpeech)
            {
                bool spokenByConvai = false;
                yield return ConvaiBridge.SpeakExact(text, tone, value => spokenByConvai = value);
                if (spokenByConvai)
                {
                    _facialFallback?.ClearDialogueExpression();
                    yield break;
                }
            }

            AudioClip clip = null;
            string error = null;
            if (api) yield return api.Synthesize(text, Voice, tone, value => clip = value, value => error = value);
            float gestureStart = Time.realtimeSinceStartup + SpeakingGesture.StartDelaySeconds;
            while (Time.realtimeSinceStartup < gestureStart) yield return null;
            SetSpeaking(true);
            PlaySpeakingGesture();

            float audioStart = Time.realtimeSinceStartup + AudioStartDelaySeconds;
            while (Time.realtimeSinceStartup < audioStart) yield return null;
            if (clip && VoiceSource)
            {
                VoiceSource.clip = clip;
                VoiceSource.Play();
                while (VoiceSource.isPlaying) yield return null;
            }
            else
            {
                if (!string.IsNullOrEmpty(error)) Debug.LogWarning($"[{DisplayName}] {error}; subtitle timing fallback");
                float end = Time.realtimeSinceStartup + Mathf.Clamp(1.3f + text.Length * 0.055f, 1.8f, 12f);
                while (Time.realtimeSinceStartup < end) yield return null;
            }
            float tailEnd = Time.realtimeSinceStartup + GestureTailSeconds;
            while (Time.realtimeSinceStartup < tailEnd) yield return null;
            SetSpeaking(false);
            _facialFallback?.ClearDialogueExpression();
            if (_animator) _animator.speed = 1f;
            if (VoiceSource) VoiceSource.clip = null;
        }

        public void StopSpeaking()
        {
            SetSpeaking(false);
            if (!_facialFallback) _facialFallback = GetComponent<ConvaiFacialSpeechFallback>();
            _facialFallback?.ClearDialogueExpression();
            if (VoiceSource) VoiceSource.Stop();
            if (_animator) _animator.speed = 1f;
        }

        private void SetSpeaking(bool value)
        {
            _speaking = value;
            if (_animator && HasAnimatorParameter(IsSpeakingParameter, AnimatorControllerParameterType.Bool))
                _animator.SetBool(IsSpeakingParameter, value);
        }

        /// <summary>Called by the reflection-safe Convai bridge when remote speech starts or stops.</summary>
        public void SetExternalSpeaking(bool value)
        {
            SetSpeaking(value);
            if (value) PlaySpeakingGesture();
            else if (_animator) _animator.speed = 1f;
        }

        /// <summary>Maps Convai's emotion stream to deliberately restrained interview-panel reactions.</summary>
        public void ApplyExternalEmotion(string emotion, int intensity)
        {
            if (string.IsNullOrWhiteSpace(emotion)) return;
            string normalized = emotion.Trim().ToLowerInvariant();
            if (normalized.Contains("joy") || normalized.Contains("happy") || normalized.Contains("approval"))
                Nod();
            else if ((normalized.Contains("surprise") || normalized.Contains("curious")) && intensity > 1)
                _nodTime = 0.12f;
            if (!_facialFallback) _facialFallback = GetComponent<ConvaiFacialSpeechFallback>();
            _facialFallback?.SetExternalEmotion(normalized, intensity);
        }

        private void PlaySpeakingGesture()
        {
            if (!_animator || SpeakingGesture == null) return;
            _animator.speed = Mathf.Max(0.1f, SpeakingGesture.Speed);
            if (!UseFullBodySpeakingGesture && _hasRestrainedHeadLayer)
            {
                _animator.CrossFadeInFixedTime(RestrainedHeadState, SpeakingGesture.CrossFadeSeconds,
                    1, SpeakingGesture.NormalizedStart);
                return;
            }
            if (!UseFullBodySpeakingGesture || string.IsNullOrWhiteSpace(SpeakingGesture.StateName)) return;
            int stateHash = Animator.StringToHash("Base Layer." + SpeakingGesture.StateName);
            if (_animator.HasState(0, stateHash))
                _animator.CrossFadeInFixedTime(stateHash, SpeakingGesture.CrossFadeSeconds, 0, SpeakingGesture.NormalizedStart);
        }

        private bool HasAnimatorParameter(int nameHash, AnimatorControllerParameterType type)
        {
            if (!_animator || !_animator.runtimeAnimatorController) return false;
            foreach (var parameter in _animator.parameters)
                if (parameter.nameHash == nameHash && parameter.type == type) return true;
            return false;
        }

        public void Nod() => _nodTime = 0f;

        private void LateUpdate()
        {
            float dt = Time.unscaledDeltaTime;
            _clock += dt;
            if (_speaking && _animator && SpeakingGesture != null)
                _animator.speed = Mathf.Max(0.1f, SpeakingGesture.Speed);
            if (_chest)
                _chest.localRotation *= Quaternion.Euler(Mathf.Sin(_clock * 1.4f) * 0.45f, 0f, 0f);

            // Restrained fallback motion for a scene-authored character without a rig.
            if (_isStaticAvatar && AvatarRoot)
            {
                float breathe = Mathf.Sin(_clock * 1.35f);
                float attention = _speaking ? 1f : 0f;
                AvatarRoot.transform.localPosition = _staticAvatarBasePosition
                    + new Vector3(0f, breathe * 0.0035f, attention * -0.018f);
                AvatarRoot.transform.localRotation = _staticAvatarBaseRotation
                    * Quaternion.Euler(breathe * 0.35f + attention * 1.25f, Mathf.Sin(_clock * 0.43f) * 0.45f, 0f);
            }

            // Fallback only for a character/controller without the authored head layer.
            float neckNod = _speaking && !_hasRestrainedHeadLayer
                ? Mathf.Sin(_clock * Mathf.PI * 2f * SubtleSpeakingNodHz) * SubtleSpeakingNodDegrees
                : 0f;
            if (_nodTime >= 0f)
            {
                _nodTime += dt;
                float dip = Mathf.Sin(Mathf.Clamp01(_nodTime / 0.6f) * Mathf.PI);
                neckNod += dip * 7f;
                if (!_neck && _isStaticAvatar && AvatarRoot)
                    AvatarRoot.transform.localRotation = _staticAvatarBaseRotation * Quaternion.Euler(dip * 7f, 0f, 0f);
                if (_nodTime >= 0.6f) _nodTime = -1f;
            }
            if (_neck) _neck.localRotation *= Quaternion.Euler(neckNod, 0f, 0f);

            // Convai applies server-produced visemes directly to the facial blendshapes.
            // Do not overwrite them with the old amplitude/sine-wave mouth fallback.
            bool externalFaceAnimation = ConvaiBridge && ConvaiBridge.IsRemoteSpeaking;
            if (!externalFaceAnimation)
            {
                float mouth = GetMouthWeight();
                if (_vrm) _vrm.Runtime.Expression.SetWeight(ExpressionKey.Aa, mouth);
                if (_jaw)
                    _jaw.localRotation = _jawBase * Quaternion.Euler(mouth * 7.5f, 0f, 0f);
                if (PlaceholderMouth)
                    PlaceholderMouth.localScale = new Vector3(_mouthBaseScale.x, _mouthBaseScale.y * Mathf.Lerp(0.35f, 1.5f, mouth), _mouthBaseScale.z);
            }

            if (_vrm)
            {
                _blinkTime += dt;
                float blink = 0f;
                if (_blinkTime > _nextBlink)
                {
                    float p = (_blinkTime - _nextBlink) / 0.12f;
                    blink = p < 1f ? Mathf.Sin(p * Mathf.PI) : 0f;
                    if (p >= 1f) { _blinkTime = 0f; _nextBlink = UnityEngine.Random.Range(2f, 5f); }
                }
                _vrm.Runtime.Expression.SetWeight(ExpressionKey.Blink, blink);
            }

        }

        private float GetMouthWeight()
        {
            if (!_speaking) return 0f;
            if (VoiceSource && VoiceSource.isPlaying)
            {
                VoiceSource.GetOutputData(_samples, 0);
                float rms = 0f;
                foreach (float sample in _samples) rms += sample * sample;
                return Mathf.Clamp01(Mathf.Sqrt(rms / _samples.Length) * 12f);
            }
            return (Mathf.Sin(_clock * 14f) * 0.5f + 0.5f) * 0.7f;
        }
    }
}
