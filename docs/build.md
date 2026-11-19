# Build and test on Windows

Use Windows x64, PowerShell, and the .NET 10 SDK with Windows Desktop support. Run the commands below from the repository root.

## Build from source

The published portable ZIP is also a source of the matching native USB DLLs. Extract it, then set `$native` to the folder containing `rtlsdr.dll`, `libusb-1.0.dll`, `pthreadVC2.dll` and `hackrf.dll`. The project also recognizes a compatible SDR++ distribution at `C:\sdrpp_windows_x64`. The repository includes the x64 HD decoder in `hd-runtime` and both embedded map resources.

```powershell
$native = 'C:\Radios\Spectraxia'
dotnet build .\Spectraxia.csproj -c Release "-p:SdrDriverDir=$native"
$app = '.\bin\Release\net10.0-windows\Spectraxia.dll'
dotnet $app --self-test
dotnet $app
```

The self-test runs offline and returns exit code zero on success. The final command opens the receiver. A normal development build saves its profile under `%LOCALAPPDATA%\Spectraxia`. The portable release uses `portable.flag` to keep its profile beside the executable.

## Replay checks

| Input | Command |
| --- | --- |
| RTL ADS-B, unsigned 8-bit interleaved I/Q at 2 MS/s, centered on 1090 MHz | `dotnet $app --replay-adsb input.cu8` |
| HackRF ADS-B, signed 8-bit interleaved I/Q at 8 MS/s, centered on 1090 MHz | `dotnet $app --replay-hackrf-adsb input.cs8` |
| FM HD Radio, nrsc5 v3.2.0 decompressed sample, HD2 | `dotnet $app --check-hd-stream sample 1488375 0 1` |

For the airband scanner check, download SDRangel's airband I/Q WAV and unzip it. With Python, NumPy and SciPy installed, run the following commands. The script uses the first 21 seconds of captured RF and creates a second fixture with timed calls for hold, skip and lockout checks. [SDRangel recordings](https://www.sdrangel.org/iq-files/) · [nrsc5 sample](https://github.com/theori-io/nrsc5/blob/v3.2.0/support/sample.xz)

```powershell
python .\tools\make-airband-fixtures.py .\airband.wav .\checks\airband
dotnet $app --check-airband .\checks\airband
```

## Create a portable ZIP

Run the packaging script with the native library folder and an x64 redistributable `vcruntime140.dll`. Use a version with two to four numeric components. The script publishes a self-contained Windows application and writes the ZIP and its SHA-256 checksum under `releases`.

```powershell
.\tools\package-portable.ps1 -Release 2026.11.19 -NativeDir $native -VcRuntime (Join-Path $native 'vcruntime140.dll')
```

For hardware checks, connect the receiver and start the application. To check a HackRF by itself, close its aircraft window and run `dotnet $app --check-hackrf-live`. This opens the device at 1090 MHz for 15 seconds and reports USB bytes, valid messages and aircraft. Exit codes 0, 1 and 2 indicate decoded messages, no samples, and samples without a decoded frame, respectively.
