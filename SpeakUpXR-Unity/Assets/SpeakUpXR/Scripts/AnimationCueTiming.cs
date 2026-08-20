using System;
using UnityEngine;

namespace SpeakUpXR
{
    /// <summary>
    /// Inspector-editable playback controls shared by entrance and speaking animations.
    /// Values are read immediately before every playback, so they can be tuned in Play Mode.
    /// </summary>
    [Serializable]
    public class AnimationCueTiming
    {
        [Tooltip("Animator state name, including a sub-state machine path when required.")]
        public string StateName;
        [Tooltip("Delay after the cue is requested, in unscaled seconds.")]
        [Min(0f)] public float StartDelaySeconds;
        [Tooltip("Source animation frame to start from. This is converted using Source Frame Rate.")]
        [Min(0)] public int StartFrame;
        [Tooltip("Frame rate used when converting Start Frame into normalized animation time.")]
        [Min(1f)] public float SourceFrameRate = 30f;
        [Tooltip("Playback speed. Changes made during Play Mode affect the next cue immediately.")]
        [Range(0.1f, 3f)] public float Speed = 1f;
        [Tooltip("Cross-fade time from the current state.")]
        [Range(0f, 1f)] public float CrossFadeSeconds = 0.16f;
        [Tooltip("Optional source clip used to convert Start Frame precisely.")]
        public AnimationClip Clip;

        public float NormalizedStart
        {
            get
            {
                if (!Clip || Clip.length <= 0.001f) return 0f;
                float seconds = StartFrame / Mathf.Max(1f, SourceFrameRate);
                return Mathf.Repeat(seconds / Clip.length, 1f);
            }
        }

        public float RemainingDuration
        {
            get
            {
                if (!Clip) return 0f;
                float startSeconds = StartFrame / Mathf.Max(1f, SourceFrameRate);
                return Mathf.Max(0f, Clip.length - startSeconds) / Mathf.Max(0.1f, Speed);
            }
        }
    }
}
