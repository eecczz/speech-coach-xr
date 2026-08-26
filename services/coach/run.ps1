# Windows용 로컬 면접 coach/TTS 서버 실행기.
param(
    [string]$Provider = "auto",
    [int]$Port = 8002
)

$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
$venv = Join-Path $PSScriptRoot ".venv"
$venvPython = Join-Path $venv "Scripts\python.exe"

# Load repository-local .env without printing secrets. Existing process-level
# environment variables take precedence.
$envFile = Join-Path $repo ".env"
if (Test-Path -LiteralPath $envFile) {
    Get-Content -LiteralPath $envFile | ForEach-Object {
        $line = $_.Trim()
        if (-not $line -or $line.StartsWith("#") -or -not $line.Contains("=")) { return }
        $parts = $line.Split("=", 2)
        $name = $parts[0].Trim()
        $value = $parts[1].Trim().Trim('"').Trim("'")
        if ($name -and -not [Environment]::GetEnvironmentVariable($name)) {
            [Environment]::SetEnvironmentVariable($name, $value, "Process")
        }
    }
}

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

function Test-LocalPort([int]$TargetPort) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $connection = $client.BeginConnect("127.0.0.1", $TargetPort, $null, $null)
        return $connection.AsyncWaitHandle.WaitOne(250) -and $client.Connected
    } catch {
        return $false
    } finally {
        $client.Dispose()
    }
}

function Find-Ollama {
    $command = Get-Command ollama -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $localPrograms = [Environment]::GetFolderPath("LocalApplicationData")
    $installed = Join-Path $localPrograms "Programs\Ollama\ollama.exe"
    if (Test-Path -LiteralPath $installed) { return $installed }

    throw "Ollama를 찾지 못했습니다. Ollama를 설치한 뒤 다시 실행하세요."
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

if ($Provider -eq "auto") {
    $configuredProvider = [string]$env:LLM_PROVIDER
    $configuredProvider = $configuredProvider.Trim().ToLowerInvariant()
    if ($configuredProvider -in @("ollama", "local")) { $Provider = "ollama" }
    elseif (-not $configuredProvider) { $Provider = "ollama" }
    else { throw "무료 무제한 실행은 LLM_PROVIDER=ollama만 지원합니다. 현재 값: $configuredProvider" }
}
if ($Provider -notin @("ollama", "local")) {
    throw "이 실행기는 로컬 Ollama 전용입니다. -Provider ollama로 실행하세요."
}
$env:LLM_PROVIDER = $Provider
$env:PYTHONPATH = $repo

$ollama = Find-Ollama
if (-not (Test-LocalPort 11434)) {
    Write-Host "[SpeakUpXR] Ollama 추론 엔진을 시작합니다: http://127.0.0.1:11434"
    Start-Process -FilePath $ollama -ArgumentList "serve" -WindowStyle Hidden
    $ollamaReady = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 250
        if (Test-LocalPort 11434) {
            $ollamaReady = $true
            break
        }
    }
    if (-not $ollamaReady) { throw "Ollama가 제한 시간 안에 시작되지 않았습니다." }
}

$model = if ($env:OLLAMA_CHAT_MODEL) { $env:OLLAMA_CHAT_MODEL } else { "qwen2.5:1.5b" }
$installedModels = (& $ollama list 2>$null | Out-String)
if ($installedModels -notmatch "(?m)^$([Regex]::Escape($model))\s") {
    Write-Host "[SpeakUpXR] 최초 실행용 로컬 모델을 준비합니다: $model"
    & $ollama pull $model
    if ($LASTEXITCODE -ne 0) { throw "Ollama 모델 다운로드 실패: $model" }
}

if (Test-LocalPort $Port) {
    Write-Host "[SpeakUpXR] 기존 coach/TTS 서버를 재사용합니다: http://127.0.0.1:$Port"
    exit 0
}

Write-Host "[SpeakUpXR] coach/TTS 서버 실행: http://127.0.0.1:$Port (LLM_PROVIDER=$Provider)"
& $venvPython -m uvicorn services.coach.app.main:app --host 127.0.0.1 --port $Port
