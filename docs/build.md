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

### APRS and P25

For APRS, run `dotnet $app --replay-aprs input.wav` on 48 kHz signed 16-bit PCM. Spectraxia CU8 recordings with their timeline also work. For a bare capture, append its rate in samples/s and center in Hz, for example `dotnet $app --replay-aprs sample.cu8 960000 144330000`. The replay tunes 144.390 MHz in software. Compare decoded packets against Dire Wolf's `atest -F 0` on the corresponding audio. [Dire Wolf](https://github.com/wb2osz/direwolf)

The optional P25 feasibility check uses Python 3 and a separate `p25` executable from stevejost/trunker. It reads a capture offline and reports site identity, in-band group grants, voice frames, and received algorithm indications. It prints observations per frequency and talkgroup. A grant or voice-frame count alone cannot establish clear audio. The Richmond example uses the control channel observed in the November 22 capture. Supply the active control frequency for your own recording. [Reference decoder revision](https://github.com/stevejost/trunker/tree/55f62ab32c414fd40f500443979984de6cbc2754)

The live SAFE-T monitor loads the bundled Windows x64 decoder in `p25-runtime`. To rebuild it, check out the reference revision linked above, install the Windows x64 Rust MSVC toolchain and run `tools\p25-control\build.ps1 -Trunker C:\path\to\trunker`. The adapter uses the upstream C4FM pipeline with strict NID checks and accepts CRC-verified control blocks. The same app path can replay a tower capture with `--replay-safet regional.cu8 2400000 853500000 853725000`. Exit code 0 means at least one direct site report, 1 means none decoded and 2 means invalid input.

```text
python tools/check-p25-capture.py richmond.cu8 --rate 2400000 --center-hz 852200000 --control-hz 853112500
```

For the Phase 1 follower regression, run `--check-safet-voice richmond-20261122-181018.cu8 p25-xts5000.cf32`. The first fixture checks the actual 852.250 MHz grant and encrypted-call skip. The second uses the recorded Motorola transmission at 96 kS/s with a scripted grant to exercise frequency translation, clear and wrong-group gating, PCM and return. Appending a talkgroup number to `--replay-safet` also reports any confirmed clear audio in the original capture window. Use 0 for any talkgroup.

## Create a portable ZIP

Run the packaging script with the native library folder and an x64 redistributable `vcruntime140.dll`. Use a version with two to four numeric components. The script publishes a self-contained Windows application and writes the ZIP and its SHA-256 checksum under `releases`.

```powershell
.\tools\package-portable.ps1 -Release 2026.11.19 -NativeDir $native -VcRuntime (Join-Path $native 'vcruntime140.dll')
```

For hardware checks, connect the receiver and start the application. To check a HackRF by itself, close its aircraft window and run `dotnet $app --check-hackrf-live`. This opens the device at 1090 MHz for 15 seconds and reports USB bytes, valid messages and aircraft. Exit codes 0, 1 and 2 indicate decoded messages, no samples, and samples without a decoded frame, respectively.
