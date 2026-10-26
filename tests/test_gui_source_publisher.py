"""Offline two-hop contracts; no network, source execution, processes or live signing.
Run python3 -I tests/test_gui_source_publisher.py [--mutations]. Mutants exist only in memory.
"""
import base64
import copy
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
BASE = (ROOT / 'src/DarkFactory.Orchestrator/Gateway/factory-gui-source-dispatch.py').read_text()
SOURCE = ROOT / 'src/DarkFactory.Orchestrator/Gateway/factory-gui-source-publisher.py'
TEXT = SOURCE.read_text()


def load(text=TEXT):
    module = types.ModuleType('publisher_contract')
    exec(compile(BASE.split("if __name__ == '__main__':")[0] + '\n' + text, str(SOURCE), 'exec'), module.__dict__)
    return module


spec = importlib.util.spec_from_file_location('offline_dispatch_fixtures', ROOT / 'tests/test_gui_source_dispatch.py')
fixtures = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixtures)
publisher = load()
REPO, HEAD = fixtures.REPO, fixtures.HEAD
PATH = '.github/workflows/inference-adapter.yml'
NAME = 'Inference adapter'
MIGRATED = b'compile-pinned migrated request workflow; never executed'


def encoded(value):
    return base64.b64encode(json.dumps(value).encode()).decode()


class FakeGitHub(fixtures.FakeGitHub):
    def __init__(self):
        super().__init__()
        self.source_run = dict(id=41, repository={'full_name': REPO}, head_repository={'full_name': REPO},
                               workflow_id=9, path=PATH, name=NAME, event='pull_request', status='completed',
                               conclusion='success', run_attempt=2, head_sha=HEAD, pull_requests=[{
                                   'number': 17, 'head': {'sha': HEAD, 'repo': {'url': publisher.API + 'repos/' + REPO}},
                                   'base': {'repo': {'url': publisher.API + 'repos/' + REPO}}}])
        self.source_workflow = dict(id=9, path=PATH, name=NAME)
        self.pr = dict(number=17, state='open', head={'sha': HEAD, 'repo': {'full_name': REPO}},
                       base={'ref': 'factory/changeset-kb-9', 'repo': {'full_name': REPO}}, merge_commit_sha='e' * 40)
        self.source_meta = dict(full_name=REPO, default_branch='main')
        self.head_content = self.default_content = MIGRATED
        self.pending = [{'context': 'inference', 'state': 'pending'}]
        self.source_fail = None

    def call(self, method, path, token, body=None):
        prefix = 'repos/' + REPO
        if method == 'GET' and (path == prefix or path.startswith(prefix + '/')):
            self.calls.append((method, path, token, copy.deepcopy(body)))
            if token != fixtures.SOURCE_TOKEN:
                raise AssertionError('source read lacks source token')
            if self.source_fail and self.source_fail in path:
                raise publisher.Refused('fake API unavailable')
            answers = {
                prefix: self.source_meta,
                prefix + '/actions/runs/41': self.source_run,
                prefix + '/actions/workflows/9': self.source_workflow,
                prefix + '/pulls/17': self.pr,
                prefix + '/commits/' + HEAD + '/statuses?per_page=100': self.pending,
            }
            if '/commits/' in path and path.endswith('/statuses?per_page=100'):
                return copy.deepcopy(self.pending)
            if '/contents/' in path:
                raw = self.default_content if path.endswith('?ref=main') else self.head_content
                return dict(type='file', encoding='base64', content=base64.b64encode(raw).decode())
            if path not in answers:
                raise AssertionError('unexpected source API route ' + path)
            return copy.deepcopy(answers[path])
        return super().call(method, path, token, body)


class Contracts(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.plan = dict(version=1, workflow=PATH, jobId='inference', timeoutMinutes=1, artifacts=[])
        self.row = dict(workflowName=NAME, path=PATH, jobId='inference', plan=encoded(self.plan),
                        sha256=publisher.hashlib.sha256(MIGRATED).hexdigest())
        self.env = dict(GITHUB_REPOSITORY=REPO, GITHUB_REF='refs/heads/main', GITHUB_SHA='d' * 40,
                        GITHUB_EVENT_NAME='workflow_run', FACTORY_GUI_DEFAULT_BRANCH='main',
                        FACTORY_GUI_PUBLISHER_PLANS=encoded([self.row]), FACTORY_GUI_SOURCE_TOKEN=fixtures.SOURCE_TOKEN,
                        FACTORY_GUI_SOURCE_JOB='malicious-context', FACTORY_GUI_SOURCE_PLAN=encoded({'untrusted': 'program'}),
                        FACTORY_GUI_SOURCE_WORKFLOW='malicious-path', FACTORY_GUI_APP_PRIVATE_KEY=fixtures.KEY,
                        FACTORY_GUI_APP_ID='123', FACTORY_GUI_COLLECTOR_REPOSITORY=fixtures.COLLECTOR,
                        FACTORY_GUI_COLLECTOR_WORKFLOW_SHA256=publisher.hashlib.sha256(fixtures.WORKFLOW_BYTES).hexdigest(),
                        FACTORY_GUI_CONFIGURATION_SHA256='f' * 64, GITHUB_RUN_ID='999', GITHUB_RUN_ATTEMPT='8',
                        RUNNER_TEMP=str(self.root), GITHUB_OUTPUT=str(self.root / 'output'))
        self.event = {'workflow_run': {'id': 41, 'event': 'pull_request', 'run_attempt': 2}}
        self.github = FakeGitHub()
        receipt = dict(version=1, repository=REPO, sha=HEAD, workflow=PATH, jobId='inference',
                       configurationSha256='f' * 64, succeeded=True)
        self.github.raw = fixtures.archive_bytes([('receipt.json', json.dumps(receipt).encode())])
        self.clock = fixtures.Clock()
        self.signatures = []
        self.addCleanup(patch.stopall)
        patch.object(publisher.secrets, 'token_hex', return_value=fixtures.NONCE).start()
        patch.object(publisher.urllib.request.OpenerDirector, 'open', side_effect=AssertionError('REAL HTTP FORBIDDEN')).start()
        patch.object(publisher.subprocess, 'run', side_effect=AssertionError('REAL PROCESS FORBIDDEN')).start()

    def signer(self, *args):
        self.signatures.append(args)
        return 'fake-jwt'

    def invoke(self):
        return publisher.publisher_run(self.env, self.event, self.github, self.signer, self.clock.sleep, self.clock)

    def denied(self, error=True):
        if error:
            self.assertEqual(0, self.invoke())
            self.assertEqual('error', self.github.statuses()[-1]['state'])
            self.assertTrue(self.github.calls[-1][1].endswith('/statuses/' + HEAD))
        else:
            with self.assertRaises(Exception):
                self.invoke()
        self.assertEqual([], self.signatures)
        self.assertEqual(0, self.github.mints)
        self.assertFalse(any(c[1].endswith('/dispatches') for c in self.github.calls))
        self.assertFalse(any(s['state'] == 'success' for s in self.github.statuses()))

    def request(self):
        env = dict(self.env, GITHUB_EVENT_NAME='pull_request', FACTORY_GUI_SOURCE_JOB='inference')
        env.pop('FACTORY_GUI_APP_PRIVATE_KEY')
        env.pop('FACTORY_GUI_PUBLISHER_PLANS')
        event = {'repository': {'full_name': REPO}, 'pull_request': copy.deepcopy(self.github.pr)}
        return env, event

    def test_valid_changeset_dispatch_uses_actual_head_pinned_plan_and_original_run(self):
        self.assertEqual(0, self.invoke())
        sent = next(c[3]['inputs'] for c in self.github.calls if c[1].endswith('/dispatches'))
        self.assertEqual((REPO, HEAD, '41', '2', self.row['plan']),
                         tuple(sent[k] for k in ('source_repository', 'source_sha', 'source_run_id', 'source_run_attempt', 'plan')))
        self.assertEqual(['pending', 'success'], [s['state'] for s in self.github.statuses()])
        self.assertTrue(all(s['context'] == 'inference' for s in self.github.statuses()))
        self.assertEqual(1, len(self.signatures))
        self.assertIn('FACTORY_GUI_APP_PRIVATE_KEY', self.env)  # only copied env consumed

    def test_exact_merge_commit_is_accepted(self):
        self.github.source_run['head_sha'] = self.github.pr['merge_commit_sha']
        self.assertEqual(0, self.invoke())
        self.assertEqual(1, self.github.mints)

    def test_request_posts_actual_head_pending_without_key_or_plan(self):
        env, event = self.request()
        self.assertEqual(0, publisher.request_run(env, event, self.github))
        self.assertEqual(1, len(self.github.calls))
        self.assertEqual('repos/' + REPO + '/statuses/' + HEAD, self.github.calls[0][1])
        self.assertEqual('pending', self.github.statuses()[0]['state'])

    def test_all_four_fixed_request_contexts_supported(self):
        env, event = self.request()
        for job in ('inference', 'live-raw', 'cargo-test', 'deterministic-fixtures-and-tour'):
            env['FACTORY_GUI_SOURCE_JOB'] = job
            self.assertEqual(0, publisher.request_run(env, event, self.github))
            self.assertEqual(job, self.github.statuses()[-1]['context'])
        self.assertEqual(4, len(self.github.calls))

    def test_request_status_failure_never_completes(self):
        env, event = self.request()
        self.github.fail_status = True
        with self.assertRaises(Exception):
            publisher.request_run(env, event, self.github)

    def test_request_default_base_refused(self):
        env, event = self.request()
        event['pull_request']['base']['ref'] = 'main'
        with self.assertRaises(Exception):
            publisher.request_run(env, event, self.github)
        self.assertEqual([], self.github.calls)

    def test_request_unknown_job_refused(self):
        env, event = self.request()
        env['FACTORY_GUI_SOURCE_JOB'] = 'arbitrary'
        with self.assertRaises(Exception):
            publisher.request_run(env, event, self.github)
        self.assertEqual([], self.github.calls)

    def test_request_fork_refused(self):
        env, event = self.request()
        event['pull_request']['head']['repo']['full_name'] = 'outsider/LensWorks'
        with self.assertRaises(Exception):
            publisher.request_run(env, event, self.github)
        self.assertEqual([], self.github.calls)

    def test_request_bad_sha_refused(self):
        env, event = self.request()
        event['pull_request']['head']['sha'] = 'main'
        with self.assertRaises(Exception):
            publisher.request_run(env, event, self.github)

    def test_nondefault_publisher_ref_refused(self):
        self.env['GITHUB_REF'] = 'refs/heads/factory/changeset-kb-9'
        self.denied(False)

    def test_raw_json_cannot_construct_sentinel(self):
        event = json.loads('{"_publisher_proof":true,"_repository":"' + REPO + '","_head":"' + HEAD + '"}')
        with self.assertRaises(Exception):
            publisher.immutable_source(self.env, event)

    def test_other_events_delegate_original_policy(self):
        env = dict(self.env, GITHUB_EVENT_NAME='pull_request_target')
        event = {'pull_request': dict(self.github.pr, base={'ref': 'main', 'repo': {'full_name': REPO}})}
        self.assertEqual((REPO, HEAD), publisher.immutable_source(env, event))

    def test_forged_event_workflow_plan_and_artifacts_are_ignored(self):
        self.event['workflow_run'].update(name='forged', path='malicious.yml', head_sha='f' * 40,
                                          plan='execute untrusted', artifacts=[{'program': 'untrusted'}])
        self.assertEqual(0, self.invoke())
        self.assertFalse(any('artifacts' in c[1] and REPO in c[1] for c in self.github.calls))
        sent = next(c[3]['inputs'] for c in self.github.calls if c[1].endswith('/dispatches'))
        self.assertEqual(self.row['plan'], sent['plan'])

    def test_api_workflow_path_forgery_refused(self):
        self.github.source_workflow['path'] = '.github/workflows/evil.yml'
        self.denied(False)

    def test_api_workflow_name_forgery_refused(self):
        self.github.source_workflow['name'] = 'forged'
        self.denied(False)

    def test_run_path_forgery_refused(self):
        self.github.source_run['path'] = '.github/workflows/evil.yml'
        self.denied(False)

    def test_modified_request_workflow_refused_before_signing(self):
        self.github.head_content = b'malicious request'
        self.denied()

    def test_modified_default_workflow_refused_before_signing(self):
        self.github.default_content = b'changed default'
        self.denied()

    def test_source_fork_run_refused(self):
        self.github.source_run['head_repository']['full_name'] = 'outsider/LensWorks'
        self.denied(False)

    def test_wrong_source_repo_refused(self):
        self.env['GITHUB_REPOSITORY'] = fixtures.COLLECTOR
        self.denied(False)

    def test_wrong_actual_run_repository_refused(self):
        self.github.source_run['repository']['full_name'] = 'outsider/LensWorks'
        self.denied(False)

    def test_current_pr_fork_refused(self):
        self.github.pr['head']['repo']['full_name'] = 'outsider/LensWorks'
        self.denied(False)

    def test_current_pr_wrong_base_repo_refused(self):
        self.github.pr['base']['repo']['full_name'] = 'outsider/LensWorks'
        self.denied(False)

    def test_default_base_pr_refused(self):
        self.github.pr['base']['ref'] = 'main'
        self.denied()

    def test_closed_pr_refused(self):
        self.github.pr['state'] = 'closed'
        self.denied()

    def test_head_moved_refused(self):
        self.github.pr['head']['sha'] = 'f' * 40
        self.github.source_run['head_sha'] = self.github.pr['merge_commit_sha']
        self.denied()

    def test_run_commit_not_head_or_merge_refused(self):
        self.github.source_run['head_sha'] = 'f' * 40
        self.denied()

    def test_run_attempt_stale_refused(self):
        self.event['workflow_run']['run_attempt'] = 1
        self.denied()

    def test_wrong_api_event_refused(self):
        self.github.source_run['event'] = 'push'
        self.denied()

    def test_wrong_payload_event_refused(self):
        self.event['workflow_run']['event'] = 'push'
        self.denied()

    def test_unsuccessful_request_refused(self):
        self.github.source_run['conclusion'] = 'failure'
        self.denied()

    def test_running_request_refused(self):
        self.github.source_run['status'] = 'in_progress'
        self.denied()

    def test_ambiguous_pr_list_refused(self):
        self.github.source_run['pull_requests'] *= 2
        self.denied(False)

    def test_nonpositive_run_id_refused(self):
        self.event['workflow_run']['id'] = 0
        self.denied(False)

    def test_boolean_run_id_refused(self):
        self.event['workflow_run']['id'] = True
        self.denied(False)

    def test_missing_pending_refused(self):
        self.github.pending = []
        self.denied()

    def test_wrong_pending_context_refused(self):
        self.github.pending[0]['context'] = 'forged'
        self.denied()

    def test_latest_context_not_pending_refused(self):
        self.github.pending.insert(0, dict(context='inference', state='success'))
        self.denied()

    def test_unrelated_newer_context_does_not_hide_pending(self):
        self.github.pending.insert(0, dict(context='other', state='failure'))
        self.assertEqual(0, self.invoke())
        self.assertEqual(1, self.github.mints)

    def test_source_default_policy_mismatch_refused(self):
        self.github.source_meta['default_branch'] = 'release'
        self.denied()

    def test_root_api_failure_never_false_pass(self):
        self.github.source_fail = '/actions/runs/'
        self.denied(False)

    def test_pending_api_failure_posts_error_without_dispatch(self):
        self.github.source_fail = '/statuses?'
        self.denied()

    def test_head_error_status_failure_never_false_pass(self):
        self.github.head_content = b'wrong'
        self.github.fail_status = True
        self.denied(False)

    def test_unknown_pinned_job_refused(self):
        self.row['jobId'] = 'evil'
        self.env['FACTORY_GUI_PUBLISHER_PLANS'] = encoded([self.row])
        self.denied(False)

    def test_plan_path_mismatch_refused(self):
        self.plan['workflow'] = 'evil'
        self.row['plan'] = encoded(self.plan)
        self.env['FACTORY_GUI_PUBLISHER_PLANS'] = encoded([self.row])
        self.denied(False)

    def test_duplicate_map_refused(self):
        self.env['FACTORY_GUI_PUBLISHER_PLANS'] = encoded([self.row, self.row])
        self.denied(False)

    def test_artifact_success_stays_pending_until_separate_delivery(self):
        self.plan['artifacts'] = [{'name': 'original-evidence', 'retentionDays': 14, 'ifNoFilesFound': 'error'}]
        self.row['plan'] = encoded(self.plan)
        self.env['FACTORY_GUI_PUBLISHER_PLANS'] = encoded([self.row])
        self.assertEqual(0, self.invoke())
        self.assertEqual(['pending'], [s['state'] for s in self.github.statuses()])
        outputs = dict(line.split('=', 1) for line in (self.root / 'output').read_text().splitlines())
        self.env.update(FACTORY_GUI_SOURCE_CONCLUSION=outputs['source_conclusion'],
                        FACTORY_GUI_SOURCE_TARGET_URL=outputs['source_target_url'],
                        FACTORY_GUI_ARTIFACT_DELIVERY_FAILED='false')
        self.assertEqual(0, publisher.publisher_delivery_run(self.env, self.event, self.github))
        self.assertEqual(['pending', 'success'], [s['state'] for s in self.github.statuses()])
        self.assertEqual(outputs['source_target_url'], self.github.statuses()[-1]['target_url'])
        self.assertEqual(1, len(self.signatures))

    def test_compiled_artifact_metadata_outputs_preserve_original_inventory(self):
        self.plan['artifacts'] = [{'name': 'original-results', 'retentionDays': 14, 'ifNoFilesFound': 'error'},
                                  {'name': 'tour-images', 'retentionDays': 3, 'ifNoFilesFound': 'warn'}]
        self.row['plan'] = encoded(self.plan)
        self.env['FACTORY_GUI_PUBLISHER_PLANS'] = encoded([self.row])
        self.assertEqual(0, self.invoke())
        outputs = dict(line.split('=', 1) for line in (self.root / 'output').read_text().splitlines())
        self.assertEqual(PATH, outputs['source_workflow'])
        self.assertEqual('2', outputs['artifact_count'])
        for index, artifact in enumerate(self.plan['artifacts']):
            self.assertEqual(artifact['name'], outputs['artifact_name_' + str(index)])
            self.assertEqual(str(artifact['retentionDays']), outputs['artifact_retention_' + str(index)])
            self.assertEqual(artifact['ifNoFilesFound'], outputs['artifact_missing_' + str(index)])

    def test_artifact_name_output_injection_refused_before_signing(self):
        self.plan['artifacts'] = [{'name': 'safe\\nforged=value', 'retentionDays': 14, 'ifNoFilesFound': 'error'}]
        self.row['plan'] = encoded(self.plan)
        self.env['FACTORY_GUI_PUBLISHER_PLANS'] = encoded([self.row])
        self.denied(False)
        self.assertFalse((self.root / 'output').exists())

    def test_artifact_retention_output_injection_refused_before_signing(self):
        self.plan['artifacts'] = [{'name': 'safe', 'retentionDays': '14\\nforged=value', 'ifNoFilesFound': 'error'}]
        self.row['plan'] = encoded(self.plan)
        self.env['FACTORY_GUI_PUBLISHER_PLANS'] = encoded([self.row])
        self.denied(False)

    def test_delivery_preserves_reported_source_failure(self):
        self.github.pending = [{'context': 'inference', 'state': 'failure'}]
        self.env.update(FACTORY_GUI_SOURCE_CONCLUSION='failure', FACTORY_GUI_ARTIFACT_DELIVERY_FAILED='false')
        self.assertEqual(0, publisher.publisher_delivery_run(self.env, self.event, self.github))
        self.assertEqual('failure', self.github.statuses()[-1]['state'])
        self.assertEqual(0, self.github.mints)

    def test_delivery_preserves_reported_error_with_missing_conclusion(self):
        self.github.pending = [{'context': 'inference', 'state': 'error'}]
        self.env.update(FACTORY_GUI_ARTIFACT_DELIVERY_FAILED='false')
        self.assertEqual(0, publisher.publisher_delivery_run(self.env, self.event, self.github))
        self.assertEqual('error', self.github.statuses()[-1]['state'])
        self.assertEqual(0, self.github.mints)

    def test_delivery_cannot_upgrade_failure_to_success(self):
        self.github.pending = [{'context': 'inference', 'state': 'failure'}]
        self.env.update(FACTORY_GUI_SOURCE_CONCLUSION='success', FACTORY_GUI_ARTIFACT_DELIVERY_FAILED='false')
        self.assertEqual(0, publisher.publisher_delivery_run(self.env, self.event, self.github))
        self.assertEqual('error', self.github.statuses()[-1]['state'])
        self.assertEqual(0, self.github.mints)

    def test_delivery_success_requires_pending_not_missing_or_error_or_old_success(self):
        self.env.update(FACTORY_GUI_SOURCE_CONCLUSION='success', FACTORY_GUI_ARTIFACT_DELIVERY_FAILED='false')
        for state in (None, 'error', 'success'):
            self.github.pending = [] if state is None else [{'context': 'inference', 'state': state}]
            self.assertEqual(0, publisher.publisher_delivery_run(self.env, self.event, self.github))
            self.assertEqual('error', self.github.statuses()[-1]['state'])
        self.assertEqual(0, self.github.mints)

    def test_delivery_accepts_configured_collector_url_without_signing(self):
        collector = 'another-owner/private-gui-control'
        self.env.update(FACTORY_GUI_COLLECTOR_REPOSITORY=collector, FACTORY_GUI_SOURCE_CONCLUSION='success',
                        FACTORY_GUI_SOURCE_TARGET_URL='https://github.com/' + collector + '/actions/runs/71',
                        FACTORY_GUI_ARTIFACT_DELIVERY_FAILED='false')
        self.assertEqual(0, publisher.publisher_delivery_run(self.env, self.event, self.github))
        self.assertEqual('success', self.github.statuses()[-1]['state'])
        self.assertEqual(self.env['FACTORY_GUI_SOURCE_TARGET_URL'], self.github.statuses()[-1]['target_url'])
        self.assertEqual(0, self.github.mints)

    def test_delivery_wrong_collector_url_never_posts_success(self):
        self.env.update(FACTORY_GUI_SOURCE_CONCLUSION='success',
                        FACTORY_GUI_SOURCE_TARGET_URL='https://github.com/outsider/control/actions/runs/71',
                        FACTORY_GUI_ARTIFACT_DELIVERY_FAILED='false')
        self.assertEqual(0, publisher.publisher_delivery_run(self.env, self.event, self.github))
        self.assertEqual('error', self.github.statuses()[-1]['state'])
        self.assertEqual(0, self.github.mints)

    def test_delivery_reproves_identity_and_posts_actual_head_success_without_signing(self):
        self.env.update(FACTORY_GUI_SOURCE_CONCLUSION='success', FACTORY_GUI_ARTIFACT_DELIVERY_FAILED='false')
        self.assertEqual(0, publisher.publisher_delivery_run(self.env, self.event, self.github))
        self.assertEqual('success', self.github.statuses()[-1]['state'])
        self.assertTrue(self.github.calls[-1][1].endswith('/statuses/' + HEAD))
        self.assertEqual(0, self.github.mints)

    def test_delivery_upload_failure_is_explicit_head_error_controller_zero(self):
        self.env.update(FACTORY_GUI_SOURCE_CONCLUSION='success', FACTORY_GUI_ARTIFACT_DELIVERY_FAILED='true')
        self.assertEqual(0, publisher.publisher_delivery_run(self.env, self.event, self.github))
        self.assertEqual('error', self.github.statuses()[-1]['state'])
        self.assertEqual(0, self.github.mints)

    def test_delivery_head_error_refused_if_status_post_fails(self):
        self.env.update(FACTORY_GUI_SOURCE_CONCLUSION='failure', FACTORY_GUI_ARTIFACT_DELIVERY_FAILED='false')
        self.github.fail_status = True
        with self.assertRaises(Exception):
            publisher.publisher_delivery_run(self.env, self.event, self.github)
        self.assertEqual(0, self.github.mints)

    def test_delivery_modified_workflow_cannot_post_success(self):
        self.env.update(FACTORY_GUI_SOURCE_CONCLUSION='success', FACTORY_GUI_ARTIFACT_DELIVERY_FAILED='false')
        self.github.head_content = b'changed'
        self.assertEqual(0, publisher.publisher_delivery_run(self.env, self.event, self.github))
        self.assertEqual('error', self.github.statuses()[-1]['state'])
        self.assertEqual(0, self.github.mints)


MUTATIONS = [
    ('default-ref', "environ.get('GITHUB_REF') == 'refs/heads/' + environ['FACTORY_GUI_DEFAULT_BRANCH']", 'True', 'test_nondefault_publisher_ref_refused'),
    ('head-workflow', "_workflow_hash(github, token, row['path'], head) == row['sha256']", 'True', 'test_modified_request_workflow_refused_before_signing'),
    ('default-workflow', "_workflow_hash(github, token, row['path'], environ['FACTORY_GUI_DEFAULT_BRANCH']) == row['sha256']", 'True', 'test_modified_default_workflow_refused_before_signing'),
    ('run-fork', "actual['head_repository']['full_name'].lower() == _SOURCE_REPOSITORY.lower()", 'True', 'test_source_fork_run_refused'),
    ('base-default', "pr['base']['ref'] != environ['FACTORY_GUI_DEFAULT_BRANCH']", 'True', 'test_default_base_pr_refused'),
    ('head-moved', 'recorded_head == head', 'True', 'test_head_moved_refused'),
    ('run-commit', "actual['head_sha'] in (head, pr.get('merge_commit_sha'))", 'True', 'test_run_commit_not_head_or_merge_refused'),
    ('attempt', "actual['run_attempt'] == event['workflow_run']['run_attempt']", 'True', 'test_run_attempt_stale_refused'),
    ('pending', "latest is not None and (latest.get('state') == 'pending'\n                or deliver and latest.get('state') == 'failure' and environ.get('FACTORY_GUI_SOURCE_CONCLUSION') == 'failure'\n                or deliver and latest.get('state') == 'error' and not environ.get('FACTORY_GUI_SOURCE_CONCLUSION'))", 'True', 'test_missing_pending_refused'),
    ('run-event', "actual['event'] == 'pull_request'", 'True', 'test_wrong_api_event_refused'),
    ('conclusion', "actual['conclusion'] == 'success'", 'True', 'test_unsuccessful_request_refused'),
    ('workflow-path', "row['path'] == workflow['path']", 'True', 'test_api_workflow_path_forgery_refused'),
    ('workflow-name', "row['workflowName'] == workflow['name']", 'True', 'test_api_workflow_name_forgery_refused'),
    ('source-default', "metadata['default_branch'] == environ['FACTORY_GUI_DEFAULT_BRANCH']", 'True', 'test_source_default_policy_mismatch_refused'),
    ('sentinel', "event.get('_publisher_proof') is _PUBLISHER_PROOF", "bool(event.get('_publisher_proof'))", 'test_raw_json_cannot_construct_sentinel'),
    ('request-status', "_source_status(github, environ, head, job, 'pending')", 'pass', 'test_request_posts_actual_head_pending_without_key_or_plan'),
]


def mutations():
    global publisher
    failures = errors = executed = 0
    for name, old, new, case in MUTATIONS:
        try:
            if old not in TEXT:
                raise ValueError('stale mutation')
            publisher = load(TEXT.replace(old, new))
            red = unittest.TextTestRunner(stream=io.StringIO()).run(Contracts(case))
            publisher = load()
            green = unittest.TextTestRunner(stream=io.StringIO()).run(Contracts(case))
            executed += 1
            if red.wasSuccessful() or not green.wasSuccessful():
                failures += 1
                print(name + ': FAILED (survived or restored not green)')
            else:
                print(name + ': RED (mutation rejected), restored GREEN')
        except Exception as error:
            errors += 1
            print(name + ': ERROR ' + type(error).__name__)
        finally:
            publisher = load()
    print('GUI_SOURCE_PUBLISHER_MUTATION_RESULT ' + json.dumps(dict(executed=executed, skipped=0, failures=failures, errors=errors)))
    return int(bool(failures or errors or executed != len(MUTATIONS)))


if __name__ == '__main__':
    if '--mutations' in sys.argv:
        sys.exit(mutations())
    result = unittest.TextTestRunner(verbosity=1).run(unittest.defaultTestLoader.loadTestsFromTestCase(Contracts))
    print('GUI_SOURCE_PUBLISHER_CONTRACT_RESULT ' + json.dumps(dict(executed=result.testsRun, skipped=len(result.skipped),
                                                                 failures=len(result.failures), errors=len(result.errors))))
    sys.exit(0 if result.wasSuccessful() and not result.skipped else 1)
