using System;
using System.Reflection;
using UnityEngine;

namespace SpeakUpXR
{
    /// <summary>
    /// Compile-safe bridge used by the Convai sandbox. It intentionally uses reflection so the
    /// project still compiles when the locally installed Asset Store package is not committed.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ConvaiSandboxProbe : MonoBehaviour
    {
        private const string PlayerTypeName = "Convai.Runtime.Components.ConvaiPlayer";
        private const string CharacterTypeName = "Convai.Runtime.Components.ConvaiCharacter";

        [Header("Local Convai objects")]
        [SerializeField] private MonoBehaviour convaiPlayer;
        [SerializeField] private MonoBehaviour convaiCharacter;

        [Header("Conversation probe")]
        [SerializeField, TextArea(2, 5)]
        private string koreanTestPrompt =
            "한국어로 답변해 주세요. 실제 기술 면접관처럼 짧게 자기소개를 요청해 주세요.";

        [SerializeField, TextArea(2, 5)]
        private string exactKoreanTestLine =
            "안녕하세요. 지원하신 직무와 본인의 강점을 간단히 소개해 주시겠습니까?";

        [Header("Runtime evidence (read only)")]
        [SerializeField] private bool packageTypesResolved;
        [SerializeField] private bool conversationReady;
        [SerializeField] private bool characterSpeaking;
        [SerializeField] private bool speechObserved;
        [SerializeField] private string characterId = string.Empty;
        [SerializeField, TextArea(2, 5)] private string lastTranscript = string.Empty;
        [SerializeField] private string lastEmotion = string.Empty;
        [SerializeField] private int lastEmotionIntensity;
        [SerializeField, TextArea(2, 4)] private string diagnostic = string.Empty;

        private EventInfo transcriptEvent;
        private EventInfo speechStartedEvent;
        private EventInfo speechStoppedEvent;
        private EventInfo emotionEvent;
        private Delegate transcriptHandler;
        private Delegate speechStartedHandler;
        private Delegate speechStoppedHandler;
        private Delegate emotionHandler;

        public bool PackageTypesResolved => packageTypesResolved;
        public bool ConversationReady => conversationReady;
        public bool CharacterSpeaking => characterSpeaking;
        public bool SpeechObserved => speechObserved;
        public string CharacterId => characterId;
        public string LastTranscript => lastTranscript;
        public string LastEmotion => lastEmotion;
        public string Diagnostic => diagnostic;

        private void OnEnable()
        {
            Application.runInBackground = true;
            AutoBindIfNeeded();
            SubscribeCharacterEvents();
            RefreshEvidence();
        }

        private void Update()
        {
            // IsInConversation changes asynchronously after the room and character-ready
            // handshakes. Poll it so editor/runtime probes do not remain stuck on the Awake value.
            RefreshEvidence();
        }

        private void OnDisable()
        {
            UnsubscribeCharacterEvents();
        }

        [ContextMenu("Convai: 자동 연결 다시 찾기")]
        public void AutoBindIfNeeded()
        {
            if (!IsExpectedType(convaiPlayer, PlayerTypeName))
                convaiPlayer = FindSceneBehaviour(PlayerTypeName);

            if (!IsExpectedType(convaiCharacter, CharacterTypeName))
                convaiCharacter = FindSceneBehaviour(CharacterTypeName);

            packageTypesResolved = convaiPlayer != null && convaiCharacter != null;
            diagnostic = packageTypesResolved
                ? "Convai Player/Character 연결 확인. API 키와 네트워크 연결 후 시험 프롬프트를 보낼 수 있습니다."
                : "Convai 4.5.0의 Player 또는 Character를 찾지 못했습니다. 로컬 패키지와 Sandbox 씬을 확인하세요.";
        }

        /// <summary>
        /// Sends a user message to Convai. Convai generates the spoken response, unlike the
        /// exact authored-line Narrative Speech route used by SendExactKoreanSpeechProbe.
        /// </summary>
        [ContextMenu("Convai: 한국어 대화 시험 보내기")]
        public void SendKoreanConversationProbe()
        {
            AutoBindIfNeeded();
            if (convaiPlayer == null)
            {
                diagnostic = "전송 실패: Convai Player가 없습니다.";
                Debug.LogWarning($"[ConvaiSandboxProbe] {diagnostic}", this);
                return;
            }

            MethodInfo sendMethod = convaiPlayer.GetType().GetMethod(
                "SendTextMessage",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(string) },
                null);

            if (sendMethod == null)
            {
                diagnostic = "전송 실패: ConvaiPlayer.SendTextMessage(string)을 찾지 못했습니다.";
                Debug.LogWarning($"[ConvaiSandboxProbe] {diagnostic}", this);
                return;
            }

            sendMethod.Invoke(convaiPlayer, new object[] { koreanTestPrompt });
            diagnostic = "한국어 사용자 입력을 Convai로 전송했습니다. 생성 응답의 음성·Viseme·감정을 관찰하세요.";
            Debug.Log($"[ConvaiSandboxProbe] {diagnostic}", this);
        }

        /// <summary>
        /// Speaks an authored line verbatim through Convai Narrative Speech. Unlike SendTextMessage,
        /// this does not ask the Convai LLM to rewrite the interview agent's line.
        /// </summary>
        [ContextMenu("Convai: 정확한 한국어 대사·립싱크 시험")]
        public void SendExactKoreanSpeechProbe()
        {
            AutoBindIfNeeded();
            if (convaiCharacter == null)
            {
                diagnostic = "정확 대사 전송 실패: Convai Character가 없습니다.";
                Debug.LogWarning($"[ConvaiSandboxProbe] {diagnostic}", this);
                return;
            }

            MethodInfo speechMethod = convaiCharacter.GetType().GetMethod(
                "SendNarrativeSpeech",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(string) },
                null);
            if (speechMethod == null)
            {
                diagnostic = "정확 대사 전송 실패: SendNarrativeSpeech(string)을 찾지 못했습니다.";
                Debug.LogWarning($"[ConvaiSandboxProbe] {diagnostic}", this);
                return;
            }

            speechMethod.Invoke(convaiCharacter, new object[] { exactKoreanTestLine });
            diagnostic = "정확한 한국어 대사를 Convai Narrative Speech로 전송했습니다. 음성·Viseme·감정을 확인하세요.";
            Debug.Log($"[ConvaiSandboxProbe] {diagnostic}", this);
        }

        private void RefreshEvidence()
        {
            if (convaiCharacter == null) return;

            PropertyInfo idProperty = convaiCharacter.GetType().GetProperty("CharacterId");
            PropertyInfo speakingProperty = convaiCharacter.GetType().GetProperty("IsSpeaking");
            PropertyInfo conversationProperty = convaiCharacter.GetType().GetProperty("IsInConversation");
            PropertyInfo emotionProperty = convaiCharacter.GetType().GetProperty("CurrentEmotion");
            PropertyInfo intensityProperty = convaiCharacter.GetType().GetProperty("CurrentEmotionIntensity");

            characterId = idProperty?.GetValue(convaiCharacter) as string ?? string.Empty;
            characterSpeaking = speakingProperty?.GetValue(convaiCharacter) as bool? ?? false;
            if (characterSpeaking) speechObserved = true;
            conversationReady = conversationProperty?.GetValue(convaiCharacter) as bool? ?? false;
            lastEmotion = emotionProperty?.GetValue(convaiCharacter) as string ?? lastEmotion;
            lastEmotionIntensity = intensityProperty?.GetValue(convaiCharacter) as int? ?? lastEmotionIntensity;
        }

        private void SubscribeCharacterEvents()
        {
            UnsubscribeCharacterEvents();
            if (convaiCharacter == null) return;

            Type type = convaiCharacter.GetType();
            transcriptEvent = type.GetEvent("OnTranscriptReceived");
            speechStartedEvent = type.GetEvent("OnSpeechStarted");
            speechStoppedEvent = type.GetEvent("OnSpeechStopped");
            emotionEvent = type.GetEvent("OnEmotionChanged");

            transcriptHandler = AddHandler(transcriptEvent, nameof(HandleTranscript));
            speechStartedHandler = AddHandler(speechStartedEvent, nameof(HandleSpeechStarted));
            speechStoppedHandler = AddHandler(speechStoppedEvent, nameof(HandleSpeechStopped));
            emotionHandler = AddHandler(emotionEvent, nameof(HandleEmotion));
        }

        private Delegate AddHandler(EventInfo eventInfo, string methodName)
        {
            if (eventInfo == null || convaiCharacter == null) return null;
            MethodInfo method = GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) return null;

            Delegate handler = Delegate.CreateDelegate(eventInfo.EventHandlerType, this, method, false);
            if (handler != null) eventInfo.AddEventHandler(convaiCharacter, handler);
            return handler;
        }

        private void UnsubscribeCharacterEvents()
        {
            RemoveHandler(transcriptEvent, transcriptHandler);
            RemoveHandler(speechStartedEvent, speechStartedHandler);
            RemoveHandler(speechStoppedEvent, speechStoppedHandler);
            RemoveHandler(emotionEvent, emotionHandler);
            transcriptHandler = null;
            speechStartedHandler = null;
            speechStoppedHandler = null;
            emotionHandler = null;
        }

        private void RemoveHandler(EventInfo eventInfo, Delegate handler)
        {
            if (eventInfo != null && handler != null && convaiCharacter != null)
                eventInfo.RemoveEventHandler(convaiCharacter, handler);
        }

        private void HandleTranscript(string text, bool isFinal)
        {
            lastTranscript = text ?? string.Empty;
            diagnostic = isFinal ? "최종 캐릭터 TTS 텍스트 수신." : "스트리밍 캐릭터 TTS 텍스트 수신.";
        }

        private void HandleSpeechStarted()
        {
            characterSpeaking = true;
            speechObserved = true;
            conversationReady = true;
        }

        private void HandleSpeechStopped()
        {
            characterSpeaking = false;
        }

        private void HandleEmotion(string emotion, int intensity)
        {
            lastEmotion = emotion ?? string.Empty;
            lastEmotionIntensity = intensity;
        }

        private static bool IsExpectedType(MonoBehaviour behaviour, string fullName) =>
            behaviour != null && behaviour.GetType().FullName == fullName;

        private static MonoBehaviour FindSceneBehaviour(string fullName)
        {
            MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour != null && behaviour.gameObject.scene.IsValid() && behaviour.GetType().FullName == fullName)
                    return behaviour;
            }

            return null;
        }
    }
}
