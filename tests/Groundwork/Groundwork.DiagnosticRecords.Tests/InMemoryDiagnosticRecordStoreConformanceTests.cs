namespace Groundwork.DiagnosticRecords.Tests;

public sealed class InMemoryDiagnosticRecordStoreConformanceTests : DiagnosticRecordStoreConformanceTests
{
    protected override IDiagnosticRecordStoreConformanceFixture CreateFixture() => new InMemoryDiagnosticRecordStoreFixture();
}
