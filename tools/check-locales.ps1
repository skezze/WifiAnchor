# Проверяет, что все переводы согласованы с английским эталоном:
# одинаковый набор ключей и одинаковые плейсхолдеры {0}, {1}...
# Запуск: powershell -File tools/check-locales.ps1
$ErrorActionPreference = 'Stop'
$dir = Join-Path (Split-Path -Parent $PSScriptRoot) 'Localization'

function Read-Locale([string]$path) {
    $json = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    $map = [ordered]@{}
    foreach ($p in $json.PSObject.Properties) { $map[$p.Name] = [string]$p.Value }
    return $map
}

function Get-Placeholders([string]$text) {
    return ([regex]::Matches($text, '\{(\d+)\}') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique) -join ','
}

$en = Read-Locale (Join-Path $dir 'en.json')
$errors = 0

foreach ($file in Get-ChildItem -LiteralPath $dir -Filter '*.json' | Where-Object { $_.Name -ne 'en.json' }) {
    $loc = Read-Locale $file.FullName

    foreach ($key in $en.Keys) {
        if (-not $loc.Contains($key)) {
            Write-Host "[$($file.Name)] нет ключа: $key"
            $errors++
            continue
        }

        if ((Get-Placeholders $en[$key]) -ne (Get-Placeholders $loc[$key])) {
            Write-Host "[$($file.Name)] не совпадают плейсхолдеры в '$key'"
            $errors++
        }

        if ($en[$key].Contains("`n") -ne $loc[$key].Contains("`n")) {
            Write-Host "[$($file.Name)] не совпадают переводы строк в '$key'"
            $errors++
        }
    }

    foreach ($key in $loc.Keys) {
        if (-not $en.Contains($key)) {
            Write-Host "[$($file.Name)] лишний ключ: $key"
            $errors++
        }
    }
}

if ($errors -gt 0) {
    Write-Host "Ошибок: $errors"
    exit 1
}

Write-Host "Переводы согласованы ($($en.Count) ключей)."
