# coach 서비스 실행 (Windows) — run.sh 의 PowerShell 대응물.
# 기본은 mock 모드(LLM 키 불필요). 예: .\run.ps1  /  .\run.ps1 -Provider gemini -Port 8002
param(
    [string]$Provider = "mock",
    [int]$Port = 8002
)

$repo = (Resolve-Path "$PSScriptRoot\..\..").Path

if (-not (Test-Path "$PSScriptRoot\.venv")) {
    Write-Host "[run.ps1] .venv 없음 — 생성 및 의존성 설치 중..."
    python -m venv "$PSScriptRoot\.venv"
    & "$PSScriptRoot\.venv\Scripts\pip.exe" install -r "$PSScriptRoot\requirements.txt"
}

$env:LLM_PROVIDER = $Provider
$env:PYTHONPATH = $repo
Write-Host "[run.ps1] LLM_PROVIDER=$Provider, port=$Port"
& "$PSScriptRoot\.venv\Scripts\uvicorn.exe" app.main:app --host 0.0.0.0 --port $Port
