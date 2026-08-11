#!/usr/bin/env python3
"""Train the research-only sparse float64 translation-worthiness reference model."""

from __future__ import annotations

import argparse
import math
import os
import tempfile
from collections.abc import Mapping, Sequence
from pathlib import Path
from typing import Any

from translation_worthiness_corpus import (
    CORPUS_ID,
    DEFAULT_CORPUS,
    DEFAULT_SPLITS,
    PROJECT_LICENSE,
    SCHEMA_VERSION,
    WorthinessCorpusError,
    validate_corpus,
)
from translation_worthiness_model import (
    ABLATION_CODES,
    DEFAULT_BUCKET_COUNT,
    DEFAULT_EPOCHS,
    DEFAULT_LEARNING_RATE,
    MODEL_FORMAT_VERSION,
    SCORE_ORIENTATION,
    canonical_json_bytes,
    choose_thresholds,
    feature_contract_sha256,
    score_rows,
    serialize_model,
    sha256_bytes,
    sha256_file,
    train_model,
)

DEFAULT_EXPERIMENT_ID = "float64-sparse-linear-synthetic-v1"


def _physical_input(path: Path, *, name: str) -> Path:
    resolved = path.resolve(strict=False)
    if not path.is_file() or path.is_symlink() or resolved.is_symlink():
        raise WorthinessCorpusError(f"{name} must be a physical file")
    return resolved


def _destination(path: Path, inputs: set[Path]) -> Path:
    resolved = path.resolve(strict=False)
    if resolved in inputs:
        raise WorthinessCorpusError("Training output must not alias an input")
    if path.exists() and (not path.is_file() or path.is_symlink() or resolved.is_symlink()):
        raise WorthinessCorpusError("Training output must be a physical file or absent")
    if path.parent.exists() and (not path.parent.is_dir() or path.parent.is_symlink()):
        raise WorthinessCorpusError("Training output parent must be a physical directory")
    return resolved


def _write_temporary(path: Path, payload: bytes) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{path.name}.", suffix=".partial", dir=path.parent
    )
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
    except BaseException:
        temporary.unlink(missing_ok=True)
        raise
    return temporary


def _publish_artifacts(
    model_path: Path,
    model_bytes: bytes,
    predictions_path: Path,
    predictions_bytes: bytes,
    manifest_path: Path,
    manifest_bytes: bytes,
) -> None:
    temporaries: list[Path] = []
    try:
        model_temporary = _write_temporary(model_path, model_bytes)
        temporaries.append(model_temporary)
        predictions_temporary = _write_temporary(predictions_path, predictions_bytes)
        temporaries.append(predictions_temporary)
        manifest_temporary = _write_temporary(manifest_path, manifest_bytes)
        temporaries.append(manifest_temporary)
        os.replace(model_temporary, model_path)
        temporaries.remove(model_temporary)
        os.replace(predictions_temporary, predictions_path)
        temporaries.remove(predictions_temporary)
        # The manifest is the commit marker and is published only after its hashed assets.
        os.replace(manifest_temporary, manifest_path)
        temporaries.remove(manifest_temporary)
    finally:
        for temporary in temporaries:
            temporary.unlink(missing_ok=True)


def _prediction_bytes(predictions: Sequence[Mapping[str, Any]]) -> bytes:
    return b"".join(canonical_json_bytes(dict(prediction)) for prediction in predictions)


def build_artifacts(
    corpus_path: Path,
    split_path: Path,
    *,
    ablation: str,
    experiment_id: str,
    bucket_count: int,
    epochs: int,
    learning_rate: float,
    trainer_path: Path,
    corpus_id: str = CORPUS_ID,
    license_id: str = PROJECT_LICENSE,
) -> tuple[bytes, bytes, bytes]:
    corpus = validate_corpus(
        corpus_path,
        split_path,
        corpus_id=corpus_id,
        license_id=license_id,
    )
    train_rows = tuple(row for row in corpus.rows if corpus.split_by_group[row.group_id] == "train")
    calibration_rows = tuple(
        row for row in corpus.rows if corpus.split_by_group[row.group_id] == "calibration"
    )
    model = train_model(
        train_rows,
        ablation=ablation,
        bucket_count=bucket_count,
        epochs=epochs,
        learning_rate=learning_rate,
    )
    suppress_max, retain_min = choose_thresholds(model, calibration_rows)
    model_bytes = serialize_model(model)
    # Test labels are not consulted: locked predictions use only identifier and text.
    predictions = score_rows(model, corpus.rows, suppress_max, retain_min)
    predictions_bytes = _prediction_bytes(predictions)
    manifest = {
        "schemaVersion": SCHEMA_VERSION,
        "artifactType": "translation-worthiness-model-manifest",
        "researchOnly": True,
        "promotionEligible": False,
        "corpusId": corpus_id,
        "license": license_id,
        "experimentId": experiment_id,
        "ablation": ablation,
        "scoreOrientation": SCORE_ORIENTATION,
        "protocol": {
            "fitSplit": "train",
            "selectionSplit": "calibration",
            "lockedTestSplit": "test",
            "state": "locked",
        },
        "thresholds": {"suppressMax": suppress_max, "retainMin": retain_min},
        "hashes": {
            "corpusSha256": sha256_file(corpus_path),
            "splitsSha256": sha256_file(split_path),
            "trainerSha256": sha256_file(trainer_path),
            "featureContractSha256": feature_contract_sha256(),
            "modelSha256": sha256_bytes(model_bytes),
            "predictionsSha256": sha256_bytes(predictions_bytes),
        },
        "model": {
            "formatVersion": MODEL_FORMAT_VERSION,
            "bucketCount": bucket_count,
            "ngramMin": 2,
            "ngramMax": 5,
            "epochs": epochs,
            "learningRate": learning_rate,
            "byteLength": len(model_bytes),
        },
    }
    return model_bytes, predictions_bytes, canonical_json_bytes(manifest)


def parse_arguments(arguments: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--corpus", type=Path, default=DEFAULT_CORPUS)
    parser.add_argument("--splits", type=Path, default=DEFAULT_SPLITS)
    parser.add_argument("--ablation", choices=tuple(ABLATION_CODES), default="combined")
    parser.add_argument("--experiment-id", default=DEFAULT_EXPERIMENT_ID)
    parser.add_argument("--corpus-id", default=CORPUS_ID)
    parser.add_argument("--license-id", default=PROJECT_LICENSE)
    parser.add_argument("--bucket-count", type=int, default=DEFAULT_BUCKET_COUNT)
    parser.add_argument("--epochs", type=int, default=DEFAULT_EPOCHS)
    parser.add_argument("--learning-rate", type=float, default=DEFAULT_LEARNING_RATE)
    parser.add_argument("--model-output", type=Path, required=True)
    parser.add_argument("--predictions-output", type=Path, required=True)
    parser.add_argument("--manifest-output", type=Path, required=True)
    return parser.parse_args(arguments)


def main(arguments: Sequence[str] | None = None) -> int:
    try:
        options = parse_arguments(arguments)
        if (
            not isinstance(options.experiment_id, str)
            or not options.experiment_id
            or len(options.experiment_id) > 64
            or any(
                character not in "abcdefghijklmnopqrstuvwxyz0123456789._-"
                for character in options.experiment_id
            )
        ):
            raise WorthinessCorpusError("Experiment ID is invalid")
        for field, value in (
            ("corpus ID", options.corpus_id),
            ("license ID", options.license_id),
        ):
            if (
                not isinstance(value, str)
                or not value
                or len(value) > 64
                or any(
                    character
                    not in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-+"
                    for character in value
                )
            ):
                raise WorthinessCorpusError(f"{field} is invalid")
        if not math.isfinite(options.learning_rate):
            raise WorthinessCorpusError("Learning rate is invalid")
        corpus_path = _physical_input(options.corpus, name="translation-worthiness corpus")
        split_path = _physical_input(options.splits, name="translation-worthiness split manifest")
        trainer_path = _physical_input(Path(__file__), name="translation-worthiness trainer")
        inputs = {corpus_path, split_path, trainer_path}
        destinations = [
            _destination(options.model_output, inputs),
            _destination(options.predictions_output, inputs),
            _destination(options.manifest_output, inputs),
        ]
        if len(set(destinations)) != len(destinations):
            raise WorthinessCorpusError("Training outputs must be distinct")
        model_bytes, predictions_bytes, manifest_bytes = build_artifacts(
            corpus_path,
            split_path,
            ablation=options.ablation,
            experiment_id=options.experiment_id,
            bucket_count=options.bucket_count,
            epochs=options.epochs,
            learning_rate=options.learning_rate,
            trainer_path=trainer_path,
            corpus_id=options.corpus_id,
            license_id=options.license_id,
        )
        _publish_artifacts(
            destinations[0],
            model_bytes,
            destinations[1],
            predictions_bytes,
            destinations[2],
            manifest_bytes,
        )
        return 0
    except WorthinessCorpusError as exc:
        print(f"translation-worthiness training error: {exc}", file=os.sys.stderr)
        return 2
    except OSError:
        print(
            "translation-worthiness training error: filesystem operation failed", file=os.sys.stderr
        )
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
