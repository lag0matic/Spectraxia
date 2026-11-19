"""Prepare the first 21 seconds of SDRangel's airband I/Q WAV and a scripted AM capture."""
import argparse
from pathlib import Path
import struct
import wave

import numpy as np
from scipy.signal import resample_poly


def write_cu8(output, iq, scale):
    data = np.empty((len(iq), 2), dtype=np.uint8)
    data[:, 0] = np.clip(np.rint(127.5 + scale * iq.real), 0, 255)
    data[:, 1] = np.clip(np.rint(127.5 + scale * iq.imag), 0, 255)
    output.write(data.tobytes())


def prepare_real(wav, folder):
    with wav.open("rb") as raw:
        header = raw.read(512)
    aux = header.index(b"auxi") + 8
    center = struct.unpack_from("<I", header, aux + 32)[0]
    with wave.open(str(wav), "rb") as src:
        assert (center, src.getframerate(), src.getnchannels(), src.getsampwidth()) == (119500000, 3000000, 2, 2)
        # Include filter overlap so each output stream is continuous between seconds.
        for target in [119537500, 120462500]:
            with (folder / f"{target}.cu8").open("wb") as out:
                for second in range(21):
                    start = second * 3000000
                    pad = min(start, 100)
                    src.setpos(start - pad)
                    frame = np.frombuffer(src.readframes(pad + 3000100), "<i2").reshape(-1, 2)
                    if len(frame) < pad + 3000000:
                        raise ValueError("The test needs the first 21 seconds of the recording")
                    iq = frame[:, 0].astype("f4") + 1j * frame[:, 1]
                    phase = (start-pad + np.arange(len(iq))) * (2j*np.pi*(center-target)/3000000)
                    shifted = resample_poly(iq * np.exp(phase), 16, 25)
                    begin = pad * 16 // 25
                    write_cu8(out, shifted[begin:begin+1920000], 1/32)


def prepare_scripted(folder):
    rate = 960000
    rng = np.random.default_rng(15477)
    with (folder / "scripted.cu8").open("wb") as out:
        for block in range(120):
            t = (np.arange(rate//20) + block*(rate//20)) / rate
            iq = rng.normal(0, 3, len(t)) + 1j*rng.normal(0, 3, len(t))
            for hz, active, tone in [
                (118025000, ((t >= .25) & (t < .75)) | ((t >= 1.05) & (t < 1.45)) | ((t >= 4.45) & (t < 4.95)), 900),
                (118125000, ((t >= 2.2) & (t < 3)) | (t >= 4.3), 1400),
                (118175000, t >= 3.2, 0),
            ]:
                iq += active * (22 + 9*np.sin(2*np.pi*tone*t)) * np.exp(2j*np.pi*(hz-118087500)*t)
            write_cu8(out, iq, 1)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("wav", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    args.output.mkdir(exist_ok=True, parents=True)
    prepare_real(args.wav, args.output)
    prepare_scripted(args.output)
