using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

namespace SpeakUpXR
{
    /// <summary>
    /// Keeps the authored world-space canvas in XR, but guarantees that the same
    /// UI is visible as a normal screen overlay when Play is started without a headset.
    /// </summary>
    [RequireComponent(typeof(Canvas))]
    public class AdaptiveWorldCanvas : MonoBehaviour
    {
        public bool OverlayWithoutXr = true;
        public Vector2 DesktopAnchor = new(0.5f, 1f);
        public Vector2 DesktopPivot = new(0.5f, 1f);
        public Vector2 DesktopPosition = new(0f, -24f);
        public Vector2 DesktopSize = new(1100f, 300f);
        [Header("XR head-locked layout")]
        public bool AttachToHeadInXr;
        public Vector3 XrLocalPosition = new(0f, -0.30f, 0.78f);
        public Vector3 XrLocalEuler;
        public float XrWorldScale = 0.00072f;

        private Canvas _canvas;
        private bool _xrAttached;
        private string _reportedLayout;

        private void Awake()
        {
            _canvas = GetComponent<Canvas>();
            _canvas.enabled = true;
            ApplyBestAvailableLayout();
        }

        private IEnumerator Start()
        {
            // XR loaders and the player head camera may become available after this
            // Canvas.Awake. Retry instead of leaving the authored world-space canvas
            // behind the player where neither desktop nor headset can see it.
            for (int frame = 0; frame < 180; frame++)
            {
                if (ApplyBestAvailableLayout() && (_xrAttached || !XRSettings.isDeviceActive))
                    yield break;
                yield return null;
            }

            if (!_xrAttached && OverlayWithoutXr) ApplyDesktopOverlay();
        }

        private bool ApplyBestAvailableLayout()
        {
            if (XRSettings.isDeviceActive && AttachToHeadInXr)
            {
                Camera head = FindHeadCamera();
                if (!head) return false;
                ApplyXrHeadLocked(head);
                return true;
            }

            if (!OverlayWithoutXr) return false;
            ApplyDesktopOverlay();
            return true;
        }

        private Camera FindHeadCamera()
        {
            FirstPersonAvatarController player = FindFirstObjectByType<FirstPersonAvatarController>();
            if (player && player.HeadCamera) return player.HeadCamera;
            Camera head = Camera.main;
            if (head && head.isActiveAndEnabled) return head;
            foreach (Camera candidate in Camera.allCameras)
                if (candidate && candidate.isActiveAndEnabled) return candidate;
            return null;
        }

        private void ApplyXrHeadLocked(Camera head)
        {
            _xrAttached = true;
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.worldCamera = head;
            _canvas.overrideSorting = true;
            _canvas.sortingOrder = 1000;
            transform.SetParent(head.transform, false);
            transform.localPosition = XrLocalPosition;
            transform.localRotation = Quaternion.Euler(XrLocalEuler);
            transform.localScale = Vector3.one * XrWorldScale;
            var rect = (RectTransform)transform;
            rect.sizeDelta = DesktopSize;
            transform.SetAsLastSibling();
            ReportLayout("XR_HEAD_LOCKED", head);
        }

        private void ApplyDesktopOverlay()
        {
            _xrAttached = false;
            transform.SetParent(null, true);

            Camera desktopCamera = FindHeadCamera();
            if (desktopCamera)
            {
                // ScreenSpaceOverlay was omitted from the current Unity Game/XR output
                // path. Rendering through the actual player camera guarantees that the
                // dialogue is present in both Game view captures and the desktop mirror.
                _canvas.renderMode = RenderMode.ScreenSpaceCamera;
                _canvas.worldCamera = desktopCamera;
                _canvas.planeDistance = Mathf.Max(desktopCamera.nearClipPlane + 0.08f, 0.15f);
            }
            else
            {
                _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _canvas.worldCamera = null;
            }
            _canvas.overrideSorting = true;
            _canvas.sortingOrder = 1000;

            var scaler = GetComponent<CanvasScaler>();
            if (!scaler) scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            // The docked Unity Game view can become extremely wide and short. Width
            // matching keeps dialogue text legible instead of shrinking it to a few pixels.
            scaler.matchWidthOrHeight = 0f;

            var rect = (RectTransform)transform;
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;
            rect.anchorMin = DesktopAnchor;
            rect.anchorMax = DesktopAnchor;
            rect.pivot = DesktopPivot;
            rect.anchoredPosition = DesktopPosition;
            rect.sizeDelta = DesktopSize;
            transform.SetAsLastSibling();
            ReportLayout(desktopCamera ? "DESKTOP_CAMERA" : "DESKTOP_OVERLAY", desktopCamera);
        }

        private void ReportLayout(string mode, Camera head)
        {
            if (_reportedLayout == mode) return;
            _reportedLayout = mode;
            Debug.Log($"[SpeakUpXR HUD] {name}: {mode}; canvas={_canvas.enabled}; " +
                      $"active={gameObject.activeInHierarchy}; camera={(head ? head.name : "screen")}");
        }
    }
}
