# SpeakUp - AI Agent Communication Coach

SpeakUp은 발표, 면접, 협상, 온라인 대화처럼 중요한 말하기 상황을 **AI agent와 함께 실시간으로 연습**하는 커뮤니케이션 코칭 서비스입니다.

사용자는 세션명과 집중 포커스를 정하고 카메라 앞에서 말합니다. 서비스는 음성, 시선, 자세, 표정, 침묵, 필러 표현 같은 신호를 실시간으로 분석해 짧은 코칭을 띄우고, 사용자의 질문에는 AI agent가 대화형으로 답합니다. 세션이 끝나면 녹화 영상, 주요 주의 구간, 전사 텍스트, 종합 리포트를 통해 다시 복기할 수 있습니다.

---

## 1. 프로젝트 목표

기존 말하기 연습은 녹화 후 사용자가 직접 영상을 돌려보며 문제를 찾아야 했습니다. SpeakUp은 이 과정을 다음처럼 바꾸는 것을 목표로 합니다.

- 연습 중에는 AI agent가 실시간으로 짧은 피드백을 제공
- 사용자가 궁금한 점을 말하거나 입력하면 agent가 즉시 답변
- 세션 종료 후에는 LLM이 전체 흐름을 요약해 리포트 생성
- 반복 세션을 저장해 이전 연습과 비교하고 성장 추적 가능

핵심 방향은 단순 영상 분석이 아니라 **말하기 습관을 반복적으로 개선하는 AI agent 기반 라이프스타일 코칭**입니다.

---

## 2. 주요 기능

| 기능 | 설명 | 핵심 코드 |
|---|---|---|
| 로그인/회원가입 | 사용자별 세션과 agent 대화 기록을 PostgreSQL에 저장 | `services/audio-pipeline/app/db.py`, `services/audio-pipeline/app/main.py` |
| 세션 생성 | 세션명과 집중 포커스를 선택해 연습 목적 설정 | `apps/web/create-project.html` |
| 기존 세션 이어하기 | 로그인 사용자의 기존 세션 목록을 불러와 이어서 연습 | `apps/web/src/app-api.ts`, `apps/web/src/practice.ts` |
| 실시간 코칭 | 최근 구간의 말 속도, 필러, 침묵, 시선, 자세, 표정, 제스처를 분석해 코칭 트리거 생성 | `apps/web/src/realtime-coaching.ts` |
| 집중 포커스 기반 HUD | 사용자가 선택한 포커스만 실시간 화면과 코칭에 반영 | `apps/web/src/practice.ts` |
| AI agent 대화 | 사용자의 질문형 발화나 직접 입력에 agent가 답변하고 대화 기록 저장 | `apps/web/src/practice.ts` |
| 음성 답변 on/off | agent 답변을 브라우저 TTS로 들을지 선택 | `apps/web/src/practice.ts` |
| 녹화 및 MP4 저장 | 실시간 연습 영상을 녹화하고 공유 가능한 MP4로 변환 | `services/audio-pipeline/app/main.py` `/convert/mp4` |
| STT/운율 분석 | 녹화 오디오를 STT와 prosody 분석으로 변환 | `services/audio-pipeline/app/stt.py`, `services/audio-pipeline/app/prosody.py` |
| LLM 리포트 | 세션 종료 후 이벤트, 전사, 운율, 비언어 신호를 종합해 구조화 리포트 생성 | `services/coach/app/llm.py`, `services/coach/app/prompts.py` |
| 리포트 복기 | 주의 구간, 점수 흐름, 전사 텍스트, 영상 재생, PDF 저장 제공 | `apps/web/src/report-page.ts`, `apps/web/src/loading.ts` |

---

## 3. 현재 사용자 흐름

1. `http://localhost:8000` 또는 `http://localhost:5173` 접속
2. 회원가입 또는 로그인
3. `세션 시작하기` 선택
4. 세션명 입력
   - 예: `경진대회 발표`, `최종 면접`, `협상 리허설`
5. 집중 포커스 선택
   - 예: 말 속도, 시선 처리, 필러 표현, 침묵 구간, 자세
6. 실시간 코칭 화면에서 카메라/마이크 권한 허용
7. 녹화 시작 후 발표 또는 대화 연습
8. 연습 중 중앙 상단 토스트와 AI agent 대화창으로 피드백 확인
9. 종료 후 AI 코칭 리포트 생성
10. 리포트에서 주의 구간을 클릭해 해당 시점의 영상 복기
11. 필요 시 PDF와 MP4로 저장

---

## 4. 아키텍처

```text
speech-coach/
├─ apps/web/                         Vite + Vanilla TypeScript 프론트엔드
│  ├─ create-project.html            로그인/세션 생성/기존 세션 선택
│  ├─ practice.html                  실시간 AI agent 코칭 화면
│  ├─ loading.html                   세션 분석 진행 화면
│  ├─ report.html                    AI 코칭 리포트 화면
│  └─ src/
│     ├─ practice.ts                 실시간 녹화, agent 대화, HUD, 세션 흐름
│     ├─ realtime-coaching.ts        최근 구간 기반 실시간 코칭 트리거
│     ├─ signals/                    비언어 신호 계산
│     ├─ mediapipe/                  Face/Pose/Hand landmark 초기화
│     ├─ ws/                         aggregator WebSocket 클라이언트
│     ├─ loading.ts                  분석 완료 후 리포트 생성/저장
│     ├─ report-page.ts              리포트 렌더링, PDF/MP4 저장
│     └─ session-store.ts            브라우저 세션/리포트 저장 모델
├─ services/
│  ├─ audio-pipeline/                FastAPI, 정적 웹 서빙, STT, prosody, auth, PostgreSQL API
│  ├─ aggregator/                    FastAPI, 실시간 신호 수집, 윈도우링, 이벤트 번들 생성
│  └─ coach/                         FastAPI, rule 기반 live HUD, LLM 종합 평가
├─ packages/schema/                  Python/TypeScript 공유 스키마
└─ docker-compose.yml                PostgreSQL + backend services
```

### 런타임 흐름

```text
브라우저
  ├─ 카메라/마이크 입력
  ├─ MediaPipe로 시선·자세·표정·제스처 신호 추출
  ├─ Web Speech 또는 chunk STT로 사용자 발화 수집
  └─ MediaRecorder로 세션 영상 녹화

        ↓ 실시간 신호

aggregator
  ├─ WebSocket으로 vision/prosody/STT frame 수집
  ├─ 5초 단위 window 생성
  └─ coach /live로 HUD용 rule 평가 요청

        ↓ 세션 종료

audio-pipeline
  ├─ ffmpeg로 오디오 변환
  ├─ Jeonbuk API 또는 local faster-whisper STT
  └─ prosody 분석

        ↓ 종합 평가

coach
  ├─ rule 기반 이벤트와 전사/운율/비언어 신호 정리
  └─ Jeonbuk/Gemini/Claude LLM으로 구조화 리포트 생성

        ↓

report.html
  ├─ 주의 구간 그래프와 영상 복기
  ├─ 종합 피드백
  ├─ 전사 텍스트
  └─ PDF/MP4 저장
```

---

## 5. 기술 스택

### Frontend

- Vite
- Vanilla TypeScript
- MediaPipe Tasks Vision
- Three.js / three-vrm
- WebSocket
- MediaRecorder
- Web Speech API
- Web Speech Synthesis API
- LocalStorage / IndexedDB 성격의 브라우저 저장 흐름

### Backend

- FastAPI
- PostgreSQL 16
- psycopg
- ffmpeg
- faster-whisper
- librosa / soundfile 기반 prosody 분석
- Pydantic schema

### LLM/STT Provider

- Jeonbuk AI API
- Gemini
- Claude
- local faster-whisper fallback

---

## 6. 사전 요구사항

- Docker Desktop
- Node.js 20.19+ 또는 22.12+
- 데스크톱 Chrome 권장
- `.env` 파일
- Jeonbuk API 키 또는 Gemini/Claude API 키

---

## 7. 로컬 실행

### 7-1. 저장소 클론

```bash
git clone <repo-url> speech-coach
cd speech-coach
```

### 7-2. 환경변수 설정

```bash
cp .env.example .env
```

`.env` 예시:

```env
LLM_PROVIDER=jeonbuk
JEONBUK_API_KEY=
JEONBUK_BASE_URL=https://ai.jb.go.kr/student-api/v1
JEONBUK_CHAT_MODEL=gemma-4-31b-turbo

STT_PROVIDER=jeonbuk
JEONBUK_STT_MODEL=cohere-transcribe

GOOGLE_API_KEY=
GEMINI_MODEL=gemini-2.5-flash-lite

ANTHROPIC_API_KEY=
CLAUDE_MODEL=claude-sonnet-4-6

WHISPER_MODEL=medium
```

API 키는 `.env`에만 저장하고 커밋하지 않습니다.

### 7-3. Docker 서비스 실행

```bash
docker compose up -d postgres audio aggregator coach
```

처음 실행하면 이미지 빌드와 STT 모델 캐시 준비 때문에 시간이 걸릴 수 있습니다.

### 7-4. 웹 개발 서버 실행

```bash
cd apps/web
npm install
npm run dev
```

개발 서버:

```text
http://localhost:5173
```

### 7-5. 프로덕션 정적 빌드

```bash
cd apps/web
npm install
npm run build
```

빌드 결과는 `services/audio-pipeline/app/static/`에 생성됩니다. Docker의 `audio` 서비스가 이 정적 파일을 서빙합니다.

```text
http://localhost:8000
```

---

## 8. 서비스와 포트

| 서비스 | 포트 | 역할 |
|---|---:|---|
| web dev | 5173 | Vite 개발 서버 |
| audio | 8000 | 정적 웹 서빙, STT, prosody, MP4 변환, auth/session/message API |
| aggregator | 8001 | 실시간 신호 수집, windowing, 이벤트 번들 생성 |
| coach | 8002 | live rule 평가, LLM 리포트 생성 |
| postgres | 5432 | 사용자, 세션, agent 메시지 저장 |

주요 API:

| API | 역할 |
|---|---|
| `POST /api/auth/signup` | 회원가입 |
| `POST /api/auth/login` | 로그인 |
| `GET /api/sessions` | 사용자 세션 목록 |
| `POST /api/sessions` | 세션 생성 |
| `POST /api/sessions/{session_id}/messages` | agent 대화 저장 |
| `POST /analyze` | 녹화 오디오 STT/prosody 분석 |
| `POST /convert/mp4` | 녹화 영상을 MP4로 변환 |
| `WS /ws/signals` | 실시간 vision/STT/prosody frame 수집 |
| `WS /ws/hud` | live HUD push |
| `POST /session/start` | aggregator 세션 시작 |
| `POST /session/end` | 이벤트 번들 생성 및 LLM 평가 |
| `POST /live` | rule 기반 실시간 평가 |
| `POST /comprehensive` | LLM 종합 리포트 생성 |

---

## 9. 집중 포커스

현재 UI에서 선택 가능한 주요 포커스:

- 말 속도
- 시선 처리
- 필러 표현
- 침묵 구간
- 자세
- 표정
- 제스처
- 논리 흐름
- 목소리 톤
- 자신감

선택한 포커스는 실시간 HUD, agent 코칭, 리포트 이벤트에 우선 반영됩니다.

---

## 10. 데이터 저장

PostgreSQL에는 다음 데이터가 저장됩니다.

- 사용자 계정
- 세션명
- 세션별 집중 포커스
- 세션 상태
- 최신 리포트 스냅샷
- AI agent 대화 기록

DB 스키마는 `services/audio-pipeline/app/db.py`의 `init_db()`에서 `CREATE TABLE IF NOT EXISTS`로 자동 생성됩니다. 팀원이 `docker compose up`을 실행하면 PostgreSQL 컨테이너와 테이블이 함께 준비됩니다.

---

## 11. 트러블슈팅

| 증상 | 확인할 것 |
|---|---|
| 화면이 예전 UI로 보임 | Chrome 강력 새로고침: `Cmd + Shift + R` |
| Docker가 안 켜짐 | Docker Desktop 실행 여부 확인 |
| DB API 오류 | `docker compose ps`, `docker compose logs postgres audio` 확인 |
| 평가 서버 오류 | `docker compose logs coach aggregator` 확인 |
| STT가 안 됨 | `STT_PROVIDER`, `JEONBUK_API_KEY`, `WHISPER_MODEL` 확인 |
| MP4 저장 실패 | `audio` 컨테이너에 ffmpeg 설치 여부 및 `/convert/mp4` 로그 확인 |
| 카메라가 안 보임 | Chrome 카메라 권한과 실제 웹캠 선택 확인 |
| AI agent 음성이 안 나옴 | 브라우저 TTS 지원 여부와 `음성 켜짐/꺼짐` 상태 확인 |
