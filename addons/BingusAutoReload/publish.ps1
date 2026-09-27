param(
    [Parameter(Mandatory)][string]$AssetPath,
    [Parameter(Mandatory)][string]$Commit,
    [string]$Tag = 'auto-reload-0.3.0-test'
)
$ErrorActionPreference = 'Stop'
$AssetPath = (Resolve-Path -LiteralPath $AssetPath).Path
$assetName = [IO.Path]::GetFileName($AssetPath)
if ($assetName -ne 'HD2-AutoReload-0.3.0-test.zip') { throw 'Unexpected addon package name.' }
if ($Commit -notmatch '^[0-9a-f]{40}$') { throw 'A full source commit hash is required.' }
$credentialLines = "protocol=https`nhost=github.com`n`n" | git credential fill
if ($LASTEXITCODE -ne 0) { throw 'GitHub Git authentication is unavailable.' }
$credential = @{}
foreach ($line in $credentialLines) {
    $parts = $line.Split('=', 2)
    if ($parts.Length -eq 2) { $credential[$parts[0]] = $parts[1] }
}
if (-not $credential['password']) { throw 'GitHub Git authentication is unavailable.' }
$headers = @{ Authorization = 'Bearer ' + $credential['password']; Accept = 'application/vnd.github+json';
    'User-Agent' = 'HD2-Helper-Addon-Release'; 'X-GitHub-Api-Version' = '2022-11-28' }
$api = 'https://api.github.com/repos/pwj891129-arch/HD2-Helper/releases'
$notes = @'
## HD2 Helper Auto Reload 0.3.0-test

헬퍼 실행 파일의 업데이트가 아닌 Arsenal / Bingus용 별도 애드온 테스트입니다.

- 현재 든 무기의 탄이 방금 0발로 바뀌면 재장전을 요청합니다.
- 실제 무기 교체 직후 새 무기가 빈 상태이면 재장전을 요청합니다.
- 빈 무기로 발사키를 새로 누르면 재장전을 요청합니다.
- 약실의 탄, 예비 탄약, 재장전 진행 상태와 현재 무기를 확인합니다.
- 탄약 정보가 불명확하거나 이미 재장전 중이면 입력하지 않습니다.
- 중복 입력을 억제하고 F8로 일시정지/재개할 수 있습니다.

### 설치 및 제한

게임을 종료한 상태에서 ZIP을 Arsenal에 가져오고 Bingus Shared Loader v15+ / API 1과 함께 활성화하여 배포하세요.
기존 헬퍼 자동 재장전과 이전 Auto Reload 진단/보조 스크립트는 끄고 테스트하세요.
기본 키는 좌클릭 / R이며 `%APPDATA%\HD2AutoReload.ini`에서 변경할 수 있습니다.
과열식 무기와 활성 언더배럴, 차량/거치 무기는 이번 테스트에서 제외했습니다.
분리된 LuaJIT 테스트 98개 검증 항목이 통과했습니다. 실제 게임 테스트 및 현재 게임 버전 호환성은 아직 확인하지 못했습니다.
Windows 입력 전송 성공은 게임 내 재장전 완료를 뜻하지 않습니다.
모드의 게임/안티치트 허용 여부는 보증하지 않습니다.

진단 로그: `%LOCALAPPDATA%\CowboyBingus\Helldivers2\Logs\hd2_helper_auto_reload.log`

### 출처

탄약 읽기 코어는 DDRK1NG의 HD2 HUD+ 0.1.2에 포함된 명시적 재사용 허용 README에 따라 활용했습니다.
원본 권한 README와 출처를 패키지에 포함했습니다. 원본 게임 부트/화면 표시 코드와 최신 HUD 버전은 복제하지 않았습니다.
[HD2 HUD+](https://www.nexusmods.com/helldivers2/mods/15298) · [Bingus Shared Loader](https://github.com/CowboyBingus/BingusSharedLoader)
'@
try {
    $releases = Invoke-RestMethod -Uri ($api + '?per_page=100') -Headers $headers
    $release = $releases | Where-Object tag_name -eq $Tag | Select-Object -First 1
    if (-not $release) {
        $body = @{ tag_name = $Tag; target_commitish = $Commit; name = 'HD2 Auto Reload 0.3.0-test (Arsenal / Bingus addon)';
            body = $notes; draft = $true; prerelease = $true } | ConvertTo-Json
        $release = Invoke-RestMethod -Method Post -Uri $api -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
    }
    $expectedHash = (Get-FileHash -LiteralPath $AssetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $existing = $release.assets | Where-Object name -eq $assetName | Select-Object -First 1
    if ($existing) {
        if ($existing.digest -ne "sha256:$expectedHash") { throw 'Existing package differs; not replacing it.' }
    } else {
        $uri = $release.upload_url.Split('{')[0] + '?name=' + [Uri]::EscapeDataString($assetName)
        $uploaded = Invoke-RestMethod -Method Post -Uri $uri -Headers $headers -ContentType 'application/zip' -InFile $AssetPath
        if ($uploaded.state -ne 'uploaded' -or $uploaded.digest -ne "sha256:$expectedHash") {
            throw 'Asset upload verification failed; leaving draft unpublished.'
        }
    }
    if ($release.draft) {
        $body = @{ draft = $false; prerelease = $true; make_latest = 'false' } | ConvertTo-Json
        $release = Invoke-RestMethod -Method Patch -Uri ($api + '/' + $release.id) -Headers $headers -ContentType 'application/json' -Body $body
    }
    $verified = Invoke-RestMethod -Uri ($api + '/tags/' + $Tag) -Headers $headers
    $asset = $verified.assets | Where-Object name -eq $assetName | Select-Object -First 1
    if ($verified.draft -or -not $verified.prerelease -or $asset.digest -ne "sha256:$expectedHash") {
        throw 'Published release verification failed.'
    }
    [ordered]@{ url = $verified.html_url; prerelease = $verified.prerelease; asset = $asset.name;
        sha256 = $expectedHash; sourceCommit = $Commit } | ConvertTo-Json
} finally {
    $headers.Clear(); $credential.Clear(); $credentialLines = $null
}
