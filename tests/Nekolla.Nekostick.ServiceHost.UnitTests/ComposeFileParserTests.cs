using Xunit;
using Nekolla.Nekostick.ServiceHost.Compose;

namespace Nekolla.Nekostick.ServiceHost.UnitTests;

public sealed class ComposeFileParserTests
{
    private readonly ComposeFileParser _parser = new();

    [Fact]
    public void Parse_valid_document_builds_complete_model()
    {
        var sha256 = new string('a', 64);
        var document = _parser.Parse($"""
            services:
              api:
                source:
                  url: https://example.com/releases/api
                  sha256: {sha256}
                args: ["--port", "$PORT"]
                env:
                  MODE: production
                start: lazy
                restart: always
                health:
                  type: http
                  path: /healthz
                  timeout: 250ms
                route:
                  prefix: /api
                  strip: true
                  methods: [GET, POST]
                  hosts: [api.example.com]
            """);

        var service = Assert.Single(document.Services);
        Assert.Equal("api", service.Key);
        Assert.Equal("https://example.com/releases/api", service.Value.Source.Url);
        Assert.Null(service.Value.Source.Path);
        Assert.Equal(sha256, service.Value.Source.Sha256);
        Assert.Equal(["--port", "$PORT"], service.Value.Args.ToArray());
        Assert.Equal("production", service.Value.Environment["MODE"]);
        Assert.Equal(ComposeStartMode.Lazy, service.Value.Start);
        Assert.Equal("/healthz", service.Value.Health.Path);
        Assert.Equal(TimeSpan.FromMilliseconds(250), service.Value.Health.Timeout);
        Assert.NotNull(service.Value.Route);
        Assert.Equal("/api", service.Value.Route!.Prefix);
        Assert.True(service.Value.Route.Strip);
        Assert.Equal(["GET", "POST"], service.Value.Route.Methods.ToArray());
        Assert.Equal(["api.example.com"], service.Value.Route.Hosts.ToArray());
    }

    [Theory]
    [InlineData("version: 1\nservices:\n  api:\n    source: { path: /tmp/api }", "document.version")]
    [InlineData("services:\n  api:\n    command: run\n    source: { path: /tmp/api }", "services.api.command")]
    [InlineData("services:\n  api:\n    source:\n      path: /tmp/api\n      checksum: abc", "services.api.source.checksum")]
    [InlineData("services:\n  api:\n    source: { path: /tmp/api }\n    health:\n      type: process\n      interval: 1s", "services.api.health.interval")]
    [InlineData("services:\n  api:\n    source: { path: /tmp/api }\n    route:\n      prefix: /api\n      rewrite: /v1", "services.api.route.rewrite")]
    public void Parse_unknown_fields_at_each_mapping_level_rejects_document(string yaml, string expectedPath)
    {
        var exception = Assert.Throws<ComposeValidationException>(() => _parser.Parse(yaml));

        Assert.Contains(exception.Errors, error => error.Path == expectedPath);
    }

    [Fact]
    public void Parse_rejects_source_with_both_url_and_path()
    {
        var exception = Assert.Throws<ComposeValidationException>(() => _parser.Parse("""
            services:
              api:
                source:
                  url: https://example.com/api
                  path: /tmp/api
            """));

        Assert.Contains(exception.Errors, error => error.Path == "services.api.source");
    }

    [Fact]
    public void Parse_rejects_http_source_url()
    {
        var exception = Assert.Throws<ComposeValidationException>(() => _parser.Parse("""
            services:
              api:
                source:
                  url: http://example.com/api
            """));

        Assert.Contains(exception.Errors, error => error.Path == "services.api.source.url");
    }

    [Theory]
    [InlineData("-api")]
    [InlineData("Api")]
    [InlineData("api_1")]
    public void Parse_rejects_service_names_outside_name_pattern(string serviceName)
    {
        var exception = Assert.Throws<ComposeValidationException>(() => _parser.Parse($"""
            services:
              {serviceName}:
                source:
                  path: /tmp/api
            """));

        Assert.Contains(exception.Errors, error => error.Path == $"services.{serviceName}");
    }

    [Fact]
    public void Parse_rejects_service_name_longer_than_63_characters()
    {
        var serviceName = new string('a', 64);
        var exception = Assert.Throws<ComposeValidationException>(() => _parser.Parse($"""
            services:
              {serviceName}:
                source:
                  path: /tmp/api
            """));

        Assert.Contains(exception.Errors, error => error.Path == $"services.{serviceName}");
    }

    [Fact]
    public void Parse_rejects_http_health_without_path()
    {
        var exception = Assert.Throws<ComposeValidationException>(() => _parser.Parse("""
            services:
              api:
                source: { path: /tmp/api }
                health:
                  type: http
            """));

        Assert.Contains(exception.Errors, error => error.Path == "services.api.health.path");
    }

    [Fact]
    public void Parse_rejects_route_prefix_without_leading_slash()
    {
        var exception = Assert.Throws<ComposeValidationException>(() => _parser.Parse("""
            services:
              api:
                source: { path: /tmp/api }
                route:
                  prefix: api
            """));

        Assert.Contains(exception.Errors, error => error.Path == "services.api.route.prefix");
    }

    [Theory]
    [InlineData("250ms", 250)]
    [InlineData("1.5s", 1500)]
    [InlineData("2m", 120000)]
    [InlineData("1h", 3600000)]
    public void Parse_duration_supports_milliseconds_seconds_minutes_and_hours(string duration, int expectedMilliseconds)
    {
        var document = _parser.Parse($"""
            services:
              api:
                source:
                  path: /tmp/api
                health:
                  type: tcp
                  timeout: {duration}
            """);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), document.Services["api"].Health.Timeout);
    }

    [Fact]
    public void Parse_defaults_start_restart_and_health()
    {
        var document = _parser.Parse("""
            services:
              api:
                source: { path: /tmp/api }
            """);

        var service = document.Services["api"];
        Assert.Equal(ComposeStartMode.Eager, service.Start);
        Assert.Equal(ComposeRestartPolicy.OnFailure, service.Restart);
        Assert.Equal(ComposeHealthCheckType.Process, service.Health.Type);
        Assert.Null(service.Health.Path);
        Assert.Equal(TimeSpan.FromSeconds(5), service.Health.Timeout);
    }

    [Theory]
    [InlineData("url: https://example.com/api")]
    [InlineData("path: /tmp/api")]
    public void Parse_strict_sources_rejects_unpinned_url_or_path(string sourceDeclaration)
    {
        var exception = Assert.Throws<ComposeValidationException>(() => _parser.Parse($"""
            strictSources: true
            services:
              api:
                source:
                  {sourceDeclaration}
            """));

        Assert.Contains(exception.Errors, error =>
            error.Path == "services.api.source.sha256" &&
            error.Message.Contains("strictSources", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_without_strict_sources_records_warning_for_unpinned_path()
    {
        var document = _parser.Parse("""
            services:
              api:
                source:
                  path: /tmp/api
            """);

        Assert.False(document.StrictSources);
        var warning = Assert.Single(document.Services["api"].Warnings);
        Assert.Contains("sha256", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_declared_sha256_has_no_warning_in_either_mode(bool strictSources)
    {
        var sha256 = new string('a', 64);
        var document = _parser.Parse($"""
            strictSources: {strictSources.ToString().ToLowerInvariant()}
            services:
              api:
                source:
                  url: https://example.com/api
                  sha256: {sha256}
            """);

        Assert.Equal(strictSources, document.StrictSources);
        Assert.Empty(document.Services["api"].Warnings);
    }
}
