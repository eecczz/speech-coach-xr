# SpeakUp XR — Unity 전환 스테이징

에디터 설치가 끝나면 이 폴더의 내용물을 새 Unity 프로젝트에 이식한다.
지정과제1(듀코젠) 대응: Unity 6 LTS · Quest 3(Android APK) 타깃 · AI 요소 = coach 백엔드.

**개발 환경 = 연구실 Windows 데스크톱**(RTX 3060 / Win11). 아래는 Windows 기준.

## 이식 순서 (에디터 + 라이선스 로그인 후)

1. 프로젝트 생성 — **빈 3D (Built-in RP)**, URP 마이그레이션 불필요.
   Unity Hub → New project → 3D (Built-in Render Pipeline) →
   이름 `SpeakUpXR-Unity`, 위치는 이 리포 루트.
   Android Build Support(+ OpenJDK, Android SDK/NDK) 모듈이 설치돼 있어야 한다.

   CLI로 만들 경우 (버전 폴더명은 실제 설치 버전으로 교체):
   ```powershell
   & "C:\Program Files\Unity\Hub\Editor\6000.0.79f1\Editor\Unity.exe" `
     -batchmode -quit -createProject "<repo>\SpeakUpXR-Unity"
   ```
2. `manifest-additions.json`의 패키지들을 `SpeakUpXR-Unity\Packages\manifest.json` `dependencies`에 병합.
3. 이 폴더의 `Assets\` 전체를 `SpeakUpXR-Unity\Assets\`로 복사.
   `Assets\StreamingAssets\default.vrm`(10MB)이 여기 포함돼 있고, 아바타는 **이 경로 하나만** 쓴다
   (`InterviewerController.VrmFileName` → StreamingAssets에서 런타임 로드). 별도 Avatars 폴더 불필요.
4. 에디터 열고 메뉴 **SpeakUpXR → Build Interview Scene** 실행 (씬 자동 생성).
5. Project Settings → XR Plug-in Management → Android 탭 → OpenXR 체크,
   OpenXR Features에서 *Meta Quest Support* 활성화.
6. Build Profiles → Android → Switch Platform → Build (APK).

## 백엔드 연결

coach 서비스(:8002)는 그대로 사용한다. Quest 실기기에서는 **개발 PC의 LAN IP**로:
`CoachApi.BaseUrl = "http://<PC-IP>:8002"` (Inspector에서 설정 가능).
`ipconfig`로 IPv4 확인, 첫 연결 시 Windows 방화벽에서 8002 인바운드 허용(사설 네트워크) 필요.

mock 모드 실행 (LLM 키 없이 동작) — `services\coach` 에서:
```powershell
$env:LLM_PROVIDER="mock"; $env:PYTHONPATH="<repo>"
.\.venv\Scripts\uvicorn.exe app.main:app --host 0.0.0.0 --port 8002
```
venv가 없으면 먼저: `py -3 -m venv .venv` → `.\.venv\Scripts\pip install -r requirements.txt`

<details><summary>macOS에서 돌릴 때</summary>

```sh
LLM_PROVIDER=mock PYTHONPATH=<repo> ./.venv/bin/uvicorn app.main:app --host 0.0.0.0 --port 8002
```
Unity 에디터 경로는 `/Applications/Unity/Hub/Editor/<버전>/Unity.app/Contents/MacOS/Unity`.
</details>

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
