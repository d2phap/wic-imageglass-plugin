# WIC Codec for ImageGlass

Brings the **Windows Imaging Component** into ImageGlass as a native codec plugin: every
image format your copy of Windows has a codec registered for becomes readable and writable,
including **JPEG XR (`.jxr`, `.hdp`, `.wdp`)**, which none of ImageGlass's built-in codecs
handle.

Built with [Vortice.Win32.Graphics.Imaging](https://github.com/amerkoleci/Vortice.Win32) and
compiled with Native AOT for Windows x64 and ARM64.


## Supported Formats

The format list is **not hardcoded**. At startup the plugin enumerates the WIC component
registry, so whatever codecs the machine has – shipped with Windows, installed from the
Microsoft Store, or third-party – show up automatically.

On a stock Windows 11 machine with the Store codec extensions installed, that is 66 readable
and 20 writable extensions:

| | |
|---|---|
| **Read** | `.bmp` `.dib` `.rle` `.gif` `.ico` `.icon` `.cur` `.jpeg` `.jpe` `.jpg` `.jfif` `.exif` `.png` `.tiff` `.tif` `.dng` `.wdp` `.jxr` `.hdp` `.dds` `.heic` `.heif` `.hif` `.avci` `.heics` `.heifs` `.avcs` `.avif` `.avifs` `.webp` `.jxl` + camera raw (`.3fr` `.ari` `.arw` `.bay` `.cap` `.cr2` `.cr3` `.crw` `.dcs` `.dcr` `.drf` `.eip` `.erf` `.fff` `.iiq` `.k25` `.kdc` `.mef` `.mos` `.mrw` `.nef` `.nrw` `.orf` `.ori` `.pef` `.ptx` `.pxn` `.raf` `.raw` `.rw2` `.rwl` `.sr2` `.srf` `.srw` `.x3f`) |
| **Write** | `.bmp` `.dib` `.rle` `.gif` `.jpeg` `.jpe` `.jpg` `.jfif` `.exif` `.png` `.tiff` `.tif` `.wdp` `.jxr` `.hdp` `.dds` `.heic` `.heif` `.hif` `.jxl` |


## Install

1. Download `wic-codec_<version>_win-x64.igplugin.zip` (or `win-arm64`) from
   [Releases](https://github.com/d2phap/wic-imageglass-plugin/releases).
2. ImageGlass → **Settings → Plugins → Add**, and pick the `.igplugin.zip`.
3. Click **Trust and enable**.


## Build

Requires the .NET 11 SDK and the Visual Studio C++ build tools (Native AOT links natively).

```powershell
# compile only – quick syntax check
dotnet build source/WicCodec.csproj -c Debug -p:Platform=x64

# publish + pack both architectures into dist/wic-codec_<version>_<arch>.igplugin.zip
./pack.ps1

# publish x64 and drop it straight into %LOCALAPPDATA%\ImageGlass\_plugins
./pack.ps1 -Rid win-x64 -Deploy
```

## License

MIT. See [LICENSE](LICENSE).
