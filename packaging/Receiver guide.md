# Spectraxia receiver guide

Run `Spectraxia.exe` from the extracted folder. The RTL-SDR Blog V4 supplies the main spectrum, waterfall and listening audio. The radio waits if the device is absent. Choose your audio output and bring up the volume in the top bar.

## Tuning and listening

Click the frequency readout to enter MHz. Roll the wheel over a digit to change that place value, or middle-click it to zero it. Drag the spectrum to tune. Shift-drag pans the view. Use the mode buttons for AM, narrow FM, wide FM, sideband, CW, DSB or raw I/Q. The filter's **Default** button recalls the mode's normal width.

On an FM broadcast station, WFM starts on analog. Choose a detected HD service in the sidebar to hear it. A station memory can recall the selected HD service.

## Airband and aircraft

Open **Airband scanner**, choose a range, and press **Start**. **Hold** stays on a channel through silence, **Skip** ignores it for 15 seconds, and **Lock out** excludes it across restarts. **Stop** returns to your previous dial and mode.

Open **ADS-B aircraft** and choose **HackRF** to track aircraft while listening or scanning on the RTL. Put the USB-connected PortaPack in HackRF mode. In the aircraft window, **Receiver settings** offers LNA and VGA adjustments and a count of verified messages over the last ten seconds. The **RTL-SDR** source uses the main receiver for aircraft until you return to listening.

## Recordings and portable data

**Record / rewind** can save audio, wideband I/Q, or both. Use **Save last 30 s** to keep what just happened. **Open recording** can read an earlier take from its existing location. The portable defaults are `Data` for settings and `Recordings` for new takes, next to the executable.

## Components and data

The USB libraries in this release come from the local Windows x64 SDR++ distribution. The V4-compatible RTL-SDR library, libusb, POSIX Threads for Windows, libhackrf, and the Microsoft Visual C++ runtime support direct USB reception. HD Radio uses nrsc5 and its dependencies. Their build information and license texts are in `hd-runtime`. Additional notices for the receiver libraries, .NET runtime and aircraft reference are in `docs/notices`.

The offline map includes airport and runway data from OurAirports, global geography from Natural Earth, and U.S. county, place and road data from the Census Bureau. Aircraft details use the Mictronics aircraft database. [OurAirports](https://github.com/davidmegginson/ourairports-data) · [Natural Earth](https://www.naturalearthdata.com/) · [U.S. Census Bureau](https://www.census.gov/geographies.html) · [Aircraft database](https://github.com/Mictronics/aircraft-database)
