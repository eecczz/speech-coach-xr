# Convai 4.5.0 면접관 통합 진단

진단일: 2026-08-21

## 결론

Convai SDK 자체는 현재 프로젝트의 Unity 6000.0.80f1, Built-in Render Pipeline, Windows 및 Meta Quest 계열 XR 조건과 호환된다. 한국어 대화와 한국어 TTS도 지원한다. 다만 아래 두 요구는 패키지를 붙이는 것만으로는 완성되지 않는다.

1. 현재 Sketchfab 스캔 모델은 얼굴 BlendShape와 Jaw/Tongue 본이 없어서 Convai의 서버 Viseme을 실제 입 모양으로 표현할 수 없다.
2. 현재 `InterviewSession`/LLM이 완성한 면접관 문장을 Convai가 그대로 읽고 Viseme까지 돌려주는 공개 Unity API는 4.5.0 패키지 코드에도 없다. 서버에 보낼 수 있는 공개 텍스트 입력은 `ConvaiPlayer.SendTextMessage`이며, 이는 캐릭터가 다시 답변을 생성하도록 하는 사용자 발화 입력이다.

따라서 가장 현실적인 시험안은 **기존 면접 로직은 유지하고, Convai가 제공하는 얼굴 리그 대응 캐릭터와 음성·Viseme 렌더링 계층만 연결하는 것**이다. 해당 진입점이 없다면 Convai가 대화 생성까지 맡도록 구조를 바꾸거나, 기존 TTS와 별도의 로컬 LipSync 솔루션을 사용해야 한다.

## 확인된 호환 항목

- Asset Store 4.5.0은 Unity 6000.0.80f1에서 Built-in/URP/HDRP 호환으로 표시된다.
- 공식 호환 문서는 Unity 2023.1 이상, Unity 6 권장, Android/Meta Quest 지원을 명시한다.
- 한국어(`ko-KR`)와 한국어 자막 폰트를 지원한다.
- 공식 Voice List에는 한국어 남성·여성 음성과 한국어 대응 다국어 음성이 있다.
- Convai LipSync는 서버에서 받은 Viseme/BlendShape 프레임을 `SkinnedMeshRenderer`의 BlendShape 또는 Jaw/Tongue Bone Effector로 전달한다.
- 커스텀 캐릭터는 Humanoid 골격과 Idle/Talk 애니메이션을 사용할 수 있다.

## 현재 캐릭터와의 차이

현재 Blender 리깅 결과는 전신 Humanoid 애니메이션과 Head Tracking에는 유효하지만 얼굴 리그가 아니다.

- 22개 변형 본: Hips/Spine/Chest/Head/팔/다리/손/발
- 얼굴 BlendShape: 0개
- Jaw/Tongue 본: 0개
- 따라서 앉기, 고개 회전, 몸 제스처: 가능
- 실제 음소별 입술·턱·혀 LipSync와 표정: 불가능

Convai의 얼굴 기능을 완전히 쓰려면 ARKit, MHA 또는 CC4 Extended 프로필에 맞는 얼굴 BlendShape 캐릭터로 교체해야 한다. 현재 스캔을 유지하려면 Blender에서 입 내부를 재구성하고 얼굴 BlendShape 또는 Jaw/Tongue 본과 가중치를 새로 만들어야 하므로 단순 자동 리깅 범위를 크게 넘는다.

## 패키지 내 캐릭터 확인

4.5.0을 실제 임포트해 확인한 결과, `Samples/LipSyncSample/Characters/Sofia`에 완성 여성 캐릭터 `Sofia.Fbx`와 `Sofia.prefab` 한 명이 포함되어 있다. 프리팹 내부 일부 메시에 `Camila` 이름이 남아 있지만 패키지의 공개 샘플명은 Sofia다. 패키지만으로 남성 2명과 여성 1명을 바로 꺼내 쓰는 구성은 아니다. 남성 2명은 Convai Character Downloader가 지원하는 외부/플레이그라운드 캐릭터를 별도로 생성·가져오거나, ARKit/CC4 대응 외부 캐릭터를 추가해야 한다.

실제 Sandbox 검증에서 Sofia는 Skinned Mesh 14개, 얼굴 BlendShape 948개, LipSync 컴포넌트 1개, Emotion 컴포넌트 2개, Gaze 컴포넌트 2개로 확인됐다. 따라서 포함 여성 캐릭터 한 명에 한해서는 얼굴 리그와 Convai 표현 계층이 준비돼 있다. 다만 포함 샘플 재질은 URP용이라 현재 Built-in 프로젝트에서 그대로 열면 분홍색으로 렌더된다. 시험 씬 생성기가 27개 재질을 Built-in Standard 재질로 로컬 변환하도록 처리했으며, 원본 패키지와 변환 결과는 Git에서 제외한다.

## 대사 소유권과 연결 방식

우리 프로젝트는 다음 정보를 계속 소유해야 한다.

- 발표/면접 주제
- 사용자 답변 기록
- 질문 및 꼬리질문
- XR 비언어 신호와 실시간 피드백
- 면접관 페르소나별 대사 배분
- 최종 리포트

Convai 통합은 `InterviewSession`이 만든 최종 대사를 받아 캐릭터별 음성·Viseme·표정을 재생하는 어댑터 형태가 안전하다. Convai의 일반 Text-in 기능은 “사용자가 캐릭터에게 보낸 텍스트”이고, 캐릭터의 응답 문장을 Convai가 다시 생성한다. 4.5.0의 `IConversationProvider`/`IConversationSession.SendAsync` 표면도 현재 placeholder이며 실제 전송은 `RTVIHandler`가 담당한다고 코드에 명시돼 있다. 공개 outbound TTS 표면은 `SetTtsEnabled` 토글뿐이고, 임의의 완성 문장을 직접 합성시키는 요청은 없다. 따라서 기존 질문을 Convai에 넣으면 같은 문장을 읽는 것이 아니라 Convai가 별도 응답을 만들어 대사 소유권이 바뀐다.

## 표정 기능 판단

Convai는 응답과 함께 감정 목록을 반환한다. 그러나 공식 Character Emotion 문서는 현재 단계에서 감정을 자동 얼굴 표정으로 적용하는 것이 아니라 감정 데이터를 전달하며, 얼굴 표정 자동 적용은 향후 기능으로 설명한다. 따라서 표정은 다음과 같이 직접 연결해야 한다.

- warm/praise → 미소, 눈썹 이완
- analytical/think → 눈썹 안쪽, 약한 눈 찡그림
- challenging/critique → 눈썹 하강, 입술 압착

이 매핑도 캐릭터가 해당 BlendShape를 가지고 있어야 작동한다.

## 안전한 시험 순서

1. Convai 4.5.0을 기존 Interview 씬이 아닌 별도 Sandbox 씬에 임포트한다.
2. 포함된 Sofia 한 명으로 한국어 문장, Audio, Viseme, 지연시간을 검증한다.
3. SDK 코드에서 외부 완성 문장을 TTS+Viseme으로 재생하는 공개 메서드가 있는지 확인한다.
4. 남성 2명은 같은 얼굴 프로필을 쓰는 캐릭터로 확보한다.
5. `IInterviewerSpeechRenderer` 어댑터를 만들어 기존 `InterviewSession`을 변경하지 않고 Convai 경로를 선택 가능하게 한다.
6. PC Play Mode 후 Meta Quest 빌드에서 마이크, VAD, LipSync, 프레임 예산을 검증한다.
7. 성공한 뒤에만 세 씬 배치 캐릭터를 Convai 캐릭터로 교체한다.

## 필요한 외부 준비

- 이미 구매 목록에 있는 Convai 4.5.0 패키지의 다운로드/임포트
- Convai API 키
- 실제 대화에 사용할 Character ID 3개
- 얼굴 리그 대응 남성 캐릭터 2명 및 여성 캐릭터 1명
- 배포 시 Convai 상용 약관/파트너 조건 확인

## 공식 근거

- Asset Store: https://assetstore.unity.com/packages/tools/behavior-ai/npc-ai-engine-dialog-actions-voice-and-lipsync-convai-235621
- Unity 호환성: https://docs.convai.com/api-docs/plugins-and-integrations/unity-plugin-beta-overview/compatibility-and-requirements
- LipSync: https://docs.convai.com/api-docs/plugins-and-integrations/unity-plugin/adding-lip-sync-to-your-character
- 커스텀 LipSync Map: https://docs.convai.com/api-docs/plugins-and-integrations/unity-plugin-beta-overview/getting-started/setup/add-lip-sync-to-your-character/lip-sync-profiles-and-mappings/creating-a-custom-map
- 한국어 지원: https://docs.convai.com/api-docs/plugins-and-integrations/unity-plugin/utilities/language-support
- Voice List: https://docs.convai.com/api-docs/api-reference/core-api-reference/character-crafting-apis/voice-list-api
- Character Emotion: https://docs.convai.com/api-docs/plugins-and-integrations/unity-plugin/utilities/character-emotion
