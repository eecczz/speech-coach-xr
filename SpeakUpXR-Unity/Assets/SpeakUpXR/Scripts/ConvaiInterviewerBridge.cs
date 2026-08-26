using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace SpeakUpXR
{
    /// <summary>
    /// Compile-safe adapter for the locally installed Convai Asset Store SDK. It uses the SDK's
    /// exact scripted Narrative Speech route, so the existing interview agent remains responsible
    /// for the words while Convai supplies the selected Korean voice, remote audio and character
    /// speech/emotion events. The project still compiles when the uncommitted SDK is absent.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ConvaiInterviewerBridge : MonoBehaviour
    {
        private const string CharacterTypeName = "Convai.Runtime.Components.ConvaiCharacter";

        [Header("Convai character")]
        public string CharacterId;
        public string CharacterName;
        public bool UseConvaiSpeech = true;
        [Tooltip("The locally installed ConvaiCharacter component. Assigned automatically by the editor installer.")]
        [SerializeField] private MonoBehaviour convaiCharacter;

        [Header("Exact scripted speech")]
        [Range(4f, 60f)] public float SpeechStartTimeoutSeconds = 30f;
        [Range(5f, 90f)] public float SpeechStopTimeoutSeconds = 60f;

        [Header("Runtime evidence (read only)")]
        [SerializeField] private bool packageAvailable;
        [SerializeField] private bool conversationReady;
        [SerializeField] private bool remoteSpeaking;
        [SerializeField] private string lastTranscript = string.Empty;
        [SerializeField] private string lastEmotion = string.Empty;
        [SerializeField] private int lastEmotionIntensity;
        [SerializeField, TextArea(2, 4)] private string diagnostic = string.Empty;

        private InterviewerController _owner;
        private EventInfo _transcriptEvent;
        private EventInfo _speechStartedEvent;
        private EventInfo _speechStoppedEvent;
        private EventInfo _emotionEvent;
        private Delegate _transcriptHandler;
        private Delegate _speechStartedHandler;
        private Delegate _speechStoppedHandler;
        private Delegate _emotionHandler;
        private bool _speechStarted;
        private bool _speechStopped;
        private bool _requestPending;

        public bool PackageAvailable => packageAvailable;
        public bool ConversationReady => conversationReady;
        public bool IsRemoteSpeaking => remoteSpeaking;
        public bool IsSpeechPending => _requestPending;
        public string Diagnostic => diagnostic;

        private void Awake()
        {
            _owner = GetComponent<InterviewerController>();
            AutoBind();
        }

        private void OnEnable()
        {
            if (!_owner) _owner = GetComponent<InterviewerController>();
            ResetTurnState();
            AutoBind();
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
            _owner?.SetExternalSpeaking(false);
            ResetTurnState();
        }

        private void ResetTurnState()
        {
            _speechStarted = false;
            _speechStopped = false;
            _requestPending = false;
            remoteSpeaking = false;
            conversationReady = false;
            lastTranscript = string.Empty;
            lastEmotion = string.Empty;
            lastEmotionIntensity = 0;
        }

        private void Update()
        {
            conversationReady = ReadBool("IsInConversation");
            bool speakingNow = ReadBool("IsSpeaking");
            if (speakingNow != remoteSpeaking)
            {
                remoteSpeaking = speakingNow;
                _owner?.SetExternalSpeaking(speakingNow);
            }
        }

        [ContextMenu("Convai: 다시 연결 찾기")]
        public void AutoBind()
        {
            if (!IsExpectedCharacter(convaiCharacter))
            {
                MonoBehaviour[] local = GetComponents<MonoBehaviour>();
                foreach (MonoBehaviour behaviour in local)
                {
                    if (IsExpectedCharacter(behaviour))
                    {
                        convaiCharacter = behaviour;
                        break;
                    }
                }
            }

            packageAvailable = convaiCharacter != null;
            conversationReady = packageAvailable && ReadBool("IsInConversation");
            diagnostic = packageAvailable
                ? "ConvaiCharacter 연결됨. 연결 준비 시 Narrative Speech로 정확한 한국어 대사를 재생합니다."
                : "로컬 ConvaiCharacter가 없어 기존 Coach TTS로 안전하게 대체됩니다.";
        }

        public IEnumerator SpeakExact(string text, string tone, Action<bool> completed)
        {
            AutoBind();
            if (!UseConvaiSpeech || convaiCharacter == null || string.IsNullOrWhiteSpace(text))
            {
                completed?.Invoke(false);
                yield break;
            }

            float readyDeadline = Time.realtimeSinceStartup + Mathf.Max(20f, SpeechStartTimeoutSeconds);
            while (!(conversationReady = ReadBool("IsInConversation")) && Time.realtimeSinceStartup < readyDeadline)
                yield return null;
            if (!conversationReady)
            {
                diagnostic = $"{CharacterName}: Convai 대화 연결을 완료하지 못했습니다.";
                completed?.Invoke(false);
                yield break;
            }

            MethodInfo speechMethod = convaiCharacter.GetType().GetMethod(
                "SendNarrativeSpeech",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(string) },
                null);
            if (speechMethod == null)
            {
                diagnostic = "Convai SDK에 SendNarrativeSpeech(string)가 없습니다.";
                completed?.Invoke(false);
                yield break;
            }

            _requestPending = true;
            _speechStarted = false;
            _speechStopped = false;
            lastTranscript = string.Empty;
            try
            {
                speechMethod.Invoke(convaiCharacter, new object[] { text });
            }
            catch (Exception exception)
            {
                _requestPending = false;
                diagnostic = $"{CharacterName}: Narrative Speech 전송 실패: {exception.GetBaseException().Message}";
                completed?.Invoke(false);
                yield break;
            }
            diagnostic = $"{CharacterName}: 정확한 대사를 Convai Narrative Speech로 전송했습니다.";

            float startDeadline = Time.realtimeSinceStartup + Mathf.Max(20f, SpeechStartTimeoutSeconds);
            while (!_speechStarted && Time.realtimeSinceStartup < startDeadline)
            {
                // Some Convai SDK versions do not emit OnSpeechStarted again after
                // leaving and re-entering Play Mode, while IsSpeaking still changes.
                // Polling the public state keeps exact speech and facial animation
                // alive on every run instead of incorrectly falling back on timeout.
                if (ReadBool("IsSpeaking"))
                {
                    _speechStarted = true;
                    remoteSpeaking = true;
                    _owner?.SetExternalSpeaking(true);
                    break;
                }
                yield return null;
            }
            if (!_speechStarted)
            {
                _requestPending = false;
                diagnostic = $"{CharacterName}: Convai 음성 시작 시간 초과.";
                completed?.Invoke(false);
                yield break;
            }

            completed?.Invoke(true);
            float stopDeadline = Time.realtimeSinceStartup + SpeechStopTimeoutSeconds;
            float silentSince = -1f;
            while (!_speechStopped && Time.realtimeSinceStartup < stopDeadline)
            {
                if (ReadBool("IsSpeaking"))
                {
                    silentSince = -1f;
                }
                else if (silentSince < 0f)
                {
                    silentSince = Time.realtimeSinceStartup;
                }
                else if (Time.realtimeSinceStartup - silentSince >= 0.18f)
                {
                    _speechStopped = true;
                    remoteSpeaking = false;
                    break;
                }
                yield return null;
            }
            _requestPending = false;
            _owner?.SetExternalSpeaking(false);
            diagnostic = _speechStopped
                ? $"{CharacterName}: Convai 음성·립싱크 턴 완료."
                : $"{CharacterName}: Convai 음성 종료 시간 초과로 제스처를 정리했습니다.";
        }

        private void Subscribe()
        {
            Unsubscribe();
            if (convaiCharacter == null) return;
            Type type = convaiCharacter.GetType();
            _transcriptEvent = type.GetEvent("OnTranscriptReceived");
            _speechStartedEvent = type.GetEvent("OnSpeechStarted");
            _speechStoppedEvent = type.GetEvent("OnSpeechStopped");
            _emotionEvent = type.GetEvent("OnEmotionChanged");
            _transcriptHandler = AddHandler(_transcriptEvent, nameof(OnTranscript));
            _speechStartedHandler = AddHandler(_speechStartedEvent, nameof(OnSpeechStarted));
            _speechStoppedHandler = AddHandler(_speechStoppedEvent, nameof(OnSpeechStopped));
            _emotionHandler = AddHandler(_emotionEvent, nameof(OnEmotionChanged));
        }

        private Delegate AddHandler(EventInfo eventInfo, string methodName)
        {
            if (eventInfo == null || convaiCharacter == null) return null;
            MethodInfo method = GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Delegate handler = Delegate.CreateDelegate(eventInfo.EventHandlerType, this, method, false);
            if (handler != null) eventInfo.AddEventHandler(convaiCharacter, handler);
            return handler;
        }

        private void Unsubscribe()
        {
            RemoveHandler(_transcriptEvent, _transcriptHandler);
            RemoveHandler(_speechStartedEvent, _speechStartedHandler);
            RemoveHandler(_speechStoppedEvent, _speechStoppedHandler);
            RemoveHandler(_emotionEvent, _emotionHandler);
            _transcriptHandler = null;
            _speechStartedHandler = null;
            _speechStoppedHandler = null;
            _emotionHandler = null;
        }

        private void RemoveHandler(EventInfo eventInfo, Delegate handler)
        {
            if (eventInfo != null && handler != null && convaiCharacter != null)
                eventInfo.RemoveEventHandler(convaiCharacter, handler);
        }

        private void OnTranscript(string transcript, bool isFinal)
        {
            if (isFinal) lastTranscript = transcript ?? string.Empty;
        }

        private void OnSpeechStarted()
        {
            if (!_requestPending) return;
            _speechStarted = true;
            _speechStopped = false;
            remoteSpeaking = true;
            _owner?.SetExternalSpeaking(true);
        }

        private void OnSpeechStopped()
        {
            if (!_requestPending) return;
            _speechStopped = true;
            remoteSpeaking = false;
            _owner?.SetExternalSpeaking(false);
        }

        private void OnEmotionChanged(string emotion, int intensity)
        {
            lastEmotion = emotion ?? string.Empty;
            lastEmotionIntensity = intensity;
            _owner?.ApplyExternalEmotion(lastEmotion, intensity);
        }

        private bool ReadBool(string propertyName)
        {
            if (convaiCharacter == null) return false;
            PropertyInfo property = convaiCharacter.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            return property?.GetValue(convaiCharacter) is bool value && value;
        }

        private static bool IsExpectedCharacter(MonoBehaviour behaviour) =>
            behaviour != null && behaviour.GetType().FullName == CharacterTypeName;
    }
}
