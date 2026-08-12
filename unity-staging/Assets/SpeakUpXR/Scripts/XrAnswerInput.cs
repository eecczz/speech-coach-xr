using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace SpeakUpXR
{
    /// <summary>Quest controller shortcut without requiring an InputAction asset.</summary>
    public class XrAnswerInput : MonoBehaviour
    {
        public InterviewSession Session;
        public bool UsePrimaryButton = true;
        public bool UseTriggerButton = true;
        private bool _wasPressed;
        private readonly List<InputDevice> _devices = new();

        private void Update()
        {
            InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Right | InputDeviceCharacteristics.Controller, _devices);
            bool pressed = false;
            foreach (var device in _devices)
            {
                if (UsePrimaryButton && device.TryGetFeatureValue(CommonUsages.primaryButton, out bool primary)) pressed |= primary;
                if (UseTriggerButton && device.TryGetFeatureValue(CommonUsages.triggerButton, out bool trigger)) pressed |= trigger;
            }
            if (pressed && !_wasPressed) Submit();
            _wasPressed = pressed;
        }

        public void Submit()
        {
            if (!Session) return;
            if (Session.State == SessionState.Idle) Session.StartInterview();
            else if (Session.State == SessionState.Listening) Session.FinishAnswer();
        }
    }
}
