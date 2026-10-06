# tools-setup.ps1 - 下载编译 SnipTranslate 所需的三套工具链
# 全部来自 NuGet，只在编译时用得到，不进最终产物

param(
    # 下载用的代理，例如 http://127.0.0.1:端口
    # 默认读环境变量 HTTP_PROXY，没设就直连
    [string]$Proxy = $env:HTTP_PROXY
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Tools = Join-Path $Root '_tools'
New-Item -ItemType Directory -Force -Path $Tools | Out-Null

$packages = @(
    @{ name = 'microsoft.net.compilers.toolset';                        ver = '4.11.0';  dir = 'roslyn' },
    @{ name = 'microsoft.netframework.referenceassemblies.net48';       ver = '1.0.3';   dir = 'net48ref' },
    @{ name = 'microsoft.windows.sdk.contracts';                        ver = '10.0.19041.2'; dir = 'sdkcontracts' }
)

foreach ($p in $packages) {
    $target = Join-Path $Tools $p.dir
    if (Test-Path $target) {
        Write-Host ("  [跳过] {0} 已存在" -f $p.dir) -ForegroundColor DarkGray
        continue
    }

    $url = "https://api.nuget.org/v3-flatcontainer/$($p.name)/$($p.ver)/$($p.name).$($p.ver).nupkg"
    $nupkg = Join-Path $Tools ($p.dir + '.nupkg')

    Write-Host ("  [下载] {0} {1}" -f $p.name, $p.ver)
    & curl.exe -sL --max-time 600 -o $nupkg $url
    if (-not (Test-Path $nupkg) -or (Get-Item $nupkg).Length -lt 10000) {
        if ($Proxy) {
            Write-Host ("         直连失败，改用代理 {0} 重试" -f $Proxy) -ForegroundColor Yellow
            & curl.exe -sL --max-time 600 -x $Proxy -o $nupkg $url
        } else {
            Write-Host "         直连失败。如果你需要代理，用 -Proxy http://127.0.0.1:端口 重跑" -ForegroundColor Yellow
        }
    }
    $fi = Get-Item $nupkg -ErrorAction SilentlyContinue
    if (-not $fi -or $fi.Length -lt 10000) { throw ("下载失败: " + $url) }

    Copy-Item $nupkg ($nupkg -replace '\.nupkg$', '.zip') -Force
    Expand-Archive -Path ($nupkg -replace '\.nupkg$', '.zip') -DestinationPath $target -Force
    Remove-Item $nupkg, ($nupkg -replace '\.nupkg$', '.zip') -Force
    Write-Host ("         -> {0}  ({1:N1} MB)" -f $p.dir, ($fi.Length / 1MB)) -ForegroundColor Green
}

# 精简：删掉编译用不到的文档和多余目录
Get-ChildItem (Join-Path $Tools 'net48ref') -Recurse -Filter '*.xml' -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem (Join-Path $Tools 'sdkcontracts\ref') -Recurse -Filter '*.xml' -ErrorAction SilentlyContinue | Remove-Item -Force
Remove-Item (Join-Path $Tools 'sdkcontracts\c') -Recurse -Force -ErrorAction SilentlyContinue

$total = (Get-ChildItem $Tools -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("  工具链就绪，共 {0:N1} MB" -f ($total / 1MB)) -ForegroundColor Green
