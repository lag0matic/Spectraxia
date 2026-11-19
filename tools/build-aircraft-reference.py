"""Build Spectraxia's offline reference from Mictronics indexedDB.zip."""

import json
import re
import sys
from itertools import groupby
from zipfile import ZIP_DEFLATED, ZipFile


def build(source, destination):
    with ZipFile(source) as archive:
        aircraft = json.loads(archive.read("aircrafts.json"))
        types = json.loads(archive.read("types.json"))
        license_text = archive.read("LICENSE")
        version = json.loads(archive.read("dbversion.json"))["version"]
    with ZipFile(destination, "w", compression=ZIP_DEFLATED) as output:
        output.writestr("LICENSE", license_text)
        output.writestr("SOURCE", f"Mictronics Aircraft Database v{version}\n"
                        "https://github.com/Mictronics/aircraft-database\n"
                        "https://opendatacommons.org/licenses/by/1-0/\n")
        for prefix, addresses in groupby(sorted(aircraft), key=lambda address: address[:3]):
            lines = []
            for address in addresses:
                if not re.fullmatch("[0-9A-F]{6}", address):
                    continue
                registration, type_code = aircraft[address][:2]
                model = types.get(type_code, [""])[0]
                if registration or type_code:
                    fields = (address, registration, type_code, model)
                    lines.append("\t".join(" ".join(field.split()) for field in fields))
            if lines:
                output.writestr(prefix + ".tsv", "\n".join(lines))


if __name__ == "__main__":
    build(*sys.argv[1:])
