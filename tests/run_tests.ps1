$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskFramework = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319'
$taskOutput = Join-Path $taskRoot '.git\test-bin'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskRefs = @('PresentationFramework', 'PresentationCore', 'WindowsBase') | ForEach-Object {
    '/r:' + (Join-Path $taskFramework ('WPF\' + $_ + '.dll'))
}
foreach ($taskSuite in @('UsageAccountingTests', 'ProductAccountingTests', 'ProductUiTests')) {
    $taskExe = Join-Path $taskOutput ($taskSuite + '.exe')
    & (Join-Path $taskFramework 'csc.exe') /nologo /target:exe /platform:x64 /optimize+ /utf8output "/main:$taskSuite" "/out:$taskExe" @taskRefs /r:System.Xaml.dll /r:System.Security.dll /r:System.Web.Extensions.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll (Join-Path $taskRoot 'src\*.cs') (Join-Path $PSScriptRoot ($taskSuite + '.cs'))
    if ($LASTEXITCODE -ne 0) { throw "$taskSuite failed to compile" }
    & $taskExe (Join-Path $taskOutput 'screenshots')
    if ($LASTEXITCODE -ne 0) { throw "$taskSuite failed" }
}
