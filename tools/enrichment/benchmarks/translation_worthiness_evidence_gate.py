#!/usr/bin/env python3
"""Research-only evidence gate for translation-worthiness challengers.

The gate distinguishes whether a benchmark experiment is ready to run from
whether evidence could support a future activation review.  It never grants
runtime authority: every result is ``promotionEligible: false`` and
``activationEligible: false``.  Inputs and outputs contain aggregate evidence
only; no source text, row identifiers, paths, or private-case statistics are
accepted.
"""

from __future__ import annotations

import math
import sys
from collections import Counter
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import Any

BENCHMARK_ROOT = Path(__file__).resolve().parent
if str(BENCHMARK_ROOT) not in sys.path:
    sys.path.insert(0, str(BENCHMARK_ROOT))

from translation_worthiness_falsifier import canonical_json  # noqa: E402

SCHEMA_VERSION = 1
FAMILY_WISE_ALPHA = 0.05
TARGET_FALSE_SUPPRESSION_UPPER = 0.005
PREREGISTERED_K = 4
PER_CELL_ALPHA = FAMILY_WISE_ALPHA / PREREGISTERED_K

# This is a bounded feasibility plan, not an activation decision.  Every other
# origin/script/length cell must retain.  Changing the set after locked-test
# access invalidates the experiment rather than changing this constant.
PREREGISTERED_CELLS = (
    "floss-decoded|mixed-script|32-127",
    "native-static|han|1-7",
    "native-static|latin|8-31",
    "ocr|latin|8-31",
)

SEVERITIES = ("critical", "high", "medium")
CELL_FIELDS = frozenset(
    {
        "cell",
        "independentPositiveGroups",
        "falseSuppressionGroups",
        "independenceDefensible",
        "lockedTest",
    }
)
PUBLIC_CORPUS_FIELDS = frozenset(
    {
        "massiveCleanPositivesOnly",
        "massiveAttributionRecorded",
        "massiveTestSeedGroups",
        "nlonAssetsPresent",
        "codeSearchNetRows",
        "codeSearchNetAllOriginalLicensedAndManual",
        "evaluationOnlyUsedForSelection",
        "crossSplitLeakageAuditComplete",
        "normalizedExactCrossSplitPairs",
        "relatedTemplateAuditComplete",
        "unresolvedRelatedTemplatePairs",
        "privateDataUsedForSelection",
        "privatePassInfluencedSelection",
    }
)
FASTTEXT_FIELDS = frozenset(
    {
        "implemented",
        "benchmarkOnly",
        "upstreamRevisionPinned",
        "sourceHashVerified",
        "executableProvenanceVerified",
        "licenseRecorded",
        "identicalTrainingManifest",
        "predictionsUsedAsLabels",
        "runtimeOrBundlePresent",
        "dualIndependentAgreementOnly",
        "adjudicatedConflictsExcluded",
        "calibrationLockedBeforeTest",
        "lockedTestLabelsHiddenFromScorer",
        "requiredLabelsPresentPerSplit",
        "reportsAggregateOnly",
        "outputsAtomicPhysicalAndExternal",
    }
)
WORKER_REPORT_FIELDS = frozenset(
    {
        "schemaVersion",
        "reportType",
        "researchOnly",
        "promotionEligible",
        "candidateArtifactBound",
        "lockedChallengerDecisionApplied",
        "productionWorkerImported",
        "publicOrSyntheticDataOnly",
        "privateDataPresent",
        "rawInputOccurrencesIdenticalAtStart",
        "cacheStatesIdenticalAtStart",
        "runCacheStatesIdenticalAtStart",
        "baseline",
        "candidate",
        "attribution",
        "reconciliation",
    }
)
WORKER_STATS_FIELDS = frozenset(
    {
        "candidateOccurrences",
        "textDecisions",
        "protectedOnlyBypassTexts",
        "runCacheHits",
        "persistentCacheHits",
        "translatorInputTexts",
        "translatorRequests",
        "modelResults",
        "translatedChildOccurrences",
    }
)
WORKER_ATTRIBUTION_FIELDS = frozenset(
    {
        "proposalsOnProtectedOnlyPath",
        "proposalsOnRunCachePath",
        "proposalsOnPersistentCachePath",
        "proposalsOnModelInputPath",
        "learnedOnlyTranslatorInputSavings",
        "protectedOnlySavingsCredited",
        "runCacheSavingsCredited",
        "persistentCacheSavingsCredited",
        "removedChildOccurrences",
    }
)
WORKER_RECONCILIATION_FIELDS = frozenset(
    {
        "baselineValid",
        "candidateValid",
        "protectedBucketUnchanged",
        "persistentCacheBucketUnchanged",
        "translatorDeltaEqualsLearnedOnly",
    }
)
ADJUDICATION_FIELDS = frozenset(
    {
        "acquisitionQueueSchemaCompatible",
        "sourceProjectIdentityCompatible",
        "duplicateAndNonfiniteJsonRejected",
        "outputsAtomicPhysicalAndExternal",
        "reviewerIdentityPrivacyControlled",
        "exactQueueAndTextBound",
        "automatedReviewRejected",
        "twoDistinctHumanReviews",
        "conflictOrLowUsesDistinctThirdHuman",
        "humanIdentityExternallyVerified",
        "projectDisjointSplit",
        "duplicateLinkedProjectsSplitTogether",
        "minimumSplitComponentsAvailable",
        "splitSelectionLabelIndependent",
    }
)


class EvidenceGateError(RuntimeError):
    """Raised when aggregate evidence is malformed rather than merely insufficient."""


@dataclass(frozen=True)
class CellEvidence:
    cell: str
    independent_positive_groups: int
    false_suppression_groups: int
    independence_defensible: bool
    locked_test: bool


@dataclass(frozen=True)
class PublicCorpusAudit:
    massive_clean_positives_only: bool
    massive_attribution_recorded: bool
    massive_test_seed_groups: int
    nlon_assets_present: bool
    code_search_net_rows: int
    code_search_net_all_original_licensed_and_manual: bool
    evaluation_only_used_for_selection: bool
    cross_split_leakage_audit_complete: bool
    normalized_exact_cross_split_pairs: int
    related_template_audit_complete: bool
    unresolved_related_template_pairs: int
    private_data_used_for_selection: bool
    private_pass_influenced_selection: bool


@dataclass(frozen=True)
class FastTextAudit:
    implemented: bool
    benchmark_only: bool
    upstream_revision_pinned: bool
    source_hash_verified: bool
    executable_provenance_verified: bool
    license_recorded: bool
    identical_training_manifest: bool
    predictions_used_as_labels: bool
    runtime_or_bundle_present: bool
    dual_independent_agreement_only: bool
    adjudicated_conflicts_excluded: bool
    calibration_locked_before_test: bool
    locked_test_labels_hidden_from_scorer: bool
    required_labels_present_per_split: bool
    reports_aggregate_only: bool
    outputs_atomic_physical_and_external: bool


@dataclass(frozen=True)
class AdjudicationAudit:
    acquisition_queue_schema_compatible: bool
    source_project_identity_compatible: bool
    duplicate_and_nonfinite_json_rejected: bool
    outputs_atomic_physical_and_external: bool
    reviewer_identity_privacy_controlled: bool
    exact_queue_and_text_bound: bool
    automated_review_rejected: bool
    two_distinct_human_reviews: bool
    conflict_or_low_uses_distinct_third_human: bool
    human_identity_externally_verified: bool
    project_disjoint_split: bool
    duplicate_linked_projects_split_together: bool
    minimum_split_components_available: bool
    split_selection_label_independent: bool


def zero_miss_upper_bound(independent_groups: int, alpha: float) -> float:
    if independent_groups <= 0 or not math.isfinite(alpha) or not 0 < alpha < 1:
        raise EvidenceGateError("Zero-miss bound inputs are invalid")
    return 1 - math.pow(alpha, 1 / independent_groups)


def minimum_independent_positive_groups(
    *,
    k: int = PREREGISTERED_K,
    family_wise_alpha: float = FAMILY_WISE_ALPHA,
    target_upper: float = TARGET_FALSE_SUPPRESSION_UPPER,
) -> int:
    """Return the minimum zero-miss group count for a Bonferroni cell."""

    if (
        type(k) is not int
        or k <= 0
        or not math.isfinite(family_wise_alpha)
        or not 0 < family_wise_alpha < 1
        or not math.isfinite(target_upper)
        or not 0 < target_upper < 1
    ):
        raise EvidenceGateError("Simultaneous planning inputs are invalid")
    per_cell_alpha = family_wise_alpha / k
    estimate = math.ceil(math.log(per_cell_alpha) / math.log(1 - target_upper))
    while zero_miss_upper_bound(estimate, per_cell_alpha) > target_upper:
        estimate += 1
    while estimate > 1 and zero_miss_upper_bound(estimate - 1, per_cell_alpha) <= target_upper:
        estimate -= 1
    return estimate


MINIMUM_GROUPS_PER_CELL = minimum_independent_positive_groups()


def _boolean(value: Any, *, name: str) -> bool:
    if type(value) is not bool:
        raise EvidenceGateError(f"{name} must be a boolean")
    return value


def _counter(value: Any, *, name: str) -> int:
    if type(value) is not int or value < 0:
        raise EvidenceGateError(f"{name} must be a nonnegative integer")
    return value


def load_cell_evidence(values: Sequence[Mapping[str, Any]]) -> tuple[CellEvidence, ...]:
    if not isinstance(values, Sequence) or isinstance(values, (str, bytes)):
        raise EvidenceGateError("Cell evidence must be a sequence")
    cells: list[CellEvidence] = []
    names: set[str] = set()
    for value in values:
        if not isinstance(value, Mapping) or frozenset(value) != CELL_FIELDS:
            raise EvidenceGateError("Cell evidence does not use the allowlist")
        cell = value["cell"]
        if not isinstance(cell, str) or cell not in PREREGISTERED_CELLS or cell in names:
            raise EvidenceGateError("Cell evidence has an unknown or duplicate cell")
        names.add(cell)
        cells.append(
            CellEvidence(
                cell=cell,
                independent_positive_groups=_counter(
                    value["independentPositiveGroups"],
                    name="independentPositiveGroups",
                ),
                false_suppression_groups=_counter(
                    value["falseSuppressionGroups"],
                    name="falseSuppressionGroups",
                ),
                independence_defensible=_boolean(
                    value["independenceDefensible"], name="independenceDefensible"
                ),
                locked_test=_boolean(value["lockedTest"], name="lockedTest"),
            )
        )
    if names != set(PREREGISTERED_CELLS):
        raise EvidenceGateError("Cell evidence does not exactly cover preregistered K")
    return tuple(sorted(cells, key=lambda item: item.cell))


def load_public_corpus_audit(value: Mapping[str, Any]) -> PublicCorpusAudit:
    if not isinstance(value, Mapping) or frozenset(value) != PUBLIC_CORPUS_FIELDS:
        raise EvidenceGateError("Public-corpus audit does not use the allowlist")
    return PublicCorpusAudit(
        massive_clean_positives_only=_boolean(
            value["massiveCleanPositivesOnly"], name="massiveCleanPositivesOnly"
        ),
        massive_attribution_recorded=_boolean(
            value["massiveAttributionRecorded"], name="massiveAttributionRecorded"
        ),
        massive_test_seed_groups=_counter(
            value["massiveTestSeedGroups"], name="massiveTestSeedGroups"
        ),
        nlon_assets_present=_boolean(value["nlonAssetsPresent"], name="nlonAssetsPresent"),
        code_search_net_rows=_counter(value["codeSearchNetRows"], name="codeSearchNetRows"),
        code_search_net_all_original_licensed_and_manual=_boolean(
            value["codeSearchNetAllOriginalLicensedAndManual"],
            name="codeSearchNetAllOriginalLicensedAndManual",
        ),
        evaluation_only_used_for_selection=_boolean(
            value["evaluationOnlyUsedForSelection"],
            name="evaluationOnlyUsedForSelection",
        ),
        cross_split_leakage_audit_complete=_boolean(
            value["crossSplitLeakageAuditComplete"],
            name="crossSplitLeakageAuditComplete",
        ),
        normalized_exact_cross_split_pairs=_counter(
            value["normalizedExactCrossSplitPairs"],
            name="normalizedExactCrossSplitPairs",
        ),
        related_template_audit_complete=_boolean(
            value["relatedTemplateAuditComplete"],
            name="relatedTemplateAuditComplete",
        ),
        unresolved_related_template_pairs=_counter(
            value["unresolvedRelatedTemplatePairs"],
            name="unresolvedRelatedTemplatePairs",
        ),
        private_data_used_for_selection=_boolean(
            value["privateDataUsedForSelection"], name="privateDataUsedForSelection"
        ),
        private_pass_influenced_selection=_boolean(
            value["privatePassInfluencedSelection"],
            name="privatePassInfluencedSelection",
        ),
    )


def load_fasttext_audit(value: Mapping[str, Any]) -> FastTextAudit:
    if not isinstance(value, Mapping) or frozenset(value) != FASTTEXT_FIELDS:
        raise EvidenceGateError("fastText audit does not use the allowlist")
    return FastTextAudit(
        implemented=_boolean(value["implemented"], name="implemented"),
        benchmark_only=_boolean(value["benchmarkOnly"], name="benchmarkOnly"),
        upstream_revision_pinned=_boolean(
            value["upstreamRevisionPinned"], name="upstreamRevisionPinned"
        ),
        source_hash_verified=_boolean(value["sourceHashVerified"], name="sourceHashVerified"),
        executable_provenance_verified=_boolean(
            value["executableProvenanceVerified"],
            name="executableProvenanceVerified",
        ),
        license_recorded=_boolean(value["licenseRecorded"], name="licenseRecorded"),
        identical_training_manifest=_boolean(
            value["identicalTrainingManifest"], name="identicalTrainingManifest"
        ),
        predictions_used_as_labels=_boolean(
            value["predictionsUsedAsLabels"], name="predictionsUsedAsLabels"
        ),
        runtime_or_bundle_present=_boolean(
            value["runtimeOrBundlePresent"], name="runtimeOrBundlePresent"
        ),
        dual_independent_agreement_only=_boolean(
            value["dualIndependentAgreementOnly"],
            name="dualIndependentAgreementOnly",
        ),
        adjudicated_conflicts_excluded=_boolean(
            value["adjudicatedConflictsExcluded"],
            name="adjudicatedConflictsExcluded",
        ),
        calibration_locked_before_test=_boolean(
            value["calibrationLockedBeforeTest"],
            name="calibrationLockedBeforeTest",
        ),
        locked_test_labels_hidden_from_scorer=_boolean(
            value["lockedTestLabelsHiddenFromScorer"],
            name="lockedTestLabelsHiddenFromScorer",
        ),
        required_labels_present_per_split=_boolean(
            value["requiredLabelsPresentPerSplit"],
            name="requiredLabelsPresentPerSplit",
        ),
        reports_aggregate_only=_boolean(value["reportsAggregateOnly"], name="reportsAggregateOnly"),
        outputs_atomic_physical_and_external=_boolean(
            value["outputsAtomicPhysicalAndExternal"],
            name="outputsAtomicPhysicalAndExternal",
        ),
    )


def load_adjudication_audit(value: Mapping[str, Any]) -> AdjudicationAudit:
    if not isinstance(value, Mapping) or frozenset(value) != ADJUDICATION_FIELDS:
        raise EvidenceGateError("Adjudication audit does not use the allowlist")
    return AdjudicationAudit(
        acquisition_queue_schema_compatible=_boolean(
            value["acquisitionQueueSchemaCompatible"],
            name="acquisitionQueueSchemaCompatible",
        ),
        source_project_identity_compatible=_boolean(
            value["sourceProjectIdentityCompatible"],
            name="sourceProjectIdentityCompatible",
        ),
        duplicate_and_nonfinite_json_rejected=_boolean(
            value["duplicateAndNonfiniteJsonRejected"],
            name="duplicateAndNonfiniteJsonRejected",
        ),
        outputs_atomic_physical_and_external=_boolean(
            value["outputsAtomicPhysicalAndExternal"],
            name="outputsAtomicPhysicalAndExternal",
        ),
        reviewer_identity_privacy_controlled=_boolean(
            value["reviewerIdentityPrivacyControlled"],
            name="reviewerIdentityPrivacyControlled",
        ),
        exact_queue_and_text_bound=_boolean(
            value["exactQueueAndTextBound"], name="exactQueueAndTextBound"
        ),
        automated_review_rejected=_boolean(
            value["automatedReviewRejected"], name="automatedReviewRejected"
        ),
        two_distinct_human_reviews=_boolean(
            value["twoDistinctHumanReviews"], name="twoDistinctHumanReviews"
        ),
        conflict_or_low_uses_distinct_third_human=_boolean(
            value["conflictOrLowUsesDistinctThirdHuman"],
            name="conflictOrLowUsesDistinctThirdHuman",
        ),
        human_identity_externally_verified=_boolean(
            value["humanIdentityExternallyVerified"],
            name="humanIdentityExternallyVerified",
        ),
        project_disjoint_split=_boolean(value["projectDisjointSplit"], name="projectDisjointSplit"),
        duplicate_linked_projects_split_together=_boolean(
            value["duplicateLinkedProjectsSplitTogether"],
            name="duplicateLinkedProjectsSplitTogether",
        ),
        minimum_split_components_available=_boolean(
            value["minimumSplitComponentsAvailable"],
            name="minimumSplitComponentsAvailable",
        ),
        split_selection_label_independent=_boolean(
            value["splitSelectionLabelIndependent"],
            name="splitSelectionLabelIndependent",
        ),
    )


def validate_worker_attribution(report: Mapping[str, Any]) -> bool:
    """Reconcile an aggregate report produced by the actual Python worker replay."""

    try:
        if (
            not isinstance(report, Mapping)
            or frozenset(report) != WORKER_REPORT_FIELDS
            or report.get("schemaVersion") != SCHEMA_VERSION
            or report.get("reportType") != "translation-worthiness-paired-actual-worker"
            or report.get("researchOnly") is not True
            or report.get("promotionEligible") is not False
            or report.get("candidateArtifactBound") is not True
            or report.get("lockedChallengerDecisionApplied") is not True
            or report.get("productionWorkerImported") is not True
            or report.get("publicOrSyntheticDataOnly") is not True
            or report.get("privateDataPresent") is not False
            or report.get("rawInputOccurrencesIdenticalAtStart") is not True
            or report.get("cacheStatesIdenticalAtStart") is not True
            or report.get("runCacheStatesIdenticalAtStart") is not True
        ):
            return False
        baseline = report["baseline"]
        candidate = report["candidate"]
        attribution = report["attribution"]
        if not all(isinstance(item, Mapping) for item in (baseline, candidate, attribution)):
            return False
        if (
            frozenset(baseline) != WORKER_STATS_FIELDS
            or frozenset(candidate) != WORKER_STATS_FIELDS
            or frozenset(attribution) != WORKER_ATTRIBUTION_FIELDS
            or not isinstance(report.get("reconciliation"), Mapping)
            or frozenset(report["reconciliation"]) != WORKER_RECONCILIATION_FIELDS
            or any(value is not True for value in report["reconciliation"].values())
        ):
            return False

        def decision_reconciles(stats: Mapping[str, Any]) -> bool:
            counters = (
                "candidateOccurrences",
                "textDecisions",
                "protectedOnlyBypassTexts",
                "runCacheHits",
                "persistentCacheHits",
                "translatorInputTexts",
                "translatorRequests",
                "modelResults",
                "translatedChildOccurrences",
            )
            if any(type(stats.get(name)) is not int or stats[name] < 0 for name in counters):
                return False
            return (
                stats["textDecisions"]
                == stats["protectedOnlyBypassTexts"]
                + stats["runCacheHits"]
                + stats["persistentCacheHits"]
                + stats["translatorInputTexts"]
                and stats["modelResults"] == stats["translatorInputTexts"]
                and stats["translatorRequests"] <= stats["translatorInputTexts"]
                and stats["textDecisions"] <= stats["candidateOccurrences"]
                and stats["translatedChildOccurrences"] <= stats["candidateOccurrences"]
            )

        learned = attribution["learnedOnlyTranslatorInputSavings"]
        removed_children = attribution["removedChildOccurrences"]
        return (
            decision_reconciles(baseline)
            and decision_reconciles(candidate)
            and type(learned) is int
            and learned >= 0
            and learned == baseline["translatorInputTexts"] - candidate["translatorInputTexts"]
            and type(removed_children) is int
            and removed_children >= 0
            and removed_children
            == baseline["translatedChildOccurrences"] - candidate["translatedChildOccurrences"]
            and attribution.get("protectedOnlySavingsCredited") == 0
            and attribution.get("runCacheSavingsCredited") == 0
            and attribution.get("persistentCacheSavingsCredited") == 0
            and attribution.get("proposalsOnProtectedOnlyPath", 0) > 0
            and attribution.get("proposalsOnRunCachePath", 0) > 0
            and attribution.get("proposalsOnPersistentCachePath", 0) > 0
            and attribution.get("proposalsOnModelInputPath", 0) > 0
        )
    except (KeyError, TypeError):
        return False


def _issue(issues: list[tuple[str, str]], severity: str, code: str) -> None:
    issues.append((severity, code))


def evaluate_evidence(
    *,
    cells: Sequence[Mapping[str, Any]],
    public_corpus: Mapping[str, Any],
    fasttext: Mapping[str, Any],
    adjudication: Mapping[str, Any],
    mandatory_false_suppressions: int,
    worker_report: Mapping[str, Any] | None = None,
    worker_candidate_specific: bool = False,
) -> dict[str, Any]:
    """Evaluate aggregate feasibility and safety evidence without granting authority."""

    cell_evidence = load_cell_evidence(cells)
    corpus = load_public_corpus_audit(public_corpus)
    challenger = load_fasttext_audit(fasttext)
    review = load_adjudication_audit(adjudication)
    mandatory_misses = _counter(mandatory_false_suppressions, name="mandatoryFalseSuppressions")
    candidate_specific = _boolean(worker_candidate_specific, name="workerCandidateSpecific")
    worker_valid = worker_report is not None and validate_worker_attribution(worker_report)
    issues: list[tuple[str, str]] = []

    cells_with_zero_misses = sum(cell.false_suppression_groups == 0 for cell in cell_evidence)
    cells_with_enough_groups = sum(
        cell.independent_positive_groups >= MINIMUM_GROUPS_PER_CELL for cell in cell_evidence
    )
    cells_with_defensible_independence = sum(cell.independence_defensible for cell in cell_evidence)
    cells_on_locked_test = sum(cell.locked_test for cell in cell_evidence)
    if mandatory_misses:
        _issue(issues, "critical", "mandatory-human-or-mixed-suppression")
    if cells_with_zero_misses != PREREGISTERED_K:
        _issue(issues, "critical", "positive-group-false-suppression")
    if cells_with_defensible_independence != PREREGISTERED_K:
        _issue(issues, "critical", "independence-not-defensible")
    if cells_on_locked_test != PREREGISTERED_K:
        _issue(issues, "critical", "phase-isolation-failure")
    if cells_with_enough_groups != PREREGISTERED_K:
        _issue(issues, "high", "insufficient-independent-positive-groups")

    if not corpus.massive_clean_positives_only or not corpus.massive_attribution_recorded:
        _issue(issues, "critical", "massive-use-or-attribution-violation")
    if corpus.nlon_assets_present:
        _issue(issues, "critical", "nlon-gpl-asset-present")
    if corpus.code_search_net_rows and not corpus.code_search_net_all_original_licensed_and_manual:
        _issue(issues, "critical", "codesearchnet-source-license-or-label-failure")
    if corpus.evaluation_only_used_for_selection:
        _issue(issues, "critical", "evaluation-only-selection-leak")
    if corpus.private_data_used_for_selection or corpus.private_pass_influenced_selection:
        _issue(issues, "critical", "private-data-selection-or-approval")
    if not corpus.cross_split_leakage_audit_complete:
        _issue(issues, "critical", "cross-split-leakage-audit-incomplete")
    if corpus.normalized_exact_cross_split_pairs:
        _issue(issues, "critical", "normalized-exact-cross-split-leak")
    if not corpus.related_template_audit_complete:
        _issue(issues, "critical", "related-template-audit-incomplete")
    if corpus.unresolved_related_template_pairs:
        _issue(issues, "critical", "unresolved-related-template-leak")
    if corpus.massive_test_seed_groups < MINIMUM_GROUPS_PER_CELL:
        _issue(issues, "high", "massive-test-groups-below-one-cell-plan")

    if not challenger.implemented:
        _issue(issues, "high", "fasttext-challenger-not-implemented")
    if not challenger.benchmark_only or challenger.runtime_or_bundle_present:
        _issue(issues, "critical", "fasttext-product-boundary-violation")
    if not (
        challenger.upstream_revision_pinned
        and challenger.source_hash_verified
        and challenger.executable_provenance_verified
        and challenger.license_recorded
    ):
        _issue(issues, "high", "fasttext-supply-chain-incomplete")
    if not challenger.identical_training_manifest:
        _issue(issues, "critical", "challenger-training-manifest-drift")
    if challenger.predictions_used_as_labels:
        _issue(issues, "critical", "challenger-predictions-used-as-labels")
    if not (
        challenger.dual_independent_agreement_only and challenger.adjudicated_conflicts_excluded
    ):
        _issue(issues, "critical", "fasttext-adjudication-contract-incomplete")
    if not (
        challenger.calibration_locked_before_test
        and challenger.locked_test_labels_hidden_from_scorer
    ):
        _issue(issues, "critical", "fasttext-phase-isolation-incomplete")
    if not challenger.required_labels_present_per_split:
        _issue(issues, "high", "fasttext-split-label-support-incomplete")
    if not challenger.reports_aggregate_only:
        _issue(issues, "critical", "fasttext-nonaggregate-report")
    if not challenger.outputs_atomic_physical_and_external:
        _issue(issues, "critical", "fasttext-output-inside-repo-or-reparse")
    if not worker_valid:
        _issue(issues, "critical", "actual-worker-attribution-invalid")
    if not candidate_specific:
        _issue(issues, "high", "candidate-specific-worker-replay-missing")

    if not review.acquisition_queue_schema_compatible:
        _issue(issues, "critical", "queue-schema-mismatch")
    if not review.source_project_identity_compatible:
        _issue(issues, "critical", "source-project-identity-incompatible")
    if not review.duplicate_and_nonfinite_json_rejected:
        _issue(issues, "critical", "unsafe-json-accepted")
    if not review.outputs_atomic_physical_and_external:
        _issue(issues, "critical", "output-inside-repo-or-reparse")
    if not review.reviewer_identity_privacy_controlled:
        _issue(issues, "critical", "reviewer-identity-privacy-uncontrolled")
    if not (
        review.exact_queue_and_text_bound
        and review.automated_review_rejected
        and review.two_distinct_human_reviews
        and review.conflict_or_low_uses_distinct_third_human
    ):
        _issue(issues, "critical", "human-review-contract-incomplete")
    if not review.human_identity_externally_verified:
        _issue(issues, "critical", "reviewer-independence-self-attested")
    if not review.project_disjoint_split or not review.split_selection_label_independent:
        _issue(issues, "critical", "project-disjoint-split-contract-failure")
    if not review.duplicate_linked_projects_split_together:
        _issue(issues, "critical", "duplicate-linked-project-split-unresolved")
    if not review.minimum_split_components_available:
        _issue(issues, "high", "insufficient-duplicate-isolated-project-components")

    issue_counts = Counter(severity for severity, _ in issues)
    research_feasible = not issues
    return {
        "schemaVersion": SCHEMA_VERSION,
        "reportType": "translation-worthiness-evidence-gate",
        "researchOnly": True,
        "promotionEligible": False,
        "activationEligible": False,
        "verdict": "research-feasible" if research_feasible else "insufficient-evidence",
        "planning": {
            "familyWiseAlpha": FAMILY_WISE_ALPHA,
            "method": "bonferroni",
            "preregisteredK": PREREGISTERED_K,
            "perCellAlpha": PER_CELL_ALPHA,
            "targetFalseSuppressionUpper": TARGET_FALSE_SUPPRESSION_UPPER,
            "minimumIndependentPositiveGroupsPerCell": MINIMUM_GROUPS_PER_CELL,
        },
        "cellGate": {
            "cellsEvaluated": len(cell_evidence),
            "cellsWithZeroFalseSuppressions": cells_with_zero_misses,
            "cellsWithEnoughIndependentPositiveGroups": cells_with_enough_groups,
            "cellsWithDefensibleIndependence": cells_with_defensible_independence,
            "cellsOnLockedTest": cells_on_locked_test,
        },
        "mandatoryFalseSuppressions": mandatory_misses,
        "actualWorkerAttributionValid": worker_valid,
        "candidateSpecificWorkerReplay": candidate_specific,
        "issueCountsBySeverity": {severity: issue_counts[severity] for severity in SEVERITIES},
        "issueCodes": sorted({code for _, code in issues}),
    }


def canonical_report(value: Mapping[str, Any]) -> str:
    return canonical_json(dict(value))
