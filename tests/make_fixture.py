#!/usr/bin/env python3
# make_fixture.py - writes a DEFLATE-compressed .xlsx for the reader test.
#
# Why a generator and not a committed binary: the value of this fixture is that
# its DEFLATE streams and CRC-32 checksums come from zlib, an implementation
# this project did not write. A committed blob would keep that property but
# would be 13 KB of hex in a source tree that is otherwise entirely text, and
# nobody reading the repo could see what it held. This script is a couple of KB
# and its output can be opened with any zip tool.
#
# The archive is deterministic - fixed 1980-01-01 timestamps, fixed compression
# level - so the same bytes appear on every machine and a failure is
# reproducible.
#
# Cell (column c, xlsx row r) holds 10*r + c. khz_xlsx_test.c recomputes that
# same rule instead of hardcoding totals, so the fixture and the expectations
# cannot drift apart.
#
# The worksheet deliberately does not use xl/worksheets/sheet1.xml. The
# workbook points to it through r:id and xl/_rels/workbook.xml.rels so the
# end-to-end reader test proves relationship-based part resolution rather than
# succeeding by filename convention.

import io
import sys
import zipfile

ROWS = 25
COLS = 8
WORKSHEET_PART = "xl/worksheets/data-sheet.xml"
WORKSHEET_TARGET = "worksheets/data-sheet.xml"
WORKSHEET_REL_ID = "rData7"

NS_MAIN = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
NS_REL = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
NS_PKG_REL = "http://schemas.openxmlformats.org/package/2006/relationships"
NS_CT = "http://schemas.openxmlformats.org/package/2006/content-types"

CONTENT_TYPES = (
    '<?xml version="1.0" encoding="UTF-8"?>'
    '<Types xmlns="%s">'
    '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
    '<Default Extension="xml" ContentType="application/xml"/>'
    '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>'
    '<Override PartName="/%s" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>'
    "</Types>"
) % (NS_CT, WORKSHEET_PART)

ROOT_RELS = (
    '<?xml version="1.0" encoding="UTF-8"?>'
    '<Relationships xmlns="%s">'
    '<Relationship Id="rId1" Type="%s/officeDocument" Target="xl/workbook.xml"/>'
    "</Relationships>"
) % (NS_PKG_REL, NS_REL)

WORKBOOK = (
    '<?xml version="1.0" encoding="UTF-8"?>'
    '<workbook xmlns="%s"><sheets>'
    '<sheet name="Sheet1" sheetId="1" r:id="%s" xmlns:r="%s"/>'
    "</sheets></workbook>"
) % (NS_MAIN, WORKSHEET_REL_ID, NS_REL)

WORKBOOK_RELS = (
    '<?xml version="1.0" encoding="UTF-8"?>'
    '<Relationships xmlns="%s">'
    '<Relationship Id="%s" Type="%s/worksheet" Target="%s"/>'
    "</Relationships>"
) % (NS_PKG_REL, WORKSHEET_REL_ID, NS_REL, WORKSHEET_TARGET)


def column_name(index):
    # Only A..H is needed at COLS = 8, but done properly so widening the
    # fixture past column Z cannot quietly produce nonsense references.
    name = ""
    index += 1
    while index > 0:
        index, rem = divmod(index - 1, 26)
        name = chr(ord("A") + rem) + name
    return name


def worksheet():
    rows = []
    for r in range(1, ROWS + 1):
        cells = "".join(
            '<c r="%s%d"><v>%d</v></c>' % (column_name(c), r, r * 10 + c)
            for c in range(COLS)
        )
        rows.append('<row r="%d">%s</row>' % (r, cells))
    return (
        '<?xml version="1.0" encoding="UTF-8"?>'
        '<worksheet xmlns="%s"><sheetData>%s</sheetData></worksheet>'
    ) % (NS_MAIN, "".join(rows))


def build():
    parts = [
        ("[Content_Types].xml", CONTENT_TYPES),
        ("_rels/.rels", ROOT_RELS),
        ("xl/workbook.xml", WORKBOOK),
        ("xl/_rels/workbook.xml.rels", WORKBOOK_RELS),
        (WORKSHEET_PART, worksheet()),
    ]
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for name, data in parts:
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o600 << 16
            z.writestr(info, data)
    return buf.getvalue()


def main():
    if len(sys.argv) != 2:
        sys.stderr.write("usage: make_fixture.py <out.xlsx>\n")
        return 2

    blob = build()

    # Refuse to write a fixture that would not test what it claims to. If any
    # part came out STORED, the reader would take its stored path, the DEFLATE
    # decoder would never run, and the test would pass while proving nothing.
    # Tiny XML parts can deflate larger than the original, and some writers
    # fall back to STORED in that case; check rather than assume.
    check = zipfile.ZipFile(io.BytesIO(blob))
    if check.testzip() is not None:
        sys.stderr.write("fixture failed its own zip check\n")
        return 1
    for info in check.infolist():
        if info.compress_type != zipfile.ZIP_DEFLATED:
            sys.stderr.write(
                "part %s is method %d, not DEFLATE; this fixture would not "
                "exercise the decoder\n" % (info.filename, info.compress_type)
            )
            return 1

    with open(sys.argv[1], "wb") as out:
        out.write(blob)

    total = sum(r * 10 + c for r in range(1, ROWS + 1) for c in range(COLS))
    sys.stderr.write(
        "fixture: %d bytes, %d parts, %d cells, sum %d\n"
        % (len(blob), len(check.infolist()), ROWS * COLS, total)
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
