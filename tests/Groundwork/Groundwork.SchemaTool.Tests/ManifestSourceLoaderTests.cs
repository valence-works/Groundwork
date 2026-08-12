using System.Text.Json;
using Groundwork.SchemaTool;
using Xunit;

namespace Groundwork.SchemaTool.Tests;

public sealed class ManifestSourceLoaderTests
{
    [Fact]
    public async Task Explicit_source_selection_does_not_load_unrelated_types()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var assembly = Path.Combine(
            AppContext.BaseDirectory,
            "ExplicitSourceFixture",
            "Groundwork.SchemaTool.ExplicitSourceFixture.dll");

        Assert.DoesNotContain(
            AppDomain.CurrentDomain.GetAssemblies(),
            candidate => candidate.GetName().Name == "Microsoft.Extensions.Identity.Stores");

        var exitCode = await GroundworkSchemaCli.RunAsync(
        [
            "validate",
            "--manifest-assembly", assembly,
            "--manifest-type", "Groundwork.SchemaTool.ExplicitSourceFixture.ExplicitManifestSource",
            "--provider", "sqlite",
            "--output", "json",
            "--offline"
        ], output, error);

        Assert.Equal(SchemaToolExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var report = JsonDocument.Parse(output.ToString());
        Assert.Equal(
            "explicit-source-fixture",
            report.RootElement.GetProperty("target").GetProperty("manifestIdentity").GetString());
    }

    [Theory]
    [InlineData("authoring")]
    [InlineData("runtime")]
    public async Task A_configurable_source_deploys_the_schema_the_options_select(string partition)
    {
        var output = new StringWriter();

        var exitCode = await RunConfigurableAsync(output, new StringWriter(), $"partition={partition}");

        Assert.Equal(SchemaToolExitCodes.Success, exitCode);
        using var report = JsonDocument.Parse(output.ToString());
        Assert.Equal(
            $"configurable-fixture.{partition}",
            report.RootElement.GetProperty("target").GetProperty("manifestIdentity").GetString());
    }

    [Fact]
    public async Task A_configurable_source_given_no_options_refuses_in_its_own_words()
    {
        var output = new StringWriter();

        var exitCode = await RunConfigurableAsync(output, new StringWriter());

        // The source is the authority on what its options mean, so the tool carries its refusal through
        // rather than restating it, and never falls back to some default schema.
        Assert.Equal(SchemaToolExitCodes.InvalidInvocation, exitCode);
        Assert.Contains("partition", ErrorMessage(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Options_given_to_a_source_that_takes_none_are_an_error_rather_than_ignored()
    {
        var output = new StringWriter();

        var exitCode = await GroundworkSchemaCli.RunAsync(
        [
            "validate",
            "--manifest-assembly", FixtureAssembly,
            "--manifest-type", "Groundwork.SchemaTool.ExplicitSourceFixture.ExplicitManifestSource",
            "--provider", "sqlite",
            "--output", "json",
            "--offline",
            "--manifest-option", "partition=authoring"
        ], output, new StringWriter());

        Assert.Equal(SchemaToolExitCodes.InvalidInvocation, exitCode);
        Assert.Contains("--manifest-option", ErrorMessage(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_manifest_option_without_a_value_is_rejected()
    {
        var error = new StringWriter();

        // Parse diagnostics reach the operator verbatim only in human output; the JSON report keeps a
        // fixed shape and points at '--help'.
        var exitCode = await RunConfigurableAsync(new StringWriter(), error, human: true, "partition");

        Assert.Equal(SchemaToolExitCodes.InvalidInvocation, exitCode);
        Assert.Contains("key=value", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_manifest_option_twice_is_rejected_rather_than_silently_last_wins()
    {
        var error = new StringWriter();

        var exitCode = await RunConfigurableAsync(
            new StringWriter(),
            error,
            human: true,
            "partition=authoring",
            "partition=runtime");

        Assert.Equal(SchemaToolExitCodes.InvalidInvocation, exitCode);
        Assert.Contains("more than once", error.ToString(), StringComparison.Ordinal);
    }

    private static string ErrorMessage(StringWriter output)
    {
        using var report = JsonDocument.Parse(output.ToString());
        return string.Join(
            Environment.NewLine,
            report.RootElement.GetProperty("diagnostics")
                .EnumerateArray()
                .Select(diagnostic => diagnostic.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task A_manifest_option_value_may_contain_equals_signs()
    {
        var error = new StringWriter();

        // Paths and connection fragments carry '=', so only the first one separates.
        var exitCode = await RunConfigurableAsync(new StringWriter(), error, "partition=a=b");

        Assert.Equal(SchemaToolExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, error.ToString());
    }

    private static string FixtureAssembly => Path.Combine(
        AppContext.BaseDirectory,
        "ExplicitSourceFixture",
        "Groundwork.SchemaTool.ExplicitSourceFixture.dll");

    private static Task<int> RunConfigurableAsync(
        TextWriter output,
        TextWriter error,
        params string[] manifestOptions) =>
        RunConfigurableAsync(output, error, human: false, manifestOptions);

    private static Task<int> RunConfigurableAsync(
        TextWriter output,
        TextWriter error,
        bool human,
        params string[] manifestOptions)
    {
        string[] arguments =
        [
            "validate",
            "--manifest-assembly", FixtureAssembly,
            "--manifest-type", "Groundwork.SchemaTool.ExplicitSourceFixture.ConfigurableManifestSource",
            "--provider", "sqlite",
            "--output", human ? "human" : "json",
            "--offline",
            .. manifestOptions.SelectMany(option => new[] { "--manifest-option", option })
        ];
        return GroundworkSchemaCli.RunAsync(arguments, output, error);
    }
}
