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

## 현재 상태 (2026-08-11)

- ✅ 씬 배치형 3인 패널: 인사 / 기술 / 임원 면접관. 캐릭터·좌석·음성 프로필을 Inspector에서 직접 교체
- ✅ 문 입장 → 착석의 가벼운 컷신, 질문/녹음 답변/STT·운율 분석, 성격별 짧은 반응과 적응형 꼬리질문
- ✅ 서버 프록시 Azure 한국어 TTS + 실제 오디오 파형 기반 립싱크, 키가 없을 때 자막 타이밍 폴백
- ✅ 종료 인사 후 `timeScale=0`, 비동기 리포트 로딩 및 월드 리포트 표시
- ✅ Quest APK 빌드 통과 (`XrSetup.BuildApk` → `Builds/SpeakUpXR.apk`, 45MB)
- ✅ mock 백엔드 E2E (에디터 Play: Space=시작, Enter=답변, 우클릭 드래그=시점)
- ⏳ 다음: 역할별 상용 VRM/FBX와 면접장 에셋으로 placeholder 교체 → Quest 실기 테스트 → 시각 품질 조정

## 실행 방법

- 백엔드(mock, 키 불필요): `services/coach`에서 Windows `.\run.ps1` / macOS `./run.sh`
- 최초/초기화용 씬 생성: **SpeakUpXR → Create Editable Interview Scene**. 생성 후에는 `Interview.unity`가 소스 오브 트루스이며 씬을 직접 편집
- XR/플레이어 설정 일괄 적용: `-executeMethod XrSetup.Configure -buildTarget Android` (배치)
- APK: `-executeMethod XrSetup.BuildApk` (배치, `-quit` 없이 — 스스로 Exit)

## 조심할 것

- 캐릭터는 런타임에 로드하지 않는다. 씬의 `SLOT_1..3` 아래 `AvatarRoot`를 VRM/FBX 프리팹으로 교체하고
  `InterviewerController.AvatarRoot`만 다시 연결한다. 좌석/성격/TTS/AI 라우팅은 슬롯에 남는다.
- UniVRM 0.129.x: `LookAtTargetTypes`는 `VRM10ObjectLookAt` 안의 중첩 enum
- OpenXR 빌드 검증: **Linear 색공간 + Vulkan 단독** 아니면 빌드 실패 (Gamma+GLES 거부)
- 현재 캐릭터는 씬에서 보이는 placeholder. 실제 모델에는 앉기 애니메이션/포즈를 적용한다.
- 스크립트 수정 시 `unity-staging/`에도 복사해 동기화 유지
- `.env`/API 키는 공유 PC에 두지 않는다. LLM은 당분간 `LLM_PROVIDER=mock`
