# Windows용 로컬 면접 coach/TTS 서버 실행기.
param(
    [string]$Provider = "mock",
    [int]$Port = 8002
)

$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
$venv = Join-Path $PSScriptRoot ".venv"
$venvPython = Join-Path $venv "Scripts\python.exe"

function Find-Python {
    $pyLauncher = Get-Command py -ErrorAction SilentlyContinue
    if ($pyLauncher) { return @($pyLauncher.Source, "-3") }

    $pythonCommand = Get-Command python -ErrorAction SilentlyContinue
    if ($pythonCommand -and $pythonCommand.Source -notlike "*WindowsApps*") {
        return @($pythonCommand.Source)
    }

    $profilePath = [Environment]::GetFolderPath("UserProfile")
    $bundled = Join-Path $profilePath ".cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe"
    if (Test-Path -LiteralPath $bundled) { return @($bundled) }

    throw "Python 3을 찾지 못했습니다. Python 3.10 이상을 설치한 뒤 다시 실행하세요."
}

if (-not (Test-Path -LiteralPath $venvPython)) {
    $python = @(Find-Python)
    Write-Host "[SpeakUpXR] 로컬 서버 환경을 처음 한 번 준비합니다."
    if ($python.Count -gt 1) {
        & $python[0] $python[1] -m venv $venv
    } else {
        & $python[0] -m venv $venv
    }
    if ($LASTEXITCODE -ne 0) { throw "가상환경 생성 실패" }
    & $venvPython -m pip install --disable-pip-version-check -r (Join-Path $PSScriptRoot "requirements.txt")
    if ($LASTEXITCODE -ne 0) { throw "서버 패키지 설치 실패" }
}

$env:LLM_PROVIDER = $Provider
$env:PYTHONPATH = $repo
Write-Host "[SpeakUpXR] coach/TTS 서버 실행: http://127.0.0.1:$Port (LLM_PROVIDER=$Provider)"
& $venvPython -m uvicorn services.coach.app.main:app --host 127.0.0.1 --port $Port
