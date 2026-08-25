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
    elseif ($configuredProvider -eq "gemini" -and $env:GOOGLE_API_KEY) { $Provider = "gemini" }
    elseif ($configuredProvider -eq "nvidia" -and $env:NVIDIA_API_KEY) { $Provider = "nvidia" }
    elseif ($configuredProvider -eq "jeonbuk" -and $env:JEONBUK_API_KEY) { $Provider = "jeonbuk" }
    elseif ($configuredProvider -eq "claude" -and $env:ANTHROPIC_API_KEY) { $Provider = "claude" }
    elseif ($configuredProvider -eq "mock") { $Provider = "mock" }
    elseif (-not $configuredProvider) { $Provider = "ollama" }
    elseif (-not $configuredProvider -and $env:NVIDIA_API_KEY) { $Provider = "nvidia" }
    elseif (-not $configuredProvider -and $env:JEONBUK_API_KEY) { $Provider = "jeonbuk" }
    else { $Provider = "mock" }
}
$env:LLM_PROVIDER = $Provider
$env:PYTHONPATH = $repo
Write-Host "[SpeakUpXR] coach/TTS 서버 실행: http://127.0.0.1:$Port (LLM_PROVIDER=$Provider)"
& $venvPython -m uvicorn services.coach.app.main:app --host 127.0.0.1 --port $Port
