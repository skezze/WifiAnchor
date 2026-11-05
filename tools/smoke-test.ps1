# Запускает собранный exe в режиме --smoke-test (интерфейс без Wi-Fi, перебор всех языков).
# Запуск: powershell -File tools/smoke-test.ps1 [-Exe путь\к\WifiAnchor.exe]
param(
    [string]$Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'bin\Release\net10.0-windows\WifiAnchor.exe')
)

$p = Start-Process -FilePath $Exe -ArgumentList '--smoke-test' -PassThru

if (-not $p.WaitForExit(30000)) {
    $p.Kill()
    Write-Host 'TIMEOUT: смоук-тест завис'
    exit 2
}

Write-Host "Код выхода: $($p.ExitCode)"
exit $p.ExitCode
