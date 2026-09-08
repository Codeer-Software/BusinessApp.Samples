<#
.SYNOPSIS
    DB マイグレーションのランナー（ADR-0020）。DB アクセスは sql CLI のみ。

.DESCRIPTION
    スキーマの正典は Designer/ddl/（現在形）、既存 DB への配達物が Designer/migrations/。
    このランナーは 4 つのことだけをする。

      -Adopt   稼働 DB に schema_migrations を作り、現時点を「適用済み」として刻む（一度だけ）。
               前提: その時点の稼働 DB が正典と同値であること（直後に -Verify で確かめる）。
      -Apply   未適用のマイグレーションを番号順に適用する。1 本 1 トランザクションで、
               schema_migrations への記録も同じトランザクションに入れる（実測 2026-08-25:
               sql CLI は --file も --query も文ごとの autocommit で、トランザクションでは
               包まない。そこでランナーが SQL 文中の BEGIN/COMMIT で包んだものを --query で
               流す。途中で失敗すると本体も記録も丸ごと巻き戻る）。
      -Status  適用済み・未適用・チェックサムの一覧。
      -Verify  稼働 DB の sqlite_master と、正典（ddl/）をインメモリに再生した正解を突き合わせる。
               比較は BusinessApp.SchemaVerifyCli（同値テストと同じ物差し）。

    適用済みマイグレーションの編集はチェックサム（SHA-256。改行は LF に正規化）で機械的に拒む。
    巻き戻し（Down）は作らない。開発中の DB は捨てて作り直す（ADR-0020 §6）。

.PARAMETER DataSource
    designer.settings.json の DataSources の Name。

.EXAMPLE
    pwsh -NoProfile -File tools/clb/migrate.ps1 -Status

.EXAMPLE
    pwsh -NoProfile -File tools/clb/migrate.ps1 -Apply
    適用後はサーバとデザイナの再起動が要る（列定義が static にキャッシュされるため）。
#>
[CmdletBinding()]
param(
    [switch]$Adopt,
    [switch]$Apply,
    [switch]$Status,
    [switch]$Verify,
    [string]$DataSource = 'BusinessAppSQLite'
)

$ErrorActionPreference = 'Stop'

$selected = @($Adopt, $Apply, $Status, $Verify) | Where-Object { $_ }
if ($selected.Count -ne 1) { throw '-Adopt / -Apply / -Status / -Verify のどれか 1 つを指定する。' }

. (Join-Path $PSScriptRoot '_designer.ps1')

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$migrationsDir = Join-Path $repoRoot 'Designer/migrations'
$baselineDir = Join-Path $migrationsDir 'baseline'

function Invoke-SqlQuery {
    param([Parameter(Mandatory)][string]$Sql)

    # 中身は _designer.ps1 が持つ（db_snapshot.ps1 と同じ 1 つを使う。docs/20 §4）。
    return Invoke-DesignerSql -RepoRoot $repoRoot -DataSource $DataSource -Sql $Sql
}

function Get-MigrationChecksum {
    param([Parameter(Mandatory)][string]$Path)

    # 改行を LF に正規化してから取る。Git の改行変換でチェックサムが揺れないようにするため。
    $text = [System.IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return [System.Convert]::ToHexString($hash).ToLowerInvariant()
}

function Get-MigrationFiles {
    if (-not (Test-Path $migrationsDir)) { throw "Designer/migrations が無い: $migrationsDir" }

    $files = @(Get-ChildItem -Path $migrationsDir -Filter '*.sql' -File | Sort-Object Name)
    $list = foreach ($f in $files) {
        if ($f.Name -notmatch '^(\d{4})_[a-z0-9_]+\.sql$') {
            throw "ファイル名が規約外: $($f.Name)（NNNN_小文字スネークケース.sql）"
        }
        [pscustomobject]@{
            Version  = [int]$Matches[1]
            Name     = $f.Name
            Path     = $f.FullName
            Checksum = Get-MigrationChecksum -Path $f.FullName
        }
    }

    $duplicates = @($list | Group-Object Version | Where-Object Count -gt 1)
    if ($duplicates.Count -gt 0) { throw "番号が重複している: $($duplicates.Name -join ', ')" }
    return @($list)
}

function Get-BaselineVersion {
    $versionFile = Join-Path $baselineDir 'VERSION'
    if (-not (Test-Path $versionFile)) { throw "baseline/VERSION が無い: $versionFile" }
    return [int](Get-Content $versionFile -Raw).Trim()
}

function Test-Adopted {
    $result = Invoke-SqlQuery "SELECT COUNT(*) AS n FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations';"
    return [int]$result.results[0].rows[0].n -eq 1
}

function Get-AppliedMigrations {
    $result = Invoke-SqlQuery 'SELECT version, name, checksum, applied_at FROM schema_migrations ORDER BY version;'
    if ($result.results[0].rowCount -eq 0) { return @() }
    return @($result.results[0].rows)
}

function Assert-NotTooOld {
    param($Applied, [int]$BaselineVersion)

    $maxApplied = 0
    if ($Applied.Count -gt 0) { $maxApplied = ($Applied | Measure-Object -Property version -Maximum).Maximum }
    if ($maxApplied -lt $BaselineVersion) {
        throw "この DB は古すぎる（適用済みの最大番号 $maxApplied < baseline VERSION $BaselineVersion）。" +
            '掃除で消したマイグレーションが要る。バックアップから戻すか、正典から作り直すこと。'
    }
}

# 稼働 DB のスキーマを正典（ddl/）と突き合わせる。比較は BusinessApp.SchemaVerifyCli
# （同値テストと同じ物差し）。出力をそのまま流し、終了コード（0 = 一致）を返す。
function Invoke-SchemaVerify {
    $cliProject = Join-Path $repoRoot 'BusinessApp/BusinessApp.SchemaVerifyCli/BusinessApp.SchemaVerifyCli.csproj'
    & dotnet build $cliProject --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'BusinessApp.SchemaVerifyCli のビルドに失敗した。' }
    $cliDll = Join-Path $repoRoot 'BusinessApp/BusinessApp.SchemaVerifyCli/bin/Debug/net8.0/BusinessApp.SchemaVerifyCli.dll'
    if (-not (Test-Path $cliDll)) { throw "ビルドしたのに見つからない: $cliDll" }

    # schema_migrations の除外はテーブルに限る（SchemaSnapshot.FromRows と同じ理由。
    # 名前だけで除くと同名トリガ等が検査の死角になる）。
    $live = Invoke-SqlQuery "SELECT type, name, tbl_name, sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' AND NOT (type = 'table' AND name = 'schema_migrations');"
    $rows = if ($live.results[0].rowCount -eq 0) { @() } else { @($live.results[0].rows) }
    $json = ConvertTo-Json -InputObject $rows -Depth 3

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'dotnet'
    $psi.ArgumentList.Add($cliDll)
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.StandardInputEncoding = [System.Text.UTF8Encoding]::new($false)
    $psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $psi.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)

    $proc = [System.Diagnostics.Process]::Start($psi)
    $proc.StandardInput.Write($json)
    $proc.StandardInput.Close()
    $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
    $stderrTask = $proc.StandardError.ReadToEndAsync()
    $proc.WaitForExit()

    if ($stderrTask.Result) { $Host.UI.WriteErrorLine($stderrTask.Result.TrimEnd()) }
    Write-Host $stdoutTask.Result.TrimEnd()
    return $proc.ExitCode
}

# 適用済みとして記録された版と、いま手元にあるファイルのずれを報告する。
# 「適用済みファイルは編集禁止」（ADR-0020）をチェックサムで機械強制する場所。
function Get-IntegrityErrors {
    param($Applied, $Files, [int]$BaselineVersion)

    $errors = @()
    $filesByVersion = @{}
    foreach ($f in $Files) { $filesByVersion[$f.Version] = $f }

    foreach ($row in $Applied) {
        $version = [int]$row.version
        if ($filesByVersion.ContainsKey($version)) {
            $file = $filesByVersion[$version]
            if ($file.Name -ne $row.name) {
                $errors += "版 $version の名前が違う（記録: $($row.name) / ファイル: $($file.Name)）。適用済みは改名しない。"
            }
            elseif ($file.Checksum -ne $row.checksum) {
                $errors += "$($row.name) が適用後に編集されている（チェックサム不一致）。適用済みは編集せず、次の番号で新しいマイグレーションを書く。"
            }
        }
        elseif ($version -gt $BaselineVersion) {
            $errors += "$($row.name)（版 $version）が適用済みと記録されているのにファイルが無い。掃除は baseline の前進とセットで行う（ADR-0020 §4）。"
        }
    }

    return $errors
}

switch ($true) {
    $Adopt {
        if (Test-Adopted) { throw '適用記録（schema_migrations）は既にある。-Adopt は一度だけ。' }

        # 採用は「いまの稼働 DB は正典と同値である」という宣言。宣言を人間の目に任せず、
        # 先に -Verify と同じ比較で機械的に確かめ、ずれていれば刻まずに止まる（ADR-0020 の動機
        # 「同期を人間の記憶に頼らない」を採用そのものにも当てる）。
        if ((Invoke-SchemaVerify) -ne 0) {
            throw '稼働 DB が正典（Designer/ddl）とずれているので採用しない。ずれを直してから -Adopt をやり直すこと。'
        }

        $files = Get-MigrationFiles
        $statements = @(
            'BEGIN;'
            'CREATE TABLE schema_migrations ('
            '    version     INTEGER PRIMARY KEY,'
            '    name        TEXT NOT NULL,'
            '    checksum    TEXT NOT NULL,'
            "    applied_at  DATETIME NOT NULL"
            ');'
        )
        foreach ($f in $files) {
            $statements += "INSERT INTO schema_migrations (version, name, checksum, applied_at) VALUES ($($f.Version), '$($f.Name)', '$($f.Checksum)', datetime('now', 'localtime'));"
        }
        $statements += 'COMMIT;'
        Invoke-SqlQuery ($statements -join "`n") | Out-Null

        Write-Host "採用した。既存のマイグレーション $($files.Count) 本を適用済みとして刻んだ。"
    }

    $Status {
        if (-not (Test-Adopted)) { throw 'まだ採用されていない。先に -Adopt を実行する。' }

        $files = Get-MigrationFiles
        $baselineVersion = Get-BaselineVersion
        $applied = Get-AppliedMigrations
        Assert-NotTooOld -Applied $applied -BaselineVersion $baselineVersion

        Write-Host "baseline VERSION: $baselineVersion（この番号までは baseline に畳み込み済み）"
        Write-Host "適用済み: $($applied.Count) 本"
        $filesByVersion = @{}
        foreach ($f in $files) { $filesByVersion[$f.Version] = $f }
        foreach ($row in $applied) {
            $version = [int]$row.version
            $state = if (-not $filesByVersion.ContainsKey($version)) {
                if ($version -le $baselineVersion) { 'baseline に畳み込み済み' } else { 'ファイルが無い!' }
            }
            elseif ($filesByVersion[$version].Checksum -ne $row.checksum) { '適用後に編集されている!' }
            else { 'OK' }
            Write-Host ("  {0:d4} {1}  適用 {2}  [{3}]" -f $version, $row.name, $row.applied_at, $state)
        }

        $maxApplied = 0
        if ($applied.Count -gt 0) { $maxApplied = ($applied | Measure-Object -Property version -Maximum).Maximum }
        $pending = @($files | Where-Object { $_.Version -gt $maxApplied })
        Write-Host "未適用: $($pending.Count) 本"
        foreach ($f in $pending) { Write-Host ("  {0:d4} {1}" -f $f.Version, $f.Name) }
    }

    $Apply {
        if (-not (Test-Adopted)) { throw 'まだ採用されていない。先に -Adopt を実行する。' }

        $files = Get-MigrationFiles
        $baselineVersion = Get-BaselineVersion
        $applied = Get-AppliedMigrations
        Assert-NotTooOld -Applied $applied -BaselineVersion $baselineVersion

        $integrityErrors = @(Get-IntegrityErrors -Applied $applied -Files $files -BaselineVersion $baselineVersion)
        if ($integrityErrors.Count -gt 0) { throw ($integrityErrors -join "`n") }

        $maxApplied = 0
        if ($applied.Count -gt 0) { $maxApplied = ($applied | Measure-Object -Property version -Maximum).Maximum }

        $appliedVersions = @($applied | ForEach-Object { [int]$_.version })
        $outOfOrder = @($files | Where-Object { $_.Version -le $maxApplied -and $_.Version -notin $appliedVersions })
        if ($outOfOrder.Count -gt 0) {
            throw "適用済みの番号より小さい未適用がある: $($outOfOrder.Name -join ', ')。番号を付け直すこと（適用は番号順）。"
        }

        $pending = @($files | Where-Object { $_.Version -gt $maxApplied } | Sort-Object Version)
        if ($pending.Count -eq 0) {
            Write-Host '未適用のマイグレーションは無い。'
            break
        }

        # 飛び番の検査は同値テストにもあるが、README の手順（テスト → Apply）を守らずに
        # 直接 -Apply された場合の防波堤としてランナーにも置く。
        $expectedVersions = @(1..$pending.Count | ForEach-Object { $maxApplied + $_ })
        $actualVersions = @($pending | ForEach-Object { $_.Version })
        if (Compare-Object $expectedVersions $actualVersions) {
            throw "未適用の番号が $($maxApplied + 1) から連続していない: $($actualVersions -join ', ')。番号を付け直すこと。"
        }

        foreach ($m in $pending) {
            $content = [System.IO.File]::ReadAllText($m.Path)
            # MigrationEquivalenceTests.ContainsTransactionStatement と鏡写し（片方だけ直さない）。
            # 素の END;（COMMIT の同義語）はトリガ本体の終端と字面で区別できないので、ここでは拒めない。
            # すり抜けた場合は下の「記録行の存在確認」が捕まえる。
            if ($content -match '(?im)^\s*(BEGIN(\s+(DEFERRED|IMMEDIATE|EXCLUSIVE))?(\s+TRANSACTION)?|COMMIT(\s+TRANSACTION)?|ROLLBACK(\s+TRANSACTION)?(\s+TO\s+\S+)?|SAVEPOINT\s+\S+|RELEASE(\s+SAVEPOINT)?\s+\S+)\s*;') {
                throw "$($m.Name): BEGIN/COMMIT/ROLLBACK/SAVEPOINT を書かない。トランザクションはランナーが張る。"
            }
            # PRAGMA はトランザクション内では効かないか（foreign_keys / journal_mode は黙って no-op）、
            # 効いてはいけないもの（writable_schema）なので機械で拒む。例外は defer_foreign_keys
            # （テーブルの作り直しに必要で、トランザクション内でも効く。README のレシピ）。
            if ($content -match '(?im)^\s*PRAGMA\s+(?!defer_foreign_keys\b)') {
                throw "$($m.Name): PRAGMA は defer_foreign_keys 以外書かない（トランザクション内では黙って無視されるか、比較の物差しを壊す）。"
            }
            if (-not $content.TrimEnd().EndsWith(';')) {
                throw "$($m.Name): 末尾の文が ; で終わっていない。"
            }

            $record = "INSERT INTO schema_migrations (version, name, checksum, applied_at) VALUES ($($m.Version), '$($m.Name)', '$($m.Checksum)', datetime('now', 'localtime'));"
            $wrapped = "BEGIN;`n$content`n$record`nCOMMIT;"
            if ($wrapped.Length -gt 30000) {
                throw "$($m.Name): 大きすぎてコマンドラインで渡せない。マイグレーションを分割すること。"
            }

            Invoke-SqlQuery $wrapped | Out-Null

            # 「成功表示のまま実は適用されていない・半端に適用された」を塞ぐ最後の網。
            # 素の END; の混入（途中で確定して記録前に落ちる）や、未終端のブロックコメント
            # （記録と COMMIT が丸ごとコメントに呑まれる）は CLI が正常終了することがあるため、
            # 別クエリで記録行を読み直して確かめる。
            $check = Invoke-SqlQuery "SELECT COUNT(*) AS n FROM schema_migrations WHERE version = $($m.Version) AND checksum = '$($m.Checksum)';"
            if ([int]$check.results[0].rows[0].n -ne 1) {
                throw "$($m.Name): 適用後の記録が見つからない。適用が半端になっている可能性がある。-Verify と -Status で状態を確かめること。"
            }
            Write-Host "適用した: $($m.Name)"
        }

        Write-Host "$($pending.Count) 本を適用した。-Verify で同値を確かめること。"
        Write-Host 'スキーマが変わったので、サーバとデザイナの再起動が要る（列定義が static にキャッシュされる）。'
    }

    $Verify {
        $exitCode = Invoke-SchemaVerify

        # スキーマ比較は「適用忘れの DML マイグレーション」を検出できない（sqlite_master に痕跡が
        # 無いため）。採用済みなら未適用が残っていないことも検査に含める。
        if (Test-Adopted) {
            $files = Get-MigrationFiles
            $applied = Get-AppliedMigrations
            $maxApplied = 0
            if ($applied.Count -gt 0) { $maxApplied = ($applied | Measure-Object -Property version -Maximum).Maximum }
            $pending = @($files | Where-Object { $_.Version -gt $maxApplied })
            if ($pending.Count -gt 0) {
                Write-Host "未適用のマイグレーションが $($pending.Count) 本ある: $($pending.Name -join ', ')。-Apply で適用すること。"
                if ($exitCode -eq 0) { $exitCode = 1 }
            }
        }
        else {
            Write-Host '（この DB はまだ採用されていない。適用状況の検査はしていない）'
        }

        exit $exitCode
    }
}
