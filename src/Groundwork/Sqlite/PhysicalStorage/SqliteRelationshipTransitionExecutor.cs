using System.Buffers.Binary;
using System.Globalization;
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
    private const string CandidateInputDigestScheme = "hmac-sha256-v1:";
    private const string CandidateInputDigestDomain =
        "groundwork.relationship-materialization.sqlite-transition-input.hmac-sha256.v1";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

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
        var initial = await EnsureCandidateAsync(candidateInputDigest, cancellationToken);
        if (initial is not null)
            return initial;

        for (var index = 0; index < sources.Length; index++)
        {
            if (options.CancelAfterProcessedSourceCount is int cancellationPoint && index >= cancellationPoint)
                throw new OperationCanceledException("Injected test-only relationship transition cancellation.", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var result = await BackfillOneAsync(index, sources[index], targets, candidateInputDigest, cancellationToken);
            if (result is not null)
                return result;
        }

        var validation = await ValidateAsync(sources.Length, candidateInputDigest, options, cancellationToken);
        if (validation is not null)
            return validation;

        return await ActivateAsync(candidateInputDigest, options, cancellationToken);
    }

    internal async Task<SqliteRelationshipTransitionSnapshot> InspectForTestOnlyAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteConnectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureInfrastructureAsync(connection, cancellationToken);
        var state = await ReadStateAsync(connection, null, cancellationToken);
        var active = await ReadActiveAsync(connection, null, cancellationToken);
        return new(
            active?.GenerationIdentity,
            active?.MaterializationFingerprint,
            state?.Phase,
            state?.ProcessedSourceCount ?? 0,
            await CountAsync(connection, ReferenceTable, cancellationToken),
            await CountAsync(connection, FenceTable, cancellationToken));
    }

    private async Task<SqliteRelationshipTransitionExecutionResult?> EnsureCandidateAsync(
        string candidateInputDigest,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureInfrastructureAsync(connection, cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var active = await ReadActiveAsync(connection, transaction, cancellationToken);
        if (IsCandidate(active))
        {
            await transaction.CommitAsync(cancellationToken);
            return SqliteRelationshipTransitionExecutionResult.Activated;
        }
        if (!ExpectedActiveMatches(active))
        {
            await transaction.CommitAsync(cancellationToken);
            return SqliteRelationshipTransitionExecutionResult.RelationshipConflict;
        }

        var state = await ReadStateAsync(connection, transaction, cancellationToken);
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
        HashSet<TargetKey> targets,
        string candidateInputDigest,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var active = await ReadActiveAsync(connection, transaction, cancellationToken);
        if (IsCandidate(active))
        {
            await transaction.CommitAsync(cancellationToken);
            return SqliteRelationshipTransitionExecutionResult.Activated;
        }
        var state = await ReadStateAsync(connection, transaction, cancellationToken)
            ?? throw new InvalidOperationException("The durable relationship transition state disappeared during backfill.");
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
                await MarkFailedAsync(connection, transaction, diagnostic, cancellationToken);
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
        int expectedSourceCount,
        string candidateInputDigest,
        SqliteRelationshipTransitionTestOptions options,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var active = await ReadActiveAsync(connection, transaction, cancellationToken);
        if (IsCandidate(active))
        {
            await transaction.CommitAsync(cancellationToken);
            return SqliteRelationshipTransitionExecutionResult.Activated;
        }
        var state = await ReadStateAsync(connection, transaction, cancellationToken)
            ?? throw new InvalidOperationException("The durable relationship transition state disappeared during validation.");
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
        if (state.ProcessedSourceCount != expectedSourceCount)
            throw new InvalidOperationException("Relationship transition cannot validate before bounded backfill completes.");
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
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        if (options.ThrowAfterValidationCommit)
            throw new SqliteRelationshipTransitionValidationAcknowledgementLostException();
        return null;
    }

    private async Task<SqliteRelationshipTransitionExecutionResult> ActivateAsync(
        string candidateInputDigest,
        SqliteRelationshipTransitionTestOptions options,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var active = await ReadActiveAsync(connection, transaction, cancellationToken);
        if (IsCandidate(active))
        {
            await transaction.CommitAsync(cancellationToken);
            return SqliteRelationshipTransitionExecutionResult.Activated;
        }
        var state = await ReadStateAsync(connection, transaction, cancellationToken)
            ?? throw new InvalidOperationException("The durable relationship transition state disappeared during activation.");
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
        if (state.Phase != SqliteRelationshipTransitionPhase.Validated || !ExpectedActiveMatches(active))
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
            await command.ExecuteNonQueryAsync(cancellationToken);
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
            return SqliteRelationshipTransitionExecutionResult.Dangling(
                RelationshipMaterializationDanglingReference.Restore(
                    requirement,
                    state.FailureCode ?? throw new InvalidOperationException(),
                    state.FailureCorrelation ?? throw new InvalidOperationException()));
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

    private async Task EnsureInfrastructureAsync(CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureInfrastructureAsync(connection, cancellationToken);
    }

    private static async Task EnsureInfrastructureAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
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
            CREATE INDEX IF NOT EXISTS ix_groundwork_relationship_reference_sidecar_v1_target
                ON {ReferenceTable} (
                    relationship_identity, candidate_generation, candidate_fingerprint,
                    target_scope, target_lookup_key, target_comparison_key);
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
                candidate_input_digest, phase, processed_source_count, failure_code, failure_correlation)
            VALUES (
                @relationshipIdentity, @candidateGeneration, @candidateFingerprint,
                @expectedKind, @expectedGeneration, @expectedFingerprint,
                @candidateInputDigest, @phase, 0, NULL, NULL);
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
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE {StateTable}
            SET phase = @phase,
                failure_code = @failureCode,
                failure_correlation = @failureCorrelation
            WHERE relationship_identity = @relationshipIdentity
              AND candidate_generation = @candidateGeneration
              AND candidate_fingerprint = @candidateFingerprint;
            """;
        AddCandidateParameters(command);
        command.Parameters.AddWithValue("@phase", (int)SqliteRelationshipTransitionPhase.Failed);
        command.Parameters.AddWithValue("@failureCode", RelationshipMaterializationDanglingReference.DiagnosticCode);
        command.Parameters.AddWithValue("@failureCorrelation", diagnostic.TargetKeyCorrelationIdentity.Value);
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
                   phase, processed_source_count, failure_code, failure_correlation
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
            reader.IsDBNull(7) ? null : reader.GetString(7));
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

    private static async Task<long> CountAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
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

        var input = framed.ToArray();
        try
        {
            var digest = HMACSHA256.HashData(diagnosticKey, input);
            try
            {
                return CandidateInputDigestScheme + Convert.ToHexString(digest).ToLowerInvariant();
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
        string? FailureCorrelation)
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
    long ReferenceCount,
    long FenceCount);

internal sealed record SqliteRelationshipTransitionTestOptions(
    bool ThrowAfterActivationCommit,
    bool ThrowAfterValidationCommit = false,
    int? CancelAfterProcessedSourceCount = null)
{
    public static SqliteRelationshipTransitionTestOptions None { get; } = new(false);
}

internal sealed class SqliteRelationshipTransitionAcknowledgementLostException : Exception;
internal sealed class SqliteRelationshipTransitionValidationAcknowledgementLostException : Exception;
