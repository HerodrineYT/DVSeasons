import os
import sys

from PIL import Image
import UnityPy
from UnityPy.enums import TextureFormat
from UnityPy.export import Texture2DConverter


def container_object(environment, asset_path: str):
    normalized = asset_path.replace("\\", "/").lower()
    matches = [
        obj
        for path, obj in environment.container.items()
        if path.replace("\\", "/").lower() == normalized
    ]
    if len(matches) != 1:
        raise RuntimeError("expected one container object for {}, found {}".format(asset_path, len(matches)))
    return matches[0]


def load_source(source_root: str, season: str, layer: int, size: tuple[int, int]) -> Image.Image:
    path = os.path.join(source_root, season, "TerrainTexture{}.png".format(layer))
    if not os.path.isfile(path):
        raise FileNotFoundError(path)
    with Image.open(path) as image:
        return image.convert("RGBA").resize(size, Image.Resampling.LANCZOS)


def encode_array_slice(image: Image.Image, width: int, height: int, mip_count: int) -> bytes:
    result = bytearray()
    mip = image.resize((width, height), Image.Resampling.LANCZOS)
    for level in range(mip_count):
        encoded, texture_format = Texture2DConverter.image_to_texture2d(
            mip, TextureFormat.RGBA32, flip=True
        )
        if texture_format != TextureFormat.RGBA32:
            raise RuntimeError("unexpected terrain array encoding: {}".format(texture_format))
        result.extend(encoded)
        if level + 1 < mip_count:
            mip = mip.resize((max(1, mip.width // 2), max(1, mip.height // 2)), Image.Resampling.LANCZOS)
    return bytes(result)


def patch_season(environment, source_root: str, season: str) -> None:
    images = []
    for layer in range(1, 17):
        asset_path = "assets/dvseasons/dv99/{}/terraintexture{}.png".format(season, layer)
        obj = container_object(environment, asset_path)
        texture = obj.read()
        source = load_source(source_root, season, layer, texture.image.size)
        images.append(source)
        texture.set_image(source, mipmap_count=max(1, int(texture.m_MipCount or 1)))
        texture.save()

    array_path = "assets/dvseasons/dv99/generated/terrain_{}.asset".format(season)
    array_obj = container_object(environment, array_path)
    texture_array = array_obj.read()
    if texture_array.m_Depth != 16:
        raise RuntimeError("{} has {} slices instead of 16".format(array_path, texture_array.m_Depth))
    encoded_slices = [
        encode_array_slice(
            image,
            int(texture_array.m_Width),
            int(texture_array.m_Height),
            int(texture_array.m_MipCount),
        )
        for image in images
    ]
    texture_array.image_data = b"".join(encoded_slices)
    texture_array.m_DataSize = len(texture_array.image_data)
    if texture_array.m_StreamData is not None:
        texture_array.m_StreamData.path = ""
        texture_array.m_StreamData.offset = 0
        texture_array.m_StreamData.size = 0
    texture_array.save()
    print("patched {}: 16 Texture2D assets and Terrain_{} Texture2DArray".format(season, season))


def main() -> int:
    if len(sys.argv) < 4:
        print(
            "usage: patch_terrain_texture_sets.py <input-bundle> <DV99-source-dir> "
            "<output-bundle> [season ...]",
            file=sys.stderr,
        )
        return 64

    input_bundle, source_root, output_bundle = sys.argv[1:4]
    seasons = sys.argv[4:] or ["spring", "autumn"]
    invalid = [season for season in seasons if season not in ("spring", "autumn", "winter")]
    if invalid:
        raise ValueError("unsupported seasons: {}".format(", ".join(invalid)))

    environment = UnityPy.load(input_bundle)
    for season in seasons:
        patch_season(environment, source_root, season)

    with open(output_bundle, "wb") as output:
        output.write(environment.file.save(packer="original"))

    verification = UnityPy.load(output_bundle)
    for season in seasons:
        array_path = "assets/dvseasons/dv99/generated/terrain_{}.asset".format(season)
        array = container_object(verification, array_path).read()
        if len(array.images) != 16 or any(image.size != (512, 512) for image in array.images):
            raise RuntimeError("saved {} array failed verification".format(season))
    print("verified", output_bundle, os.path.getsize(output_bundle), "bytes")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
