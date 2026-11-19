<img src="assets/Spectraxia.png" alt="Spectraxia icon" width="96" />

# Spectraxia

Spectraxia is a Windows x64 desktop receiver for the RTL-SDR Blog V4. Tune by dragging the spectrum, rewind a signal you just missed, or let the airband scanner catch the next transmission. With a second receiver, you can listen to aircraft on the RTL while a HackRF feeds the live aircraft map.

**[Download the portable Windows release](https://github.com/lag0matic/Spectraxia/releases/latest)** · [Receiver guide](packaging/Receiver%20guide.md) · [Build and test](docs/build.md)

## Get on the air

1. Extract the entire portable ZIP to a writable folder and run `Spectraxia.exe`. The .NET desktop runtime is bundled.
2. Connect an RTL-SDR Blog V4 with a working Windows USB driver. Choose your audio output and raise the volume. A fresh portable copy starts at zero volume.
3. For broadcast FM, choose **WFM**, click the frequency readout, enter a station in MHz, and press Enter. When the receiver detects HD services, choose one from the FM audio selector. Analog keeps playing until you select a service.

## Listen and track aircraft at the same time

Connect a HackRF One with a 1090 MHz antenna feed. If you use a PortaPack, select **HackRF mode** on its screen. Choose **ADS-B aircraft → HackRF · keep listening on RTL**, then open **Airband scanner** in the main window and press **Start**. The scanner follows AM voice transmissions while the aircraft map runs its own USB session. Each receiver has independent gain settings. With only an RTL, choose the RTL-SDR option in the aircraft menu to dedicate it to the map.

| Feature | What you can do |
| --- | --- |
| Tuning | Scroll individual VFO digits, drag the spectrum, zoom around a signal, and recall named stations. The band strip includes CB channel numbers. |
| Listening | Use AM, NFM, WFM, USB, LSB, CW, DSB, or raw I/Q. Broadcast FM supports stereo, RDS/RBDS and detected HD Radio programs. |
| Interference controls | Adjust tuner gain, RTL digital AGC, squelch, filter width, DC removal and notches. |
| Recording and rewind | Save decoded audio, wideband I/Q, or both. Rewind about 30 seconds and tune a different signal inside the recorded capture. |
| Airband scanning | Sweep a frequency range, hold for replies, skip a channel temporarily or lock it out across sessions. |
| Aircraft map | Decode 1090 MHz DF17 extended squitter, plot reported positions on an offline map, and open an aircraft's details from the list. |

In the portable build, settings and memories live in `Data` beside the executable. New recordings default to `Recordings`. Close the app before moving the folder or carrying those directories into a new release.

## AI-assisted development

Spectraxia was created with AI assistance. OpenAI Codex helped write the C# receiver, decoder integrations, tests, packaging scripts and documentation. The maintainer defined how the radio should behave and tested it on real hardware, including local HD Radio reception and simultaneous RTL airband scanning and HackRF aircraft tracking.

## License and third-party components

Spectraxia's original code and documentation are available under the [MIT License](LICENSE). The bundled libraries and datasets retain their respective licenses.

Spectraxia uses the nrsc5 decoder for FM HD Radio, native SDR libraries for USB access, and published geographic and aircraft reference data for the map. Component provenance and notices are documented in the [receiver guide](packaging/Receiver%20guide.md), [nrsc5 build record](hd-runtime/BUILD.txt) and [packaging notices](packaging/notices).
