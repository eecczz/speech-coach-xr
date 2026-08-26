// 데스크톱/에디터 미리보기용 시점 회전 — 우클릭 드래그로 고개를 돌린다.
// Quest 실기에서는 마우스가 없고 TrackedPoseDriver(헤드트래킹)가 카메라를 잡으므로 무해.

using UnityEngine;

namespace SpeakUpXR
{
    public class DesktopLook : MonoBehaviour
    {
        public float Sensitivity = 2.5f;
        private float _yaw, _pitch;

        public void ResetView()
        {
            _yaw = 0f;
            _pitch = 0f;
            transform.localRotation = Quaternion.identity;
        }

        private void Update()
        {
            if (!Input.GetMouseButton(1)) return;
            _yaw += Input.GetAxis("Mouse X") * Sensitivity;
            _pitch = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * Sensitivity, -80f, 80f);
            transform.localRotation = Quaternion.Euler(_pitch, _yaw, 0);
        }
    }
}
