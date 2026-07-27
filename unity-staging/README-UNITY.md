# SpeakUp XR — Unity 전환 스테이징

에디터 설치가 끝나면 이 폴더의 내용물을 새 Unity 프로젝트에 이식한다.
지정과제1(듀코젠) 대응: Unity 6 LTS · Quest 3(Android APK) 타깃 · AI 요소 = coach 백엔드.

## 이식 순서 (에디터 + 라이선스 로그인 후)

1. 프로젝트 생성 (빈 3D, Built-in RP — URP 마이그레이션 불필요):
   ```sh
   "/Applications/Unity/Hub/Editor/6000.0.79f1/Unity.app/Contents/MacOS/Unity" \
     -batchmode -quit -createProject "<repo>/SpeakUpXR-Unity"
   ```
2. `manifest-additions.json`의 패키지들을 `SpeakUpXR-Unity/Packages/manifest.json` `dependencies`에 병합.
3. `Assets/` 폴더 전체를 `SpeakUpXR-Unity/Assets/`로 복사.
4. `apps/web/public/avatars/default.vrm` → `SpeakUpXR-Unity/Assets/SpeakUpXR/Avatars/`로 복사.
5. 에디터 열고 메뉴 **SpeakUpXR → Build Interview Scene** 실행 (씬 자동 생성).
6. Project Settings → XR Plug-in Management → Android 탭 → OpenXR 체크,
   OpenXR Features에서 *Meta Quest Support* 활성화.
7. Build Profiles → Android → Switch Platform → Build (APK).

## 백엔드 연결

coach 서비스(:8002)는 그대로 사용한다. Quest 실기기에서는 Mac의 LAN IP로:
`CoachApi.BaseUrl = "http://<Mac-IP>:8002"` (Inspector에서 설정 가능).
mock 모드 실행: `services/coach` 에서
`LLM_PROVIDER=mock PYTHONPATH=<repo> ./.venv/bin/uvicorn app.main:app --host 0.0.0.0 --port 8002`

## 파일 맵

| 파일 | 역할 (WebXR 대응물) |
|---|---|
| `Assets/SpeakUpXR/Scripts/CoachApi.cs` | 백엔드 클라이언트 (`llm-brain.ts`) |
| `Assets/SpeakUpXR/Scripts/InterviewSession.cs` | 면접 상태머신 (`interview-session.ts` + `interview-brain.ts`) |
| `Assets/SpeakUpXR/Scripts/InterviewerController.cs` | VRM 면접관 시선·눈깜빡임·입·끄덕임 (`interviewer.ts`) |
| `Assets/SpeakUpXR/Scripts/InterviewHud.cs` | 질문/상태 월드 UI (`interview-ui.ts`) |
| `Assets/SpeakUpXR/Editor/SceneBootstrap.cs` | 면접실 씬 자동 생성 (`interview-room.ts`) |

TTS/STT는 1차 이식에서 자막 우선(ISpeech 시임만 둠) — 음성은 백엔드 STT(audio-pipeline)
연동 후 추가. Three.js 데모(`apps/web/interview-xr.html`)는 작동 레퍼런스로 보존.
