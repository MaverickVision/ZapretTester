$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../docs'))
$markdown = Join-Path $output 'USER_GUIDE.ru.md'
$body = (ConvertFrom-Markdown -Path $markdown).Html
$picture = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $output 'zapret-scoring-dashboard.png')))
$body = $body.Replace('src="zapret-scoring-dashboard.png"', 'src="data:image/png;base64,' + $picture + '"')
$head = @'
<!doctype html>
<html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Zapret Tester — инструкция пользователя</title>
<style>
:root{color-scheme:light;--ink:#182533;--muted:#546273;--line:#dbe4ec;--accent:#087f8c}
*{box-sizing:border-box}html{scroll-behavior:smooth}body{margin:0;background:#f2f5f8;color:var(--ink);font:16px/1.65 "Segoe UI",Arial,sans-serif}
main{max-width:1020px;margin:32px auto;padding:42px 52px 60px;background:white;border:1px solid var(--line);border-radius:16px}
h1{font-size:34px;line-height:1.22;letter-spacing:-.7px;margin:0 0 16px}h2{font-size:25px;line-height:1.3;margin:44px 0 16px;padding-top:20px;border-top:1px solid var(--line)}
h3{font-size:19px;margin:26px 0 12px}p{margin:12px 0}li{margin:7px 0}a{color:var(--accent);text-underline-offset:3px}a[id]{scroll-margin-top:25px}
strong{font-weight:650}table{width:100%;border-collapse:collapse;font-size:14px;margin:18px 0;overflow-wrap:anywhere}th,td{padding:12px 14px;border:1px solid var(--line);text-align:left;vertical-align:top}th{background:#eff5f7}
tr:nth-child(even) td{background:#fafcfd}pre{padding:18px 22px;background:#f4f7fa;border:1px solid var(--line);border-radius:9px;overflow-x:auto;font-size:14px;line-height:1.6}code{font-family:Consolas,monospace;font-size:.9em;overflow-wrap:anywhere}
img{display:block;max-width:100%;height:auto;border:1px solid var(--line);border-radius:10px;margin:20px 0}em{color:var(--muted)}
body:before{content:"РУКОВОДСТВО ПОЛЬЗОВАТЕЛЯ";display:block;max-width:1020px;margin:30px auto -15px;padding:0 10px;color:var(--accent);font-size:12px;font-weight:700;letter-spacing:1.6px}
@media(max-width:700px){body{font-size:15px}main{margin:24px 8px;padding:26px 18px;border-radius:10px}h1{font-size:28px}h2{font-size:22px}th,td{padding:8px;font-size:13px}}
@media print{body{background:white;font-size:10pt}body:before{display:none}main{max-width:none;margin:0;padding:0;border:0}h1{font-size:24pt}h2{font-size:17pt;break-after:avoid}h3{break-after:avoid}tr,img,pre{break-inside:avoid}a{color:inherit}table{font-size:9pt}th,td{padding:7px}}
</style></head><body><main>
'@
$html = $head + $body + '</main></body></html>'
$target = Join-Path $output 'USER_GUIDE.ru.html'
[IO.File]::WriteAllText($target, $html)
$anchors = [regex]::Matches($html, 'id="([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
$missing = @([regex]::Matches($html, 'href="#([^"]+)"') | Where-Object { $_.Groups[1].Value -notin $anchors })
if ($missing.Count) { throw 'Broken table of contents links.' }
if (-not $html.Contains('src="data:image/png;base64,')) { throw 'Screenshot was not embedded.' }
if ($html -match 'C:\\Users\\|D:\\Projects GIT|<script|<iframe') { throw 'Unexpected private paths or active content.' }
[pscustomobject]@{HtmlBytes=(Get-Item $target).Length; ContentsLinks=([regex]::Matches($html,'href="#').Count); EmbeddedScreenshot=$true}
