# SpeakUp XR — Unity 면접 구현

메인 프로젝트는 `SpeakUpXR-Unity/`다. `unity-staging/Assets/SpeakUpXR`는 코드 리뷰용 미러이며 항상 동기화한다.

## 플레이 흐름

1. 문 앞에서 시작해 문이 열리고 지원자석까지 이동한다.
2. 따뜻한 인사 담당이 인사와 첫 질문을 한다.
3. Quest 오른쪽 컨트롤러 트리거/A 버튼으로 실제 음성 답변을 끝낸다. 10초 동안 응답이 없으면 면접관이 안내한 뒤 다음 질문으로 넘어간다.
4. 답변 WAV를 `audio-pipeline:8000/analyze`로 보내 STT, WPM, 필러를 얻고 HMD 방향으로 질문자 응시를 근사한다.
5. `coach:8002/interview/next`가 짧은 반응과 후속 질문, 각각의 담당 면접관을 고른다.
6. 종료 인사 후 `Time.timeScale=0`으로 멈추고 리포트 로딩/결과를 표시한다.

## 씬 편집 원칙

- `Assets/SpeakUpXR/Scenes/Interview.unity`의 오브젝트가 소스 오브 트루스다.
- `SpeakUpXR → Create Editable Interview Scene`은 최초 생성/초기화용이다. 실행할 때마다 현재 씬을 덮어쓰므로 아트 편집 후에는 다시 실행하지 않는다.
- 패널은 `SLOT_1_...`, `SLOT_2_...`, `SLOT_3_...` 세 개다. 각 슬롯의 placeholder 자식을 삭제하고 VRM/FBX 프리팹을 자식으로 넣은 뒤 `InterviewerController.AvatarRoot`에 연결한다.
- 자리 이동은 슬롯 Transform만 옮긴다. 캐릭터 교체 후에도 Persona ID, 성격, TTS 음성은 슬롯에 유지된다.
- 입장 동선은 `EntranceCutscene_EDIT_WAYPOINTS_HERE/Waypoint_Entrance`, `Waypoint_Seat`, 문은 `DoorPivot_EDIT_ME`에서 조정한다.
- HUD와 리포트 위치는 각각 `InterviewHud_EDIT_POSITION`, `InterviewReport_EDIT_LAYOUT`에서 직접 조정한다.

## 3인 음성 기본값

| 슬롯 | 성격 | Azure 한국어 음성 | 조절 |
|---|---|---|---|
| 1 | 따뜻한 인사 담당 | `ko-KR-SunHiNeural` | rate -4%, pitch +2% |
| 2 | 분석적인 실무 담당 | `ko-KR-HyunsuNeural` | 기본 |
| 3 | 압박형 임원 담당 | `ko-KR-InJoonNeural` | rate -6%, pitch -4% |

음성은 나이를 직접 보증하는 메타데이터가 아니라 성별이 명시된 서로 다른 한국어 보이스에 속도/피치를 절제해 적용한 캐릭터 연출값이다. 실제 캐릭터와 들어본 뒤 Inspector에서 교체한다.

TTS 키는 Unity/Quest에 넣지 않는다. coach 서버의 `.env`에만 둔다.

```env
AZURE_SPEECH_KEY=...
AZURE_SPEECH_REGION=koreacentral
```

키가 없거나 TTS 서버가 실패하면 자막 타이밍과 합성 립싱크 폴백으로 면접 흐름은 계속된다.

Quest에서는 두 서버 주소를 개발 PC LAN IP로 바꾼다.

- `CoachApi.BaseUrl = http://<PC-IP>:8002`
- `CoachApi.AudioBaseUrl = http://<PC-IP>:8000`

Windows 방화벽의 사설 네트워크에서 8000/8002 포트를 허용해야 한다.
