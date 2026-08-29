$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
dotnet restore (Join-Path $root 'MGGX-PC-Agent.sln')
msbuild (Join-Path $root 'MGGX-PC-Agent.sln') /m /p:Configuration=Release /p:RestoreIgnoreFailedSources=false
dotnet test (Join-Path $root 'MGGX-PC-Agent.sln') -c Release --no-build --logger "trx;LogFileName=tests.trx"
dotnet publish (Join-Path $root 'src/MGGX.PCAgent.Service/MGGX.PCAgent.Service.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $artifacts 'publish/service')
$controlPublish = Join-Path $artifacts 'publish/control'
msbuild (Join-Path $root 'src/MGGX.PCAgent.Control/MGGX.PCAgent.Control.csproj') /t:Publish /p:Configuration=Release /p:RuntimeIdentifier=win-x64 /p:SelfContained=true /p:PublishDir="$controlPublish\"
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" (Join-Path $root 'installer/MGGX-PC-Agent.iss')
$portable = Join-Path $artifacts 'MGGX-PC-Agent-portable-x64.zip'
Compress-Archive -Path (Join-Path $artifacts 'publish/*') -DestinationPath $portable -Force
Get-ChildItem $artifacts -File | Where-Object { $_.Extension -in '.exe', '.zip' } | ForEach-Object {
  "{0} *{1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content (Join-Path $artifacts 'SHA256SUMS.txt') -Encoding ascii
