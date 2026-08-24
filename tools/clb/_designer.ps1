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
