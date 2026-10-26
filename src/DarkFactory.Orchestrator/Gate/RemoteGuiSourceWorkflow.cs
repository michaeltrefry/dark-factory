using System.Security.Cryptography;
using System.Text;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>Sealed generic source-job workflow installed only on the separate trusted collector's reviewed default branch.
/// Inputs are data to the fixed installed client, never shell programs executed as the credentialed collector.</summary>
public static class RemoteGuiSourceWorkflow
{
    public const string Path = ".github/workflows/factory-gui-source-job.yml";
    public const string Repository = "michaeltrefry/dark-factory-gui-control";
    public const string Environment = "factory-gui-dispatch";
    public static string Text { get; } = """
        name: Factory isolated GUI source job
        run-name: factory-gui-source-${{ inputs.nonce }}
        on:
          workflow_dispatch:
            inputs:
              source_repository:
                required: true
                type: string
              source_sha:
                required: true
                type: string
              nonce:
                required: true
                type: string
              plan:
                required: true
                type: string
              source_run_id:
                required: true
                type: string
              source_run_attempt:
                required: true
                type: string
              lw_require_real:
                default: ''
                type: string
              acceptance_probe:
                default: none
                type: choice
                options: [none, recovery]
        permissions: {}
        jobs:
          source-job:
            runs-on: [factory-gui-collector]
            timeout-minutes: 185
            steps:
              - name: Validate the fixed trusted execution mode
                env:
                  FACTORY_GUI_ACCEPTANCE_PROBE: ${{ inputs.acceptance_probe }}
                run: 'case "$FACTORY_GUI_ACCEPTANCE_PROBE" in none|recovery) ;; *) exit 64 ;; esac'
              - name: Execute source data as lwrunner under the shared GUI lease
                if: inputs.acceptance_probe == 'none'
                id: source
                env:
                  FACTORY_GUI_SOURCE_PROTOCOL: factory-gui-source-v1
                  FACTORY_GUI_SOURCE_PLAN: ${{ inputs.plan }}
                  FACTORY_GUI_SOURCE_REPOSITORY: ${{ inputs.source_repository }}
                  FACTORY_GUI_SOURCE_SHA: ${{ inputs.source_sha }}
                  FACTORY_GUI_SOURCE_RUN_ID: ${{ inputs.source_run_id }}
                  FACTORY_GUI_SOURCE_RUN_ATTEMPT: ${{ inputs.source_run_attempt }}
                  FACTORY_GUI_VAR_LW_REQUIRE_REAL: ${{ inputs.lw_require_real }}
                run: /usr/local/libexec/dark-factory/factory-gui-source-job
              - name: Exercise fixed recovery and cleanup refusal handling
                if: inputs.acceptance_probe == 'recovery'
                id: recovery
                env:
                  FACTORY_GUI_SOURCE_PROTOCOL: factory-gui-source-v1
                  FACTORY_GUI_SOURCE_REPOSITORY: ${{ inputs.source_repository }}
                  FACTORY_GUI_SOURCE_SHA: ${{ inputs.source_sha }}
                  FACTORY_GUI_SOURCE_RUN_ID: ${{ inputs.source_run_id }}
                  FACTORY_GUI_SOURCE_RUN_ATTEMPT: ${{ inputs.source_run_attempt }}
                run: /usr/local/libexec/dark-factory/factory-gui-source-job --exercise-recovery
              - name: Retain bounded source evidence
                if: always() && (steps.source.outputs.artifact_root != '' || steps.recovery.outputs.artifact_root != '')
                uses: actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02
                with:
                  name: factory-gui-source-${{ inputs.nonce }}
                  path: ${{ steps.source.outputs.artifact_root || steps.recovery.outputs.artifact_root }}
                  if-no-files-found: ignore
                  retention-days: 7
              - name: Remove the protected bounded export after upload
                if: always() && (steps.source.outputs.artifact_root != '' || steps.recovery.outputs.artifact_root != '')
                env:
                  FACTORY_GUI_EXPORT_ROOT: ${{ steps.source.outputs.artifact_root || steps.recovery.outputs.artifact_root }}
                run: /usr/local/libexec/dark-factory/factory-gui-source-job --cleanup-export "$FACTORY_GUI_EXPORT_ROOT"
        """ + "\n";
    public static string Sha256 { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text)));
    public static string Dispatcher { get; } = Resource("factory-gui-source-dispatch.py");
    public static string Script(string entry)
    {
        if (entry is not ("main" or "artifact_delivery_main" or "request_main" or "publisher_main" or "publisher_delivery_main"))
            throw new InvalidOperationException("Unknown compiled source bridge entrypoint.");
        var start = Dispatcher.LastIndexOf("if __name__ == '__main__':", StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException("Compiled source bridge has no entrypoint.");
        var publisher = entry is "request_main" or "publisher_main" or "publisher_delivery_main"
            ? "\n" + Resource("factory-gui-source-publisher.py") : "";
        return "python3 -I - <<'FACTORY_GUI_BRIDGE'\n" + Dispatcher[..start] + publisher
            + "\nif __name__ == '__main__':\n    sys.exit(" + entry + "())\nFACTORY_GUI_BRIDGE\n";
    }
    static string Resource(string name)
    {
        using var stream = typeof(RemoteGuiSourceWorkflow).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Missing compiled trusted source dispatcher.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
