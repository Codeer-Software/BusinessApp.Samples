<#
.SYNOPSIS
    開発サーバに、指定した利用者で HTTP だけでログインし、Web API を叩く。ブラウザは使わない。

.DESCRIPTION
    ブラウザの利用者を切り替えずに、別の利用者で API を叩くための道具（docs/31 §3。台本は docs/qa/04 の PRM-17）。
    **ブラウザの欄にパスワードを入れることを Claude はしない**ので、画面を通らない項目はこれで流す。
    **Cookie は自前で持つので、ブラウザのセッションには触れない**（同じ利用者でも、別の利用者でも）。

    - 資格情報は `Designer/LocalEnvironment.md`（Git 追跡外）の「開発用アカウント」の表から、実行時に読む。
      **画面にも出力にも出さない。** `admin` は表の行ではないので、行が無ければ識別名をそのまま使う
      （利用者の表が空のとき、サーバ起動時に識別名と同じ字で作られる——docs/31 §3）
    - **`X-ANTIFORGERY-TOKEN` の Cookie は `Secure` 付きで発行されるので、http の localhost では `WebSession` が送り返さない**——Cookie を自前で持つ
    - **`[AutoValidateAntiforgeryToken]` のある API は、`X-ANTIFORGERY-TOKEN` の見出しが無いと 400 になる**——同じ名前の Cookie から読んで載せる
    - **`-Path` はカンマで区切って並べる**（空白を入れない。`pwsh -File` は空白で引数を分ける）
    - 応答の本文が JSON か文字なら写し、それ以外（`/api/module_data/list` の MessagePack など）はバイト数だけを出す
      ——**バイト数では件数は分からない**（候補が 0 件でも 200 で返る）。件数は画面か DB で見る

    **終了コード**: 0 = 全部の要求が 2xx（`-ExpectCode` を渡したなら、全部がその断りだった）。
    1 = ログインに失敗した。2 = ログインは通ったが、利用者として読めなかった（current_user が値を返さない）。
    3 = 2xx 以外が返った。4 = `-ExpectCode` と違う応答が返った——**その時点で止め、残りの Path は投げない**。

.EXAMPLE
    # PRM-17: 会計の役割を持たない admin で、可否・取消・訂正・複製の 4 本を投げる（鍵の字は小文字始まり、値は文字列）。
    # **-ExpectCode を必ず渡す**——守りが壊れて可否が断らなかった回に、取消（取り返しがつかない）まで進まないように。
    # 念のため先に稼働 DB を退避する: pwsh -NoProfile -File tools/clb/db_snapshot.ps1 -Save -Name before-prm17
    pwsh -NoProfile -File tools/server/api_as.ps1 -User admin -ExpectCode E-NOT-AUTHORIZED `
        -Path /api/journals/availability,/api/journals/reverse,/api/journals/correct,/api/journals/duplicate `
        -Body '{"originalEntryId":"200"}'

.EXAMPLE
    # PRM-11 のうちサーバの読み取り条件だけ: 経理担当で、画面の候補の窓が打つ一覧の要求を投げ直す
    # （本文はブラウザで window.fetch を包んで捉える）。**画面に候補が出るかは、これでは分からない**——台本の期待は画面で見る。
    pwsh -NoProfile -File tools/server/api_as.ps1 -User soumu_ippan -Path /api/module_data/list -BodyFile <捉えた本文の JSON>
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)][string]$User,
    [Parameter(Mandatory)][string[]]$Path,
    [string]$Body,
    [string]$BodyFile,
    [string]$ExpectCode,
    [string]$BaseUrl = 'http://localhost:5085'
)

$ErrorActionPreference = 'Stop'

# `pwsh -File` は `-Path a, b` を配列にしない（空白の後ろが別の引数になる）ので、カンマで区切った 1 つの字も受けて分ける
$Path = @($Path | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

if ($Body -and $BodyFile) { throw '-Body と -BodyFile は同時に指定できない。' }
$payload = if ($BodyFile) { Get-Content -Raw -Encoding utf8 $BodyFile } elseif ($Body) { $Body } else { $null }
if ($BodyFile -and -not "$payload".Trim()) { throw "-BodyFile が空である: $BodyFile" }

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..' '..')
$localEnv = Join-Path $repoRoot 'Designer' 'LocalEnvironment.md'
$row = if (Test-Path $localEnv) {
    Get-Content -Encoding utf8 $localEnv | Where-Object { $_ -match "^\|\s*``?$([regex]::Escape($User))``?\s*\|" } | Select-Object -First 1
}
$secret = if ($row) { ($row -split '\|')[2].Trim().Trim([char]96) }
          elseif ($User -eq 'admin') { $User }
          else { throw "Designer/LocalEnvironment.md の開発用アカウントの表に $User の行が無い。" }

$jar = @{}

function Receive-Cookie($response) {
    foreach ($line in @($response.Headers['Set-Cookie'])) {
        if (-not $line) { continue }
        $pair = ($line -split ';')[0]
        $at = $pair.IndexOf('=')
        if ($at -gt 0) { $jar[$pair.Substring(0, $at).Trim()] = $pair.Substring($at + 1) }
    }
}

function Send-Request([string]$method, [string]$relative, $content) {
    $headers = @{}
    if ($jar.Count -gt 0) { $headers['Cookie'] = ($jar.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join '; ' }
    if ($jar.ContainsKey('X-ANTIFORGERY-TOKEN')) { $headers['X-ANTIFORGERY-TOKEN'] = [uri]::UnescapeDataString($jar['X-ANTIFORGERY-TOKEN']) }
    $request = @{ Uri = "$BaseUrl$relative"; Method = $method; Headers = $headers; UseBasicParsing = $true; SkipHttpErrorCheck = $true }
    if ($null -ne $content) {
        $request['ContentType'] = 'application/json'
        $request['Body'] = [System.Text.Encoding]::UTF8.GetBytes($content)
    }
    $response = Invoke-WebRequest @request
    Receive-Cookie $response
    return $response
}

function Test-Json($response) { return "$($response.Headers['Content-Type'])" -match 'json|text' }

function Format-Response($response) {
    if (Test-Json $response) { return "$($response.StatusCode) $($response.Content)" }
    $length = if ($response.RawContentStream) { $response.RawContentStream.Length } else { 0 }
    return "$($response.StatusCode) ($($response.Headers['Content-Type'])) bytes=$length（件数はこの字からは分からない）"
}

function Get-FirstViolationCode($response) {
    if (-not (Test-Json $response)) { return $null }
    try { $json = $response.Content | ConvertFrom-Json } catch { return $null }
    $violations = @($json.violations)
    if ($violations.Count -eq 0) { return $null }
    return "$($violations[0].code)"
}

$null = Send-Request 'GET' '/api/account/antiforgery' $null
$login = Send-Request 'POST' '/api/account/login' (@{ Id = $User; Password = $secret } | ConvertTo-Json -Compress)
"login: $($login.StatusCode)"
if ($login.StatusCode -ne 200) { exit 1 }

# ログインで Cookie が替わるので、トークンを取り直す
$null = Send-Request 'GET' '/api/account/antiforgery' $null
$me = Send-Request 'GET' '/api/account/current_user' $null
"current_user: $(Format-Response $me)"
$meValue = if (Test-Json $me) { try { ($me.Content | ConvertFrom-Json).value } catch { $null } }
if ($me.StatusCode -ne 200 -or -not "$meValue") {
    'current_user が利用者を返さなかった——セッションが載っていない。以降の要求は投げない。'
    exit 2
}

$exitCode = 0
foreach ($relative in $Path) {
    $response = Send-Request 'POST' $relative $payload
    "$relative -> $(Format-Response $response)"
    if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) { $exitCode = 3; break }
    if ($ExpectCode) {
        $code = Get-FirstViolationCode $response
        if ($code -ne $ExpectCode) {
            "期待した断り $ExpectCode ではなかった（最初の違反のコード: $(if ($code) { $code } else { 'なし' })）——残りの Path は投げない。"
            $exitCode = 4
            break
        }
    }
}

$logout = Send-Request 'POST' '/api/account/logout' $null
"logout: $($logout.StatusCode)"
exit $exitCode
