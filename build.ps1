# build.ps1 - 用系统自带的 .NET Framework + NuGet 上的 Roslyn 编译器构建 SnipTranslate
# 产物：<项目根>\SnipTranslate.exe（单文件，零运行时依赖）

param(
    [switch]$NoIcon
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path

$Csc       = Join-Path $Root '_tools\roslyn\tasks\net472\csc.exe'
$RefRoot   = Join-Path $Root '_tools\net48ref\build\.NETFramework\v4.8'
$WinMdRoot = Join-Path $Root '_tools\sdkcontracts\ref\netstandard2.0'
$FwRuntime = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'

foreach ($p in @($Csc, $RefRoot, $WinMdRoot, $FwRuntime)) {
    if (-not (Test-Path $p)) { throw "缺少编译依赖：$p`n请先运行 tools-setup.ps1" }
}

# ---------- 1. 生成多尺寸图标 ----------
# 具体画法和 ICO 组装在 make-icon.ps1 里（内嵌 C#，字节布局精确可控）。
# 设计：圆角蓝底 + 白色对话气泡 + 字母 A，不含任何中文。
$buildDir = Join-Path $Root 'build'
if (-not (Test-Path $buildDir)) { New-Item -ItemType Directory -Path $buildDir | Out-Null }
$iconPath = Join-Path $buildDir 'app.ico'

if (-not $NoIcon) {
    & (Join-Path $Root 'make-icon.ps1') -Out $iconPath
    if (-not (Test-Path $iconPath)) { throw "图标生成失败：$iconPath" }
    Write-Host ("  [1/3] 图标就绪（{0:N1} KB）" -f ((Get-Item $iconPath).Length / 1KB)) -ForegroundColor DarkGray
}

# ---------- 2. 收集引用 ----------
$refs = New-Object System.Collections.Generic.List[string]
foreach ($n in @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Drawing.dll',
                 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'System.Speech.dll')) {
    $p = Join-Path $RefRoot $n
    if (-not (Test-Path $p)) { throw "缺少引用程序集：$p" }
    $refs.Add($p)
}
Get-ChildItem (Join-Path $RefRoot 'Facades') -Filter '*.dll' | ForEach-Object { $refs.Add($_.FullName) }
$refs.Add((Join-Path $FwRuntime 'System.Runtime.WindowsRuntime.dll'))
foreach ($n in @('Windows.WinMD', 'Windows.Foundation.FoundationContract.winmd',
                 'Windows.Foundation.UniversalApiContract.winmd')) {
    $refs.Add((Join-Path $WinMdRoot $n))
}
Write-Host "  [2/3] 引用 $($refs.Count) 个程序集" -ForegroundColor DarkGray

# ---------- 3. 编译 ----------
$sources = @(Get-ChildItem (Join-Path $Root 'src') -Filter '*.cs' |
             Sort-Object Name | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw 'src 目录下没有 .cs 文件' }

$out = Join-Path $Root 'SnipTranslate.exe'
if (Test-Path $out) { Remove-Item $out -Force }

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/platform:anycpu'
    '/optimize+'
    '/langversion:latest'
    '/codepage:65001'          # 源码是 UTF-8 无 BOM，必须显式指定，否则中文串会乱码
    '/nowarn:1685,1701,1702'
    "/out:$out"
)
if (-not $NoIcon) {
    $cscArgs += "/win32icon:$iconPath"                       # 资源管理器 / 任务栏用的图标
    $cscArgs += "/resource:$iconPath,SnipTranslate.app.ico"  # 托盘图标从这里按尺寸取
}
foreach ($r in $refs) { $cscArgs += "/r:$r" }
$cscArgs += $sources

& $Csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败（csc 退出码 $LASTEXITCODE）" }
if (-not (Test-Path $out)) { throw '编译没有产出 exe' }

$fi = Get-Item $out
Write-Host ""
Write-Host "  编译成功" -ForegroundColor Green
Write-Host ("  产物 : {0}" -f $fi.FullName)
Write-Host ("  体积 : {0:N0} 字节 ({1:N1} KB)" -f $fi.Length, ($fi.Length / 1KB))
Write-Host ("  源文件 {0} 个" -f $sources.Count)
Write-Host ""
