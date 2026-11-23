"""Summarize site identity, in-band grants, and clear voice evidence from a CU8 replay."""
import argparse
import json
import subprocess
from collections import Counter, defaultdict
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--decoder", default="p25", help="path to the stevejost/trunker p25 executable")
    parser.add_argument("--rate", type=int, required=True)
    parser.add_argument("--center-hz", type=int, required=True)
    parser.add_argument("--control-hz", type=int, required=True)
    parser.add_argument("--modulation", choices=("c4fm", "cqpsk"), default="c4fm")
    args = parser.parse_args()
    if args.rate <= 12500 or abs(args.control_hz - args.center_hz) + 6250 > args.rate / 2:
        parser.error("the full control channel must lie inside the capture")
    if not args.capture.is_file() or not args.capture.stat().st_size or args.capture.stat().st_size % 2:
        parser.error("expected an interleaved unsigned 8-bit I/Q file")
    command = [args.decoder, "trunk", "--input", str(args.capture), "--format", "u8",
               "--sample-rate", str(args.rate), "--center-freq", str(args.center_hz),
               "--frequency", str(args.control_hz), "--modulation", args.modulation, "--json-output"]
    sites = Counter()
    channels = defaultdict(Counter)
    algorithms = defaultdict(set)
    with subprocess.Popen(command, stdout=subprocess.PIPE, text=True, encoding="utf-8") as child:
        for line in child.stdout:
            event = json.loads(line)
            if event.get("name") == "RFSS_STS_BCST":
                sites[(event["system_id"], event["rfss_id"], event["site_id"], event["nac"])] += 1
            if event.get("manufacturer_id", 0) != 0:
                continue
            for suffix in ("", "_a", "_b"):
                frequency = event.get("frequency" + suffix)
                group = event.get("talkgroup" + suffix)
                if not frequency or not group or abs(frequency*1e6 - args.center_hz) + 6250 > args.rate/2:
                    continue
                key = (group, frequency)
                if event.get("name", "").startswith("GRP_V_CH_GRANT"):
                    if suffix == "_b" and (group, frequency) == (event.get("talkgroup_a"), event.get("frequency_a")):
                        continue
                    channels[key]["grant reports"] += 1
                if event.get("type") == "voice_frame":
                    channels[key]["voice frames"] += 1
                if event.get("type") in ("voice_header", "crypto_control"):
                    algorithms[key].add(event["algorithm"])
        if child.wait():
            raise SystemExit("P25 replay failed")
    for (system, rfss, site, nac), count in sites.most_common():
        print(f"System {system}, RFSS {rfss}, site {site}, NAC {nac} ({count} reports)")
    for (group, frequency), counts in sorted(channels.items()):
        flags = ", ".join(sorted(algorithms[(group, frequency)])) or "no algorithm identified"
        print(f"TG {group:5d}  {frequency:.5f} MHz   {counts['grant reports']} grant reports   "
              f"{counts['voice frames']} voice frames   {flags}")
    return 0 if sites else 1


if __name__ == "__main__":
    raise SystemExit(main())
