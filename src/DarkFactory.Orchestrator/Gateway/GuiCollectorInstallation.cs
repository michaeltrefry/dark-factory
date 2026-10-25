using System.Text.Json.Nodes;

namespace DarkFactory.Orchestrator.Gateway;

public sealed partial class GuiCollectorDeployment
{
    internal static long VerifyInstallerReceipt(JsonObject receipt, JsonObject plan, string planHash, long? knownRunner = null)
    {
        var id = receipt["runnerId"]?.GetValue<long>() ?? 0;
        var configuration = receipt["configurationSha256"]?.GetValue<string>();
        var uid = plan["uid"]?.GetValue<int>() ?? 0;
        var helper = plan["helperSha256"]?.GetValue<string>();
        var sourceJob = plan["sourceJobSha256"]?.GetValue<string>();
        if (uid < 500 || uid == 502 || helper is null || helper.Length != 64 || helper.Any(c => !char.IsAsciiHexDigit(c))
            || sourceJob is null || sourceJob.Length != 64 || sourceJob.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidOperationException("Installer plan lacks a distinct collector UID or pinned executable hashes.");
        if (receipt["version"]?.GetValue<int>() != 1 || receipt["planSha256"]?.GetValue<string>() != planHash
            || receipt["runnerName"]?.GetValue<string>() != plan["runnerName"]?.GetValue<string>()
            || receipt["uid"]?.GetValue<int>() != plan["uid"]?.GetValue<int>() || id <= 0 || knownRunner is not null && id != knownRunner
            || receipt["helperSha256"]?.GetValue<string>() != plan["helperSha256"]?.GetValue<string>()
            || receipt["sourceJobSha256"]?.GetValue<string>() != plan["sourceJobSha256"]?.GetValue<string>()
            || configuration is null || configuration.Length != 64 || configuration.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidOperationException("Root installer receipt does not bind the exact plan, registration, payload hashes and installed configuration.");
        return id;
    }

    /// <summary>Three explicit installer boundaries: register/start; authenticated online + protected backup; exact remote
    /// old-ID deletion; verified local retirement. Delegates supply only local root invocation and durable owner checkpointing.
    /// Every permission/hash/identity proof is freshly read through this gateway, never supplied as an authority attestation.</summary>
    public async Task<JsonObject> CompleteInstallationAsync(byte[] planBytes, Evidence reservation, IReadOnlyDictionary<string, string> expectedHashes,
        long? knownRunnerId, Func<Authority, string, CancellationToken, Task<JsonObject>> installer,
        Func<long, CancellationToken, Task> recordRunnerId, CancellationToken ct, TimeSpan? onlinePoll = null, int onlineAttempts = 60)
    {
        if (onlineAttempts is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(onlineAttempts));
        var plan = JsonNode.Parse(planBytes)?.AsObject() ?? throw new InvalidOperationException("Invalid installer plan.");
        var planHash = Hash(planBytes);
        var name = plan["runnerName"]!.GetValue<string>();
        var fresh = await InspectAsync(planBytes, ct);
        var initial = Authorize(fresh, name, expectedHashes, reservation: reservation);
        var token = knownRunnerId is null ? await RegistrationTokenAsync(Collector, ct) : "";
        var registered = await installer(initial, token, ct);
        token = "";
        var runnerId = VerifyInstallerReceipt(registered, plan, planHash, knownRunnerId);
        if (registered["status"]?.GetValue<string>() is not ("awaiting-owner-online-verification-old-untouched" or "complete"))
            throw new InvalidOperationException("Registration stage returned no accepted protected receipt; old registration untouched.");
        // Registration already occurred. Save its identity on a non-cancelled token before any subsequent authority boundary.
        await recordRunnerId(runnerId, CancellationToken.None);
        var rootWasRetired = registered["stages"] is JsonArray priorStages && priorStages.Any(s => s?.GetValue<string>() == "old-retired");
        Runner? online = null;
        for (var attempt = 0; attempt < onlineAttempts; attempt++)
        {
            online = (await RunnersAsync(Collector, ct)).SingleOrDefault(r => r.Id == runnerId && r.Name == name
                && r.Status == "online" && (r.Labels.SequenceEqual([BootstrapLabel(planHash)]) || rootWasRetired && r.Labels.SequenceEqual([Label])));
            if (online is not null) break;
            if (attempt + 1 < onlineAttempts) await Task.Delay(onlinePoll ?? TimeSpan.FromSeconds(2), time, ct);
        }
        if (online is null) throw new InvalidOperationException("Replacement was not authenticated online with only its isolated custom label; old registration untouched. Resume retries this stage.");
        fresh = await InspectAsync(planBytes, ct);
        var backup = await installer(Authorize(fresh, name, expectedHashes, online, retiredReceipt: rootWasRetired ? registered : null), "", ct);
        VerifyInstallerReceipt(backup, plan, planHash, runnerId);
        if (backup["status"]?.GetValue<string>() is not ("awaiting-owner-exact-old-registration-removal" or "complete")
            || backup["stages"] is not JsonArray stages || !stages.Any(s => s?.GetValue<string>() == "old-backup-protected"))
            throw new InvalidOperationException("Protected exact old credential/service backup was not confirmed; old registration untouched.");
        var old = plan["old"]!.AsObject();
        var removed = await RetireAsync(Collector, online, old["runnerId"]!.GetValue<long>(), old["runnerName"]!.GetValue<string>(), ct, SourceRepository);
        fresh = await InspectAsync(planBytes, ct);
        var complete = await installer(Authorize(fresh, name, expectedHashes, online, removed, retiredReceipt: rootWasRetired ? backup : null), "", ct);
        VerifyInstallerReceipt(complete, plan, planHash, runnerId);
        if (complete["status"]?.GetValue<string>() != "complete")
            throw new InvalidOperationException("Local retirement is not complete; retain the protected backup and owner journal, then resume.");
        return complete;
    }

    /// <summary>Publish the only production job label AFTER durable local retirement and settings/configuration persistence.
    /// A bootstrap-only runner is online but cannot receive any sealed GUI jobs. Lost PUT answers are reconciled by GET.</summary>
    public async Task PromoteRunnerAsync(byte[] planBytes, JsonObject retiredReceipt, CancellationToken ct)
    {
        var plan = JsonNode.Parse(planBytes)!.AsObject();
        var hash = Hash(planBytes);
        var id = VerifyInstallerReceipt(retiredReceipt, plan, hash);
        if (retiredReceipt["status"]?.GetValue<string>() != "complete" || retiredReceipt["stages"] is not JsonArray stages
            || !stages.Any(s => s?.GetValue<string>() == "old-retired"))
            throw new InvalidOperationException("Final job labels cannot be exposed before verified durable local retirement.");
        var evidence = await InspectAsync(planBytes, ct);
        var runner = evidence.Runners.SingleOrDefault(r => r.Id == id && r.Name == plan["runnerName"]!.GetValue<string>() && r.Status == "online")
            ?? throw new InvalidOperationException("Retired collector registration is not authenticated online.");
        _ = Authorize(evidence, runner.Name, evidence.Workflows.ToDictionary(w => w.Path, w => w.Sha256), runner,
            plan["old"]!["runnerId"]!.GetValue<long>(), retiredReceipt: retiredReceipt);
        var installedPin = await SendAsync(HttpMethod.Get, RepoPath(SourceRepository) + "/environments/" + Gate.RemoteGuiSourceWorkflow.Environment
            + "/variables/FACTORY_GUI_CONFIGURATION_SHA256", ownerToken, null, ct);
        if (installedPin["value"]?.GetValue<string>() != retiredReceipt["configurationSha256"]?.GetValue<string>())
            throw new InvalidOperationException("Protected dispatcher configuration pin changed; production labels remain withheld.");
        if (runner.Labels.SequenceEqual([Label])) return;
        if (!runner.Labels.SequenceEqual([BootstrapLabel(hash)]))
            throw new InvalidOperationException("Collector label inventory changed; unexpected labels are not overwritten.");
        try
        {
            await SendAsync(HttpMethod.Put, RepoPath(Collector) + $"/actions/runners/{id}/labels", ownerToken,
                new JsonObject { ["labels"] = new JsonArray(Label) }, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            var reconciled = (await RunnersAsync(Collector, ct)).SingleOrDefault(r => r.Id == id && r.Name == runner.Name);
            if (reconciled is null || !reconciled.Labels.SequenceEqual([Label])) throw;
        }
        var confirmed = (await RunnersAsync(Collector, ct)).SingleOrDefault(r => r.Id == id && r.Name == runner.Name && r.Status == "online");
        if (confirmed is null || !confirmed.Labels.SequenceEqual([Label]))
            throw new InvalidOperationException("Final collector label promotion was not confirmed; resume reconciles it before claiming readiness.");
    }
}
