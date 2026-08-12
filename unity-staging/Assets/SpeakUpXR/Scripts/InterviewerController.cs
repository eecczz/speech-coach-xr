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
        public string DisplayName = "따뜻한 인사 담당";
        public InterviewerPersonality Personality = InterviewerPersonality.Warm;
        public InterviewerVoice Voice = new();

        [Header("Scene references (all author-time objects)")]
        public GameObject AvatarRoot;
        public Transform LookTarget;
        [Tooltip("Optional mouth transform for non-VRM placeholder characters")]
        public Transform PlaceholderMouth;
        public AudioSource VoiceSource;

        private Vrm10Instance _vrm;
        private Animator _animator;
        private Transform _neck;
        private Transform _chest;
        private Quaternion _neckBase;
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

        private static readonly int IsSpeakingParameter = Animator.StringToHash("IsSpeaking");
        private static readonly int GestureStyleParameter = Animator.StringToHash("GestureStyle");

        public Transform GazePoint => _neck ? _neck : (AvatarRoot ? AvatarRoot.transform : transform);

        private void Awake()
        {
            if (!AvatarRoot && transform.childCount > 0) AvatarRoot = transform.GetChild(0).gameObject;
            if (!VoiceSource) VoiceSource = GetComponent<AudioSource>() ?? gameObject.AddComponent<AudioSource>();
            VoiceSource.spatialBlend = 1f;
            VoiceSource.rolloffMode = AudioRolloffMode.Linear;
            VoiceSource.minDistance = 0.6f;
            VoiceSource.maxDistance = 8f;

            _vrm = AvatarRoot ? AvatarRoot.GetComponentInChildren<Vrm10Instance>(true) : null;
            _animator = AvatarRoot ? AvatarRoot.GetComponentInChildren<Animator>(true) : null;
            _isStaticAvatar = AvatarRoot && !_vrm && !_animator;
            if (_isStaticAvatar)
            {
                _staticAvatarBasePosition = AvatarRoot.transform.localPosition;
                _staticAvatarBaseRotation = AvatarRoot.transform.localRotation;
            }
            if (_animator)
            {
                _neck = _animator.GetBoneTransform(HumanBodyBones.Neck);
                _chest = _animator.GetBoneTransform(HumanBodyBones.Chest) ?? _animator.GetBoneTransform(HumanBodyBones.Spine);
                if (HasAnimatorParameter(GestureStyleParameter, AnimatorControllerParameterType.Int))
                {
                    int gestureStyle = Personality == InterviewerPersonality.Challenging
                        ? 2
                        : Personality == InterviewerPersonality.Analytical ? 1 : 0;
                    _animator.SetInteger(GestureStyleParameter, gestureStyle);
                }
            }
            if (_neck) _neckBase = _neck.localRotation;
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
            AudioClip clip = null;
            string error = null;
            if (api) yield return api.Synthesize(text, Voice, tone, value => clip = value, value => error = value);
            SetSpeaking(true);
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
            SetSpeaking(false);
            if (VoiceSource) VoiceSource.clip = null;
        }

        public void StopSpeaking()
        {
            SetSpeaking(false);
            if (VoiceSource) VoiceSource.Stop();
        }

        private void SetSpeaking(bool value)
        {
            _speaking = value;
            if (_animator && HasAnimatorParameter(IsSpeakingParameter, AnimatorControllerParameterType.Bool))
                _animator.SetBool(IsSpeakingParameter, value);
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
            if (_chest) _chest.localRotation = _chestBase * Quaternion.Euler(Mathf.Sin(_clock * 1.4f) * 0.7f, 0, 0);

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

            if (_nodTime >= 0f)
            {
                _nodTime += dt;
                float dip = Mathf.Sin(Mathf.Clamp01(_nodTime / 0.6f) * Mathf.PI);
                if (_neck) _neck.localRotation = _neckBase * Quaternion.Euler(dip * 10f, 0, 0);
                else if (_isStaticAvatar && AvatarRoot)
                    AvatarRoot.transform.localRotation = _staticAvatarBaseRotation * Quaternion.Euler(dip * 7f, 0f, 0f);
                if (_nodTime >= 0.6f) { _nodTime = -1f; if (_neck) _neck.localRotation = _neckBase; }
            }

            float mouth = GetMouthWeight();
            if (_vrm) _vrm.Runtime.Expression.SetWeight(ExpressionKey.Aa, mouth);
            if (PlaceholderMouth)
                PlaceholderMouth.localScale = new Vector3(_mouthBaseScale.x, _mouthBaseScale.y * Mathf.Lerp(0.35f, 1.5f, mouth), _mouthBaseScale.z);

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
