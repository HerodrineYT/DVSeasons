import shutil
import sys

from PIL import Image
import UnityPy


def main() -> int:
    if len(sys.argv) != 5:
        print(
            "usage: patch_bundle_texture.py <input-bundle> <asset-suffix> <replacement-png> <output-bundle>",
            file=sys.stderr,
        )
        return 64

    input_bundle, asset_suffix, replacement_path, output_bundle = sys.argv[1:]
    environment = UnityPy.load(input_bundle)
    normalized_suffix = asset_suffix.replace("\\", "/").lower()
    matches = [
        (path, obj)
        for path, obj in environment.container.items()
        if path.replace("\\", "/").lower().endswith(normalized_suffix)
    ]
    if len(matches) != 1:
        print("expected exactly one asset match, found {}".format(len(matches)), file=sys.stderr)
        for path, _ in matches:
            print(path, file=sys.stderr)
        return 1

    asset_path, obj = matches[0]
    texture = obj.read()
    original_size = texture.image.size
    with Image.open(replacement_path) as replacement:
        replacement = replacement.convert("RGBA").resize(original_size, Image.Resampling.LANCZOS)
        texture.image = replacement
        texture.save()

    with open(output_bundle, "wb") as output:
        output.write(environment.file.save(packer="original"))
    print("patched", asset_path, "at", original_size, "=>", output_bundle)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
