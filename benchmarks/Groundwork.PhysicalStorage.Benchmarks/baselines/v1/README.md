# Future physical-storage baseline registry v1

This registry is deliberately disabled and empty. The current smoke and scheduled profiles are
harness scaffolding: neither can be promoted, and neither can support an Elsa migration decision.
Do not add synthetic numbers, smoke results, or scheduled-profile results to this index.

Before any baseline can be considered, issue #50 must execute the 1K/100K/1M matrix with the closed,
reviewed workload-profile selection: `ordinary-json-v1` for indexed-query and scan-characterization,
and `storage-growth-1k-v1` for storage-growth. It must retain the ratified 10% indexed-query
acceptance and 50% scan-characterization query-selectivity shapes and supply exact-HEAD live evidence for
SQLite, SQL Server, PostgreSQL, and MongoDB; target-scoped provider database-work signals;
sustained concurrent-load and actual bounded recovery evidence; and an approved
immutable-baseline process. A later reviewed change may activate this registry only after those
requirements are complete. Elsa owns its separate EF-oracle join and migration verdict.
