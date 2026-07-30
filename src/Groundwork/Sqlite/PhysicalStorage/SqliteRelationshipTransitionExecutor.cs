using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Text;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.PhysicalStorage;

/// <summary>
/// Internal, test-only SQLite proof of the relationship materialization transition protocol.
/// It is deliberately not wired into any public store factory or provider capability surface.
/// </summary>
internal sealed class SqliteRelationshipTransitionExecutor
{
    private const string StateTable = "groundwork_relationship_transition_state_v1";
    private const string ActiveTable = "groundwork_relationship_active_v1";
    private const string ReferenceTable = "groundwork_relationship_reference_sidecar_v1";
    private const string FenceTable = "groundwork_relationship_target_fence_v1";
    private const string ReferenceTargetIndex = "ix_groundwork_relationship_reference_sidecar_v1_target";
    private const string CandidateInputDigestScheme = "hmac-sha256-v1:";
    private const string CandidateInputDigestDomain =
        "groundwork.relationship-materialization.sqlite-transition-input.hmac-sha256.v1";
    private const string FailureEnvelopeMacScheme = "hmac-sha256-v1:";
    private const string FailureEnvelopeMacDomain =
        "groundwork.relationship-materialization.sqlite-transition-failure-envelope.hmac-sha256.v1";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly SchemaColumn[] StateColumns =
    [
        new("relationship_identity", "TEXT", true, 1),
        new("candidate_generation", "TEXT", true, 2),
        new("candidate_fingerprint", "TEXT", true, 3),
        new("expected_kind", "INTEGER", true, 0),
        new("expected_generation", "TEXT", false, 0),
        new("expected_fingerprint", "TEXT", false, 0),
        new("candidate_input_digest", "TEXT", true, 0),
        new("phase", "INTEGER", true, 0),
        new("processed_source_count", "INTEGER", true, 0),
        new("failure_code", "TEXT", false, 0),
        new("failure_correlation", "TEXT", false, 0),
        new("failure_mac", "TEXT", false, 0)
    ];
    private static readonly SchemaColumn[] ActiveColumns =
    [
        new("relationship_identity", "TEXT", true, 1),
        new("generation_identity", "TEXT", true, 0),
        new("materialization_fingerprint", "TEXT", true, 0)
    ];
    private static readonly SchemaColumn[] ReferenceColumns =
    [
        new("relationship_identity", "TEXT", true, 1),
        new("candidate_generation", "TEXT", true, 2),
        new("candidate_fingerprint", "TEXT", true, 3),
        new("source_scope", "TEXT", true, 4),
        new("source_lookup_key", "TEXT", true, 5),
        new("source_comparison_key", "TEXT", true, 6),
        new("target_scope", "TEXT", true, 0),
        new("target_lookup_key", "TEXT", true, 0),
        new("target_comparison_key", "TEXT", true, 0)
    ];
    private static readonly SchemaColumn[] FenceColumns =
    [
        new("relationship_identity", "TEXT", true, 1),
        new("candidate_generation", "TEXT", true, 2),
        new("candidate_fingerprint", "TEXT", true, 3),
        new("target_scope", "TEXT", true, 4),
        new("target_lookup_key", "TEXT", true, 5),
        new("target_comparison_key", "TEXT", true, 6)
    ];

    private readonly string connectionString;
    private readonly PhysicalRelationshipPlan plan;
    private readonly RelationshipMaterializationTransitionRequirement requirement;
    private readonly byte[] diagnosticKey;

    private SqliteRelationshipTransitionExecutor(
        string connectionString,
        PhysicalRelationshipPlan plan,
        RelationshipMaterializationTransitionRequirement requirement,
        ReadOnlySpan<byte> diagnosticKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(requirement);
        if (diagnosticKey.Length < 32)
            throw new ArgumentException("The test-only diagnostic key must contain at least 32 bytes.", nameof(diagnosticKey));
        if (!Equals(plan.MaterializationSchema, requirement.CandidateGeneration.Schema))
        {
            throw new ArgumentException(
                "The transition candidate must bind the exact compiled relationship materialization schema.",
                nameof(requirement));
        }

        this.connectionString = connectionString;
        this.plan = plan;
        this.requirement = requirement;
        this.diagnosticKey = diagnosticKey.ToArray();
    }

    /// <summary>
    /// Internal-only admission seam for durable provider tests. Public manifest admission remains
    /// unconditionally fail-closed at <c>GW-RELATIONSHIP-012</c>.
    /// </summary>
    internal static SqliteRelationshipTransitionExecutor CreateForTestOnly(
        string connectionString,
        PhysicalRelationshipPlan plan,
        RelationshipMaterializationTransitionRequirement requirement,
        ReadOnlySpan<byte> diagnosticKey) =>
        new(connectionString, plan, requirement, diagnosticKey);

    internal async Task<SqliteRelationshipTransitionExecutionResult> ExecuteAsync(
        IReadOnlyList<SqliteRelationshipTransitionSourceRecord> sourceRecords,
        IReadOnlyList<SqliteRelationshipTransitionTargetRecord> targetRecords,
        SqliteRelationshipTransitionTestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceRecords);
        ArgumentNullException.ThrowIfNull(targetRecords);
        options ??= SqliteRelationshipTransitionTestOptions.None;
        var sources = NormalizeSources(sourceRecords);
        var targets = NormalizeTargets(targetRecords);
        var candidateInputDigest = CreateCandidateInputDigest(sources, targets);

        await EnsureInfrastructureAsync(cancellationToken);
        var initial = await EnsureCandidateAsync(sources, targets, candidateInputDigest, cancellationToken);
        if (initial is not null)
            return initial;

        for (var index = 0; index < sources.Length; index++)
        {
            if (options.CancelAfterProcessedSourceCount is int cancellationPoint && index >= cancellationPoint)
                throw new OperationCanceledException("Injected test-only relationship transition cancellation.", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var result = await BackfillOneAsync(
                index,
                sources[index],
                sources,
                targets,
                candidateInputDigest,
                cancellationToken);
            if (result is not null)
                return result;
        }

        var validation = await ValidateAsync(sources, targets, candidateInputDigest, options, cancellationToken);
        if (validation is not null)
            return validation;

        return await ActivateAsync(sources, targets, candidateInputDigest, options, cancellationToken);
    }

    internal async Task<SqliteRelationshipTransitionSnapshot> InspectForTestOnlyAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteConnectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureInfrastructureAsync(connection, cancellationToken);
        var state = await ReadStateAsync(connection, null, cancellationToken);
        var active = await ReadActiveAsync(connection, null, cancellationToken);
        var references = await ReadCandidateReferencesAsync(connection, null, cancellationToken);
        var fences = await ReadCandidateFencesAsync(connection, null, cancellationToken);
        return new(
            active?.GenerationIdentity,
            active?.MaterializationFingerprint,
            state?.Phase,
            state?.ProcessedSourceCount ?? 0,
            references,
            fences);
    }

    private async Task<SqliteRelationshipTransitionExecutionResult?> EnsureCandidateAsync(
        IReadOnlyList<SqliteRelationshipTransitionSourceRecord> sources,
        HashSet<TargetKey> targets,
        string candidateInputDigest,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureInfrastructureAsync(connection, cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var active = await ReadActiveAsync(connection, transaction, cancellationToken);
        var state = await ReadStateAsync(connection, transaction, cancellationToken);
        var activeResult = await ResolveActiveCandidateAsync(
            connection,
            transaction,
            active,
            state,
            sources,
            targets,
            candidateInputDigest,
            cancellationToken);
        if (activeResult is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return activeResult;
        }
        if (!ExpectedActiveMatches(active))
        {
            await transaction.CommitAsync(cancellationToken);
            return SqliteRelationshipTransitionExecutionResult.RelationshipConflict;
        }

        if (state is not null && !state.MatchesRequirement(requirement))
        {
            await transaction.CommitAsync(cancellationToken);
            return SqliteRelationshipTransitionExecutionResult.RelationshipConflict;
        }
        if (state?.Phase == SqliteRelationshipTransitionPhase.Failed)
        {
            await transaction.CommitAsync(cancellationToken);
            return RestoreStoredFailure(state);
        }
        if (state is not null && !state.MatchesInput(candidateInputDigest))
        {
            await transaction.CommitAsync(cancellationToken);
            return SqliteRelationshipTransitionExecutionResult.RelationshipConflict;
        }
        if (state is null)
            await InsertStateAsync(connection, transaction, candidateInputDigest, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return null;
    }

    private async Task<SqliteRelationshipTransitionExecutionResult?> BackfillOneAsync(
        int index,
        SqliteRelationshipTransitionSourceRecord source,
        IReadOnlyList<SqliteRelationshipTransitionSourceRecord> sources,
        HashSet<TargetKey> targets,
        string candidateInputDigest,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var active = await ReadActiveAsync(connection, transaction, cancellationToken);
        var state = await ReadStateAsync(connection, transaction, cancellationToken);
        var activeResult = await ResolveActiveCandidateAsync(
            connection,
            transaction,
            active,
            state,
            sources,
            targets,
            candidateInputDigest,
            cancellationToken);
        if (activeResult is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return activeResult;
        }
        if (state is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return SqliteRelationshipTransitionExecutionResult.RelationshipConflict;
        }
        if (state.Phase == SqliteRelationshipTransitionPhase.Failed)
        {
            await transaction.CommitAsync(cancellationToken);
            return RestoreStoredFailure(state);
        }
        if (!state.Matches(requirement, candidateInputDigest))
        {
            await transaction.CommitAsync(cancellationToken);
            return SqliteRelationshipTransitionExecutionResult.RelationshipConflict;
        }
        if (state.Phase is SqliteRelationshipTransitionPhase.Validated or SqliteRelationshipTransitionPhase.Active ||
            state.ProcessedSourceCount > index)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        if (state.ProcessedSourceCount != index)
            throw new InvalidOperationException("Relationship transition source replay must resume at its durable bounded progress point.");

        var projected = plan.ProjectReferenceIdentity(source.SerializedReference);
        if (projected is { } identity)
        {
            var target = new TargetKey(source.TargetScope, identity.LookupKey, identity.ComparisonKey);
            if (!targets.Contains(target))
            {
                var diagnostic = CreateDanglingDiagnostic(source.TargetScope, identity.ComparisonKey);
                await MarkFailedAsync(connection, transaction, diagnostic, candidateInputDigest, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return SqliteRelationshipTransitionExecutionResult.Dangling(diagnostic);
            }

            await InsertReferenceAsync(connection, transaction, source, identity, cancellationToken);
            await InsertFenceAsync(connection, transaction, target, cancellationToken);
        }
        await UpdateProgressAsync(connection, transaction, index + 1, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return null;
    }

    private async Task<SqliteRelationshipTransitionExecutionResult?> ValidateAsync(
        IReadOnlyList<SqliteRelationshipTransitionSourceRecord> sources,
        HashSet<TargetKey> targets,
        string candidateInputDigest,
        SqliteRelationshipTransitionTestOptions options,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var active = await ReadActiveAsync(connection, transaction, cancellationToken);
        var state = await ReadStateAsync(connection, transaction, cancellationToken);
        var activeResult = await ResolveActiveCandidateAsync(
            connection,
            transaction,
            active,
            state,
            sources,
            targets,
            candidateInputDigest,
            cancellationToken);
        if (activeResult is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return activeResult;
        }
        if (state is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return SqliteRelationshipTransitionExecutionResult.RelationshipConflict;
        }
        if (state.Phase == SqliteRelationshipTransitionPhase.Failed)
        {
            await transaction.CommitAsync(cancellationToken);
            return RestoreStoredFailure(state);
        }
        if (!state.Matches(requirement, candidateInputDigest))
        {
            await transaction.CommitAsync(cancellationToken);
            return SqliteRelationshipTransitionExecutionResult.RelationshipConflict;
        }
        if (state.ProcessedSourceCount != sources.Count ||
            !await CandidateMaterializationMatchesAsync(connection, transaction, sources, targets, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return SqliteRelationshipTransitionExecutionResult.RelationshipConflict;
        }
        if (state.Phase == SqliteRelationshipTransitionPhase.Preparing)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                UPDATE {StateTable}
                SET phase = @phase
                WHERE relationship_identity = @relationshipIdentity
                  AND candidate_generation = @candidateGeneration
                  AND candidate_fingerprint = @candidateFingerprint;
                """;
            AddCandidateParameters(command);
            command.Parameters.AddWithValue("@phase", (int)SqliteRelationshipTransitionPhase.Validated);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return SqliteRelationshipTransitionExecutionResult.RelationshipConflict;
            }
        }
        await transaction.CommitAsync(cancellationToken);
        if (options.ThrowAfterValidationCommit)
            throw new SqliteRelationshipTransitionValidationAcknowledgementLostException();
        return null;
    }

    private async Task<SqliteRelationshipTransitionExecutionResult> ActivateAsync(
        IReadOnlyList<SqliteRelationshipTransitionSourceRecord> sources,
        HashSet<TargetKey> targets,
        string candidateInputDigest,
        SqliteRelationshipTransitionTestOptions options,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var active = await ReadActiveAsync(connection, transaction, cancellationToken);
        var state = await ReadStateAsync(connection, transaction, cancellationToken);
        var activeResult = await ResolveActiveCandidateAsync(
            connection,
            transaction,
            active,
            state,
            sources,
            targets,
            candidateInputDigest,
            cancellationToken);
        if (activeResult is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return activeResult;
        }
        if (state is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return SqliteRelationshipTransitionExecutionResult.RelationshipConflict;
        }
        if (state.Phase == SqliteRelationshipTransitionPhase.Failed)
        {
            await transaction.CommitAsync(cancellationToken);
            return RestoreStoredFailure(state);
        }
        if (!state.Matches(requirement, candidateInputDigest))
        {
            await transaction.CommitAsync(cancellationToken);
            return SqliteRelationshipTransitionExecutionResult.RelationshipConflict;
        }
        if (state.Phase != SqliteRelationshipTransitionPhase.Validated ||
            !ExpectedActiveMatches(active) ||
            !await CandidateMaterializationMatchesAsync(connection, transaction, sources, targets, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return SqliteRelationshipTransitionExecutionResult.RelationshipConflict;
        }

        var changed = await CompareAndSwapActiveAsync(connection, transaction, cancellationToken);
        if (!changed)
        {
            await transaction.CommitAsync(cancellationToken);
            return SqliteRelationshipTransitionExecutionResult.RelationshipConflict;
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                UPDATE {StateTable}
                SET phase = @phase
                WHERE relationship_identity = @relationshipIdentity
                  AND candidate_generation = @candidateGeneration
                  AND candidate_fingerprint = @candidateFingerprint;
                """;
            AddCandidateParameters(command);
            command.Parameters.AddWithValue("@phase", (int)SqliteRelationshipTransitionPhase.Active);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return SqliteRelationshipTransitionExecutionResult.RelationshipConflict;
            }
        }
        await transaction.CommitAsync(cancellationToken);
        if (options.ThrowAfterActivationCommit)
            throw new SqliteRelationshipTransitionAcknowledgementLostException();
        return SqliteRelationshipTransitionExecutionResult.Activated;
    }

    private async Task<bool> CompareAndSwapActiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (requirement.ExpectedActive.IsAbsent)
        {
            command.CommandText = $"""
                INSERT INTO {ActiveTable} (relationship_identity, generation_identity, materialization_fingerprint)
                SELECT @relationshipIdentity, @candidateGeneration, @candidateFingerprint
                WHERE NOT EXISTS (
                    SELECT 1 FROM {ActiveTable} WHERE relationship_identity = @relationshipIdentity);
                """;
            AddCandidateParameters(command);
        }
        else
        {
            command.CommandText = $"""
                UPDATE {ActiveTable}
                SET generation_identity = @candidateGeneration,
                    materialization_fingerprint = @candidateFingerprint
                WHERE relationship_identity = @relationshipIdentity
                  AND generation_identity = @expectedGeneration
                  AND materialization_fingerprint = @expectedFingerprint;
                """;
            AddCandidateParameters(command);
            command.Parameters.AddWithValue("@expectedGeneration", requirement.ExpectedActive.ExactGeneration!.GenerationIdentity);
            command.Parameters.AddWithValue("@expectedFingerprint", requirement.ExpectedActive.ExactGeneration.MaterializationFingerprint);
        }
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private bool ExpectedActiveMatches(ActiveGeneration? active)
    {
        if (requirement.ExpectedActive.IsAbsent)
            return active is null;
        var expected = requirement.ExpectedActive.ExactGeneration!;
        return active is not null &&
               string.Equals(active.GenerationIdentity, expected.GenerationIdentity, StringComparison.Ordinal) &&
               string.Equals(active.MaterializationFingerprint, expected.MaterializationFingerprint, StringComparison.Ordinal);
    }

    private bool IsCandidate(ActiveGeneration? active) =>
        active is not null &&
        string.Equals(active.GenerationIdentity, requirement.CandidateGeneration.GenerationIdentity, StringComparison.Ordinal) &&
        string.Equals(active.MaterializationFingerprint, requirement.CandidateGeneration.MaterializationFingerprint, StringComparison.Ordinal);

    private async Task<SqliteRelationshipTransitionExecutionResult?> ResolveActiveCandidateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveGeneration? active,
        TransitionState? state,
        IReadOnlyList<SqliteRelationshipTransitionSourceRecord> sources,
        HashSet<TargetKey> targets,
        string candidateInputDigest,
        CancellationToken cancellationToken)
    {
        if (!IsCandidate(active))
            return null;

        if (state is null ||
            state.Phase != SqliteRelationshipTransitionPhase.Active ||
            state.ProcessedSourceCount != sources.Count ||
            !state.Matches(requirement, candidateInputDigest) ||
            !await CandidateMaterializationMatchesAsync(connection, transaction, sources, targets, cancellationToken))
        {
            return SqliteRelationshipTransitionExecutionResult.RelationshipConflict;
        }

        return SqliteRelationshipTransitionExecutionResult.Activated;
    }

    private async Task<bool> CandidateMaterializationMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<SqliteRelationshipTransitionSourceRecord> sources,
        HashSet<TargetKey> targets,
        CancellationToken cancellationToken)
    {
        var expectedReferences = new HashSet<SqliteRelationshipTransitionReferenceSnapshot>();
        var expectedFences = new HashSet<SqliteRelationshipTransitionFenceSnapshot>();
        foreach (var source in sources)
        {
            var projected = plan.ProjectReferenceIdentity(source.SerializedReference);
            if (projected is not { } identity)
                continue;

            var target = new TargetKey(source.TargetScope, identity.LookupKey, identity.ComparisonKey);
            if (!targets.Contains(target))
                return false;

            expectedReferences.Add(CreateReferenceSnapshot(source, identity));
            expectedFences.Add(CreateFenceSnapshot(target));
        }

        var actualReferences = await ReadCandidateReferencesAsync(connection, transaction, cancellationToken);
        var actualFences = await ReadCandidateFencesAsync(connection, transaction, cancellationToken);
        return expectedReferences.Count == actualReferences.Count &&
               expectedReferences.SetEquals(actualReferences) &&
               expectedFences.Count == actualFences.Count &&
               expectedFences.SetEquals(actualFences);
    }

    private SqliteRelationshipTransitionReferenceSnapshot CreateReferenceSnapshot(
        SqliteRelationshipTransitionSourceRecord source,
        PortableStringIdentityProjection target) =>
        new(
            requirement.CandidateGeneration.RelationshipIdentity,
            requirement.CandidateGeneration.GenerationIdentity,
            requirement.CandidateGeneration.MaterializationFingerprint,
            source.SourceScope,
            source.SourceLookupKey,
            source.SourceComparisonKey,
            source.TargetScope,
            target.LookupKey,
            target.ComparisonKey);

    private SqliteRelationshipTransitionFenceSnapshot CreateFenceSnapshot(TargetKey target) =>
        new(
            requirement.CandidateGeneration.RelationshipIdentity,
            requirement.CandidateGeneration.GenerationIdentity,
            requirement.CandidateGeneration.MaterializationFingerprint,
            target.Scope,
            target.LookupKey,
            target.ComparisonKey);

    private RelationshipMaterializationDanglingReference CreateDanglingDiagnostic(
        string targetScope,
        string targetComparisonKey) =>
        new(
            requirement,
            RelationshipMaterializationKeyCorrelationIdentity.Create(
                diagnosticKey,
                requirement,
                targetScope,
                targetComparisonKey));

    private SqliteRelationshipTransitionExecutionResult RestoreStoredFailure(TransitionState state)
    {
        try
        {
            var failureCode = state.FailureCode ?? throw new InvalidOperationException();
            var failureCorrelation = state.FailureCorrelation ?? throw new InvalidOperationException();
            var failureMac = state.FailureMac ?? throw new InvalidOperationException();
            if (!state.MatchesRequirement(requirement) ||
                !IsCanonicalMac(failureMac, FailureEnvelopeMacScheme) ||
                !FixedTimeEquals(
                    failureMac,
                    CreateFailureEnvelopeMac(state.CandidateInputDigest, failureCode, failureCorrelation)))
            {
                return SqliteRelationshipTransitionExecutionResult.RelationshipConflict;
            }

            return SqliteRelationshipTransitionExecutionResult.Dangling(
                RelationshipMaterializationDanglingReference.Restore(
                    requirement,
                    failureCode,
                    failureCorrelation));
        }
        catch (ArgumentException)
        {
            return SqliteRelationshipTransitionExecutionResult.RelationshipConflict;
        }
        catch (InvalidOperationException)
        {
            return SqliteRelationshipTransitionExecutionResult.RelationshipConflict;
        }
    }

    private static bool IsCanonicalMac(string value, string scheme) =>
        value.Length == scheme.Length + 64 &&
        value.StartsWith(scheme, StringComparison.Ordinal) &&
        value.AsSpan(scheme.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static bool FixedTimeEquals(string actual, string expected)
    {
        var actualBytes = StrictUtf8.GetBytes(actual);
        var expectedBytes = StrictUtf8.GetBytes(expected);
        try
        {
            return actualBytes.Length == expectedBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualBytes);
            CryptographicOperations.ZeroMemory(expectedBytes);
        }
    }

    private async Task EnsureInfrastructureAsync(CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureInfrastructureAsync(connection, cancellationToken);
    }

    private static async Task EnsureInfrastructureAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {StateTable} (
                relationship_identity TEXT NOT NULL,
                candidate_generation TEXT NOT NULL,
                candidate_fingerprint TEXT NOT NULL,
                expected_kind INTEGER NOT NULL,
                expected_generation TEXT NULL,
                expected_fingerprint TEXT NULL,
                candidate_input_digest TEXT NOT NULL,
                phase INTEGER NOT NULL,
                processed_source_count INTEGER NOT NULL,
                failure_code TEXT NULL,
                failure_correlation TEXT NULL,
                failure_mac TEXT NULL,
                PRIMARY KEY (relationship_identity, candidate_generation, candidate_fingerprint)
            );
            CREATE TABLE IF NOT EXISTS {ActiveTable} (
                relationship_identity TEXT NOT NULL PRIMARY KEY,
                generation_identity TEXT NOT NULL,
                materialization_fingerprint TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS {ReferenceTable} (
                relationship_identity TEXT NOT NULL,
                candidate_generation TEXT NOT NULL,
                candidate_fingerprint TEXT NOT NULL,
                source_scope TEXT NOT NULL,
                source_lookup_key TEXT NOT NULL,
                source_comparison_key TEXT NOT NULL,
                target_scope TEXT NOT NULL,
                target_lookup_key TEXT NOT NULL,
                target_comparison_key TEXT NOT NULL,
                PRIMARY KEY (
                    relationship_identity, candidate_generation, candidate_fingerprint,
                    source_scope, source_lookup_key, source_comparison_key)
            );
            CREATE TABLE IF NOT EXISTS {FenceTable} (
                relationship_identity TEXT NOT NULL,
                candidate_generation TEXT NOT NULL,
                candidate_fingerprint TEXT NOT NULL,
                target_scope TEXT NOT NULL,
                target_lookup_key TEXT NOT NULL,
                target_comparison_key TEXT NOT NULL,
                PRIMARY KEY (
                    relationship_identity, candidate_generation, candidate_fingerprint,
                    target_scope, target_lookup_key, target_comparison_key)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await UpgradeLegacyStateSchemaAsync(connection, transaction, cancellationToken);
        await ValidateTableSchemaAsync(connection, transaction, StateTable, StateColumns, cancellationToken);
        await ValidateTableSchemaAsync(connection, transaction, ActiveTable, ActiveColumns, cancellationToken);
        await ValidateTableSchemaAsync(connection, transaction, ReferenceTable, ReferenceColumns, cancellationToken);
        await ValidateTableSchemaAsync(connection, transaction, FenceTable, FenceColumns, cancellationToken);

        command.CommandText = $"""
            CREATE INDEX IF NOT EXISTS {ReferenceTargetIndex}
                ON {ReferenceTable} (
                    relationship_identity, candidate_generation, candidate_fingerprint,
                    target_scope, target_lookup_key, target_comparison_key);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await ValidateIndexSchemaAsync(
            connection,
            transaction,
            ReferenceTable,
            ReferenceTargetIndex,
            ["relationship_identity", "candidate_generation", "candidate_fingerprint", "target_scope", "target_lookup_key", "target_comparison_key"],
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task UpgradeLegacyStateSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var actual = await ReadTableSchemaAsync(connection, transaction, StateTable, cancellationToken);
        var legacy = StateColumns.Where(column => column.Name != "failure_mac").ToArray();
        if (!actual.SequenceEqual(legacy))
            return;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"ALTER TABLE {StateTable} ADD COLUMN failure_mac TEXT NULL;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ValidateTableSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        IReadOnlyList<SchemaColumn> expected,
        CancellationToken cancellationToken)
    {
        var actual = await ReadTableSchemaAsync(connection, transaction, table, cancellationToken);
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"Durable relationship transition table '{table}' does not match its required versioned schema.");
        }
    }

    private static async Task<IReadOnlyList<SchemaColumn>> ReadTableSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        var columns = new List<SchemaColumn>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info('{table}');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(new(
                reader.GetString(1),
                reader.GetString(2).ToUpperInvariant(),
                reader.GetInt32(3) == 1,
                reader.GetInt32(5)));
        }

        return columns;
    }

    private static async Task ValidateIndexSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string index,
        IReadOnlyList<string> expectedColumns,
        CancellationToken cancellationToken)
    {
        var found = false;
        await using (var listCommand = connection.CreateCommand())
        {
            listCommand.Transaction = transaction;
            listCommand.CommandText = $"PRAGMA index_list('{table}');";
            await using var reader = await listCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!string.Equals(reader.GetString(1), index, StringComparison.Ordinal))
                    continue;

                found = reader.GetInt32(2) == 0 &&
                        string.Equals(reader.GetString(3), "c", StringComparison.Ordinal) &&
                        reader.GetInt32(4) == 0;
                break;
            }
        }

        var actualColumns = new List<string>();
        await using (var infoCommand = connection.CreateCommand())
        {
            infoCommand.Transaction = transaction;
            infoCommand.CommandText = $"PRAGMA index_info('{index}');";
            await using var reader = await infoCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                actualColumns.Add(reader.GetString(2));
        }

        if (!found || !actualColumns.SequenceEqual(expectedColumns))
        {
            throw new InvalidOperationException(
                $"Durable relationship transition index '{index}' does not match its required versioned schema.");
        }
    }

    private async Task InsertStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string candidateInputDigest,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {StateTable} (
                relationship_identity, candidate_generation, candidate_fingerprint,
                expected_kind, expected_generation, expected_fingerprint,
                candidate_input_digest, phase, processed_source_count,
                failure_code, failure_correlation, failure_mac)
            VALUES (
                @relationshipIdentity, @candidateGeneration, @candidateFingerprint,
                @expectedKind, @expectedGeneration, @expectedFingerprint,
                @candidateInputDigest, @phase, 0, NULL, NULL, NULL);
            """;
        AddCandidateParameters(command);
        command.Parameters.AddWithValue("@expectedKind", requirement.ExpectedActive.IsAbsent ? 0 : 1);
        command.Parameters.AddWithValue("@expectedGeneration", (object?)requirement.ExpectedActive.ExactGeneration?.GenerationIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("@expectedFingerprint", (object?)requirement.ExpectedActive.ExactGeneration?.MaterializationFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("@candidateInputDigest", candidateInputDigest);
        command.Parameters.AddWithValue("@phase", (int)SqliteRelationshipTransitionPhase.Preparing);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertReferenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteRelationshipTransitionSourceRecord source,
        PortableStringIdentityProjection identity,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT OR IGNORE INTO {ReferenceTable} (
                relationship_identity, candidate_generation, candidate_fingerprint,
                source_scope, source_lookup_key, source_comparison_key,
                target_scope, target_lookup_key, target_comparison_key)
            VALUES (
                @relationshipIdentity, @candidateGeneration, @candidateFingerprint,
                @sourceScope, @sourceLookupKey, @sourceComparisonKey,
                @targetScope, @targetLookupKey, @targetComparisonKey);
            """;
        AddCandidateParameters(command);
        command.Parameters.AddWithValue("@sourceScope", source.SourceScope);
        command.Parameters.AddWithValue("@sourceLookupKey", source.SourceLookupKey);
        command.Parameters.AddWithValue("@sourceComparisonKey", source.SourceComparisonKey);
        command.Parameters.AddWithValue("@targetScope", source.TargetScope);
        command.Parameters.AddWithValue("@targetLookupKey", identity.LookupKey);
        command.Parameters.AddWithValue("@targetComparisonKey", identity.ComparisonKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertFenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TargetKey target,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT OR IGNORE INTO {FenceTable} (
                relationship_identity, candidate_generation, candidate_fingerprint,
                target_scope, target_lookup_key, target_comparison_key)
            VALUES (
                @relationshipIdentity, @candidateGeneration, @candidateFingerprint,
                @targetScope, @targetLookupKey, @targetComparisonKey);
            """;
        AddCandidateParameters(command);
        command.Parameters.AddWithValue("@targetScope", target.Scope);
        command.Parameters.AddWithValue("@targetLookupKey", target.LookupKey);
        command.Parameters.AddWithValue("@targetComparisonKey", target.ComparisonKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task MarkFailedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RelationshipMaterializationDanglingReference diagnostic,
        string candidateInputDigest,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE {StateTable}
            SET phase = @phase,
                failure_code = @failureCode,
                failure_correlation = @failureCorrelation,
                failure_mac = @failureMac
            WHERE relationship_identity = @relationshipIdentity
              AND candidate_generation = @candidateGeneration
              AND candidate_fingerprint = @candidateFingerprint;
            """;
        AddCandidateParameters(command);
        command.Parameters.AddWithValue("@phase", (int)SqliteRelationshipTransitionPhase.Failed);
        command.Parameters.AddWithValue("@failureCode", RelationshipMaterializationDanglingReference.DiagnosticCode);
        command.Parameters.AddWithValue("@failureCorrelation", diagnostic.TargetKeyCorrelationIdentity.Value);
        command.Parameters.AddWithValue(
            "@failureMac",
            CreateFailureEnvelopeMac(
                candidateInputDigest,
                RelationshipMaterializationDanglingReference.DiagnosticCode,
                diagnostic.TargetKeyCorrelationIdentity.Value));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateProgressAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int processedSourceCount,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE {StateTable}
            SET processed_source_count = @processedSourceCount
            WHERE relationship_identity = @relationshipIdentity
              AND candidate_generation = @candidateGeneration
              AND candidate_fingerprint = @candidateFingerprint;
            """;
        AddCandidateParameters(command);
        command.Parameters.AddWithValue("@processedSourceCount", processedSourceCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<TransitionState?> ReadStateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT expected_kind, expected_generation, expected_fingerprint, candidate_input_digest,
                   phase, processed_source_count, failure_code, failure_correlation, failure_mac
            FROM {StateTable}
            WHERE relationship_identity = @relationshipIdentity
              AND candidate_generation = @candidateGeneration
              AND candidate_fingerprint = @candidateFingerprint;
            """;
        AddCandidateParameters(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            (SqliteRelationshipTransitionPhase)reader.GetInt32(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }

    private async Task<ActiveGeneration?> ReadActiveAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT generation_identity, materialization_fingerprint
            FROM {ActiveTable}
            WHERE relationship_identity = @relationshipIdentity;
            """;
        command.Parameters.AddWithValue("@relationshipIdentity", requirement.CandidateGeneration.RelationshipIdentity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetString(0), reader.GetString(1))
            : null;
    }

    private async Task<IReadOnlyList<SqliteRelationshipTransitionReferenceSnapshot>> ReadCandidateReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT relationship_identity, candidate_generation, candidate_fingerprint,
                   source_scope, source_lookup_key, source_comparison_key,
                   target_scope, target_lookup_key, target_comparison_key
            FROM {ReferenceTable}
            WHERE relationship_identity = @relationshipIdentity
              AND candidate_generation = @candidateGeneration
              AND candidate_fingerprint = @candidateFingerprint
            ORDER BY source_scope, source_lookup_key, source_comparison_key,
                     target_scope, target_lookup_key, target_comparison_key;
            """;
        AddCandidateParameters(command);
        var results = new List<SqliteRelationshipTransitionReferenceSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8)));
        }

        return results;
    }

    private async Task<IReadOnlyList<SqliteRelationshipTransitionFenceSnapshot>> ReadCandidateFencesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT relationship_identity, candidate_generation, candidate_fingerprint,
                   target_scope, target_lookup_key, target_comparison_key
            FROM {FenceTable}
            WHERE relationship_identity = @relationshipIdentity
              AND candidate_generation = @candidateGeneration
              AND candidate_fingerprint = @candidateFingerprint
            ORDER BY target_scope, target_lookup_key, target_comparison_key;
            """;
        AddCandidateParameters(command);
        var results = new List<SqliteRelationshipTransitionFenceSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return results;
    }

    private void AddCandidateParameters(SqliteCommand command)
    {
        command.Parameters.AddWithValue("@relationshipIdentity", requirement.CandidateGeneration.RelationshipIdentity);
        command.Parameters.AddWithValue("@candidateGeneration", requirement.CandidateGeneration.GenerationIdentity);
        command.Parameters.AddWithValue("@candidateFingerprint", requirement.CandidateGeneration.MaterializationFingerprint);
    }

    private static SqliteRelationshipTransitionSourceRecord[] NormalizeSources(
        IReadOnlyList<SqliteRelationshipTransitionSourceRecord> sourceRecords)
    {
        var ordered = sourceRecords.OrderBy(SourceKey.Create, StringComparer.Ordinal).ToArray();
        foreach (var source in ordered)
            source.Validate();
        if (ordered.Select(SourceKey.Create).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("Relationship transition source records must have unique source identities.", nameof(sourceRecords));
        return ordered;
    }

    private static HashSet<TargetKey> NormalizeTargets(IReadOnlyList<SqliteRelationshipTransitionTargetRecord> targetRecords)
    {
        var targets = new HashSet<TargetKey>();
        foreach (var target in targetRecords)
        {
            target.Validate();
            targets.Add(new(target.TargetScope, target.TargetLookupKey, target.TargetComparisonKey));
        }
        return targets;
    }

    private string CreateFailureEnvelopeMac(
        string candidateInputDigest,
        string failureCode,
        string failureCorrelation)
    {
        using var framed = new MemoryStream();
        WriteFramedString(framed, FailureEnvelopeMacDomain);
        WriteFramedString(framed, requirement.CandidateGeneration.RelationshipIdentity);
        WriteFramedString(framed, requirement.CandidateGeneration.GenerationIdentity);
        WriteFramedString(framed, requirement.CandidateGeneration.MaterializationFingerprint);
        WriteFramedString(framed, candidateInputDigest);
        WriteFramedString(framed, failureCode);
        WriteFramedString(framed, failureCorrelation);
        return CreateMac(FailureEnvelopeMacScheme, framed);
    }

    private string CreateCandidateInputDigest(
        IReadOnlyList<SqliteRelationshipTransitionSourceRecord> sources,
        IReadOnlyCollection<TargetKey> targets)
    {
        using var framed = new MemoryStream();
        WriteFramedString(framed, CandidateInputDigestDomain);
        WriteFramedString(framed, requirement.CandidateGeneration.RelationshipIdentity);
        WriteFramedString(framed, requirement.CandidateGeneration.GenerationIdentity);
        WriteFramedString(framed, requirement.CandidateGeneration.MaterializationFingerprint);
        WriteFramedUnsignedInteger(framed, checked((uint)sources.Count));
        foreach (var source in sources)
        {
            WriteFramedString(framed, source.SourceScope);
            WriteFramedString(framed, source.SourceLookupKey);
            WriteFramedString(framed, source.SourceComparisonKey);
            WriteFramedString(framed, source.TargetScope);
            WriteFramedNullableString(framed, source.SerializedReference);
        }

        var orderedTargets = targets.OrderBy(TargetKeyOrder.Create, StringComparer.Ordinal).ToArray();
        WriteFramedUnsignedInteger(framed, checked((uint)orderedTargets.Length));
        foreach (var target in orderedTargets)
        {
            WriteFramedString(framed, target.Scope);
            WriteFramedString(framed, target.LookupKey);
            WriteFramedString(framed, target.ComparisonKey);
        }

        return CreateMac(CandidateInputDigestScheme, framed);
    }

    private string CreateMac(string scheme, MemoryStream framed)
    {
        var input = framed.ToArray();
        try
        {
            var digest = HMACSHA256.HashData(diagnosticKey, input);
            try
            {
                return scheme + Convert.ToHexString(digest).ToLowerInvariant();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private static void WriteFramedString(Stream stream, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        stream.WriteByte(1);
        var encoded = StrictUtf8.GetBytes(value);
        try
        {
            WriteFramedUnsignedInteger(stream, checked((uint)encoded.Length));
            stream.Write(encoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private static void WriteFramedNullableString(Stream stream, string? value)
    {
        if (value is null)
        {
            stream.WriteByte(0);
            return;
        }

        WriteFramedString(stream, value);
    }

    private static void WriteFramedUnsignedInteger(Stream stream, uint value)
    {
        Span<byte> encoded = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(encoded, value);
        stream.Write(encoded);
    }

    private sealed record TransitionState(
        int ExpectedKind,
        string? ExpectedGeneration,
        string? ExpectedFingerprint,
        string CandidateInputDigest,
        SqliteRelationshipTransitionPhase Phase,
        int ProcessedSourceCount,
        string? FailureCode,
        string? FailureCorrelation,
        string? FailureMac)
    {
        public bool MatchesRequirement(RelationshipMaterializationTransitionRequirement current) =>
            ExpectedKind == (current.ExpectedActive.IsAbsent ? 0 : 1) &&
            string.Equals(ExpectedGeneration, current.ExpectedActive.ExactGeneration?.GenerationIdentity, StringComparison.Ordinal) &&
            string.Equals(ExpectedFingerprint, current.ExpectedActive.ExactGeneration?.MaterializationFingerprint, StringComparison.Ordinal);

        public bool MatchesInput(string candidateInputDigest) =>
            string.Equals(CandidateInputDigest, candidateInputDigest, StringComparison.Ordinal);

        public bool Matches(RelationshipMaterializationTransitionRequirement current, string candidateInputDigest) =>
            MatchesRequirement(current) && MatchesInput(candidateInputDigest);
    }

    private sealed record ActiveGeneration(string GenerationIdentity, string MaterializationFingerprint);
    private sealed record SchemaColumn(string Name, string Type, bool NotNull, int PrimaryKeyOrder);
    private sealed record TargetKey(string Scope, string LookupKey, string ComparisonKey);

    private static class SourceKey
    {
        public static string Create(SqliteRelationshipTransitionSourceRecord source) =>
            string.Concat(source.SourceScope.Length, ":", source.SourceScope, "|", source.SourceLookupKey.Length, ":", source.SourceLookupKey, "|", source.SourceComparisonKey.Length, ":", source.SourceComparisonKey);
    }

    private static class TargetKeyOrder
    {
        public static string Create(TargetKey target) =>
            string.Concat(target.Scope.Length, ":", target.Scope, "|", target.LookupKey.Length, ":", target.LookupKey, "|", target.ComparisonKey.Length, ":", target.ComparisonKey);
    }
}

internal sealed record SqliteRelationshipTransitionSourceRecord(
    string SourceScope,
    string SourceLookupKey,
    string SourceComparisonKey,
    string TargetScope,
    string? SerializedReference)
{
    internal void Validate()
    {
        Require(SourceScope, nameof(SourceScope));
        Require(SourceLookupKey, nameof(SourceLookupKey));
        Require(SourceComparisonKey, nameof(SourceComparisonKey));
        Require(TargetScope, nameof(TargetScope));
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Relationship transition identities must be non-empty.", name);
    }
}

internal sealed record SqliteRelationshipTransitionTargetRecord(
    string TargetScope,
    string TargetLookupKey,
    string TargetComparisonKey)
{
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(TargetScope) ||
            string.IsNullOrWhiteSpace(TargetLookupKey) ||
            string.IsNullOrWhiteSpace(TargetComparisonKey))
        {
            throw new ArgumentException("Relationship transition target identities must be non-empty.");
        }
    }
}

internal enum SqliteRelationshipTransitionPhase
{
    Preparing = 0,
    Validated = 1,
    Active = 2,
    Failed = 3
}

internal enum SqliteRelationshipTransitionStatus
{
    Activated,
    RelationshipConflict,
    DanglingReference,
    Failed
}

internal sealed record SqliteRelationshipTransitionExecutionResult(
    SqliteRelationshipTransitionStatus Status,
    RelationshipMaterializationDanglingReference? DanglingReference = null)
{
    public static SqliteRelationshipTransitionExecutionResult Activated { get; } = new(SqliteRelationshipTransitionStatus.Activated);
    public static SqliteRelationshipTransitionExecutionResult RelationshipConflict { get; } = new(SqliteRelationshipTransitionStatus.RelationshipConflict);
    public static SqliteRelationshipTransitionExecutionResult Failed { get; } = new(SqliteRelationshipTransitionStatus.Failed);
    public static SqliteRelationshipTransitionExecutionResult Dangling(RelationshipMaterializationDanglingReference diagnostic) =>
        new(SqliteRelationshipTransitionStatus.DanglingReference, diagnostic);
}

internal sealed record SqliteRelationshipTransitionSnapshot(
    string? ActiveGeneration,
    string? ActiveFingerprint,
    SqliteRelationshipTransitionPhase? CandidatePhase,
    int ProcessedSourceCount,
    IReadOnlyList<SqliteRelationshipTransitionReferenceSnapshot> References,
    IReadOnlyList<SqliteRelationshipTransitionFenceSnapshot> Fences)
{
    public int ReferenceCount => References.Count;
    public int FenceCount => Fences.Count;
}

internal sealed record SqliteRelationshipTransitionReferenceSnapshot(
    string RelationshipIdentity,
    string CandidateGeneration,
    string CandidateFingerprint,
    string SourceScope,
    string SourceLookupKey,
    string SourceComparisonKey,
    string TargetScope,
    string TargetLookupKey,
    string TargetComparisonKey);

internal sealed record SqliteRelationshipTransitionFenceSnapshot(
    string RelationshipIdentity,
    string CandidateGeneration,
    string CandidateFingerprint,
    string TargetScope,
    string TargetLookupKey,
    string TargetComparisonKey);

internal sealed record SqliteRelationshipTransitionTestOptions(
    bool ThrowAfterActivationCommit,
    bool ThrowAfterValidationCommit = false,
    int? CancelAfterProcessedSourceCount = null)
{
    public static SqliteRelationshipTransitionTestOptions None { get; } = new(false);
}

internal sealed class SqliteRelationshipTransitionAcknowledgementLostException : Exception;
internal sealed class SqliteRelationshipTransitionValidationAcknowledgementLostException : Exception;
