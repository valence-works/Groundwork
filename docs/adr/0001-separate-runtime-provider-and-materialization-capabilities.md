# Separate runtime provider and materialization capabilities

Groundwork separates runtime provider capability from materialization capability. `ProviderCapabilityReport` describes whether a provider can serve a manifest's runtime semantics; `MaterializationCapabilityReport` describes whether a provider can prepare storage for a manifest.

At the time of this decision, the compatibility materialization package owned `MaterializationPlan`, typed `MaterializationOperation` records, `MaterializationCapabilityReport`, and `MaterializationPlanner`. `Groundwork.Core` owned runtime provider capability semantics and did not reference materialization, preserving a clean dependency direction even though this required breaking changes to existing planner and materializer interfaces. The compatibility package was later removed with the portable document model; route-native schema evolution now owns storage preparation.

Provider packages expose runtime and materialization capability reports separately. Provider materializers execute a self-contained `MaterializationPlan` directly rather than accepting `StorageManifest` and re-deriving operation details inside each adapter.
