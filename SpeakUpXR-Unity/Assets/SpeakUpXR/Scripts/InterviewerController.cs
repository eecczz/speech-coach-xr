// VRM interviewer — C# port of interviewer.ts (gaze, blink, breathing, mouth, nod).
// Loads default.vrm from StreamingAssets at runtime through the VRM10 importer with
// canLoadVrm0X, so one code path covers VRM 0.x and 1.0 (0.x is auto-migrated).

using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UniVRM10;

namespace SpeakUpXR
{
    public class InterviewerController : MonoBehaviour
    {
        [Tooltip("VRM file name inside StreamingAssets")]
        public string VrmFileName = "default.vrm";
        [Tooltip("Where the interviewer looks (the user's head camera)")]
        public Transform LookTarget;

        private Vrm10Instance _vrm;
        private Transform _neck;
        private Transform _chest;
        private Quaternion _neckBase, _chestBase;

        private bool _speaking;
        private float _nodT = -1f;
        private float _blinkT, _blinkNext = 2.5f;
        private float _clock;

        private IEnumerator Start()
        {
            // UnityWebRequest works for StreamingAssets on both macOS editor and Android APK.
            var url = System.IO.Path.Combine(Application.streamingAssetsPath, VrmFileName);
            if (!url.Contains("://")) url = "file://" + url;
            using var www = UnityWebRequest.Get(url);
            yield return www.SendWebRequest();
            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[interviewer] VRM load failed: {www.error}");
                yield break;
            }

            var task = Vrm10.LoadBytesAsync(www.downloadHandler.data, canLoadVrm0X: true);
            while (!task.IsCompleted) yield return null;
            _vrm = task.Result;
            var root = _vrm.gameObject;
            root.name = "Interviewer";
            root.transform.SetParent(transform, false);
            // Parent (this GameObject) is placed/rotated by SceneBootstrap.

            var anim = root.GetComponent<Animator>();
            _neck = anim ? anim.GetBoneTransform(HumanBodyBones.Neck) : null;
            _chest = anim ? anim.GetBoneTransform(HumanBodyBones.Chest)
                          ?? anim.GetBoneTransform(HumanBodyBones.Spine) : null;
            if (_neck) _neckBase = _neck.localRotation;
            if (_chest) _chestBase = _chest.localRotation;

            ApplyRestingPose(anim);

            if (LookTarget != null)
            {
                _vrm.LookAtTargetType = VRM10ObjectLookAt.LookAtTargetTypes.SpecifiedTransform;
                _vrm.LookAtTarget = LookTarget;
            }
        }

        /// <summary>Drop arms from T-pose to a natural resting posture (port of applyRestingPose).</summary>
        private static void ApplyRestingPose(Animator anim)
        {
            if (!anim) return;
            Rotate(anim, HumanBodyBones.LeftUpperArm, 0, 0, 75f);
            Rotate(anim, HumanBodyBones.RightUpperArm, 0, 0, -75f);
            Rotate(anim, HumanBodyBones.LeftLowerArm, 12f, 0, 0);
            Rotate(anim, HumanBodyBones.RightLowerArm, 12f, 0, 0);
        }

        private static void Rotate(Animator anim, HumanBodyBones bone, float x, float y, float z)
        {
            var t = anim.GetBoneTransform(bone);
            if (t) t.localRotation *= Quaternion.Euler(x, y, z);
        }

        public void SetSpeaking(bool on) => _speaking = on;
        public void Nod() => _nodT = 0f;

        private void LateUpdate()
        {
            if (_vrm == null) return;
            var dt = Time.deltaTime;
            _clock += dt;

            // Breathing — subtle chest pitch oscillation.
            if (_chest) _chest.localRotation = _chestBase * Quaternion.Euler(Mathf.Sin(_clock * 1.4f) * 0.7f, 0, 0);

            // Acknowledging nod (~0.6s dip).
            if (_nodT >= 0f)
            {
                _nodT += dt;
                float dip = Mathf.Sin(Mathf.Clamp01(_nodT / 0.6f) * Mathf.PI);
                if (_neck) _neck.localRotation = _neckBase * Quaternion.Euler(dip * 10f, 0, 0);
                if (_nodT >= 0.6f) { _nodT = -1f; if (_neck) _neck.localRotation = _neckBase; }
            }

            // Talking mouth + periodic blink via VRM expressions.
            var expr = _vrm.Runtime.Expression;
            float mouth = _speaking ? (Mathf.Sin(_clock * 14f) * 0.5f + 0.5f) * 0.7f : 0f;
            expr.SetWeight(ExpressionKey.Aa, mouth);

            _blinkT += dt;
            float blink = 0f;
            if (_blinkT > _blinkNext)
            {
                float p = (_blinkT - _blinkNext) / 0.12f;
                blink = p < 1f ? Mathf.Sin(p * Mathf.PI) : 0f;
                if (p >= 1f) { _blinkT = 0f; _blinkNext = Random.Range(2f, 5f); }
            }
            expr.SetWeight(ExpressionKey.Blink, blink);
        }
    }
}
