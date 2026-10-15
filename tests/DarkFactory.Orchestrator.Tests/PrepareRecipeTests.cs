using System.Net;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.Prepare;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>sc-26232: the prepare recipe's strict, versioned schema (<c>.factory/prepare.yaml</c>), and where it sits in the gate's floor.</summary>
public class PrepareRecipeTests
{
    internal const string Sha = "740fbe95c69e09e9cd17654a5e4fef2d7021254b06fd2b8c5557b79a1496b50c";

    /// <summary>The LensWorks recipe (docs/prepare-recipes.md): the DNG SDK, then the Rust catalog library the Xcode build links.</summary>
    internal const string LensWorks = $$"""
        version: 1
        artifacts:
          - name: dng-sdk
            url: https://download.adobe.com/pub/adobe/dng/dng_sdk_1_7_1_2724_20260908.zip
            sha256: {{Sha}}
            max_bytes: 209715200
            unpack: zip
        steps:
          - name: rust-catalog
            argv: [/opt/homebrew/bin/cargo, build, --manifest-path, adapters/catalog/Cargo.toml, --locked, --release]
            env:
              CARGO_TARGET_DIR: ${worktree}/.build/rust-catalog
              PHOTOCATALOG_DNG_SDK: ${artifact:dng-sdk}/dng_sdk_1_7_1
              MACOSX_DEPLOYMENT_TARGET: "26.2"
            timeout_minutes: 20
        """;

    [Fact]
    public void The_lensworks_recipe_parses_with_every_value_as_written()
    {
        var recipe = PrepareRecipe.Parse(LensWorks);

        var artifact = Assert.Single(recipe.Artifacts);
        Assert.Equal(("dng-sdk", "https://download.adobe.com/pub/adobe/dng/dng_sdk_1_7_1_2724_20260908.zip", Sha, 209715200L, ArtifactUnpack.Zip, 4 * 209715200L),
            (artifact.Name, artifact.Url.ToString(), artifact.Sha256, artifact.MaxBytes, artifact.Unpack, artifact.MaxUnpackedBytes));
        var step = Assert.Single(recipe.Steps);
        Assert.Equal("/opt/homebrew/bin/cargo", step.Argv[0]);
        Assert.Equal(".", step.WorkingDirectory);
        Assert.Equal(TimeSpan.FromMinutes(20), step.Timeout);
        Assert.Equal("${artifact:dng-sdk}/dng_sdk_1_7_1", step.Environment["PHOTOCATALOG_DNG_SDK"]);
        Assert.Equal("26.2", step.Environment["MACOSX_DEPLOYMENT_TARGET"]);
        Assert.Equal("/w/.build/rust-catalog", PrepareRecipe.Expand(step.Environment["CARGO_TARGET_DIR"], "/w", new Dictionary<string, string>()));
        Assert.Equal("/cache/x/content/dng_sdk_1_7_1",
            PrepareRecipe.Expand(step.Environment["PHOTOCATALOG_DNG_SDK"], "/w", new Dictionary<string, string> { ["dng-sdk"] = "/cache/x/content" }));
    }

    [Fact]
    public void A_recipe_with_steps_only_and_a_working_directory_parses()
    {
        var recipe = PrepareRecipe.Parse("version: 1\nsteps:\n  - name: gen\n    argv: [make, gen]\n    cwd: tools/gen/\n");

        Assert.Empty(recipe.Artifacts);
        Assert.Equal("tools/gen", recipe.Steps[0].WorkingDirectory);
        Assert.Null(recipe.Steps[0].Timeout);
    }

    public static TheoryData<string, string> Refused => new()
    {
        { "not: [valid", "is not valid YAML" },
        { "- a\n- b\n", "must be one YAML mapping" },
        { "version: 2\nsteps: [{name: a, argv: [make]}]\n", "version must be 1" },
        { "steps: [{name: a, argv: [make]}]\n", "is missing 'version'" },
        { "version: 1\n", "is missing 'steps'" },
        { "version: 1\nsteps: []\n", "steps must be a list of 1 to" },
        { "version: 1\nextra: 1\nsteps: [{name: a, argv: [make]}]\n", "unknown key 'extra'" },
        { "version: 1\nsteps: [{name: a, argv: [make], shell: true}]\n", "unknown key 'shell'" },
        { "version: 1\nsteps: [{name: a, argv: []}]\n", "argv must be a list of 1" },
        { "version: 1\nsteps: [{name: a, argv: 'make all'}]\n", "argv must be a list" },
        { "version: 1\nsteps: [{name: A!, argv: [make]}]\n", "name must be 1-64 characters" },
        { "version: 1\nsteps: [{name: a, argv: [make]}, {name: a, argv: [make]}]\n", "the name 'a' is used twice" },
        { "version: 1\nsteps: [{name: a, argv: [bin/make]}]\n", "must be an absolute path" },
        { "version: 1\nsteps: [{name: a, argv: [-rf]}]\n", "must be an absolute path" },
        { "version: 1\nsteps: [{name: a, argv: [/usr/bin/../bin/make]}]\n", "no '.' or '..' path segment" },
        { "version: 1\nsteps: [{name: a, argv: [curl, -o, x, 'https://e.com/x']}]\n", "may not run curl" },
        { "version: 1\nsteps: [{name: a, argv: [/usr/bin/wget, x]}]\n", "may not run wget" },
        { "version: 1\nsteps: [{name: a, argv: [sudo, make]}]\n", "may not run sudo" },
        { "version: 1\nsteps: [{name: a, argv: [make], cwd: ../x}]\n", "cwd must be a relative directory" },
        { "version: 1\nsteps: [{name: a, argv: [make], cwd: /tmp}]\n", "cwd must be a relative directory" },
        { "version: 1\nsteps: [{name: a, argv: [make], env: {PATH: /evil}}]\n", "may not set PATH" },
        { "version: 1\nsteps: [{name: a, argv: [make], env: {DYLD_INSERT_LIBRARIES: x}}]\n", "may not set DYLD_INSERT_LIBRARIES" },
        { "version: 1\nsteps: [{name: a, argv: [make], env: {ANTHROPIC_BASE_URL: x}}]\n", "may not set ANTHROPIC_BASE_URL" },
        { "version: 1\nsteps: [{name: a, argv: [make], env: {1X: y}}]\n", "is not a variable name" },
        { "version: 1\nsteps: [{name: a, argv: [make, '${home}']}]\n", "unknown placeholder ${home}" },
        { "version: 1\nsteps: [{name: a, argv: [make, '${artifact:sdk}']}]\n", "does not declare: ${artifact:sdk}" },
        { "version: 1\nsteps: [{name: a, argv: [make, '${worktree']}]\n", "unterminated placeholder" },
        { "version: 1\nsteps: [{name: a, argv: [make], timeout_minutes: 0}]\n", "timeout_minutes must be a whole number from 1 to 120" },
        { "version: 1\nsteps: [{name: a, argv: [make], timeout_minutes: 121}]\n", "timeout_minutes must be a whole number from 1 to 120" },
        { $"version: 1\nartifacts: [{{name: s, url: 'http://e.com/a', sha256: {Sha}, max_bytes: 1}}]\nsteps: [{{name: a, argv: [make]}}]\n", "must be an https URL" },
        { $"version: 1\nartifacts: [{{name: s, url: 'https://u:p@e.com/a', sha256: {Sha}, max_bytes: 1}}]\nsteps: [{{name: a, argv: [make]}}]\n", "user name or password" },
        { $"version: 1\nartifacts: [{{name: s, url: 'https://localhost/a', sha256: {Sha}, max_bytes: 1}}]\nsteps: [{{name: a, argv: [make]}}]\n", "names the local host" },
        { $"version: 1\nartifacts: [{{name: s, url: 'https://10.1.2.3/a', sha256: {Sha}, max_bytes: 1}}]\nsteps: [{{name: a, argv: [make]}}]\n", "a private (10/8) address" },
        { $"version: 1\nartifacts: [{{name: s, url: 'https://[::1]/a', sha256: {Sha}, max_bytes: 1}}]\nsteps: [{{name: a, argv: [make]}}]\n", "the loopback address" },
        { $"version: 1\nartifacts: [{{name: s, url: 'https://e.com/a#x', sha256: {Sha}, max_bytes: 1}}]\nsteps: [{{name: a, argv: [make]}}]\n", "fragment" },
        { "version: 1\nartifacts: [{name: s, url: 'https://e.com/a', sha256: abc, max_bytes: 1}]\nsteps: [{name: a, argv: [make]}]\n", "sha256 must be 64 hexadecimal" },
        { $"version: 1\nartifacts: [{{name: s, url: 'https://e.com/a', sha256: {Sha}}}]\nsteps: [{{name: a, argv: [make]}}]\n", "is missing 'max_bytes'" },
        { $"version: 1\nartifacts: [{{name: s, url: 'https://e.com/a', sha256: {Sha}, max_bytes: 0}}]\nsteps: [{{name: a, argv: [make]}}]\n", "max_bytes must be a whole number" },
        { $"version: 1\nartifacts: [{{name: s, url: 'https://e.com/a', sha256: {Sha}, max_bytes: 99999999999}}]\nsteps: [{{name: a, argv: [make]}}]\n", "max_bytes must be a whole number" },
        { $"version: 1\nartifacts: [{{name: s, url: 'https://e.com/a', sha256: {Sha}, max_bytes: 1, unpack: rar}}]\nsteps: [{{name: a, argv: [make]}}]\n", "unpack must be none, zip, tar or tar.gz" },
        { $"version: 1\nartifacts: [{{name: s, url: 'https://e.com/a', sha256: {Sha}, max_bytes: 1, max_unpacked_bytes: 5}}]\nsteps: [{{name: a, argv: [make]}}]\n", "only for an artifact that is unpacked" },
        { $"version: 1\nartifacts: [{{name: s, url: 'https://e.com/a', sha256: {Sha}, max_bytes: 1}}, {{name: s, url: 'https://e.com/b', sha256: {Sha}, max_bytes: 1}}]\nsteps: [{{name: a, argv: [make]}}]\n", "the name 's' is declared twice" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void A_recipe_off_the_schema_is_refused_saying_why(string yaml, string why)
    {
        var refused = Assert.Throws<PrepareRecipeException>(() => PrepareRecipe.Parse(yaml));

        Assert.StartsWith(PrepareRecipe.Path, refused.Message);
        Assert.Contains(why, refused.Message);
    }

    [Fact]
    public void A_program_may_be_a_cached_artifacts_path_but_must_lead_with_the_placeholder()
    {
        var recipe = PrepareRecipe.Parse($"version: 1\nartifacts: [{{name: tool, url: 'https://e.com/t.tar.gz', sha256: {Sha}, max_bytes: 10, unpack: tar.gz}}]\n"
            + "steps: [{name: a, argv: ['${artifact:tool}/bin/gen', '${worktree}/out']}]\n");

        Assert.Equal("${artifact:tool}/bin/gen", recipe.Steps[0].Argv[0]);
        Assert.Throws<PrepareRecipeException>(() => PrepareRecipe.Parse("version: 1\nsteps: [{name: a, argv: ['x${worktree}']}]\n"));
    }

    [Theory]
    [InlineData(".factory/prepare.yaml")]
    [InlineData(".FACTORY/anything.yml")]
    public void The_recipe_always_calls_the_security_review_in(string path)
    {
        Assert.Contains($"{path} (the prepare recipe)", RiskyPaths.Touched([path]));
    }
}

/// <summary>sc-26232: which addresses and URLs an artifact may be downloaded from (SSRF), judged before anything is requested and again at connect.</summary>
public class ArtifactAddressTests
{
    [Theory]
    [InlineData("127.0.0.1", "loopback")]
    [InlineData("10.0.0.5", "private (10/8)")]
    [InlineData("172.16.9.1", "private (172.16/12)")]
    [InlineData("192.168.1.1", "private (192.168/16)")]
    [InlineData("100.64.0.1", "CGNAT")]
    [InlineData("100.127.255.254", "CGNAT")]
    [InlineData("169.254.169.254", "link-local")]
    [InlineData("0.0.0.0", "unspecified")]
    [InlineData("224.0.0.1", "multicast")]
    [InlineData("255.255.255.255", "reserved or broadcast")]
    [InlineData("198.18.0.1", "benchmarking")]
    [InlineData("::1", "loopback")]
    [InlineData("::", "unspecified")]
    [InlineData("fd00::1", "unique-local")]
    [InlineData("fe80::1", "link-local")]
    [InlineData("ff02::1", "multicast")]
    [InlineData("::ffff:10.0.0.1", "mapped")]
    [InlineData("::ffff:127.0.0.1", "mapped")]
    [InlineData("64:ff9b::a00:1", "NAT64")]
    [InlineData("2002:0a00:0001::1", "6to4")]
    [InlineData("2001:db8::1", "documentation")]
    public void A_non_public_address_is_refused(string address, string why)
    {
        Assert.Contains(why, ArtifactAddresses.Blocked(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("100.128.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700::1111")]
    [InlineData("::ffff:93.184.216.34")]
    public void A_public_address_is_allowed(string address)
    {
        Assert.Null(ArtifactAddresses.Blocked(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task A_host_is_refused_if_any_address_it_resolves_to_is_not_public_and_pinned_to_the_first_otherwise()
    {
        var resolver = new FakeResolver { ["mixed.example"] = ["93.184.216.34", "10.0.0.7"], ["good.example"] = ["93.184.216.34", "2606:4700::1111"], ["none.example"] = [] };

        var mixed = await Assert.ThrowsAsync<ArtifactAddressException>(() => ArtifactAddresses.ChooseAsync("mixed.example", resolver, CancellationToken.None));
        Assert.Contains("10.0.0.7", mixed.Message);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), await ArtifactAddresses.ChooseAsync("good.example", resolver, CancellationToken.None));
        await Assert.ThrowsAsync<ArtifactAddressException>(() => ArtifactAddresses.ChooseAsync("none.example", resolver, CancellationToken.None));
        // An IP literal is judged as itself, with no lookup.
        await Assert.ThrowsAsync<ArtifactAddressException>(() => ArtifactAddresses.ChooseAsync("[::1]", resolver, CancellationToken.None));
        Assert.Equal(["mixed.example", "good.example", "none.example"], resolver.Asked);
    }

    [Theory]
    [InlineData("https://download.adobe.com/x.zip", null)]
    [InlineData("http://download.adobe.com/x.zip", "https")]
    [InlineData("ftp://e.com/x", "https")]
    [InlineData("https://sub.localhost/x", "local host")]
    [InlineData("https://169.254.169.254/latest/meta-data", "link-local")]
    public void A_url_is_checked_before_anything_is_resolved(string url, string? why)
    {
        var problem = ArtifactUrls.Problem(new Uri(url));

        if (why is null)
        {
            Assert.Null(problem);
        }
        else
        {
            Assert.Contains(why, problem);
        }
    }
}

/// <summary>A resolver answering from a table (and recording what it was asked).</summary>
internal sealed class FakeResolver : IArtifactResolver
{
    private readonly Dictionary<string, string[]> _answers = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Asked { get; } = [];

    public string[] this[string host]
    {
        set => _answers[host] = value;
    }

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
    {
        Asked.Add(host);
        return _answers.TryGetValue(host, out var addresses)
            ? Task.FromResult<IReadOnlyList<IPAddress>>(addresses.Select(IPAddress.Parse).ToList())
            : Task.FromException<IReadOnlyList<IPAddress>>(new System.Net.Sockets.SocketException(11001));
    }
}
