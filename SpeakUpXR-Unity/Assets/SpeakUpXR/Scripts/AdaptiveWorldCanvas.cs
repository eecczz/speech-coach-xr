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

        private void Awake()
        {
            if (!OverlayWithoutXr || XRSettings.isDeviceActive)
                return;

            var canvas = GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.worldCamera = null;

            var scaler = GetComponent<CanvasScaler>();
            if (!scaler) scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var rect = (RectTransform)transform;
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;
            rect.anchorMin = DesktopAnchor;
            rect.anchorMax = DesktopAnchor;
            rect.pivot = DesktopPivot;
            rect.anchoredPosition = DesktopPosition;
            rect.sizeDelta = DesktopSize;
        }
    }
}
