# Spectraxia receiver guide

Run `Spectraxia.exe` from the extracted folder. The RTL-SDR Blog V4 supplies the main spectrum, waterfall and listening audio. The radio waits if the device is absent. Choose your audio output and bring up the volume in the top bar.

## Tuning and listening

Click the frequency readout to enter MHz. Roll the wheel over a digit to change that place value, or middle-click it to zero it. Drag the spectrum to tune. Shift-drag pans the view. Use the mode buttons for AM, narrow FM, wide FM, sideband, CW, DSB or raw I/Q. The filter's **Default** button recalls the mode's normal width.

On an FM broadcast station, WFM starts on analog. Choose a detected HD service in the sidebar to hear it. A station memory can recall the selected HD service.

Turn on **Voice filters** for speech on any listening band. The starting passband is 250–3,200 Hz. Raise **High-pass** to cut rumble, or lower **Low-pass** to soften hiss. If a voice sounds muffled, widen the low-pass to 3,800 or 4,500 Hz. **Reduce steady noise** learns the background in several audio bands and turns down bands where little rises above it. It works best on a fairly constant background with pauses between transmissions. Use the main checkbox to compare the sound instantly. The sliders and noise setting persist across restarts.

These controls process listening audio, including scanner, HD Radio and saved audio. Raw I/Q retains the full capture. When you listen back to I/Q, the current voice filter settings apply.

## Airband and aircraft

Open **Airband scanner**, choose a range, and press **Start**. **Hold** stays on a channel through silence, **Skip** ignores it for 15 seconds, and **Lock out** excludes it across restarts. **Stop** returns to your previous dial and mode.

Open **ADS-B aircraft** and choose **HackRF** to track aircraft while listening or scanning on the RTL. Put the USB-connected PortaPack in HackRF mode. In the aircraft window, **Receiver settings** offers LNA and VGA adjustments and a count of verified messages over the last ten seconds. The **RTL-SDR** source uses the main receiver for aircraft until you return to listening.

The map colors each aircraft by its transmitted emitter category. Match the symbol to the colored **Category** cell in the aircraft list. Light aircraft are green, large transports blue, heavies orange, and rotorcraft purple. A gray diamond means the category is unknown.

Select an aircraft in the list to show up to ten minutes of its received positions in the aircraft's color. Ctrl-click additional rows to compare trails. Clear the selection to hide them. A trail starts fresh after position reports have gone stale. Double-click a row to center the map and open its details.

## Recordings and portable data

**Record / rewind** can save audio, wideband I/Q, or both. Use **Save last 30 s** to keep what just happened. **Open recording** can read an earlier take from its existing location. The portable defaults are `Data` for settings and `Recordings` for new takes, next to the executable.

## Components and data

The USB libraries in this release come from the local Windows x64 SDR++ distribution. The V4-compatible RTL-SDR library, libusb, POSIX Threads for Windows, libhackrf, and the Microsoft Visual C++ runtime support direct USB reception. HD Radio uses nrsc5 and its dependencies. Their build information and license texts are in `hd-runtime`. Additional notices for the receiver libraries, .NET runtime and aircraft reference are in `docs/notices`.

The offline map includes airport and runway data from OurAirports, global geography from Natural Earth, and U.S. county, place and road data from the Census Bureau. Aircraft details use the Mictronics aircraft database. [OurAirports](https://github.com/davidmegginson/ourairports-data) · [Natural Earth](https://www.naturalearthdata.com/) · [U.S. Census Bureau](https://www.census.gov/geographies.html) · [Aircraft database](https://github.com/Mictronics/aircraft-database)
