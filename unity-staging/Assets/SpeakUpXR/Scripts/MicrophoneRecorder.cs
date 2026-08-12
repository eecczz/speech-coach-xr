using System;
using System.IO;
using UnityEngine;

namespace SpeakUpXR
{
    /// <summary>Records one answer from the active microphone and emits a PCM WAV.</summary>
    public class MicrophoneRecorder : MonoBehaviour
    {
        public string PreferredDevice = "";
        [Range(8000, 48000)] public int SampleRate = 16000;
        [Range(15, 300)] public int MaxAnswerSeconds = 180;
        public bool IsRecording { get; private set; }

        private AudioClip _clip;
        private string _device;

        private void Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
#endif
        }

        public bool Begin()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            {
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
                Debug.LogWarning("[microphone] permission requested; press answer again after accepting");
                return false;
            }
#endif
            if (Microphone.devices.Length == 0)
            {
                Debug.LogWarning("[microphone] no input device; use desktop answer override");
                return false;
            }
            _device = Array.Exists(Microphone.devices, d => d == PreferredDevice) ? PreferredDevice : Microphone.devices[0];
            _clip = Microphone.Start(_device, false, MaxAnswerSeconds, SampleRate);
            IsRecording = _clip;
            return IsRecording;
        }

        public byte[] End()
        {
            if (!IsRecording || !_clip) return null;
            int frames = Mathf.Max(0, Microphone.GetPosition(_device));
            Microphone.End(_device);
            IsRecording = false;
            if (frames <= SampleRate / 4) return null;

            int channels = _clip.channels;
            var samples = new float[frames * channels];
            _clip.GetData(samples, 0);
            Destroy(_clip);
            _clip = null;
            return EncodeWav(samples, channels, SampleRate);
        }

        private static byte[] EncodeWav(float[] samples, int channels, int sampleRate)
        {
            const short bits = 16;
            using var stream = new MemoryStream(44 + samples.Length * 2);
            using var writer = new BinaryWriter(stream);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + samples.Length * 2);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * bits / 8);
            writer.Write((short)(channels * bits / 8));
            writer.Write(bits);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(samples.Length * 2);
            foreach (float value in samples)
                writer.Write((short)(Mathf.Clamp(value, -1f, 1f) * short.MaxValue));
            writer.Flush();
            return stream.ToArray();
        }
    }
}
