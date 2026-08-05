#!/usr/bin/env python3
"""Stage exact, network-free Windows OCR runtimes from a verified download cache."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tarfile
import tempfile
import zipfile
from pathlib import Path, PurePosixPath
from typing import Any

SCHEMA_VERSION = 1
MAX_ARCHIVE_ENTRIES = 100_000
MAX_EXPANDED_BYTES = 4 * 1024**3
WINDOWS_RESERVED_NAMES = {
    "aux",
    "con",
    "nul",
    "prn",
    *(f"com{number}" for number in range(1, 10)),
    *(f"lpt{number}" for number in range(1, 10)),
}


class StageError(RuntimeError):
    """Raised when an OCR component cannot be staged exactly."""


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(8 * 1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def canonical_json_bytes(value: object) -> bytes:
    return (json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n").encode()


def require_object(value: object, description: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise StageError(f"{description} must be an object")
    return value


def require_text(value: object, description: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise StageError(f"{description} must be non-empty text")
    return value


def require_sha256(value: object, description: str) -> str:
    text = require_text(value, description)
    if len(text) != 64 or any(character not in "0123456789abcdef" for character in text):
        raise StageError(f"{description} must be a lowercase SHA-256")
    return text


def validate_relative_path(value: str, description: str) -> PurePosixPath:
    if "\\" in value or ":" in value:
        raise StageError(f"{description} contains an unsafe Windows path")
    pure = PurePosixPath(value)
    if pure.is_absolute() or not pure.parts or any(part in {"", ".", ".."} for part in pure.parts):
        raise StageError(f"{description} is not a canonical relative path")
    for part in pure.parts:
        if part[-1] in {" ", "."}:
            raise StageError(f"{description} has a trailing dot or space")
        base = part.split(".", 1)[0].casefold()
        if base in WINDOWS_RESERVED_NAMES:
            raise StageError(f"{description} contains a reserved Windows name")
    return pure


def resolve_inside(root: Path, relative: PurePosixPath) -> Path:
    root = root.resolve()
    candidate = root.joinpath(*relative.parts).resolve()
    try:
        candidate.relative_to(root)
    except ValueError as exc:
        raise StageError(f"Path escapes its staging root: {relative.as_posix()}") from exc
    return candidate


def verify_cached_file(cache: Path, spec: dict[str, Any], description: str) -> Path:
    filename = require_text(spec.get("fileName"), f"{description} fileName")
    if Path(filename).name != filename:
        raise StageError(f"{description} fileName must be a safe leaf name")
    expected_bytes = spec.get("bytes")
    if not isinstance(expected_bytes, int) or expected_bytes < 1:
        raise StageError(f"{description} bytes must be a positive integer")
    expected_sha256 = require_sha256(spec.get("sha256"), f"{description} sha256")
    path = cache / filename
    if not path.is_file() or path.is_symlink():
        raise StageError(f"{description} is missing from the verified cache: {path}")
    if path.stat().st_size != expected_bytes:
        raise StageError(f"{description} byte length does not match the lock")
    if sha256_file(path) != expected_sha256:
        raise StageError(f"{description} SHA-256 does not match the lock")
    return path


def extract_locked_zip_member(
    archive: Path,
    member_name: str,
    output: Path,
    expected_bytes: int,
    expected_sha256: str,
    description: str,
) -> None:
    relative = validate_relative_path(member_name, f"{description} member")
    canonical_name = relative.as_posix()
    with zipfile.ZipFile(archive) as handle:
        matches = [info for info in handle.infolist() if info.filename == canonical_name]
        if len(matches) != 1:
            raise StageError(
                f"{description} source must contain exactly one locked member: {canonical_name}"
            )
        info = matches[0]
        reject_zip_link(info, description)
        if info.is_dir() or info.file_size != expected_bytes:
            raise StageError(f"{description} member byte length does not match the lock")
        output.parent.mkdir(parents=True, exist_ok=True)
        with handle.open(info) as source, output.open("xb") as destination:
            shutil.copyfileobj(source, destination, 1024 * 1024)
    if output.stat().st_size != expected_bytes or sha256_file(output) != expected_sha256:
        raise StageError(f"{description} member identity does not match the lock")


def reject_zip_link(info: zipfile.ZipInfo, description: str) -> None:
    unix_mode = (info.external_attr >> 16) & 0xFFFF
    if unix_mode and (unix_mode & 0o170000) not in {0, 0o040000, 0o100000}:
        raise StageError(f"{description} contains a link or special entry: {info.filename}")


def extract_zip_exact(archive: Path, output: Path, description: str) -> None:
    output.mkdir(parents=True, exist_ok=False)
    seen: set[str] = set()
    expanded = 0
    with zipfile.ZipFile(archive) as handle:
        infos = handle.infolist()
        if not 1 <= len(infos) <= MAX_ARCHIVE_ENTRIES:
            raise StageError(f"{description} has an unsafe entry count")
        for info in infos:
            reject_zip_link(info, description)
            name = info.filename.rstrip("/")
            if not name:
                continue
            relative = validate_relative_path(name, f"{description} entry")
            key = relative.as_posix().casefold()
            if key in seen:
                raise StageError(f"{description} contains a duplicate path: {name}")
            seen.add(key)
            target = resolve_inside(output, relative)
            if info.is_dir():
                target.mkdir(parents=True, exist_ok=True)
                continue
            expanded += info.file_size
            if expanded > MAX_EXPANDED_BYTES:
                raise StageError(f"{description} expands beyond the safety limit")
            target.parent.mkdir(parents=True, exist_ok=True)
            with handle.open(info) as source, target.open("xb") as destination:
                shutil.copyfileobj(source, destination, 1024 * 1024)
            if target.stat().st_size != info.file_size:
                raise StageError(f"{description} extracted a truncated entry: {name}")


def wheel_destination(runtime: Path, site_packages: Path, name: str) -> Path:
    relative = validate_relative_path(name, "wheel entry")
    first = relative.parts[0]
    if not first.endswith(".data"):
        return resolve_inside(site_packages, relative)
    if len(relative.parts) < 3:
        raise StageError(f"Wheel data entry is incomplete: {name}")
    scheme = relative.parts[1]
    remainder = PurePosixPath(*relative.parts[2:])
    if scheme in {"purelib", "platlib"}:
        return resolve_inside(site_packages, remainder)
    if scheme == "scripts":
        return resolve_inside(runtime / "Scripts", remainder)
    if scheme == "headers":
        return resolve_inside(runtime / "Include", remainder)
    if scheme == "data":
        return resolve_inside(runtime, remainder)
    raise StageError(f"Wheel contains an unsupported installation scheme: {scheme}")


def install_wheel(
    archive: Path,
    runtime: Path,
    site_packages: Path,
    written: set[str],
) -> None:
    expanded = 0
    with zipfile.ZipFile(archive) as handle:
        infos = handle.infolist()
        if not 1 <= len(infos) <= MAX_ARCHIVE_ENTRIES:
            raise StageError(f"Wheel has an unsafe entry count: {archive.name}")
        for info in infos:
            reject_zip_link(info, f"wheel {archive.name}")
            name = info.filename.rstrip("/")
            if not name:
                continue
            target = wheel_destination(runtime, site_packages, name)
            key = str(target).casefold()
            if key in written:
                raise StageError(f"OCR wheels contain a duplicate install path: {name}")
            written.add(key)
            if info.is_dir():
                target.mkdir(parents=True, exist_ok=True)
                continue
            expanded += info.file_size
            if expanded > MAX_EXPANDED_BYTES:
                raise StageError(f"Wheel expands beyond the safety limit: {archive.name}")
            target.parent.mkdir(parents=True, exist_ok=True)
            with handle.open(info) as source, target.open("xb") as destination:
                shutil.copyfileobj(source, destination, 1024 * 1024)
            if target.stat().st_size != info.file_size:
                raise StageError(f"Wheel entry was truncated: {archive.name}:{name}")


def install_sdist_source(
    archive: Path,
    site_packages: Path,
    source_root: str,
    written: set[str],
) -> None:
    root = validate_relative_path(source_root, "sdist sourceRoot").as_posix().rstrip("/") + "/"
    copied = 0
    expanded = 0
    with tarfile.open(archive, mode="r:gz") as handle:
        members = handle.getmembers()
        if not 1 <= len(members) <= MAX_ARCHIVE_ENTRIES:
            raise StageError(f"Source distribution has an unsafe entry count: {archive.name}")
        for member in members:
            normalized = member.name.replace("\\", "/")
            if not normalized.startswith(root):
                continue
            remainder = normalized[len(root) :].rstrip("/")
            if not remainder:
                continue
            relative = validate_relative_path(remainder, "sdist source entry")
            target = resolve_inside(site_packages, relative)
            key = str(target).casefold()
            if key in written:
                raise StageError(f"OCR sources contain a duplicate install path: {remainder}")
            written.add(key)
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
                continue
            if not member.isfile():
                raise StageError(
                    f"Selected sdist source contains a link or special entry: {member.name}"
                )
            expanded += member.size
            if expanded > MAX_EXPANDED_BYTES:
                raise StageError(
                    f"Source distribution expands beyond the safety limit: {archive.name}"
                )
            source = handle.extractfile(member)
            if source is None:
                raise StageError(f"Could not read selected sdist entry: {member.name}")
            target.parent.mkdir(parents=True, exist_ok=True)
            with source, target.open("xb") as destination:
                shutil.copyfileobj(source, destination, 1024 * 1024)
            if target.stat().st_size != member.size:
                raise StageError(f"Source distribution entry was truncated: {member.name}")
            copied += 1
    if copied == 0:
        raise StageError(
            f"Source distribution did not contain its locked sourceRoot: {archive.name}"
        )


def configure_embeddable_python(runtime: Path) -> None:
    path_files = list(runtime.glob("python*._pth"))
    if len(path_files) != 1:
        raise StageError("Embeddable Python must contain exactly one python*._pth file")
    source_lines = path_files[0].read_text(encoding="utf-8").splitlines()
    active = [
        line.strip() for line in source_lines if line.strip() and not line.lstrip().startswith("#")
    ]
    if any(line == "import site" for line in active):
        raise StageError("Embeddable Python unexpectedly enables import site")
    required = [line for line in active if line != "."]
    required.extend([".", "Lib/site-packages"])
    path_files[0].write_bytes(("\n".join(dict.fromkeys(required)) + "\n").encode())


def runtime_metadata(runtime: Path) -> dict[str, Any]:
    script = """
import importlib.metadata as metadata
import json
import onnxruntime
import platform
rows = sorted(
    (item.metadata['Name'], item.version)
    for item in metadata.distributions()
)
print(json.dumps({
    'python': platform.python_version(),
    'packages': rows,
    'providers': onnxruntime.get_available_providers(),
}, sort_keys=True))
"""
    result = subprocess.run(
        [str(runtime / "python.exe"), "-I", "-B", "-c", script],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    if result.returncode != 0:
        raise StageError(f"Staged OCR runtime import probe failed: {result.stderr.strip()}")
    return require_object(json.loads(result.stdout), "OCR runtime probe")


def expected_packages(
    packages_by_id: dict[str, dict[str, Any]],
    package_ids: list[str],
) -> list[list[str]]:
    rows = [
        [
            require_text(packages_by_id[item].get("name"), f"package {item} name"),
            require_text(packages_by_id[item].get("version"), f"package {item} version"),
        ]
        for item in package_ids
    ]
    return sorted(rows)


def stage_runtime(
    name: str,
    lock: dict[str, Any],
    packages_by_id: dict[str, dict[str, Any]],
    cache: Path,
    output: Path,
) -> dict[str, Any]:
    python_spec = require_object(lock["pythonRuntimes"][name], f"{name} Python")
    runtime = output / "runtime" / f"ocr-{name}"
    extract_zip_exact(
        verify_cached_file(cache, python_spec, f"{name} Python"),
        runtime,
        f"{name} Python",
    )
    site_packages = runtime / "Lib" / "site-packages"
    site_packages.mkdir(parents=True, exist_ok=True)
    common = list(lock["runtimeSets"]["common"])
    provider = list(lock["runtimeSets"][name])
    package_ids = [*common, *provider]
    written: set[str] = set()
    for package_id in package_ids:
        spec = packages_by_id[package_id]
        archive = verify_cached_file(cache, spec, f"package {package_id}")
        archive_type = spec.get("archiveType")
        if archive_type == "wheel":
            install_wheel(archive, runtime, site_packages, written)
        elif archive_type == "sdist-src":
            install_sdist_source(
                archive,
                site_packages,
                require_text(spec.get("sourceRoot"), f"package {package_id} sourceRoot"),
                written,
            )
        else:
            raise StageError(f"Package {package_id} has an unsupported archiveType")
    configure_embeddable_python(runtime)
    metadata = runtime_metadata(runtime)
    if metadata.get("python") != python_spec.get("version"):
        raise StageError(f"Staged {name} Python version does not match the lock")
    if metadata.get("packages") != expected_packages(packages_by_id, package_ids):
        raise StageError(f"Staged {name} Python package closure does not match the lock")
    providers = metadata.get("providers")
    if not isinstance(providers, list) or "CPUExecutionProvider" not in providers:
        raise StageError(f"Staged {name} runtime does not expose CPUExecutionProvider")
    if name == "directml" and "DmlExecutionProvider" not in providers:
        raise StageError("Staged DirectML runtime does not expose DmlExecutionProvider")
    return metadata


def derive_dictionary(source: Path, output: Path) -> None:
    import yaml

    document = yaml.safe_load(source.read_text(encoding="utf-8"))
    characters = document["PostProcess"]["character_dict"]
    if not isinstance(characters, list) or not all(isinstance(item, str) for item in characters):
        raise StageError("Recognizer YAML does not contain a text character_dict")
    output.write_bytes(("\n".join(characters) + "\n").encode())
    print(json.dumps({"entries": len(characters)}, sort_keys=True))


def run_dictionary_derivation(runtime: Path, source: Path, output: Path) -> int:
    result = subprocess.run(
        [
            str(runtime / "python.exe"),
            "-I",
            "-B",
            str(Path(__file__).resolve()),
            "derive-dictionary",
            "--source",
            str(source),
            "--output",
            str(output),
        ],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    if result.returncode != 0:
        raise StageError(f"Could not derive the OCR dictionary: {result.stderr.strip()}")
    value = require_object(json.loads(result.stdout), "dictionary derivation result")
    entries = value.get("entries")
    if not isinstance(entries, int):
        raise StageError("Dictionary derivation did not report its entry count")
    return entries


def stage_model_pack(
    lock: dict[str, Any],
    packages_by_id: dict[str, dict[str, Any]],
    cache: Path,
    output: Path,
) -> dict[str, Any]:
    spec = require_object(lock.get("modelPack"), "modelPack")
    model_root = output / "models" / "ocr"
    model_root.mkdir(parents=True, exist_ok=False)
    manifest: dict[str, Any] = {
        "schemaVersion": SCHEMA_VERSION,
        "modelId": require_text(spec.get("modelId"), "modelPack modelId"),
        "revision": require_text(spec.get("revision"), "modelPack revision"),
    }
    for component_name in ("detector", "recognizer", "classifier"):
        component = require_object(spec.get(component_name), f"modelPack {component_name}")
        relative = validate_relative_path(
            require_text(component.get("path"), f"modelPack {component_name} path"),
            f"modelPack {component_name} path",
        )
        destination = resolve_inside(model_root, relative)
        derived_from = component.get("derivedFrom")
        if derived_from is None:
            source = verify_cached_file(cache, component, f"modelPack {component_name}")
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, destination)
        else:
            derivation = require_object(derived_from, f"modelPack {component_name} derivedFrom")
            package_id = require_text(
                derivation.get("packageId"),
                f"modelPack {component_name} derivedFrom packageId",
            )
            if package_id not in packages_by_id:
                raise StageError(
                    f"modelPack {component_name} references an unknown package: {package_id}"
                )
            package_archive = verify_cached_file(
                cache,
                packages_by_id[package_id],
                f"modelPack {component_name} source package",
            )
            extract_locked_zip_member(
                package_archive,
                require_text(
                    derivation.get("path"),
                    f"modelPack {component_name} derivedFrom path",
                ),
                destination,
                component.get("bytes"),
                require_sha256(component.get("sha256"), f"modelPack {component_name} sha256"),
                f"modelPack {component_name}",
            )
        manifest[component_name] = {
            "path": relative.as_posix(),
            "sha256": require_sha256(component.get("sha256"), f"modelPack {component_name} sha256"),
        }
    dictionary = require_object(spec.get("dictionary"), "modelPack dictionary")
    dictionary_source = require_object(
        dictionary.get("derivedFrom"),
        "dictionary derivedFrom",
    )
    source = verify_cached_file(cache, dictionary_source, "recognizer dictionary source")
    dictionary_relative = validate_relative_path(
        require_text(dictionary.get("path"), "modelPack dictionary path"),
        "modelPack dictionary path",
    )
    dictionary_path = resolve_inside(model_root, dictionary_relative)
    entries = run_dictionary_derivation(
        output / "runtime" / "ocr-directml",
        source,
        dictionary_path,
    )
    if entries != dictionary.get("entries"):
        raise StageError("Derived OCR dictionary entry count does not match the lock")
    if dictionary_path.stat().st_size != dictionary.get("bytes"):
        raise StageError("Derived OCR dictionary byte length does not match the lock")
    if sha256_file(dictionary_path) != dictionary.get("sha256"):
        raise StageError("Derived OCR dictionary SHA-256 does not match the lock")
    manifest["dictionary"] = {
        "path": dictionary_relative.as_posix(),
        "sha256": require_sha256(dictionary.get("sha256"), "modelPack dictionary sha256"),
    }
    manifest_path = model_root / require_text(spec.get("fileName"), "modelPack fileName")
    manifest_path.write_bytes(canonical_json_bytes(manifest))
    if manifest_path.stat().st_size != spec.get("bytes"):
        raise StageError("Generated OCR model-pack byte length does not match the lock")
    if sha256_file(manifest_path) != spec.get("sha256"):
        raise StageError("Generated OCR model-pack SHA-256 does not match the lock")
    return {
        "path": manifest_path.relative_to(output).as_posix(),
        "bytes": manifest_path.stat().st_size,
        "sha256": sha256_file(manifest_path),
        "modelId": spec["modelId"],
        "revision": spec["revision"],
    }


def is_notice_path(relative: str) -> bool:
    parts = [part.casefold() for part in relative.replace("\\", "/").split("/")]
    filename = parts[-1]
    if any(marker in filename for marker in ("license", "licence", "notice", "copying", "authors")):
        return True
    return any(part.endswith(".dist-info") for part in parts) and "licenses" in parts


def file_rows(root: Path, *, notices_only: bool = False) -> list[dict[str, Any]]:
    rows = []
    for path in sorted(root.rglob("*"), key=lambda item: item.as_posix().casefold()):
        if not path.is_file() or path.is_symlink():
            continue
        relative = path.relative_to(root).as_posix()
        if notices_only and not is_notice_path(relative):
            continue
        rows.append(
            {
                "path": relative,
                "bytes": path.stat().st_size,
                "sha256": sha256_file(path),
            }
        )
    return rows


def copy_governance_files(
    lock_path: Path,
    inventory_source: Path,
    lock: dict[str, Any],
    cache: Path,
    output: Path,
) -> None:
    licenses = output / "licenses"
    supplemental = licenses / "ocr-runtime"
    supplemental.mkdir(parents=True, exist_ok=False)
    shutil.copyfile(inventory_source, licenses / inventory_source.name)
    shutil.copyfile(lock_path, output / "ocr-components.lock.json")
    for raw_spec in lock.get("supplementalLicenses", []):
        spec = require_object(raw_spec, "supplemental license")
        source = verify_cached_file(
            cache,
            spec,
            f"supplemental license {spec.get('id', 'unknown')}",
        )
        shutil.copyfile(source, supplemental / source.name)


def stage(args: argparse.Namespace) -> None:
    lock_path = args.lock.resolve(strict=True)
    cache = args.cache.resolve(strict=True)
    inventory_source = args.inventory.resolve(strict=True)
    output = args.output.resolve()
    if output.exists():
        raise StageError(f"OCR component output must not already exist: {output}")
    lock = require_object(json.loads(lock_path.read_text(encoding="utf-8")), "OCR lock")
    if lock.get("schemaVersion") != SCHEMA_VERSION:
        raise StageError("OCR lock has an unsupported schemaVersion")
    raw_packages = lock.get("packages")
    if not isinstance(raw_packages, list) or not raw_packages:
        raise StageError("OCR lock contains no packages")
    packages_by_id: dict[str, dict[str, Any]] = {}
    for raw_package in raw_packages:
        package = require_object(raw_package, "OCR package")
        package_id = require_text(package.get("id"), "OCR package id")
        if package_id in packages_by_id:
            raise StageError(f"OCR lock contains duplicate package id: {package_id}")
        packages_by_id[package_id] = package
    temporary = Path(tempfile.mkdtemp(prefix="bstrings-ocr-stage-", dir=output.parent))
    try:
        cpu = stage_runtime("cpu", lock, packages_by_id, cache, temporary)
        directml = stage_runtime("directml", lock, packages_by_id, cache, temporary)
        model_pack = stage_model_pack(lock, packages_by_id, cache, temporary)
        copy_governance_files(lock_path, inventory_source, lock, cache, temporary)
        inventory = {
            "schemaVersion": SCHEMA_VERSION,
            "profile": lock.get("profile"),
            "componentLockSha256": sha256_file(lock_path),
            "runtimes": {"cpu": cpu, "directml": directml},
            "modelPack": model_pack,
            "runtimeFiles": {
                name: file_rows(temporary / "runtime" / f"ocr-{name}")
                for name in ("cpu", "directml")
            },
            "noticeFiles": {
                name: file_rows(temporary / "runtime" / f"ocr-{name}", notices_only=True)
                for name in ("cpu", "directml")
            },
        }
        (temporary / "licenses" / "ocr-runtime-files.json").write_bytes(
            canonical_json_bytes(inventory)
        )
        os.replace(temporary, output)
    except BaseException:
        shutil.rmtree(temporary, ignore_errors=True)
        raise
    print(
        json.dumps(
            {
                "output": str(output),
                "cpuProviders": cpu["providers"],
                "directmlProviders": directml["providers"],
                "modelPackSha256": model_pack["sha256"],
            },
            sort_keys=True,
        )
    )


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    stage_parser = subparsers.add_parser("stage")
    stage_parser.add_argument("--lock", type=Path, required=True)
    stage_parser.add_argument("--inventory", type=Path, required=True)
    stage_parser.add_argument("--cache", type=Path, required=True)
    stage_parser.add_argument("--output", type=Path, required=True)
    derive = subparsers.add_parser("derive-dictionary")
    derive.add_argument("--source", type=Path, required=True)
    derive.add_argument("--output", type=Path, required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_arguments()
    try:
        if args.command == "derive-dictionary":
            derive_dictionary(args.source.resolve(strict=True), args.output.resolve())
        else:
            stage(args)
        return 0
    except (StageError, OSError, ValueError, json.JSONDecodeError) as exc:
        print(f"OCR component staging failed: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
