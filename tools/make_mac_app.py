#!/usr/bin/env python3
"""Packt ein dotnet-publish-Verzeichnis (osx-*) als NetworkFolderManager.app in eine ZIP-Datei.

Läuft auch unter Windows: Die Unix-Rechte (ausführbar) werden direkt in die ZIP-Einträge geschrieben,
damit die App auf dem Mac startet. (Auf einem Mac/CI bevorzugt: ditto + codesign, siehe .github/workflows/build-mac.yml.)

Aufruf: make_mac_app.py <publish-dir> <out.zip> <version> <plist-template> <icon.png>
"""
import os
import struct
import sys
import zipfile

publish, out_zip, version, plist_template, icon_png = sys.argv[1:6]
APP = "NetworkFolderManager.app"
EXEC = 0o100755 << 16
FILE = 0o100644 << 16
DIR = (0o040755 << 16) | 0x10


def icns_from_png(png: bytes) -> bytes:
    # 'ic08' = 256x256 PNG
    entry = b"ic08" + struct.pack(">I", len(png) + 8) + png
    return b"icns" + struct.pack(">I", len(entry) + 8) + entry


def add(zf, name, data, attr):
    info = zipfile.ZipInfo(name)
    info.create_system = 3  # Unix
    info.external_attr = attr
    info.compress_type = zipfile.ZIP_DEFLATED
    zf.writestr(info, data)


with zipfile.ZipFile(out_zip, "w") as zf:
    plist = open(plist_template, encoding="utf-8").read().replace("@VERSION@", version)
    add(zf, f"{APP}/Contents/Info.plist", plist.encode("utf-8"), FILE)
    add(zf, f"{APP}/Contents/Resources/appicon.icns", icns_from_png(open(icon_png, "rb").read()), FILE)
    for root, _, files in os.walk(publish):
        for f in files:
            path = os.path.join(root, f)
            rel = os.path.relpath(path, publish).replace(os.sep, "/")
            executable = rel == "NetworkFolderManager" or f.endswith(".dylib")
            add(zf, f"{APP}/Contents/MacOS/{rel}", open(path, "rb").read(), EXEC if executable else FILE)
print(out_zip)
