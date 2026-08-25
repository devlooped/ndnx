# Install ndnx from GitHub Releases.
#   irm https://github.com/devlooped/ndnx/releases/latest/download/install.ps1 | iex
# Env / flags: NDNX_VERSION, NDNX_PREFIX, NDNX_ARCHIVE, NDNX_RID, NDNX_REPO, NDNX_SKIP_PATH
# Also accepts --version --prefix --archive --rid --repo --skip-path

$ErrorActionPreference = 'Stop'

$Repo = if ($env:NDNX_REPO) { $env:NDNX_REPO } else { 'devlooped/ndnx' }
$Version = $env:NDNX_VERSION
$Prefix = $env:NDNX_PREFIX
$Archive = $env:NDNX_ARCHIVE
$Rid = $env:NDNX_RID
$SkipPath = $env:NDNX_SKIP_PATH -eq '1'

function Get-NdnxRuntimeIdentifier {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    $archName = switch ($arch) {
        'X64' { 'x64' }
        'Arm64' { 'arm64' }
        default {
            throw "ndnx: unsupported architecture '$arch'"
        }
    }

    if ($IsWindows -or $env:OS -eq 'Windows_NT') {
        return "win-$archName"
    }

    if (Get-Variable IsMacOS -ErrorAction SilentlyContinue) {
        if ($IsMacOS) { return "osx-$archName" }
        if ($IsLinux) { return "linux-$archName" }
    }

    throw "ndnx: unsupported OS"
}

function Send-EnvironmentChange {
    if (-not ($IsWindows -or $env:OS -eq 'Windows_NT')) {
        return
    }

    if (-not ('Win32.NativeBroadcast' -as [type])) {
        Add-Type -Namespace Win32 -Name NativeBroadcast -MemberDefinition @"
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern IntPtr SendMessageTimeout(
    IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam,
    uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
"@
    }

    $result = [UIntPtr]::Zero
    [void][Win32.NativeBroadcast]::SendMessageTimeout(
        [IntPtr]0xffff,
        0x1a,
        [UIntPtr]::Zero,
        'Environment',
        2,
        5000,
        [ref]$result)
}

function Add-NdnxToUserPath([string]$dir) {
    $parts = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (-not $parts) { $parts = '' }
    $entries = $parts.Split([char]';', [StringSplitOptions]::RemoveEmptyEntries)
    $env:Path = "$dir;$env:Path"
    if ($entries -contains $dir) {
        return
    }

    $updated = if ($parts) { "$parts;$dir" } else { $dir }
    [Environment]::SetEnvironmentVariable('Path', $updated, 'User')
    Send-EnvironmentChange
    Write-Host "added $dir to the user PATH"
}

for ($i = 0; $i -lt $args.Count; $i++) {
    switch -Regex ($args[$i]) {
        '^--version$|^-Version$' { $Version = $args[++$i]; continue }
        '^--prefix$|^-Prefix$' { $Prefix = $args[++$i]; continue }
        '^--archive$|^-Archive$' { $Archive = $args[++$i]; continue }
        '^--rid$|^-Rid$' { $Rid = $args[++$i]; continue }
        '^--repo$|^-Repo$' { $Repo = $args[++$i]; continue }
        '^--skip-path$|^-SkipPath$' { $SkipPath = $true; continue }
        default { throw "ndnx: unrecognized argument '$($args[$i])'" }
    }
}

if (-not $Rid) {
    $Rid = Get-NdnxRuntimeIdentifier
}

$windows = $Rid.StartsWith('win', [StringComparison]::OrdinalIgnoreCase)
$binary = if ($windows) { 'ndnx.exe' } else { 'ndnx' }
$ext = if ($windows) { 'zip' } else { 'tar.gz' }

if (-not $Prefix) {
    $Prefix = if ($windows) {
        Join-Path $env:LOCALAPPDATA 'ndnx'
    } else {
        Join-Path $HOME '.local/bin'
    }
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("ndnx-install-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    if (-not $Archive) {
        if ($Version) {
            if ($Version -eq 'ci') {
                $tag = 'ci'
                $resolved = 'ci'
            } elseif ($Version.StartsWith('v')) {
                $tag = $Version
                $resolved = $tag.TrimStart('v')
            } else {
                $tag = "v$Version"
                $resolved = $Version
            }
        } else {
            $release = Invoke-RestMethod -Headers @{ Accept = 'application/vnd.github+json' } `
                -Uri "https://api.github.com/repos/$Repo/releases/latest"
            $tag = $release.tag_name
            if (-not $tag) { throw "ndnx: could not resolve latest release of $Repo" }
            $resolved = $tag.TrimStart('v')
        }

        $name = "ndnx-$resolved-$Rid.$ext"
        $base = "https://github.com/$Repo/releases/download/$tag"
        $Archive = Join-Path $tmp $name
        Invoke-WebRequest -Uri "$base/$name" -OutFile $Archive
        Invoke-WebRequest -Uri "$base/$name.sha256" -OutFile "$Archive.sha256"
    }

    if (Test-Path "$Archive.sha256") {
        $expected = ((Get-Content -Raw "$Archive.sha256").Trim() -split '\s+')[0].ToLowerInvariant()
        $actual = (Get-FileHash -Algorithm SHA256 -Path $Archive).Hash.ToLowerInvariant()
        if ($actual -ne $expected) {
            throw "ndnx: SHA256 mismatch for $(Split-Path $Archive -Leaf)`n  expected: $expected`n  actual:   $actual"
        }
    }

    $extract = Join-Path $tmp 'extract'
    New-Item -ItemType Directory -Path $extract | Out-Null
    if ($windows) {
        Expand-Archive -Path $Archive -DestinationPath $extract -Force
    } else {
        tar -xzf $Archive -C $extract
    }

    $source = Join-Path $extract $binary
    if (-not (Test-Path $source)) {
        throw "ndnx: archive did not contain $binary"
    }

    New-Item -ItemType Directory -Force -Path $Prefix | Out-Null
    $dest = Join-Path $Prefix $binary
    Copy-Item -Force -Path $source -Destination $dest
    Write-Host "installed $dest"

    if (-not $SkipPath) {
        Add-NdnxToUserPath $Prefix
    }
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
