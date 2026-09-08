<#
.SYNOPSIS
    デザイナ exe を headless で呼ぶための共通処理。sql.ps1 / designcheck.ps1 から dot-source する。

.DESCRIPTION
    exe のパス解決と、待ち合わせつきの実行を 1 か所に置く。
    片方だけ直して片方が古いまま、という壊れ方（追跡外ファイルの書式変更で起きる）を避ける。
#>

function Get-DesignerExePath {
    param([Parameter(Mandatory)][string]$RepoRoot)

    # exe のパスは Git 追跡外の LocalEnvironment.md にある（CLAUDE.md §5）。
    $localEnv = Join-Path $RepoRoot 'Designer/LocalEnvironment.md'
    if (-not (Test-Path $localEnv)) {
        throw "Designer/LocalEnvironment.md が無い。DesignerExePath を書いておくこと。"
    }

    $line = Select-String -Path $localEnv -Pattern '^DesignerExePath:\s*(.+)$' | Select-Object -First 1
    if (-not $line) { throw 'LocalEnvironment.md に DesignerExePath: の行が無い。' }

    $exe = $line.Matches[0].Groups[1].Value.Trim()
    if (-not (Test-Path $exe)) { throw "デザイナ exe が見つからない: $exe" }
    return $exe
}

function Invoke-DesignerSql {
    <#
    .SYNOPSIS
        `sql` サブコマンドで SQL を 1 本流し、結果 JSON を解析して返す。失敗したら例外。

    .DESCRIPTION
        **migrate.ps1 と db_snapshot.ps1 が同じ処理を持たないための置き場**
        （docs/20_実装の原則.md §4「重複定義を避ける」）。
        素の出力をそのまま返す薄い口が要るときは sql.ps1 を使う——あちらは終了コードごと素通しする。

        **失敗の理由は 1 行に畳んでから投げる。** exe の標準出力は JSON とは限らず、
        接続に失敗した種類のエラーでは接続先の断片が載りうる（CLAUDE.md §5）。
    #>
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$DataSource,
        [Parameter(Mandatory)][string]$Sql
    )

    $exe = Get-DesignerExePath -RepoRoot $RepoRoot
    $projectRoot = Join-Path $RepoRoot 'Designer/Design'
    $result = Invoke-Designer -Exe $exe -Arguments @(
        'sql', $projectRoot, '--datasource', $DataSource, '--query', $Sql)

    # 終了コードの判定が先。失敗時の標準出力は JSON とは限らず、パース例外で本当の原因が隠れる。
    if ($result.ExitCode -ne 0) {
        $reason = $null
        try { $reason = ($result.StandardOutput | ConvertFrom-Json).error } catch {}
        if (-not $reason) {
            $reason = (($result.StandardOutput -split "`r?`n") | Where-Object { $_.Trim() } |
                Select-Object -First 1)
        }
        throw "SQL が失敗した: $reason"
    }
    return $result.StandardOutput | ConvertFrom-Json
}

function Invoke-Designer {
    <#
    .SYNOPSIS
        デザイナ exe を実行し、標準出力を返す。標準エラーは標準エラーへ流す。

    .DESCRIPTION
        デザイナ exe は WinExe なので、PowerShell の & 演算子だと待たずに戻る。
        標準出力と標準エラーは**非同期で読む**。片方を ReadToEnd で待つ間に、
        もう片方のパイプバッファが埋まると双方が相手を待って止まる。

        標準エラーを標準出力に混ぜない。混ぜると「結果 JSON を標準出力に返す」契約が壊れ、
        呼び出し側の JSON パースが失敗する。
    #>
    param(
        [Parameter(Mandatory)][string]$Exe,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    # exe はリダイレクト先に OS のレガシーコードページ（日本語 Windows では CP932）で書く
    # （2026-08-25 実測。CLB 1.3.20。改善提案は docs/CLB改善提案/ の FB-003）。pwsh 7 の既定は UTF-8 読みなので、
    # 指定しないと日本語（トリガの文言など）が化け、CP932 の後続バイト 0x5C（\）が JSON を壊す。
    # FB-003 が実現して exe が UTF-8 で書くようになったら、この指定は逆に文字化けの原因になる。
    # CLB を上げて出力が化けたら、まずここを疑って外すこと。
    # なお ja-JP は ANSI も OEM も 932 なので、exe がどちらで書いているかはこの環境では
    # 区別できない（他ロケールの開発機では未検証。化けたら ANSICodePage も試すこと）。
    $legacy = [System.Text.Encoding]::GetEncoding(
        [System.Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage)
    $psi.StandardOutputEncoding = $legacy
    $psi.StandardErrorEncoding = $legacy
    foreach ($a in $Arguments) { $psi.ArgumentList.Add($a) }

    $proc = [System.Diagnostics.Process]::Start($psi)
    $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
    $stderrTask = $proc.StandardError.ReadToEndAsync()
    $proc.WaitForExit()

    $stderr = $stderrTask.Result
    if ($stderr) { $Host.UI.WriteErrorLine($stderr.TrimEnd()) }

    return [pscustomobject]@{
        StandardOutput = $stdoutTask.Result
        ExitCode       = $proc.ExitCode
    }
}
