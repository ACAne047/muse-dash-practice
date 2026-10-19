"""Disassemble exact RVA/Length targets from the locally installed game."""

import argparse
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import struct

from capstone import Cs, CS_ARCH_X86, CS_MODE_64


def number(value):
    return int(value, 0) if isinstance(value, str) else int(value)


def read_json(path):
    opener = gzip.open if path.suffix == ".gz" else open
    with opener(path, "rt", encoding="utf-8-sig") as stream:
        return json.load(stream)


def pe_sections(data):
    if data[:2] != b"MZ":
        raise ValueError("Not a PE file")
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        raise ValueError("Invalid PE signature")
    machine, count = struct.unpack_from("<HH", data, pe + 4)
    optional_size = struct.unpack_from("<H", data, pe + 20)[0]
    if machine != 0x8664 or struct.unpack_from("<H", data, pe + 24)[0] != 0x20B:
        raise ValueError("Expected x64 PE32+")
    image_base = struct.unpack_from("<Q", data, pe + 48)[0]
    sections = []
    for index in range(count):
        header = pe + 24 + optional_size + index * 40
        _, rva, raw_size, raw_offset = struct.unpack_from("<IIII", data, header + 8)
        sections.append((rva, raw_size, raw_offset))
    return image_base, sections


def file_offset(sections, rva, length, data_length):
    if length <= 0:
        raise ValueError("Method length must be positive")
    for start, raw_size, raw in sections:
        if start <= rva and rva + length <= start + raw_size:
            offset = raw + rva - start
            if offset + length > data_length:
                break
            return offset
    raise ValueError(f"RVA/Length outside section file data: {rva:#x}/{length:#x}")


def main():
    root = Path(__file__).resolve().parents[2]
    metadata = Path(__file__).resolve().parent / "metadata"
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-root", default=os.environ.get("MUSEDASH_GAME_ROOT"))
    parser.add_argument("--targets", type=Path, default=metadata / "character-native-targets.json")
    parser.add_argument("--symbols", type=Path, default=metadata / "native-symbols.json.gz")
    parser.add_argument("--output", type=Path, default=root / "artifacts" / "analysis")
    parser.add_argument("--match", help="Filter target names/labels by substring")
    args = parser.parse_args()
    if not args.game_root:
        parser.error("Specify --game-root or MUSEDASH_GAME_ROOT")

    data = (Path(args.game_root) / "GameAssembly.dll").read_bytes()
    expected = read_json(root / "maintenance-state.json")["gameassembly_sha256"]
    if hashlib.sha256(data).hexdigest().upper() != expected.upper():
        parser.error("GameAssembly fingerprint differs; regenerate targets for the new game first")
    image_base, sections = pe_sections(data)
    symbols = {number(item["Rva"]): item["Name"] for item in read_json(args.symbols)}
    targets = read_json(args.targets)
    targets = [item for item in targets if not args.match or
               args.match in item.get("Name", "") or args.match in item["Label"]]
    if not targets:
        parser.error("No matching exact-length targets")

    disassembler = Cs(CS_ARCH_X86, CS_MODE_64)
    args.output.mkdir(parents=True, exist_ok=True)
    for target in targets:
        rva, length = number(target["Rva"]), number(target["Length"])
        offset = file_offset(sections, rva, length, len(data))
        label = target["Label"]
        lines = [f"{label} RVA={rva:#x} Length={length:#x}"]
        for instruction in disassembler.disasm(data[offset:offset + length], image_base + rva):
            comment = ""
            if instruction.mnemonic in ("call", "jmp") and instruction.op_str.startswith("0x"):
                comment = symbols.get(int(instruction.op_str, 16) - image_base, "")
            lines.append(f"{instruction.address - image_base:08x}  "
                         f"{instruction.mnemonic:8} {instruction.op_str:38} {comment}")
        filename = re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", label).rstrip(". ")[:160]
        # Prefixing the RVA also avoids Windows device-name and duplicate-name collisions.
        destination = args.output / f"{rva:08x}-{filename}.asm.txt"
        destination.write_text("\n".join(lines) + "\n", encoding="utf-8")
        print(f"{destination.name}: {length:#x} bytes")


if __name__ == "__main__":
    main()
