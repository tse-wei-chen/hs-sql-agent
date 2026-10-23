import type {
  DmlReconciliationItem,
  DmlReconciliationRequest,
  DmlReconciliationResolution,
} from "@/api/dml-reconciliation";

const unknownOutcomeChoices: DmlReconciliationResolution[] = [
  "committed", "not_committed", "indeterminate",
];

// UI decisions are intentionally narrower than the server-supplied set.
// No future/unknown state or fabricated evidence classification can create
// a reconciliation action; server-side authorization remains authoritative.
export function permittedDmlResolutions(
  item: DmlReconciliationItem | null,
  matchingCommitWitness = false,
): DmlReconciliationResolution[] {
  if (!item || !Array.isArray(item.allowedResolutions)) return [];
  let safe: DmlReconciliationResolution[] = [];
  if (
    item.commitEvidence === "TargetCommitAcknowledged" &&
    (item.status === "OutcomeUnknown" || item.status === "ApprovalStateUnknown")
  ) {
    safe = ["committed"];
  } else if (
    ((item.commitEvidence === "PhysicalCommitUnknown" &&
      item.status === "OutcomeUnknown") ||
      (item.commitEvidence === "RollbackUnconfirmed" &&
        item.status === "RollbackOutcomeUnknown"))
  ) {
    safe = unknownOutcomeChoices;
  }
  return safe.filter(
    (value) => item.allowedResolutions.includes(value)
      && (!matchingCommitWitness || value === "committed"),
  );
}

export function hasExplicitTimezone(value: string): boolean {
  return /(?:Z|[+-]\d{2}:\d{2})$/i.test(value) && Number.isFinite(Date.parse(value));
}

export interface DmlReconciliationForm {
  resolution: DmlReconciliationResolution | "";
  evidenceReference: string;
  note: string;
  confirmation: string;
  confirmedIndependentReview: boolean;
}

export function canSubmitDmlReconciliation(
  item: DmlReconciliationItem | null,
  form: DmlReconciliationForm,
  matchingCommitWitness = false,
): boolean {
  if (!item || !hasExplicitTimezone(item.updatedAt)) return false;
  if (!form.confirmedIndependentReview || form.confirmation !== item.requestId) return false;
  const evidence = form.evidenceReference.trim();
  const note = form.note.trim();
  if (!evidence || evidence.length > 256 || !note || note.length > 700) return false;
  return permittedDmlResolutions(item, matchingCommitWitness)
    .some((choice) => choice === form.resolution);
}

export function buildDmlReconciliationRequest(
  item: DmlReconciliationItem,
  form: DmlReconciliationForm,
  matchingCommitWitness = false,
): DmlReconciliationRequest {
  if (!canSubmitDmlReconciliation(item, form, matchingCommitWitness)
      || form.resolution === "") {
    throw new Error("Operator reconciliation evidence and explicit confirmation are required.");
  }
  return {
    expectedStatus: item.status,
    expectedUpdatedAt: item.updatedAt,
    resolution: form.resolution,
    evidenceReference: form.evidenceReference.trim(),
    note: form.note.trim(),
  };
}

export function dmlWitnessStatusDescription(status: string): string {
  switch (status) {
    case "CommittedWitnessMatch":
      return "Matching committed witness observed in the bound target database. This is positive COMMIT evidence, not worker fencing.";
    case "NotObserved":
      return "No committed witness observed. This does NOT establish rollback, non-commit or worker quiescence.";
    case "EvidenceMismatch":
      return "The target witness does not match persisted approval evidence. Investigate possible table tampering or identity mismatch.";
    case "TargetConfigurationChanged":
      return "The current target configuration no longer matches the original encrypted approval binding; no target probe was performed.";
    case "WitnessNotConfigured":
      return "Target witness verification is not enabled for this host.";
    case "ReadUnavailable":
      return "Target witness could not be read. Its physical transaction outcome remains unknown.";
    case "ApprovalChanged":
      return "The approval changed during inspection. Refresh before acting.";
    default:
      return "Unrecognized witness evidence. No physical transaction conclusion can be drawn.";
  }
}

export function dmlCommitEvidenceDescription(evidence: string): string {
  switch (evidence) {
    case "TargetCommitAcknowledged":
      return "Target COMMIT was acknowledged. Only a committed attestation is possible.";
    case "PhysicalCommitUnknown":
      return "The target COMMIT may or may not have completed. Review independent evidence.";
    case "WorkerFencingRequired":
      return "Execution may still be running. No settlement is allowed without real worker fencing.";
    case "RollbackUnconfirmed":
      return "A failed DML attempt could not confirm rollback. Inspect independent target evidence; do not retry the consumed approval.";
    default:
      return "Unknown evidence classification. Settlement is unavailable.";
  }
}
