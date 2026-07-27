# SpeakUp XR — 프로젝트 컨텍스트

2026 AI·가상융합(XR) 서비스 개발자 경진대회(지정과제1·듀코젠) 출품작.
AI 가상 면접관 기반 몰입형 면접 코칭 앱. **마감 2026-08-27** (보고서 + 시연영상 + 실행파일).

- 타깃: **Quest 3 / Android APK 단일** · Unity 6 LTS(6000.0.80f1) · **Built-in RP (URP 아님)**
- 스택: OpenXR(Meta Quest Support) + XRI 3.0.8 + UniVRM 0.129.2 + FastAPI 백엔드
- 개발 PC: 연구실 Windows(RTX 3060). macOS는 문서/백엔드 작업 위주

## 디렉터리

| 경로 | 역할 |
|---|---|
| `SpeakUpXR-Unity/` | **메인 Unity 프로젝트** (씬·스크립트·XR 설정 전부 여기) |
| `unity-staging/` | 스크립트 원본 미러 (Unity 없는 환경에서의 리뷰용) — 프로젝트와 항상 동기화할 것 |
| `services/coach/` | FastAPI 백엔드 (`/interview/next`, `/interview/report`) — 그대로 사용 |
| `apps/web/` | 구 WebXR 프로토타입 — **레퍼런스 전용, 수정 금지** |

## 현재 상태 (2026-07-27)

- ✅ 다대다 면접 씬: 역할별 면접관 4인 패널(인사/직무/기술/임원, kind별로 담당자가 발화),
  지원자석 3석(사용자 중앙 + 좌우 NPC), 채광창 있는 10×6.8m 룸 — `SceneBootstrap.Build()`로 재생성
- ✅ Quest APK 빌드 통과 (`XrSetup.BuildApk` → `Builds/SpeakUpXR.apk`, 45MB)
- ✅ mock 백엔드 E2E (에디터 Play: Space=시작, Enter=답변, 우클릭 드래그=시점)
- ⏳ 다음: STT/음성 연동 → Quest 실기 테스트 → 리포트 UI → 아트 조립(에셋 스토어 환경 + 역할별 VRM)

## 실행 방법

- 백엔드(mock, 키 불필요): `services/coach`에서 Windows `.\run.ps1` / macOS `./run.sh`
- 씬 재생성: 에디터 메뉴 **SpeakUpXR → Build Interview Scene** (씬은 코드가 소스 오브 트루스)
- XR/플레이어 설정 일괄 적용: `-executeMethod XrSetup.Configure -buildTarget Android` (배치)
- APK: `-executeMethod XrSetup.BuildApk` (배치, `-quit` 없이 — 스스로 Exit)

## 조심할 것

- 아바타는 `StreamingAssets/default.vrm` 한 파일을 6명이 공유 — 외형은
  `InterviewerController`의 HairTint/OutfitTint/BodyScale로 변주 중. 모델 확보 시 `VrmFileName`만 교체
- UniVRM 0.129.x: `LookAtTargetTypes`는 `VRM10ObjectLookAt` 안의 중첩 enum
- OpenXR 빌드 검증: **Linear 색공간 + Vulkan 단독** 아니면 빌드 실패 (Gamma+GLES 거부)
- 착석은 서 있는 리그를 -0.34m 가라앉힌 임시 처리 — 제대로 된 착석 포즈 TODO
- 스크립트 수정 시 `unity-staging/`에도 복사해 동기화 유지
- `.env`/API 키는 공유 PC에 두지 않는다. LLM은 당분간 `LLM_PROVIDER=mock`
