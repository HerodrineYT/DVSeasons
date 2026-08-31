import hashlib
import os
import sys

import UnityPy


def sha256(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def embedded_hashes(bundle) -> dict[str, str]:
    result = {}
    for name, item in bundle.files.items():
        reader = getattr(item, "reader", item)
        result[name] = hashlib.sha256(bytes(reader.bytes)).hexdigest().upper()
    return result


def main() -> int:
    if len(sys.argv) != 4:
        print("usage: repack_unity_bundle.py <input> <output> <packer>", file=sys.stderr)
        return 64

    input_path, output_path, packer = sys.argv[1:]
    environment = UnityPy.load(input_path)
    before_assets = sorted(environment.container.keys())
    before_objects = [(obj.path_id, obj.type.name) for obj in environment.objects]
    before_embedded = embedded_hashes(environment.file)
    packed = environment.file.save(packer=packer)
    with open(output_path, "wb") as output:
        output.write(packed)

    verification = UnityPy.load(output_path)
    after_assets = sorted(verification.container.keys())
    after_objects = [(obj.path_id, obj.type.name) for obj in verification.objects]
    after_embedded = embedded_hashes(verification.file)
    if (before_assets != after_assets or before_objects != after_objects or
            before_embedded != after_embedded):
        print("verification failed: decompressed bundle contents changed", file=sys.stderr)
        return 2

    print("packer", packer)
    print("assets", len(after_assets))
    print("objects", len(after_objects))
    print("embedded files byte-identical", len(after_embedded))
    print("bytes", os.path.getsize(input_path), "=>", os.path.getsize(output_path))
    print("sha256", sha256(output_path))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
