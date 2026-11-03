# Puts the third-party files Pulse is built with in place, from their official sources, and
# refuses any file whose SHA-256 is not the one pinned here.
#
#     powershell -ExecutionPolicy Bypass -File fetch-third-party.ps1
#
# They are not kept in the repository: they are other projects' signed releases, and keeping
# copies here would let a changed file slip in unnoticed. Pinning the hash means the exact
# bytes that were tested are the bytes that ship. To move to a newer version, change the
# version, address and hash together, and test it like any other change.
#
#   PawnIO, the optional sensor driver's installer   https://github.com/namazso/PawnIO.Setup
#   PresentMon, which measures frame rates           https://github.com/GameTechDev/PresentMon
#   Dear ImGui, which the in-game overlay draws with  https://github.com/ocornut/imgui

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$files = @(
    @{
        Path   = 'PawnIO_setup.exe'
        Url    = 'https://github.com/namazso/PawnIO.Setup/releases/download/2.1.0/PawnIO_setup.exe'
        Sha256 = 'a3a46226c5e2824f4cdd42be0eecbabfc672c86f7889710f5ab1e6ad385b47a0'
    },
    @{
        Path   = 'Resources\PresentMon\PresentMon-2.5.1-x64.exe'
        Url    = 'https://github.com/GameTechDev/PresentMon/releases/download/v2.5.1/PresentMon-2.5.1-x64.exe'
        Sha256 = '9bec3083069f58f911e6a512f4806db51a27bd096103087bc1d05ef54c80a191'
    }
)

# Dear ImGui 1.93.0 (MIT), which the in-game overlay draws with: the files it is built from, each
# from the release's commit and each pinned. Source rather than a release download because ImGui
# ships none; the commit is the v1.93.0 tag's.
$files += @(
    @{
        Path   = 'InGame\third_party\imgui\imgui.cpp'
        Url    = 'https://raw.githubusercontent.com/ocornut/imgui/c1aaac65e896a45f81f47a4d68f9959e0f86c859/imgui.cpp'
        Sha256 = '9542082a1eaeffcab7b3d8069e707839ce1714d167005e9f273ac9f656b415ed'
    },
    @{
        Path   = 'InGame\third_party\imgui\imgui.h'
        Url    = 'https://raw.githubusercontent.com/ocornut/imgui/c1aaac65e896a45f81f47a4d68f9959e0f86c859/imgui.h'
        Sha256 = '314462301948fbd5673f5ead6f9a53acf54767cbee57e186e69f8f8b022c7094'
    },
    @{
        Path   = 'InGame\third_party\imgui\imgui_draw.cpp'
        Url    = 'https://raw.githubusercontent.com/ocornut/imgui/c1aaac65e896a45f81f47a4d68f9959e0f86c859/imgui_draw.cpp'
        Sha256 = 'fff3bb04ca11e32041144e83a37b89ec92f4d5c62d681eb7ef29ee6cd7663e84'
    },
    @{
        Path   = 'InGame\third_party\imgui\imgui_tables.cpp'
        Url    = 'https://raw.githubusercontent.com/ocornut/imgui/c1aaac65e896a45f81f47a4d68f9959e0f86c859/imgui_tables.cpp'
        Sha256 = '36635c1cf2ad1b7c88cdced456cceba3a1d02266d78a63d77e9cce92f4d48d61'
    },
    @{
        Path   = 'InGame\third_party\imgui\imgui_widgets.cpp'
        Url    = 'https://raw.githubusercontent.com/ocornut/imgui/c1aaac65e896a45f81f47a4d68f9959e0f86c859/imgui_widgets.cpp'
        Sha256 = 'd2d388f711688f6a5ce1ae3b30168c1bf4f954ead40235b0b2b9667452850c93'
    },
    @{
        Path   = 'InGame\third_party\imgui\imgui_internal.h'
        Url    = 'https://raw.githubusercontent.com/ocornut/imgui/c1aaac65e896a45f81f47a4d68f9959e0f86c859/imgui_internal.h'
        Sha256 = '61bc50172d907cc931159cdce48fbff1db616cda33251c9c3469e316056939e6'
    },
    @{
        Path   = 'InGame\third_party\imgui\imconfig.h'
        Url    = 'https://raw.githubusercontent.com/ocornut/imgui/c1aaac65e896a45f81f47a4d68f9959e0f86c859/imconfig.h'
        Sha256 = 'fa91f606da783669ffd485f3f9e0cd0d92cddeac459efce4c2b9327cce7a3233'
    },
    @{
        Path   = 'InGame\third_party\imgui\imstb_rectpack.h'
        Url    = 'https://raw.githubusercontent.com/ocornut/imgui/c1aaac65e896a45f81f47a4d68f9959e0f86c859/imstb_rectpack.h'
        Sha256 = '889b396795202d1457560a797a7242e96f6f132d4b88ca2d69be58bf05e1771f'
    },
    @{
        Path   = 'InGame\third_party\imgui\imstb_textedit.h'
        Url    = 'https://raw.githubusercontent.com/ocornut/imgui/c1aaac65e896a45f81f47a4d68f9959e0f86c859/imstb_textedit.h'
        Sha256 = 'a985f5fa0ed97353d493b497961e9eef52082edcd045cf6954b69990ec9d0741'
    },
    @{
        Path   = 'InGame\third_party\imgui\imstb_truetype.h'
        Url    = 'https://raw.githubusercontent.com/ocornut/imgui/c1aaac65e896a45f81f47a4d68f9959e0f86c859/imstb_truetype.h'
        Sha256 = 'c51a0f7e7ea760f2366bd3752635ec58e21fccfec4a832501639990ba6ce0528'
    },
    @{
        Path   = 'InGame\third_party\imgui\LICENSE.txt'
        Url    = 'https://raw.githubusercontent.com/ocornut/imgui/c1aaac65e896a45f81f47a4d68f9959e0f86c859/LICENSE.txt'
        Sha256 = '173506a2d6f7fb67990d257fb2507f188690eca39060c39469ae7bef43aae2a3'
    },
    @{
        Path   = 'InGame\third_party\imgui\backends\imgui_impl_dx11.cpp'
        Url    = 'https://raw.githubusercontent.com/ocornut/imgui/c1aaac65e896a45f81f47a4d68f9959e0f86c859/backends/imgui_impl_dx11.cpp'
        Sha256 = 'ee561259e75ca08c4fcb109db05ba2ef98979175ec1bb7688078a9dfabf3e977'
    },
    @{
        Path   = 'InGame\third_party\imgui\backends\imgui_impl_dx11.h'
        Url    = 'https://raw.githubusercontent.com/ocornut/imgui/c1aaac65e896a45f81f47a4d68f9959e0f86c859/backends/imgui_impl_dx11.h'
        Sha256 = '393acaa0fd95acb8c2431a17d6e952fc07ba29344074855aea5679ef3dd5d604'
    },
    @{
        Path   = 'InGame\third_party\imgui\backends\imgui_impl_dx12.cpp'
        Url    = 'https://raw.githubusercontent.com/ocornut/imgui/c1aaac65e896a45f81f47a4d68f9959e0f86c859/backends/imgui_impl_dx12.cpp'
        Sha256 = 'b3a780be746161f8a869729f354ad5a23ff1b0800ba77d386d0c52c8150fbc56'
    },
    @{
        Path   = 'InGame\third_party\imgui\backends\imgui_impl_dx12.h'
        Url    = 'https://raw.githubusercontent.com/ocornut/imgui/c1aaac65e896a45f81f47a4d68f9959e0f86c859/backends/imgui_impl_dx12.h'
        Sha256 = 'f9b26b6994e33c2fb80b343c076273564fde9ed3859685f0c2c52428c4634ebf'
    }
)

# Windows PowerShell 5.1 does not use TLS 1.2 by default, and GitHub requires it.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

foreach ($f in $files) {
    $target = Join-Path $here $f.Path

    if (Test-Path $target) {
        $have = (Get-FileHash $target -Algorithm SHA256).Hash.ToLower()
        if ($have -eq $f.Sha256) { Write-Output "ok        $($f.Path)"; continue }
        # A different file where this one belongs is never replaced silently: it could be a
        # newer version someone is testing, or it could be something that should not be there.
        throw "$($f.Path) is present but its SHA-256 is $have, not the pinned $($f.Sha256). Move it away and run this again."
    }

    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    $temp = "$target.download"
    Write-Output "fetching  $($f.Path)"
    Invoke-WebRequest -Uri $f.Url -OutFile $temp -UseBasicParsing

    $got = (Get-FileHash $temp -Algorithm SHA256).Hash.ToLower()
    if ($got -ne $f.Sha256) {
        Remove-Item $temp -Force
        throw "$($f.Path) downloaded with SHA-256 $got, not the pinned $($f.Sha256). Not used."
    }
    Move-Item $temp $target -Force
    Write-Output "verified  $($f.Path)"
}
