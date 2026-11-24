# Spectraxia receiver guide

Run `Spectraxia.exe` from the extracted folder. The RTL-SDR Blog V4 supplies the main spectrum, waterfall and listening audio. The radio waits if the device is absent. Choose your audio output and bring up the volume in the top bar.

## Tuning and listening

Click the frequency readout to enter MHz. Roll the wheel over a digit to change that place value, or middle-click it to zero it. Drag the spectrum to tune. Shift-drag pans the view. Use the mode buttons for AM, narrow FM, wide FM, sideband, CW, DSB or raw I/Q. The filter's **Default** button recalls the mode's normal width.

On an FM broadcast station, WFM starts on analog. Choose a detected HD service in the sidebar to hear it. A station memory can recall the selected HD service.

Turn on **Voice filters** for speech on any listening band. The starting passband is 250–3,200 Hz. Raise **High-pass** to cut rumble, or lower **Low-pass** to soften hiss. If a voice sounds muffled, widen the low-pass to 3,800 or 4,500 Hz. **Reduce steady noise** learns the background in several audio bands and turns down bands where little rises above it. It works best on a fairly constant background with pauses between transmissions. Use the main checkbox to compare the sound instantly. The sliders and noise setting persist across restarts.

These controls process listening audio, including scanner, HD Radio and saved audio. Raw I/Q retains the full capture. When you listen back to I/Q, the current voice filter settings apply.

## Favorites

Open **Favorites…** and click a station button to recall its frequency, listening mode, filter width and saved HD service. Favorites appear under their band automatically. To make a custom group, select a station, type a **Category**, and press **Save labels**. Clear the category to return to automatic grouping. The sort button switches between name and frequency.

Choose a group or **All favorites**, check the stations you want, and press **Scan checked**. The scan follows the displayed order, opening audio when a station clears **Squelch dBFS**. Set the threshold just above quiet-channel noise. **Hold** stays on the current favorite, **Skip** advances immediately, and **Reply delay** allows time for a response. **Stop** restores your listening dial. Uncheck continuously transmitting stations such as NOAA Weather Radio when scanning for intermittent traffic. The checkboxes and sort order persist.

## APRS packet radio

Click **APRS** to listen on 144.390 MHz NFM with the RTL. Position reports appear on the offline map. The packet list also shows messages, weather, status and the AX.25 path. An asterisk marks a digipeater that forwarded a packet. Select a row for its raw packet, or double-click a station with a position to center the map. Receiver volume and voice squelch do not affect packet decoding. Close the window to return to your previous station if you stayed on the APRS dial.

**Replay recording…** reads a 48 kHz PCM WAV or wideband Spectraxia I/Q recording. For a bare CU8 file, enter its capture rate and RF center. **Tune 144.390** returns to live reception. APRS and the separate HackRF aircraft receiver can run together.

## Airband and aircraft

Open **Airband scanner**, choose a range, and press **Start**. **Hold** stays on a channel through silence, **Skip** ignores it for 15 seconds, and **Lock out** excludes it across restarts. **Stop** returns to your previous dial and mode.

Open **ADS-B aircraft** and choose **HackRF** to track aircraft while listening or scanning on the RTL. Put the USB-connected PortaPack in HackRF mode. In the aircraft window, **Receiver settings** offers LNA and VGA adjustments and a count of verified messages over the last ten seconds. The **RTL-SDR** source uses the main receiver for aircraft until you return to listening.

Choose **Network feed** to use a BaseStation receiver over TCP. Set its hostname or IP and port under **Set network feed address…**. Enter a hostname such as `receiver.local` for the usual port 30003, or enter `receiver.local:31003` for a different port. Saving a new address reconnects an open network map. The portable app remembers the address in `Data/frequency.sbs-feed`. The map shows SBS reports per ten seconds and reconnects when the receiver comes back online. Set RF gain on the remote receiver. When using port 30003, enable raw AVR output on port 30002 on the same receiver (readsb option `--net-ro-port 30002`) for aircraft shapes and colors. A track starts gray until its identification message supplies a category. Selection, trails, distance and aircraft details work as usual.

The map colors each aircraft by its transmitted emitter category. Match the symbol to the colored **Category** cell in the aircraft list. Light aircraft are green, large transports blue, heavies orange, and rotorcraft purple. A gray diamond means the category is unknown.

Click a plane on the map or select its list row to show up to ten minutes of its received positions. Ctrl-click additional aircraft or rows to compare trails. Clear the selection to hide them. A trail starts fresh after position reports have gone stale. Double-click a row to center the map and open its details.

Choose **Range → Place receiver on map**, zoom to your antenna and click its location. Spectraxia saves this point locally. Range rings adjust their spacing with zoom. The selected plane’s label and the list’s **Range (NM)** column show ground distance from your receiver in nautical miles. You can hide the rings in the Range menu and still read distances.

## SAFE-T listening

Open **SAFE-T** and pick a tower. **Any talkgroup** follows Phase 1 group voice grants automatically. To hear one group, select its numeric ID as it appears in the talkgroup picker. **Control only** keeps the tower monitor silent. **Following TG** means the radio is checking an assigned voice channel. **Clear voice** means the decoder has confirmed the group and received an unencrypted voice indication. Speech uses the main volume, mute and audio output.

When the assignment fits inside the current capture, Spectraxia continues decoding the control channel alongside the call. For a more distant assignment it temporarily retunes the RTL and returns at the end of the call or after loss of signal. During that retune the control-message count can fall. Encrypted calls are skipped. Phase 2 TDMA audio is unsupported.

Use **Controls** for live tuner gain and digital AGC. The main **Bias-T** button powers a compatible antenna amplifier. After a gain change allow ten seconds for the message count to reflect it. Close **SAFE-T** to return to the previous dial or choose **Tune site** after tuning away.

## Recordings and portable data

**Record / rewind** can save audio, wideband I/Q, or both. Use **Save last 30 s** to keep what just happened. **Open recording** can read an earlier take from its existing location. The portable defaults are `Data` for settings and `Recordings` for new takes, next to the executable.

## Components and data

The USB libraries in this release come from the local Windows x64 SDR++ distribution. The V4-compatible RTL-SDR library, libusb, POSIX Threads for Windows, libhackrf, and the Microsoft Visual C++ runtime support direct USB reception. HD Radio uses nrsc5 and its dependencies. Their build information and license texts are in `hd-runtime`. Additional notices for the receiver libraries, .NET runtime and aircraft reference are in `docs/notices`.

The offline map includes airport and runway data from OurAirports, global geography from Natural Earth, and U.S. county, place and road data from the Census Bureau. Aircraft details use the Mictronics aircraft database. [OurAirports](https://github.com/davidmegginson/ourairports-data) · [Natural Earth](https://www.naturalearthdata.com/) · [U.S. Census Bureau](https://www.census.gov/geographies.html) · [Aircraft database](https://github.com/Mictronics/aircraft-database)
