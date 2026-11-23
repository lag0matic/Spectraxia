# TYT TH-9800D microphone interface bench notes

This is a proposed hardware experiment. The released application receives APRS through an SDR. Keypad emulation, APRS transmission and satellite Doppler control are future work.

## Six-contact mic port

The TYT drawings identify pins 1 through 6 respectively as SW2, SW1, +5 V (5M), GND, MIC and PTT. These are schematic pin numbers. Confirm orientation and continuity on the specific radio and cable before applying power. [Radio jack J24, page 3](https://www.radioscanner.ru/files/download/file19457/th-9800schematics.pdf#page=3) · [Hand mic J6, page 7](https://www.radioscanner.ru/files/download/file19457/th-9800schematics.pdf#page=7)

An inline six-contact adapter keeps the stock mic connected. For a soldered tap, use a pair of breakouts, two six-pin jacks and a straight-through 6P6C cord. Verify all six conductors end-to-end with the assembly unplugged. [Tempo PA1903](https://www.tempocom.com/products/adapter-rj11-modular-6-way-blister/) · [SparkFun breakout pair](https://www.sparkfun.com/sparkfun-rj11-breakout.html) · [Six-pin jack](https://www.sparkfun.com/rj11-6-pin-connector.html) · [Straight-through cord](https://www.sfcable.com/rj12-6p6c-straight-modular-telephone-cable.html)

With a verified breakout, connect the mic before powering up. Use DC volts, reference pin 4, and measure pins 1 and 2 separately at idle and while holding UP and DOWN with PTT released. Check for repeatable voltage pairs. A scope can capture transient levels if the meter readings change too quickly. The first control test is one repeatable step confirmed on the radio display. Button emulation alone gives no display readback. A separate [remote-head CAT project](https://github.com/SleepyNinja0o/TH9800_CAT) reads the faceplate link.

## Audio and satellite experiments

For APRS transmission, supply modem audio at mic level with DC blocking, and reproduce the PTT switch separately. An SDR can continue to receive. To capture the radio's own speaker audio, use an isolated speaker-to-line interface rated for bridged outputs, then a USB audio input. Both speaker conductors are driven. Grounding either to the PC can fault the amplifier. [TYT manual, External Speaker](https://s3.image.ro.s3.amazonaws.com/download/TH-9800_manual.pdf)

A receive-only Doppler trial can use an RTL-SDR, current orbital elements and station location. A 70 cm FM downlink can start around 10 kHz high and end around 10 kHz low. For a 70 cm uplink, an example five-memory sequence goes from -10 to +10 kHz in 5 kHz steps. Verify the satellite's current frequencies, operating mode and tone. Start on a known memory and step between transmissions. [AMSAT FM guide](https://www.amsat.org/wordpress/wp-content/uploads/2014/01/Work_FM_Sats-20131010.pdf) · [AMSAT memory example](https://www.amsat.org/wordpress/wp-content/uploads/2018/01/Getting-Ready-for-Fox-1D.pdf)
