#!/usr/bin/env python3
"""Verify the non-promotable scheduled physical-storage benchmark evidence."""

from __future__ import annotations

import argparse
import hashlib
import itertools
import json
import pathlib
import re
import subprocess
from dataclasses import dataclass
from typing import Iterable


@dataclass(frozen=True)
class Provider:
    """A provider's command-line/artifact token and serialized enum token."""

    artifact_token: str
    request_token: str
    identity: str


PROVIDERS = (
    Provider("sqlite", "sqlite", "groundwork.sqlite"),
    Provider("sqlserver", "sqlServer", "groundwork.sql-server"),
    Provider("postgresql", "postgreSql", "groundwork.postgre-sql"),
    Provider("mongodb", "mongoDb", "groundwork.mongo-db"),
)
FORMS = {
    "shared": "sharedDocuments",
    "dedicated": "dedicatedDocumentTable",
    "entity": "physicalEntityTable",
}
DATASETS = (1000, 100000, 1000000)
SELECTIVITY_BASIS_POINTS = (1000, 5000)
WORKLOADS = (
    "clientResetPointReadBatch",
    "reusedClientPointReadBatch",
    "indexedQuery",
    "mixedCompoundOrdering",
    "insert",
    "update",
    "delete",
    "unitOfWork",
    "concurrentCreate",
    "optimisticConcurrency",
    "paginationAndCount",
    "backfillMigration",
    "clientRestartValidation",
    "storageGrowth",
)
PAYLOAD_TEMPLATE = (
    '{"status":"{status}","rank":{rank},"category":"{category}",'
    '"padding":{padding:null-or-fixed-x-utf8-bytes}}'
)
PAYLOAD_TEMPLATE_DIGEST = hashlib.sha256(PAYLOAD_TEMPLATE.encode("utf-8")).hexdigest()


def payload_profile_for(workload: str) -> dict[str, object]:
    """Return the closed reviewed profile bound to one workload.

    Profiles deliberately do not multiply the matrix: ordinary writes retain the
    established 0-byte shape and storageGrowth has an explicit 1 KiB shape.
    """
    storage_growth = workload == "storageGrowth"
    return {
        "id": "storage-growth-1k-v1" if storage_growth else "ordinary-json-v1",
        "version": "v1",
        "canonicalTemplate": PAYLOAD_TEMPLATE,
        "canonicalTemplateDigest": PAYLOAD_TEMPLATE_DIGEST,
        "paddingBytes": 1024 if storage_growth else 0,
        "entropy": "deterministicFixedCharacter",
        "contentShape": (
            "json/object/status-rank-category-padding-fixed-x-utf8"
            if storage_growth
            else "json/object/status-rank-category-padding-null"
        ),
        "applicableWorkloads": ["storageGrowth"] if storage_growth else [
            item for item in WORKLOADS if item != "storageGrowth"
        ],
        "reviewed": True,
    }


def canonical_json(value: object) -> str:
    """Produce the one JSON representation used for matrix identity hashing."""
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True)


@dataclass(frozen=True)
class VerificationMatrix:
    providers: tuple[Provider, ...]
    forms: tuple[str, ...]
    datasets: tuple[int, ...]
    selectivity_basis_points: tuple[int, ...]
    workloads: tuple[str, ...]
    independent_runs: int


def matrix_evidence(matrix: VerificationMatrix) -> tuple[dict[str, object], str]:
    """Return the closed matrix claim and its canonical SHA-256 identity.

    The aggregate has always checked these dimensions while reading shards. Retaining
    them in its own artifact makes the successful aggregate independently auditable
    without turning a non-promotable harness result into a performance baseline.
    """
    claim: dict[str, object] = {
        "providers": [provider.request_token for provider in matrix.providers],
        "storageForms": [FORMS[form] for form in matrix.forms],
        "datasetSizes": list(matrix.datasets),
        "querySelectivityBasisPoints": list(matrix.selectivity_basis_points),
        "workloads": list(matrix.workloads),
        "independentMeasuredRuns": matrix.independent_runs,
    }
    return claim, hashlib.sha256(canonical_json(claim).encode("utf-8")).hexdigest()


def validate_coverage_artifact(artifact: dict[str, object]) -> None:
    """Fail closed if this writer drifts from the published v1 coverage contract."""
    required = {
        "contract", "verificationMode", "coverageVerified", "deepGroupVerification",
        "promotable", "matrix", "matrixDigest", "requiredShardCount",
        "verifiedWorkerCount", "verifiedMeasuredWorkerCount", "resultEqualityGroupCount",
        "gitCommit", "gitTreeDigest",
    }
    if set(artifact) != required:
        raise SystemExit("scheduled coverage artifact does not match the strict v1 property set")
    if artifact["contract"] != "groundwork.physical-storage.scheduled-coverage/v1":
        raise SystemExit("scheduled coverage artifact has an unsupported contract version")
    if artifact["verificationMode"] not in {"scheduled-scaffold", "test-fixture-matrix-only"}:
        raise SystemExit("scheduled coverage artifact has an unsupported verification mode")
    if artifact["coverageVerified"] is not True or artifact["promotable"] is not False:
        raise SystemExit("scheduled coverage artifact must be verified and non-promotable")
    matrix = artifact["matrix"]
    if not isinstance(matrix, dict):
        raise SystemExit("scheduled coverage artifact has no matrix claim")
    required_matrix_properties = {
        "providers", "storageForms", "datasetSizes", "querySelectivityBasisPoints",
        "workloads", "independentMeasuredRuns",
    }
    if set(matrix) != required_matrix_properties:
        raise SystemExit("scheduled coverage artifact matrix does not match the strict v1 property set")

    def require_closed_list(name: str, allowed: set[object]) -> None:
        values = matrix[name]
        serialized_values = (
            [canonical_json(value) for value in values]
            if isinstance(values, list)
            else []
        )
        if (
                not isinstance(values, list)
                or not values
                or any(
                    not any(type(value) is type(candidate) and value == candidate for candidate in allowed)
                    for value in values)
                or len(serialized_values) != len(set(serialized_values))):
            raise SystemExit(f"scheduled coverage artifact has an invalid {name} matrix dimension")

    require_closed_list("providers", {provider.request_token for provider in PROVIDERS})
    require_closed_list("storageForms", set(FORMS.values()))
    require_closed_list("workloads", set(WORKLOADS))
    require_closed_list("datasetSizes", set(DATASETS))
    require_closed_list("querySelectivityBasisPoints", set(SELECTIVITY_BASIS_POINTS))
    independent_runs = matrix["independentMeasuredRuns"]
    if type(independent_runs) is not int or independent_runs < 1:
        raise SystemExit("scheduled coverage artifact has an invalid independentMeasuredRuns matrix dimension")
    matrix_digest = artifact["matrixDigest"]
    if not isinstance(matrix_digest, str) or not re.fullmatch(r"[0-9a-f]{64}", matrix_digest):
        raise SystemExit("scheduled coverage artifact has an invalid matrix digest")
    expected_digest = hashlib.sha256(canonical_json(artifact["matrix"]).encode("utf-8")).hexdigest()
    if matrix_digest != expected_digest:
        raise SystemExit("scheduled coverage artifact matrix digest does not bind its matrix claim")
    if artifact["verificationMode"] == "scheduled-scaffold" and artifact["deepGroupVerification"] is not True:
        raise SystemExit("scheduled scaffold coverage requires deep group verification")
    if artifact["verificationMode"] == "test-fixture-matrix-only" and artifact["deepGroupVerification"] is not False:
        raise SystemExit("matrix-only fixture coverage cannot claim deep group verification")
    if artifact["verificationMode"] == "scheduled-scaffold":
        production_matrix, _ = matrix_evidence(VerificationMatrix(
            PROVIDERS,
            tuple(FORMS),
            DATASETS,
            SELECTIVITY_BASIS_POINTS,
            WORKLOADS,
            3))
        if matrix != production_matrix:
            raise SystemExit("scheduled scaffold coverage must bind the complete production matrix")
    if any(
            type(artifact[name]) is not int or artifact[name] < 0
            for name in (
                "requiredShardCount", "verifiedWorkerCount", "verifiedMeasuredWorkerCount",
                "resultEqualityGroupCount",
            )):
        raise SystemExit("scheduled coverage artifact has invalid count fields")
    if not isinstance(artifact["gitCommit"], str) or not artifact["gitCommit"]:
        raise SystemExit("scheduled coverage artifact has no Git commit")
    if (
            artifact["verificationMode"] == "scheduled-scaffold"
            and not re.fullmatch(r"(?:[0-9a-f]{40}|[0-9a-f]{64})", artifact["gitCommit"])):
        raise SystemExit("scheduled scaffold coverage has an invalid Git commit")
    if not isinstance(artifact["gitTreeDigest"], str) or not re.fullmatch(r"[0-9a-f]{64}", artifact["gitTreeDigest"]):
        raise SystemExit("scheduled coverage artifact has an invalid Git tree digest")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=pathlib.Path, required=True)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--expected-git-commit", required=True)
    parser.add_argument(
        "--group-verifier",
        type=pathlib.Path,
        help=(
            "Path to the built Groundwork.PhysicalStorage.Benchmarks.dll used to verify each "
            "scheduled evidence group before this aggregate reads it."
        ),
    )
    parser.add_argument(
        "--skip-deep-verification",
        action="store_true",
        help=(
            "Permit skeletal, matrix-only fixtures in --test-mode. "
            "Never permitted for scheduled evidence."
        ),
    )
    parser.add_argument(
        "--test-mode",
        action="store_true",
        help="Permit a deliberately narrowed matrix for executable verifier tests.",
    )
    parser.add_argument("--providers", help="Comma-separated artifact provider tokens (test mode only).")
    parser.add_argument("--forms", help="Comma-separated form tokens (test mode only).")
    parser.add_argument("--datasets", help="Comma-separated dataset sizes (test mode only).")
    parser.add_argument("--selectivity-bps", help="Comma-separated query selectivities (test mode only).")
    parser.add_argument("--workloads", help="Comma-separated workload tokens (test mode only).")
    parser.add_argument("--independent-runs", type=int, help="Measured repetitions (test mode only).")
    args = parser.parse_args()
    narrowed_options = (
        args.providers,
        args.forms,
        args.datasets,
        args.selectivity_bps,
        args.workloads,
        args.independent_runs,
    )
    if any(option is not None for option in narrowed_options) and not args.test_mode:
        parser.error("matrix overrides require --test-mode; production verification is fixed at 36 shards and 4,032 workers")
    if args.skip_deep_verification and not args.test_mode:
        parser.error("--skip-deep-verification is only permitted with --test-mode")
    if args.test_mode and not args.skip_deep_verification:
        parser.error("--test-mode requires --skip-deep-verification and cannot claim scheduled-scaffold evidence")
    if args.group_verifier is not None and args.skip_deep_verification:
        parser.error("--group-verifier and --skip-deep-verification cannot be used together")
    if args.group_verifier is None and not args.skip_deep_verification:
        parser.error(
            "--group-verifier is required unless a skeletal --test-mode fixture explicitly uses "
            "--skip-deep-verification"
        )
    if args.group_verifier is not None and not args.group_verifier.is_file():
        parser.error(f"--group-verifier must name a built verifier file: {args.group_verifier}")
    return args


def comma_separated(value: str | None, defaults: Iterable[str], option: str) -> tuple[str, ...]:
    if value is None:
        return tuple(defaults)
    result = tuple(item.strip() for item in value.split(",") if item.strip())
    if not result:
        raise SystemExit(f"{option} cannot be empty")
    if len(set(result)) != len(result):
        raise SystemExit(f"{option} cannot contain duplicates")
    return result


def matrix_from_args(args: argparse.Namespace) -> VerificationMatrix:
    providers_by_token = {provider.artifact_token: provider for provider in PROVIDERS}
    provider_tokens = comma_separated(args.providers, providers_by_token, "--providers")
    unknown_providers = sorted(set(provider_tokens) - set(providers_by_token))
    if unknown_providers:
        raise SystemExit(f"unknown provider token(s): {unknown_providers}")

    forms = comma_separated(args.forms, FORMS, "--forms")
    unknown_forms = sorted(set(forms) - set(FORMS))
    if unknown_forms:
        raise SystemExit(f"unknown form token(s): {unknown_forms}")

    dataset_tokens = comma_separated(args.datasets, (str(dataset) for dataset in DATASETS), "--datasets")
    try:
        datasets = tuple(int(dataset) for dataset in dataset_tokens)
    except ValueError as error:
        raise SystemExit("--datasets must contain integers") from error
    if any(dataset <= 0 for dataset in datasets):
        raise SystemExit("--datasets must contain positive integers")

    selectivity_tokens = comma_separated(
        args.selectivity_bps,
        (str(selectivity) for selectivity in SELECTIVITY_BASIS_POINTS),
        "--selectivity-bps",
    )
    try:
        selectivity_basis_points = tuple(int(selectivity) for selectivity in selectivity_tokens)
    except ValueError as error:
        raise SystemExit("--selectivity-bps must contain integers") from error
    if any(selectivity <= 0 or selectivity >= 10000 for selectivity in selectivity_basis_points):
        raise SystemExit("--selectivity-bps must be between 1 and 9999")

    workloads = comma_separated(args.workloads, WORKLOADS, "--workloads")
    unknown_workloads = sorted(set(workloads) - set(WORKLOADS))
    if unknown_workloads:
        raise SystemExit(f"unknown workload token(s): {unknown_workloads}")

    independent_runs = args.independent_runs if args.independent_runs is not None else 3
    if independent_runs <= 0:
        raise SystemExit("--independent-runs must be positive")
    return VerificationMatrix(
        tuple(providers_by_token[token] for token in provider_tokens),
        forms,
        datasets,
        selectivity_basis_points,
        workloads,
        independent_runs,
    )


def resolve_evidence_file(
        evidence_root: pathlib.Path,
        serialized_path: object,
        artifact_name: str,
        artifact_kind: str) -> pathlib.Path:
    """Resolve an evidence file without allowing its manifest to escape its group root."""
    if not isinstance(serialized_path, str) or not serialized_path:
        raise SystemExit(f"{artifact_name} has an invalid {artifact_kind} path")

    posix_path = pathlib.PurePosixPath(serialized_path)
    windows_path = pathlib.PureWindowsPath(serialized_path)
    if posix_path.is_absolute() or windows_path.is_absolute():
        raise SystemExit(f"{artifact_name} {artifact_kind} path must be relative")
    if ".." in posix_path.parts or ".." in windows_path.parts:
        raise SystemExit(f"{artifact_name} {artifact_kind} path must not contain '..'")
    if "\\" in serialized_path:
        raise SystemExit(f"{artifact_name} {artifact_kind} path must use canonical '/' separators")

    try:
        canonical_root = evidence_root.resolve(strict=True)
        candidate = (canonical_root / posix_path).resolve(strict=True)
    except FileNotFoundError as error:
        raise SystemExit(f"{artifact_name} has a missing {artifact_kind} artifact") from error

    try:
        candidate.relative_to(canonical_root)
    except ValueError as error:
        raise SystemExit(f"{artifact_name} {artifact_kind} path escapes its evidence root") from error
    if not candidate.is_file():
        raise SystemExit(f"{artifact_name} {artifact_kind} path must name a file")
    return candidate


def verify_scheduled_group(
        group_verifier: pathlib.Path,
        shard_root: pathlib.Path,
        expected_git_commit: str,
        artifact_name: str) -> None:
    """Require the harness to validate a group before aggregate-level inspection."""
    command = [
        "dotnet",
        str(group_verifier),
        "verify-scheduled-group",
        "--root",
        str(shard_root),
        "--expected-git-commit",
        expected_git_commit,
    ]
    try:
        completed = subprocess.run(command, check=False, capture_output=True, text=True)
    except OSError as error:
        raise SystemExit(f"{artifact_name} deep group verifier could not run: {error}") from error
    if completed.returncode != 0:
        diagnostic = (completed.stderr or completed.stdout).strip()
        raise SystemExit(
            f"{artifact_name} deep group verification failed with exit code {completed.returncode}"
            f"{': ' + diagnostic if diagnostic else ''}"
        )


def verify(
        root: pathlib.Path,
        run_id: str,
        expected_git_commit: str,
        matrix: VerificationMatrix,
        group_verifier: pathlib.Path | None,
        skip_deep_verification: bool) -> dict[str, object]:
    shards_root = root / "shards"
    expected_shards = {
        f"physical-storage-scheduled-{run_id}-{provider.artifact_token}-{form}-n{dataset}":
            (provider, form, dataset)
        for provider, form, dataset in itertools.product(matrix.providers, matrix.forms, matrix.datasets)
    }
    if not shards_root.is_dir():
        raise SystemExit(f"scheduled shard directory is missing: {shards_root}")
    actual_shards = {path.name for path in shards_root.iterdir() if path.is_dir()}
    if actual_shards != set(expected_shards):
        missing = sorted(set(expected_shards) - actual_shards)
        extra = sorted(actual_shards - set(expected_shards))
        raise SystemExit(f"scheduled shard set mismatch; missing={missing}, extra={extra}")

    expected_workers = {
        (provider.request_token, FORMS[form], dataset, selectivity, workload,
         payload_profile_for(workload)["id"], role, independent_run)
        for provider, form, dataset, workload in itertools.product(
            matrix.providers, matrix.forms, matrix.datasets, matrix.workloads)
        for selectivity in matrix.selectivity_basis_points
        for role, independent_run in itertools.chain(
            (("untimedWarmup", 0),),
            (("measured", run) for run in range(1, matrix.independent_runs + 1)),
        )
    }
    actual_workers: set[tuple[object, ...]] = set()
    result_digests: dict[tuple[object, ...], set[str]] = {}
    workload_fingerprints: dict[tuple[object, ...], set[str]] = {}
    git_commits: set[str] = set()
    git_tree_digests: set[str] = set()
    digest_pattern = re.compile(r"^[0-9a-f]{64}$")
    measured_count = 0
    expected_runs_per_shard = (
        len(matrix.selectivity_basis_points) * len(matrix.workloads) * (1 + matrix.independent_runs)
    )

    for artifact_name, (provider, form, dataset) in sorted(expected_shards.items()):
        shard_root = shards_root / artifact_name / "evidence"
        if group_verifier is not None:
            verify_scheduled_group(group_verifier, shard_root, expected_git_commit, artifact_name)
        manifest_path = shard_root / "run-group.json"
        if not manifest_path.is_file():
            raise SystemExit(f"{artifact_name} has no run-group.json")
        manifest = json.loads(manifest_path.read_text())
        if manifest.get("promotable") is not False:
            raise SystemExit(f"{artifact_name} makes a promotional claim")
        if manifest.get("gitCommit") != expected_git_commit:
            raise SystemExit(f"{artifact_name} does not match expected Git commit {expected_git_commit}")
        if manifest.get("gitDirty") is not False:
            raise SystemExit(f"{artifact_name} was produced from a dirty worktree")
        manifest_tree_digest = manifest.get("gitTreeDigest")
        if not isinstance(manifest_tree_digest, str) or not digest_pattern.fullmatch(manifest_tree_digest):
            raise SystemExit(f"{artifact_name} has an invalid Git tree digest")
        git_tree_digests.add(manifest_tree_digest)
        runs = manifest.get("runs", [])
        if len(runs) != expected_runs_per_shard:
            raise SystemExit(f"{artifact_name} has {len(runs)} workers; expected {expected_runs_per_shard}")

        for entry in runs:
            request_path = resolve_evidence_file(shard_root, entry.get("request"), artifact_name, "request")
            response_path = resolve_evidence_file(shard_root, entry.get("response"), artifact_name, "response")
            invocation = json.loads(request_path.read_text())
            response = json.loads(response_path.read_text())
            if response.get("succeeded") is not True:
                raise SystemExit(f"{artifact_name} contains a failed worker response")
            if (response.get("gitCommit") != expected_git_commit or
                    response.get("gitTreeDigest") != manifest_tree_digest):
                raise SystemExit(f"{artifact_name} worker response Git identity does not match its run group")

            request = invocation["request"]
            shape = request["dataShape"]
            workload = request["workloads"][0]
            role = invocation["role"]
            independent_run = invocation["independentRun"]
            expected_profile = payload_profile_for(workload)
            if shape.get("payloadProfile") != expected_profile:
                raise SystemExit(f"{artifact_name} has an unreviewed or mismatched payload profile for {workload}")
            if shape.get("payloadPaddingBytes") != expected_profile["paddingBytes"]:
                raise SystemExit(f"{artifact_name} payload padding does not match its declared profile")
            worker = (
                request["configuration"]["providers"][0],
                request["configuration"]["storageForms"][0],
                shape["datasetSize"],
                shape["querySelectivityBasisPoints"],
                workload,
                expected_profile["id"],
                role,
                independent_run,
            )
            if worker in actual_workers:
                raise SystemExit(f"duplicate scheduled worker tuple: {worker}")
            actual_workers.add(worker)

            if worker[0] != provider.request_token or worker[1] != FORMS[form] or worker[2] != dataset:
                raise SystemExit(f"{artifact_name} contains out-of-shard worker {worker}")
            if shape["querySelectivityBasisPoints"] not in matrix.selectivity_basis_points:
                raise SystemExit(f"{artifact_name} contains an unexpected data shape")

            if role == "measured":
                measured_count += 1
                evidence_path = resolve_evidence_file(
                    shard_root,
                    entry.get("consumerEvidence"),
                    artifact_name,
                    "consumer evidence")
                evidence_bytes = evidence_path.read_bytes()
                evidence_digest = hashlib.sha256(evidence_bytes).hexdigest()
                if evidence_digest != entry["consumerEvidenceDigest"]:
                    raise SystemExit(f"{artifact_name} consumer evidence digest mismatch")
                evidence = json.loads(evidence_bytes)
                if evidence.get("promotable") is not False:
                    raise SystemExit(f"{artifact_name} measured evidence makes a promotional claim")
                if evidence.get("gitCommit") != expected_git_commit or evidence.get("gitDirty") is not False:
                    raise SystemExit(f"{artifact_name} measured evidence Git identity is not clean exact-head")
                git_commits.add(evidence["gitCommit"])
                result = evidence["results"]
                if len(result) != 1:
                    raise SystemExit(f"{artifact_name} worker does not contain exactly one result")
                result = result[0]
                expected_workload_identity = "groundwork.physical-storage/" + re.sub(
                    r"(?<!^)(?=[A-Z])", "-", workload).lower()
                if (
                    result["workloadIdentity"] != expected_workload_identity
                    or result["providerIdentity"] != provider.identity
                    or result["storageForm"] != FORMS[form]
                    or result["dataShape"] != shape
                    or result["independentRun"] != independent_run
                    or result["rawSampleCount"] < 1
                    or result["rawOperationLatencyCount"] < 1
                ):
                    raise SystemExit(f"{artifact_name} consumer evidence does not match worker {worker}")
                digest = result["resultDigest"]
                if not digest_pattern.fullmatch(digest):
                    raise SystemExit(f"{artifact_name} has an invalid result digest")
                digest_key = (
                    shape["datasetSize"],
                    expected_profile["id"],
                    shape["querySelectivityBasisPoints"],
                    workload,
                )
                result_digests.setdefault(digest_key, set()).add(digest)
                workload_fingerprints.setdefault(digest_key, set()).add(result["workloadFingerprint"])

    if actual_workers != expected_workers:
        missing = sorted(expected_workers - actual_workers)
        extra = sorted(actual_workers - expected_workers)
        raise SystemExit(f"scheduled worker coverage mismatch; missing={missing[:10]}, extra={extra[:10]}")
    unequal = {key: sorted(values) for key, values in result_digests.items() if len(values) != 1}
    if unequal:
        raise SystemExit(f"cross-provider/form observable results differ: {unequal}")
    fingerprint_drift = {
        key: sorted(values)
        for key, values in workload_fingerprints.items()
        if len(values) != 1
    }
    if fingerprint_drift:
        raise SystemExit(f"cross-provider/form workload fingerprints differ: {fingerprint_drift}")
    if len(git_commits) != 1:
        raise SystemExit(f"scheduled evidence spans multiple Git commits: {sorted(git_commits)}")
    if len(git_tree_digests) != 1:
        raise SystemExit(f"scheduled evidence spans multiple Git tree digests: {sorted(git_tree_digests)}")

    matrix_claim, matrix_digest = matrix_evidence(matrix)
    return {
        "contract": "groundwork.physical-storage.scheduled-coverage/v1",
        "verificationMode": (
            "test-fixture-matrix-only" if skip_deep_verification else "scheduled-scaffold"
        ),
        "coverageVerified": True,
        "deepGroupVerification": not skip_deep_verification,
        "promotable": False,
        "matrix": matrix_claim,
        "matrixDigest": matrix_digest,
        "requiredShardCount": len(expected_shards),
        "verifiedWorkerCount": len(actual_workers),
        "verifiedMeasuredWorkerCount": measured_count,
        "resultEqualityGroupCount": len(result_digests),
        "gitCommit": next(iter(git_commits)),
        "gitTreeDigest": next(iter(git_tree_digests)),
    }


def main() -> None:
    args = parse_args()
    verification = verify(
        args.root,
        args.run_id,
        args.expected_git_commit,
        matrix_from_args(args),
        args.group_verifier,
        args.skip_deep_verification)
    validate_coverage_artifact(verification)
    (args.root / "coverage-verification.json").write_text(
        json.dumps(verification, indent=2, sort_keys=True) + "\n")
    print(json.dumps(verification, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
