# Immutable physical-storage baseline registry v1

The committed registry is the sole promotable selector. Approval authority is a reviewed
Groundwork merge commit on `main`, supplied to validation as an independently trusted commit set.
The generation binds that authority to its exact source commit/tree and immutable run-group and
artifact content digests. A 40-hex string, caller-supplied artifact path, or artifact-local
signature is not approval authority: direct paths are diagnostic-only and cannot satisfy
scheduled gating.

Generations are append-only. A successor retains its predecessor, names it through
`supersedesGenerationId`, and changes separate explicit activation records. A previous-to-candidate
transition rejects deleted, mutated, or reordered history; cycles/forward references; and active
activation removal or replacement without exact supersession. A compatibility tuple may have at
most one active generation. Selection fails closed if the source, fixed input profile, provider image/version/
effective-settings identity, machine fingerprint, content digests, or tuple set drift; it also
fails when evidence is incomplete or the review/hosted-check prerequisites are absent. Required
correctness, result, native-plan, and synchronized-contention maps each name a canonical retained
artifact; selection verifies both the artifact SHA-256 and its parsed map contents.

The registry is intentionally empty (`no-active-generations`). Do not add synthetic
numbers, smoke results, or partial scheduled results. Before activation, issue #50 must execute the
closed clean-HEAD 1K/100K/1M × four-provider × three-form × workload × selectivity matrix; retain
raw/summary/correctness/result/native-plan/synchronized-contention/recovery evidence digests; and
pass three adversarial exact-range reviews plus hosted checks. Controlled execution, immutable
registry population, physical-form selections, and Elsa's EF-oracle verdict remain later work.
