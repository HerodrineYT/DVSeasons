"""Read-only Unity 2019 packed shader variant check. Exit 1 on a missing variant."""
import hashlib
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / ".tools/unitypy313"))
import UnityPy
from UnityPy.export.ShaderConverter import ShaderProgram
from UnityPy.helpers import CompressionHelper
from UnityPy.streams import EndianBinaryReader

KEYWORD = "UNITY_SINGLE_PASS_STEREO"
REQUIRED = {
    "Hidden/DVSeasons/ProceduralSnow": {0: [(), ("DVPS_FAST_HDR",)]},
    "Hidden/DVSeasons/PuddleIceGBuffer": {0: [()], 1: [()]},
    "Hidden/DVSeasons/SnowGlare": {0: [()]},
    "Hidden/DVSeasons/SnowVehicle": {
        0: [()], 2: [()], 5: [(), ("INSTANCING_ON",)], 6: [(), ("INSTANCING_ON",)]},
    "Hidden/DVSeasons/SnowVehicleStandard": {
        3: [(), ("_ALPHATEST_ON",), ("_NORMALMAP", "_METALLICGLOSSMAP", "_DETAIL_MULX2"),
            ("_ALPHATEST_ON", "_NORMALMAP", "_METALLICGLOSSMAP", "_DETAIL_MULX2"), ("INSTANCING_ON",)]},
}


def first(value):
    return value[0] if isinstance(value, list) else value


def audit(path):
    content = path.read_bytes()
    report = {"bundle": str(path), "bytes": len(content),
              "sha256": hashlib.sha256(content).hexdigest(), "shaders": [], "errors": []}
    found = set()
    for obj in UnityPy.load(str(path)).objects:
        if obj.type.name != "Shader":
            continue
        shader = obj.read()
        name = shader.m_ParsedForm.m_Name
        if name not in REQUIRED:
            continue
        found.add(name)
        row = {"name": name, "platforms": shader.platforms, "passes": []}
        report["shaders"].append(row)
        if 4 not in shader.platforms:
            report["errors"].append(name + ": no D3D11 program")
            continue
        platform = shader.platforms.index(4)
        offset = first(shader.offsets[platform])
        size = first(shader.compressedLengths[platform])
        raw = CompressionHelper.decompress_lz4(bytes(shader.compressedBlob)[offset:offset + size],
                                               first(shader.decompressedLengths[platform]))
        programs = ShaderProgram(EndianBinaryReader(raw, endian="<"), shader.object_reader.version)
        for index, required_options in REQUIRED[name].items():
            shader_pass = shader.m_ParsedForm.m_SubShaders[0].m_Passes[index]
            names = {value: key for key, value in shader_pass.m_NameIndices}
            pass_row = {"index": index, "name": shader_pass.m_State.m_Name, "stages": {}}
            row["passes"].append(pass_row)
            for stage in ("progVertex", "progFragment"):
                variants = []
                for variant in getattr(shader_pass, stage).m_SubPrograms:
                    # D3D11 vertex/pixel SM4.0 and SM5.0. Ignore other platforms.
                    if variant.m_GpuProgramType not in (15, 16, 17, 18):
                        continue
                    program = programs.m_SubPrograms[variant.m_BlobIndex]
                    global_metadata = {names[i] for i in variant.m_GlobalKeywordIndices or []}
                    local_metadata = {names[i] for i in variant.m_LocalKeywordIndices or []}
                    globals_blob = set(program.m_Keywords)
                    locals_blob = set(program.m_LocalKeywords or [])
                    if global_metadata != globals_blob or local_metadata != locals_blob:
                        report["errors"].append(f"{name} pass {index} {stage}: metadata/blob keyword mismatch")
                    keywords = globals_blob | locals_blob
                    variants.append({"blob_index": variant.m_BlobIndex,
                                     "keywords": sorted(keywords), "bytecode_bytes": len(program.m_ProgramCode)})
                pass_row["stages"][stage] = variants
                for options in required_options:
                    # Both mono and stereo must remain available, including
                    # every existing HDR and runtime instancing combination.
                    for stereo in (False, True):
                        needed = set(options) | ({KEYWORD} if stereo else set())
                        relevant = {KEYWORD, "INSTANCING_ON", "_ALPHATEST_ON", "_NORMALMAP", "_METALLICGLOSSMAP",
                                    "_DETAIL_MULX2", "_EMISSION", "_GLOSSYREFLECTIONS_OFF"}
                        native = name == "Hidden/DVSeasons/SnowVehicleStandard"
                        if not any((set(v["keywords"]) & relevant if native else set(v["keywords"])) == needed
                                   and v["bytecode_bytes"] > 0 for v in variants):
                            report["errors"].append(f"{name} pass {index} {stage}: missing {sorted(needed)}")
    for name in REQUIRED.keys() - found:
        report["errors"].append("Shader missing: " + name)
    report["pass"] = not report["errors"]
    return report


result = audit(Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "Resources/Runtime/AssetBundles/dvseasons_dv99")
serialized = json.dumps(result, ensure_ascii=True, indent=2)
if len(sys.argv) > 2:
    Path(sys.argv[2]).write_text(serialized + "\n", encoding="utf-8")
for shader in result["shaders"]:
    print(shader["name"] + ": " + "; ".join(
        "pass " + str(p["index"]) + " " + ", ".join(
            stage.replace("prog", "") + "=" + str(len(variants)) for stage, variants in p["stages"].items())
        for p in shader["passes"]))
for error in result["errors"]:
    print("ERROR: " + error)
print("STEREO_BUNDLE_AUDIT_" + ("PASS" if result["pass"] else "FAIL") + " sha256=" + result["sha256"])
sys.exit(0 if result["pass"] else 1)
