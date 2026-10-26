using System.Text.Json.Nodes;

namespace DarkFactory.Orchestrator.Gateway;

public sealed partial class GuiCollectorDeployment
{
    internal static long VerifyInstallerReceipt(JsonObject receipt, JsonObject plan, string planHash, long? knownRunner = null)
    {
        VerifyInstallerBinding(receipt, plan, planHash);
        var id = receipt["runnerId"]?.GetValue<long>() ?? 0;
        if (id <= 0 || knownRunner is not null && id != knownRunner)
            throw new InvalidOperationException("Root installer receipt does not bind the exact registration identity.");
        return id;
    }

    static void VerifyInstallerBinding(JsonObject receipt, JsonObject plan, string planHash)
    {
        var configuration = receipt["configurationSha256"]?.GetValue<string>();
        var uid = plan["uid"]?.GetValue<int>() ?? 0;
        var helper = plan["helperSha256"]?.GetValue<string>();
        var sourceJob = plan["sourceJobSha256"]?.GetValue<string>();
        if (uid < 500 || uid == 502 || helper is null || helper.Length != 64 || helper.Any(c => !char.IsAsciiHexDigit(c))
            || sourceJob is null || sourceJob.Length != 64 || sourceJob.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidOperationException("Installer plan lacks a distinct collector UID or pinned executable hashes.");
        if (receipt["version"]?.GetValue<int>() != 1 || receipt["planSha256"]?.GetValue<string>() != planHash
            || receipt["runnerName"]?.GetValue<string>() != plan["runnerName"]?.GetValue<string>()
            || receipt["uid"]?.GetValue<int>() != plan["uid"]?.GetValue<int>()
            || receipt["helperSha256"]?.GetValue<string>() != plan["helperSha256"]?.GetValue<string>()
            || receipt["sourceJobSha256"]?.GetValue<string>() != plan["sourceJobSha256"]?.GetValue<string>()
            || configuration is null || configuration.Length != 64 || configuration.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidOperationException("Root installer receipt does not bind the exact plan, registration, payload hashes and installed configuration.");
    }

    internal async Task<Authority> RegistrationAbsenceAsync(byte[] planBytes, Evidence reservation, IReadOnlyDictionary<string, string> expectedHashes,
        JsonObject challenge, CancellationToken ct)
    {
        var plan = JsonNode.Parse(planBytes)!.AsObject();
        var hash = Hash(planBytes);
        VerifyInstallerBinding(challenge, plan, hash);
        var intent = challenge["registrationIntent"] as JsonObject;
        var now = time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
        var created = intent?["createdAt"]?.GetValue<double>() ?? 0;
        var first = challenge["firstStartedAt"]?.GetValue<double>() ?? 0;
        var attempts = challenge["attempts"]?.GetValue<int>() ?? 0;
        var nonce = intent?["attemptNonce"]?.GetValue<string>();
        var state = intent?["state"]?.GetValue<string>();
        if (challenge["status"]?.GetValue<string>() != "registration-reconciliation-required" || challenge["runnerId"] is not null
            || nonce is null || !System.Text.RegularExpressions.Regex.IsMatch(nonce, @"\A[0-9a-f]{32}\z")
            || intent?["localUnstartedVerified"]?.GetValue<bool>() != true || state is not ("reserved" or "unreleased")
            || !double.IsFinite(created) || !double.IsFinite(first) || first <= 0 || created < first || created > now
            || now > first + 3600 || attempts is < 1 or >= 2)
            throw new InvalidOperationException("Registration is not a bounded, plan-bound, root-verified unstarted intent; uncertain POSTs are never retried.");
        if (state == "reserved" && (intent!["pid"] is not null || intent["startIdentity"] is not null)
            || state == "unreleased" && ((intent!["pid"]?.GetValue<int>() ?? 0) <= 0
                || string.IsNullOrWhiteSpace(intent["startIdentity"]?.GetValue<string>())))
            throw new InvalidOperationException("Root registration intent lacks the exact gated child identity proof.");
        // This read is AFTER the persisted root intent/challenge, not a reused preparation snapshot. It revalidates
        // App/owner permissions and the complete registration inventory before any proof or fresh token is issued.
        var fresh = await InspectAsync(planBytes, ct);
        var name = plan["runnerName"]!.GetValue<string>();
        var observed = time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
        if (observed < created || observed > first + 3600 || fresh.Runners.Any(r => r.Name == name))
            throw new InvalidOperationException("The exact new registration is not freshly absent after its unstarted intent; no retry is authorized.");
        return Authorize(fresh, name, expectedHashes, reservation: reservation) with {
            RegistrationAbsence = new RegistrationAbsenceProof(nonce, observed, name, "absent", true)
        };
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
        if (registered["status"]?.GetValue<string>() == "registration-reconciliation-required")
        {
            if (knownRunnerId is not null) throw new InvalidOperationException("A known registration cannot be reset by an unstarted-intent challenge.");
            var reconciled = await RegistrationAbsenceAsync(planBytes, reservation, expectedHashes, registered, ct);
            token = await RegistrationTokenAsync(Collector, ct);
            registered = await installer(reconciled, token, ct);
            token = "";
            if (registered["status"]?.GetValue<string>() == "registration-reconciliation-required")
                throw new InvalidOperationException("Unstarted registration reconciliation did not complete within its one automatic retry; no further attempt is authorized.");
        }
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
