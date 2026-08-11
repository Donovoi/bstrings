from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path

BENCHMARK_ROOT = Path(__file__).resolve().parents[1] / "benchmarks"
if str(BENCHMARK_ROOT) not in sys.path:
    sys.path.insert(0, str(BENCHMARK_ROOT))

from translation_worthiness_evidence_gate import (  # noqa: E402
    MINIMUM_GROUPS_PER_CELL,
    PREREGISTERED_CELLS,
    EvidenceGateError,
    canonical_report,
    evaluate_evidence,
    minimum_independent_positive_groups,
    validate_worker_attribution,
)
from translation_worthiness_falsifier import paired_fake_translator_report  # noqa: E402


class TranslationWorthinessEvidenceGateTests(unittest.TestCase):
    @staticmethod
    def cells(groups: int = MINIMUM_GROUPS_PER_CELL) -> list[dict[str, object]]:
        return [
            {
                "cell": cell,
                "independentPositiveGroups": groups,
                "falseSuppressionGroups": 0,
                "independenceDefensible": True,
                "lockedTest": True,
            }
            for cell in PREREGISTERED_CELLS
        ]

    @staticmethod
    def public_corpus(groups: int = MINIMUM_GROUPS_PER_CELL) -> dict[str, object]:
        return {
            "massiveCleanPositivesOnly": True,
            "massiveAttributionRecorded": True,
            "massiveTestSeedGroups": groups,
            "nlonAssetsPresent": False,
            "codeSearchNetRows": 0,
            "codeSearchNetAllOriginalLicensedAndManual": True,
            "evaluationOnlyUsedForSelection": False,
            "crossSplitLeakageAuditComplete": True,
            "normalizedExactCrossSplitPairs": 0,
            "relatedTemplateAuditComplete": True,
            "unresolvedRelatedTemplatePairs": 0,
            "privateDataUsedForSelection": False,
            "privatePassInfluencedSelection": False,
        }

    @staticmethod
    def fasttext(*, implemented: bool = True) -> dict[str, object]:
        return {
            "implemented": implemented,
            "benchmarkOnly": True,
            "upstreamRevisionPinned": True,
            "sourceHashVerified": True,
            "executableProvenanceVerified": True,
            "licenseRecorded": True,
            "identicalTrainingManifest": True,
            "predictionsUsedAsLabels": False,
            "runtimeOrBundlePresent": False,
            "dualIndependentAgreementOnly": True,
            "adjudicatedConflictsExcluded": True,
            "calibrationLockedBeforeTest": True,
            "lockedTestLabelsHiddenFromScorer": True,
            "requiredLabelsPresentPerSplit": True,
            "reportsAggregateOnly": True,
            "outputsAtomicPhysicalAndExternal": True,
        }

    @staticmethod
    def actual_worker() -> dict[str, object]:
        report = paired_fake_translator_report()
        report.update(
            {
                "reportType": "translation-worthiness-paired-actual-worker",
                "candidateArtifactBound": True,
                "lockedChallengerDecisionApplied": True,
                "productionWorkerImported": True,
                "publicOrSyntheticDataOnly": True,
                "privateDataPresent": False,
                "rawInputOccurrencesIdenticalAtStart": True,
            }
        )
        return report

    @staticmethod
    def adjudication() -> dict[str, object]:
        return {
            "acquisitionQueueSchemaCompatible": True,
            "sourceProjectIdentityCompatible": True,
            "duplicateAndNonfiniteJsonRejected": True,
            "outputsAtomicPhysicalAndExternal": True,
            "reviewerIdentityPrivacyControlled": True,
            "exactQueueAndTextBound": True,
            "automatedReviewRejected": True,
            "twoDistinctHumanReviews": True,
            "conflictOrLowUsesDistinctThirdHuman": True,
            "humanIdentityExternallyVerified": True,
            "projectDisjointSplit": True,
            "duplicateLinkedProjectsSplitTogether": True,
            "minimumSplitComponentsAvailable": True,
            "splitSelectionLabelIndependent": True,
        }

    def evaluate(
        self,
        *,
        cells: list[dict[str, object]] | None = None,
        public_corpus: dict[str, object] | None = None,
        fasttext: dict[str, object] | None = None,
        adjudication: dict[str, object] | None = None,
        mandatory_false_suppressions: int = 0,
        worker_report: dict[str, object] | None = None,
        worker_candidate_specific: bool = True,
    ) -> dict[str, object]:
        return evaluate_evidence(
            cells=cells or self.cells(),
            public_corpus=public_corpus or self.public_corpus(),
            fasttext=fasttext or self.fasttext(),
            adjudication=adjudication or self.adjudication(),
            mandatory_false_suppressions=mandatory_false_suppressions,
            worker_report=worker_report if worker_report is not None else self.actual_worker(),
            worker_candidate_specific=worker_candidate_specific,
        )

    def test_zero_miss_simultaneous_planning_numbers_are_exact(self) -> None:
        self.assertEqual(598, minimum_independent_positive_groups(k=1))
        self.assertEqual(736, minimum_independent_positive_groups(k=2))
        self.assertEqual(817, minimum_independent_positive_groups(k=3))
        self.assertEqual(875, minimum_independent_positive_groups(k=4))
        self.assertEqual(919, minimum_independent_positive_groups(k=5))
        self.assertEqual(956, minimum_independent_positive_groups(k=6))

    def test_complete_evidence_is_research_feasible_but_never_activation_eligible(self) -> None:
        report = self.evaluate()

        self.assertEqual("research-feasible", report["verdict"])
        self.assertTrue(report["researchOnly"])
        self.assertFalse(report["promotionEligible"])
        self.assertFalse(report["activationEligible"])
        self.assertEqual(4, report["planning"]["preregisteredK"])
        self.assertEqual(875, report["planning"]["minimumIndependentPositiveGroupsPerCell"])
        self.assertEqual(
            {"critical": 0, "high": 0, "medium": 0},
            report["issueCountsBySeverity"],
        )

    def test_default_massive_512_groups_cannot_satisfy_even_the_four_cell_plan(self) -> None:
        report = self.evaluate(
            cells=self.cells(512),
            public_corpus=self.public_corpus(512),
            fasttext=self.fasttext(implemented=False),
            worker_candidate_specific=False,
        )

        self.assertEqual("insufficient-evidence", report["verdict"])
        self.assertEqual(0, report["cellGate"]["cellsWithEnoughIndependentPositiveGroups"])
        self.assertIn("insufficient-independent-positive-groups", report["issueCodes"])
        self.assertIn("massive-test-groups-below-one-cell-plan", report["issueCodes"])
        self.assertIn("fasttext-challenger-not-implemented", report["issueCodes"])
        self.assertIn("candidate-specific-worker-replay-missing", report["issueCodes"])
        self.assertGreaterEqual(report["issueCountsBySeverity"]["high"], 4)
        self.assertFalse(report["activationEligible"])

    def test_any_mandatory_or_group_false_suppression_is_critical(self) -> None:
        cells = self.cells()
        cells[0]["falseSuppressionGroups"] = 1

        report = self.evaluate(cells=cells, mandatory_false_suppressions=1)

        self.assertIn("mandatory-human-or-mixed-suppression", report["issueCodes"])
        self.assertIn("positive-group-false-suppression", report["issueCodes"])
        self.assertGreaterEqual(report["issueCountsBySeverity"]["critical"], 2)
        self.assertEqual("insufficient-evidence", report["verdict"])

    def test_related_template_license_phase_and_private_leaks_are_falsifiers(self) -> None:
        public = self.public_corpus()
        public.update(
            {
                "nlonAssetsPresent": True,
                "codeSearchNetRows": 1,
                "codeSearchNetAllOriginalLicensedAndManual": False,
                "evaluationOnlyUsedForSelection": True,
                "crossSplitLeakageAuditComplete": False,
                "normalizedExactCrossSplitPairs": 1,
                "relatedTemplateAuditComplete": False,
                "unresolvedRelatedTemplatePairs": 2,
                "privateDataUsedForSelection": True,
                "privatePassInfluencedSelection": True,
            }
        )
        cells = self.cells()
        cells[1]["lockedTest"] = False
        cells[2]["independenceDefensible"] = False

        report = self.evaluate(cells=cells, public_corpus=public)

        expected = {
            "codesearchnet-source-license-or-label-failure",
            "cross-split-leakage-audit-incomplete",
            "evaluation-only-selection-leak",
            "independence-not-defensible",
            "nlon-gpl-asset-present",
            "normalized-exact-cross-split-leak",
            "phase-isolation-failure",
            "private-data-selection-or-approval",
            "related-template-audit-incomplete",
            "unresolved-related-template-leak",
        }
        self.assertTrue(expected.issubset(report["issueCodes"]))
        self.assertGreaterEqual(report["issueCountsBySeverity"]["critical"], len(expected))

    def test_fasttext_supply_chain_teacher_and_product_boundary_are_falsifiers(self) -> None:
        fasttext = self.fasttext()
        fasttext.update(
            {
                "benchmarkOnly": False,
                "upstreamRevisionPinned": False,
                "sourceHashVerified": False,
                "executableProvenanceVerified": False,
                "licenseRecorded": False,
                "identicalTrainingManifest": False,
                "predictionsUsedAsLabels": True,
                "runtimeOrBundlePresent": True,
                "dualIndependentAgreementOnly": False,
                "adjudicatedConflictsExcluded": False,
                "calibrationLockedBeforeTest": False,
                "lockedTestLabelsHiddenFromScorer": False,
                "requiredLabelsPresentPerSplit": False,
                "reportsAggregateOnly": False,
                "outputsAtomicPhysicalAndExternal": False,
            }
        )

        report = self.evaluate(fasttext=fasttext)

        self.assertIn("fasttext-product-boundary-violation", report["issueCodes"])
        self.assertIn("fasttext-supply-chain-incomplete", report["issueCodes"])
        self.assertIn("challenger-training-manifest-drift", report["issueCodes"])
        self.assertIn("challenger-predictions-used-as-labels", report["issueCodes"])
        self.assertIn("fasttext-adjudication-contract-incomplete", report["issueCodes"])
        self.assertIn("fasttext-phase-isolation-incomplete", report["issueCodes"])
        self.assertIn("fasttext-split-label-support-incomplete", report["issueCodes"])
        self.assertIn("fasttext-nonaggregate-report", report["issueCodes"])
        self.assertIn("fasttext-output-inside-repo-or-reparse", report["issueCodes"])
        self.assertFalse(report["activationEligible"])

    def test_adjudication_schema_json_atomicity_privacy_and_human_contract_are_gated(self) -> None:
        adjudication = self.adjudication()
        adjudication.update(
            {
                "acquisitionQueueSchemaCompatible": False,
                "sourceProjectIdentityCompatible": False,
                "duplicateAndNonfiniteJsonRejected": False,
                "outputsAtomicPhysicalAndExternal": False,
                "reviewerIdentityPrivacyControlled": False,
                "exactQueueAndTextBound": False,
                "automatedReviewRejected": False,
                "twoDistinctHumanReviews": False,
                "conflictOrLowUsesDistinctThirdHuman": False,
                "humanIdentityExternallyVerified": False,
                "projectDisjointSplit": False,
                "duplicateLinkedProjectsSplitTogether": False,
                "minimumSplitComponentsAvailable": False,
                "splitSelectionLabelIndependent": False,
            }
        )

        report = self.evaluate(adjudication=adjudication)

        expected = {
            "human-review-contract-incomplete",
            "insufficient-duplicate-isolated-project-components",
            "duplicate-linked-project-split-unresolved",
            "output-inside-repo-or-reparse",
            "project-disjoint-split-contract-failure",
            "queue-schema-mismatch",
            "reviewer-identity-privacy-uncontrolled",
            "reviewer-independence-self-attested",
            "source-project-identity-incompatible",
            "unsafe-json-accepted",
        }
        self.assertTrue(expected.issubset(report["issueCodes"]))
        self.assertGreaterEqual(report["issueCountsBySeverity"]["critical"], 9)
        self.assertGreaterEqual(report["issueCountsBySeverity"]["high"], 1)

    def test_actual_worker_replay_reconciles_and_tampering_fails(self) -> None:
        worker = self.actual_worker()
        self.assertTrue(validate_worker_attribution(worker))

        for mutator in (
            lambda item: item["candidate"].__setitem__("translatorInputTexts", 2),
            lambda item: item["attribution"].__setitem__("persistentCacheSavingsCredited", 1),
            lambda item: item.__setitem__("cacheStatesIdenticalAtStart", False),
        ):
            changed = copy.deepcopy(worker)
            mutator(changed)
            with self.subTest(changed=changed):
                self.assertFalse(validate_worker_attribution(changed))
                report = self.evaluate(worker_report=changed)
                self.assertIn("actual-worker-attribution-invalid", report["issueCodes"])

    def test_fake_replay_cannot_masquerade_as_candidate_specific_actual_worker_evidence(
        self,
    ) -> None:
        fake = paired_fake_translator_report()

        self.assertFalse(validate_worker_attribution(fake))
        report = self.evaluate(worker_report=fake, worker_candidate_specific=True)

        self.assertIn("actual-worker-attribution-invalid", report["issueCodes"])
        self.assertEqual("insufficient-evidence", report["verdict"])

    def test_aggregate_report_excludes_cells_text_paths_and_hashes(self) -> None:
        report = self.evaluate()
        serialized = canonical_report(report)

        self.assertNotIn('"cell"', serialized)
        self.assertNotIn('"text"', serialized)
        self.assertNotIn("native-static|latin", serialized)
        self.assertNotIn("C:\\", serialized)
        self.assertNotIn("sha256", serialized.casefold())

    def test_malformed_or_incomplete_aggregate_inputs_are_rejected(self) -> None:
        cells = self.cells()
        cells.pop()
        with self.assertRaisesRegex(EvidenceGateError, "preregistered K"):
            self.evaluate(cells=cells)

        public = self.public_corpus()
        public["unexpected"] = True
        with self.assertRaisesRegex(EvidenceGateError, "allowlist"):
            self.evaluate(public_corpus=public)

        fasttext = self.fasttext()
        fasttext["implemented"] = 1
        with self.assertRaisesRegex(EvidenceGateError, "boolean"):
            self.evaluate(fasttext=fasttext)


if __name__ == "__main__":
    unittest.main()
