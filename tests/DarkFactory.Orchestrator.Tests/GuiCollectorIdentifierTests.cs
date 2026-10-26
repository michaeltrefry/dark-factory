using System.Text.Json;
using DarkFactory.Orchestrator.Gateway;

namespace DarkFactory.Orchestrator.Tests;

public sealed class GuiCollectorIdentifierTests
{
    const string Runtime = "com.apple.CoreSimulator.SimRuntime.iOS-26-5";
    const string Device = "com.apple.CoreSimulator.SimDeviceType.iPhone-17";

    [Theory]
    [InlineData("iOS-26-5", "iPhone-17")]
    [InlineData(Runtime, "iPhone-17")]
    [InlineData("iOS-26-5", Device)]
    [InlineData(Runtime, Device)]
    public async Task Real_full_inventory_identifiers_accept_both_supported_pinned_spellings_without_double_prefixing(string runtime, string device)
    {
        var configuration = GuiCollectorHost.DefaultConfiguration();
        configuration["runtime"] = runtime;
        configuration["deviceType"] = device;
        var calls = new List<string>();
        var error = await Record.ExceptionAsync(() => GuiCollectorHost.VerifyAsync(configuration, CancellationToken.None,
            (program, arguments, _) => { calls.Add(program + " " + string.Join(' ', arguments)); return Probe(program, arguments); }));
        Assert.Null(error);
        Assert.Equal(runtime, configuration["runtime"]!.GetValue<string>());
        Assert.Equal(device, configuration["deviceType"]!.GetValue<string>());
        Assert.Contains("/usr/bin/xcrun simctl list runtimes -j", calls);
        Assert.Contains("/usr/bin/xcrun simctl list devicetypes -j", calls);
        Assert.DoesNotContain(calls, c => c.Contains(" boot", StringComparison.Ordinal) || c.Contains(" create", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("com.apple.CoreSimulator.SimRuntime.iOS-99-0", Device)]
    [InlineData(Runtime, "com.apple.CoreSimulator.SimDeviceType.iPhone-99")]
    [InlineData("com.apple.CoreSimulator.SimRuntime.com.apple.CoreSimulator.SimRuntime.iOS-26-5", Device)]
    [InlineData(Runtime, "com.apple.CoreSimulator.SimDeviceType.com.apple.CoreSimulator.SimDeviceType.iPhone-17")]
    public async Task A_different_or_already_double_prefixed_pin_is_not_matched_by_suffix_or_fallback(string runtime, string device)
    {
        var configuration = GuiCollectorHost.DefaultConfiguration();
        configuration["runtime"] = runtime;
        configuration["deviceType"] = device;
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuiCollectorHost.VerifyAsync(configuration, CancellationToken.None,
            (program, arguments, _) => Probe(program, arguments)));
    }

    static Task<string> Probe(string program, string[] arguments) => Task.FromResult((program, string.Join(' ', arguments)) switch {
        ("/usr/bin/xcode-select", "-p") => "/Applications/Xcode.app/Contents/Developer\n",
        ("/usr/bin/xcodebuild", "-version") => "Xcode 26.5\nBuild version 17F42\n",
        ("/usr/bin/xcodebuild", "-checkFirstLaunchStatus" or "-license check") => "",
        ("/usr/bin/xcrun", "simctl list runtimes -j") => JsonSerializer.Serialize(new { runtimes = new[] { new { identifier = Runtime, isAvailable = true } } }),
        ("/usr/bin/xcrun", "simctl list devicetypes -j") => JsonSerializer.Serialize(new { devicetypes = new[] { new { identifier = Device, productFamily = "iPhone" } } }),
        _ => throw new InvalidOperationException("Unexpected metadata probe; no native command is run.")
    });
}
