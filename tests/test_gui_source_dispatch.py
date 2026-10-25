"""Offline bridge contracts. Only fake GitHub/signing/clock; temporary artifact outputs.
Run: python3 -I tests/test_gui_source_dispatch.py [--mutations]
Mutations are loaded in memory: the shared dispatcher is NEVER rewritten.
"""
import base64
import copy
import datetime
import hashlib
import importlib.machinery
import importlib.util
import io
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import urllib.error
import zipfile

SOURCE = Path(__file__).resolve().parents[1] / 'src/DarkFactory.Orchestrator/Gateway/factory-gui-source-dispatch.py'
TEXT = SOURCE.read_text()
sys.dont_write_bytecode = True


def load(text=None):
    class MemoryLoader(importlib.machinery.SourceFileLoader):
        def get_code(self, fullname):
            return compile(TEXT if text is None else text, str(SOURCE), 'exec')
    spec = importlib.util.spec_from_file_location('gui_source_dispatch_contract', SOURCE,
                                                loader=MemoryLoader('gui_source_dispatch_contract', str(SOURCE)))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)  # __main__ never runs
    return module


dispatch = load()
REPO = 'michaeltrefry/LensWorks'
COLLECTOR = 'michaeltrefry/dark-factory-gui-control'
HEAD = 'a' * 40
TIP = 'b' * 40
NONCE = 'c' * 32
NOW = 1800000000
WORKFLOW_BYTES = b'trusted sealed workflow, never executed'
SOURCE_TOKEN = 'fake-source-token-contract'
APP_TOKEN = 'fake-installation-token-contract'
KEY = 'fake-private-material-' * 60


def archive_bytes(entries=None):
    output = io.BytesIO()
    with zipfile.ZipFile(output, 'w') as archive:
        for name, value in (entries or [('0/results.txt', b'source evidence')]):
            if isinstance(name, zipfile.ZipInfo):
                archive.writestr(name, value)
            else:
                archive.writestr(name, value)
    return output.getvalue()


class Clock:
    def __init__(self):
        self.now = NOW
        self.sleeps = []

    def __call__(self):
        return self.now

    def sleep(self, seconds):
        self.sleeps.append(seconds)
        self.now += seconds


class FakeGitHub:
    def __init__(self):
        self.calls = []
        self.archives = []
        self.app = {'permissions': {'actions': 'write', 'contents': 'read'}, 'id': 123}
        self.installation = {'id': 7, 'permissions': {'actions': 'write', 'contents': 'read'}}
        self.minted = {'token': APP_TOKEN, 'expires_at': datetime.datetime.fromtimestamp(
            NOW + 3600, datetime.timezone.utc).isoformat(),
            'permissions': {'actions': 'write', 'contents': 'read'},
            'repositories': [{'full_name': COLLECTOR, 'name': COLLECTOR.split('/')[1]}]}
        self.metadata = {'default_branch': 'main', 'private': True, 'full_name': COLLECTOR}
        self.workflow = {'type': 'file', 'encoding': 'base64', 'content': base64.b64encode(WORKFLOW_BYTES).decode()}
        self.rows = [dict(id=71, display_title='factory-gui-source-' + NONCE,
                          event='workflow_dispatch', head_sha=TIP, path=dispatch.WORKFLOW,
                          run_attempt=1, status='completed', conclusion='success',
                          html_url='https://github.com/' + COLLECTOR + '/actions/runs/71')]
        self.artifacts = [dict(id=19, name='factory-gui-source-' + NONCE, expired=False,
                               size_in_bytes=128, workflow_run={'id': 71})]
        self.total = None
        self.raw = archive_bytes()
        self.renewed = None
        self.mints = 0
        self.fail_status = False
        self.fail_dispatch = False
        self.fail_inventory = False
        self.on_dispatch = None

    def call(self, method, path, token, body=None):
        self.calls.append((method, path, token, copy.deepcopy(body)))
        if '/statuses/' in path:
            if self.fail_status:
                raise dispatch.Refused('fake source status unavailable')
            if token != SOURCE_TOKEN:
                raise AssertionError('status sent with non-source credential')
            return {}
        if path == 'app':
            if token != 'fake-jwt':
                raise AssertionError('App metadata must use JWT')
            return copy.deepcopy(self.app)
        if path == 'repos/' + COLLECTOR + '/installation':
            if token != 'fake-jwt':
                raise AssertionError('installation lookup must use JWT')
            return copy.deepcopy(self.installation)
        if path == 'app/installations/7/access_tokens':
            if token != 'fake-jwt':
                raise AssertionError('mint must use JWT')
            self.mints += 1
            return copy.deepcopy(self.renewed if self.mints > 1 and self.renewed is not None else self.minted)
        if token != APP_TOKEN:
            raise AssertionError('collector call lacks scoped installation credential')
        prefix = 'repos/' + COLLECTOR
        if path == prefix:
            return copy.deepcopy(self.metadata)
        if path == prefix + '/commits/main':
            return {'sha': TIP}
        if path == prefix + '/contents/' + dispatch.WORKFLOW + '?ref=' + TIP:
            return copy.deepcopy(self.workflow)
        if path.endswith('/dispatches'):
            if self.fail_dispatch:
                raise dispatch.Refused('fake dispatch failure')
            if self.on_dispatch:
                self.on_dispatch()
            return {}
        if '/runs?event=workflow_dispatch&per_page=100&page=' in path:
            return {'workflow_runs': copy.deepcopy(self.rows)}
        if path.endswith('/artifacts?per_page=100'):
            if self.fail_inventory:
                raise dispatch.Refused('fake artifact failure')
            return {'total_count': len(self.artifacts) if self.total is None else self.total,
                    'artifacts': copy.deepcopy(self.artifacts)}
        if path.endswith('/cancel'):
            return {}
        raise AssertionError('unexpected fake GitHub call: ' + method + ' ' + path)

    def archive(self, path, token):
        self.archives.append((path, token))
        if token != APP_TOKEN:
            raise AssertionError('archive needs installation credential')
        return self.raw

    def statuses(self):
        return [body for method, path, token, body in self.calls if '/statuses/' in path]


class Contracts(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.plan = dict(version=1, workflow='.github/workflows/ci.yml', jobId='mac-tests',
                         timeoutMinutes=1, artifacts=[{'ifNoFilesFound': 'error'}])
        self.env = dict(GITHUB_REPOSITORY=REPO, GITHUB_REF='refs/heads/main', GITHUB_SHA='d' * 40,
                        GITHUB_EVENT_NAME='pull_request_target', FACTORY_GUI_DEFAULT_BRANCH='main',
                        FACTORY_GUI_COLLECTOR_REPOSITORY=COLLECTOR, FACTORY_GUI_SOURCE_WORKFLOW=self.plan['workflow'],
                        FACTORY_GUI_SOURCE_JOB='mac-tests', FACTORY_GUI_SOURCE_TOKEN=SOURCE_TOKEN,
                        FACTORY_GUI_APP_PRIVATE_KEY=KEY, FACTORY_GUI_APP_ID='123', GITHUB_RUN_ID='41',
                        GITHUB_RUN_ATTEMPT='2', RUNNER_TEMP=str(self.root), GITHUB_OUTPUT=str(self.root / 'output'),
                        FACTORY_GUI_COLLECTOR_WORKFLOW_SHA256=hashlib.sha256(WORKFLOW_BYTES).hexdigest())
        self.event = {'pull_request': {'base': {'ref': 'main', 'repo': {'full_name': REPO}},
                                       'head': {'sha': HEAD, 'repo': {'full_name': REPO}}}}
        self.set_plan()
        self.github = FakeGitHub()
        self.env['FACTORY_GUI_CONFIGURATION_SHA256'] = 'f' * 64
        self.receipt = dict(version=1, repository=REPO, sha=HEAD, workflow=self.plan['workflow'],
                            jobId=self.plan['jobId'], configurationSha256='f' * 64, succeeded=True)
        self.set_receipt()
        self.clock = Clock()
        self.signatures = []
        self.addCleanup(patch.stopall)
        patch.object(dispatch.secrets, 'token_hex', return_value=NONCE).start()
        patch.object(dispatch.urllib.request.OpenerDirector, 'open', side_effect=AssertionError('REAL NETWORK FORBIDDEN')).start()
        patch.object(dispatch.subprocess, 'run', side_effect=AssertionError('REAL PROCESS FORBIDDEN')).start()

    def set_plan(self):
        self.env['FACTORY_GUI_SOURCE_PLAN'] = base64.b64encode(json.dumps(self.plan).encode()).decode()

    def set_receipt(self):
        self.github.raw = archive_bytes([('0/results.txt', b'source evidence'),
                                         ('receipt.json', json.dumps(self.receipt).encode())])

    def output_value(self, name):
        return dict(line.split('=', 1) for line in (self.root / 'output').read_text().splitlines())[name]

    def signer(self, app_id, key, now):
        self.signatures.append((app_id, key, now))
        return 'fake-jwt'

    def run_bridge(self):
        return dispatch.run(self.env, self.event, self.github, self.signer, self.clock.sleep, self.clock)

    def refused(self):
        with self.assertRaises(dispatch.Refused):
            dispatch.execute(self.env, self.event, self.github, self.signer, self.clock.sleep, self.clock)
        self.assertFalse(any(x['state'] == 'success' for x in self.github.statuses()))

    def deliver_artifacts(self, failed='false', conclusion=None):
        self.env['FACTORY_GUI_SOURCE_CONCLUSION'] = self.output_value('source_conclusion') if conclusion is None else conclusion
        self.env['FACTORY_GUI_ARTIFACT_DELIVERY_FAILED'] = failed
        self.env['FACTORY_GUI_SOURCE_TARGET_URL'] = self.output_value('source_target_url')
        return dispatch.artifact_delivery(self.env, self.event, self.github)

    def test_success_exports_evidence_and_status_on_actual_pr_head(self):
        self.assertEqual(0, self.run_bridge())
        self.assertEqual(['pending'], [x['state'] for x in self.github.statuses()], 'must not pass before hosted evidence delivery')
        self.assertEqual(0, self.deliver_artifacts())
        self.assertEqual(['pending', 'success'], [x['state'] for x in self.github.statuses()])
        routes = [p for _, p, _, _ in self.github.calls if '/statuses/' in p]
        self.assertTrue(all(p.endswith('/' + HEAD) for p in routes))
        output = self.output_value('artifact_root')
        self.assertEqual(b'source evidence', (Path(output) / '0/results.txt').read_bytes())
        self.assertEqual('mac-tests', self.github.statuses()[-1]['context'])

    def test_token_mint_is_repository_and_permission_scoped(self):
        self.run_bridge()
        mint = next(c for c in self.github.calls if c[1].endswith('/access_tokens'))
        self.assertEqual({'repositories': ['dark-factory-gui-control'],
                          'permissions': {'actions': 'write', 'contents': 'read'}}, mint[3])
        self.assertNotIn('FACTORY_GUI_APP_PRIVATE_KEY', self.env)
        self.assertEqual([('123', KEY, NOW)], self.signatures)

    def test_dispatch_payload_has_no_key_or_tokens_and_binds_source_execution(self):
        self.run_bridge()
        sent = next(c[3] for c in self.github.calls if c[1].endswith('/dispatches'))
        self.assertEqual('main', sent['ref'])
        self.assertEqual({'source_repository': REPO, 'source_sha': HEAD, 'nonce': NONCE,
                          'plan': self.env['FACTORY_GUI_SOURCE_PLAN'], 'source_run_id': '41',
                          'source_run_attempt': '2', 'lw_require_real': ''}, sent['inputs'])
        for secret in (KEY, SOURCE_TOKEN, APP_TOKEN, 'fake-jwt'):
            self.assertNotIn(secret, json.dumps(sent))
        self.assertTrue(all(KEY not in json.dumps(c) for c in self.github.calls))

    def test_app_actions_write_required(self):
        self.github.app['permissions']['actions'] = 'read'
        self.refused()
        self.assertFalse(any(c[1].endswith('/access_tokens') for c in self.github.calls))
        self.assertEqual([], self.github.statuses())

    def test_installation_approval_required(self):
        self.github.installation['permissions']['actions'] = 'read'
        self.refused()
        self.assertFalse(any(c[1].endswith('/access_tokens') for c in self.github.calls))

    def test_collector_must_be_private(self):
        self.github.metadata['private'] = False
        self.refused()

    def test_collector_cannot_be_fork(self):
        self.github.metadata['fork'] = True
        self.refused()

    def test_minted_permissions_cannot_include_extra_write(self):
        self.github.minted['permissions']['issues'] = 'write'
        self.refused()

    def test_minted_metadata_read_is_allowed(self):
        self.github.minted['permissions']['metadata'] = 'read'
        self.assertEqual(0, self.run_bridge())

    def test_collector_metadata_identity_pinned(self):
        self.github.metadata['full_name'] = 'outsider/control'
        self.refused()

    def test_minted_permissions_must_include_actions_write(self):
        self.github.minted['permissions']['actions'] = 'read'
        self.refused()

    def test_minted_permissions_must_not_expand_to_contents_write(self):
        self.github.minted['permissions']['contents'] = 'write'
        self.refused()

    def test_minted_repository_scope_cannot_include_source(self):
        self.github.minted['repositories'].append({'full_name': REPO, 'name': 'LensWorks'})
        self.refused()

    def test_minted_repository_scope_cannot_name_other_repository(self):
        self.github.minted['repositories'][0]['full_name'] = 'outsider/control'
        self.refused()

    def test_pr_head_repository_must_be_allowlisted(self):
        self.event['pull_request']['head']['repo']['full_name'] = 'outsider/LensWorks'
        self.refused()

    def test_pr_base_must_be_protected_default(self):
        self.event['pull_request']['base']['ref'] = 'feature'
        self.refused()

    def push_event(self):
        self.receipt['sha'] = self.env['GITHUB_SHA']
        self.set_receipt()
        self.env['GITHUB_EVENT_NAME'] = 'push'
        self.event = {'after': self.env['GITHUB_SHA'], 'ref': self.env['GITHUB_REF'],
                      'repository': {'full_name': REPO}}

    def test_push_event_commit_must_match_authenticated_event(self):
        self.push_event()
        self.event['after'] = 'f' * 40
        self.refused()

    def test_push_event_ref_must_match_default(self):
        self.push_event()
        self.event['ref'] = 'refs/heads/feature'
        self.refused()

    def test_push_event_repository_must_match_source(self):
        self.push_event()
        self.event['repository']['full_name'] = 'outsider/repo'
        self.refused()

    def test_zip_raw_dot_component_refused(self):
        with self.assertRaises(dispatch.Refused):
            dispatch.extract_artifacts(archive_bytes([('0/./result', b'x')]), self.root / 'export', 1)

    def test_zip_raw_empty_component_refused(self):
        with self.assertRaises(dispatch.Refused):
            dispatch.extract_artifacts(archive_bytes([('0//result', b'x')]), self.root / 'export', 1)

    def test_token_expired_refused(self):
        self.github.minted['expires_at'] = datetime.datetime.fromtimestamp(NOW, datetime.timezone.utc).isoformat()
        self.refused()
        self.assertEqual(1, self.github.mints, 'must reject before any expired-token renewal')

    def test_token_overlong_refused(self):
        self.github.minted['expires_at'] = datetime.datetime.fromtimestamp(NOW + 3661, datetime.timezone.utc).isoformat()
        self.refused()

    def test_collector_must_be_separate_allowlisted_repository(self):
        self.env['FACTORY_GUI_COLLECTOR_REPOSITORY'] = REPO
        self.refused()
        self.assertEqual([], self.signatures)

    def test_source_repository_allowlist(self):
        self.env['GITHUB_REPOSITORY'] = 'outsider/repo'
        self.event['pull_request']['base']['repo']['full_name'] = 'outsider/repo'
        self.event['pull_request']['head']['repo']['full_name'] = 'outsider/repo'
        self.refused()
        self.assertEqual([], self.github.calls)

    def test_source_protected_default_ref_required(self):
        self.env['GITHUB_REF'] = 'refs/pull/42/merge'
        self.refused()
        self.assertEqual([], self.github.calls)

    def test_pull_request_event_never_accepted(self):
        self.env['GITHUB_EVENT_NAME'] = 'pull_request'
        self.refused()

    def test_pr_base_repository_mismatch_refused(self):
        self.event['pull_request']['base']['repo']['full_name'] = 'outsider/repo'
        self.refused()

    def test_source_head_must_be_immutable(self):
        self.event['pull_request']['head']['sha'] = 'refs/heads/main'
        self.refused()

    def test_push_reports_env_commit_not_intrinsic_pr_head(self):
        self.push_event()
        self.receipt['sha'] = self.env['GITHUB_SHA']
        self.set_receipt()
        self.assertEqual(0, self.run_bridge())
        self.assertTrue(all(p.endswith('/' + self.env['GITHUB_SHA']) for _, p, _, _ in self.github.calls if '/statuses/' in p))

    def test_plan_identity_mismatch_refused(self):
        self.plan['jobId'] = 'other-job'
        self.set_plan()
        self.refused()
        self.assertEqual([], self.signatures)

    def test_plan_timeout_bound(self):
        self.plan['timeoutMinutes'] = 181
        self.set_plan()
        self.refused()

    def test_workflow_hash_checked_at_immutable_central_commit(self):
        self.github.workflow['content'] = base64.b64encode(b'unreviewed workflow').decode()
        self.refused()
        self.assertFalse(any(c[1].endswith('/dispatches') for c in self.github.calls))

    def test_workflow_must_be_file(self):
        self.github.workflow['type'] = 'symlink'
        self.refused()

    def test_central_metadata_and_default_commit_read_once(self):
        self.run_bridge()
        self.assertEqual(1, sum(c[1] == 'repos/' + COLLECTOR for c in self.github.calls))
        self.assertEqual(1, sum(c[1] == 'repos/' + COLLECTOR + '/commits/main' for c in self.github.calls))

    def test_moved_central_head_refused(self):
        self.github.rows[0]['head_sha'] = 'e' * 40
        self.refused()

    def test_wrong_nonce_never_accepted(self):
        self.github.rows[0]['display_title'] = 'factory-gui-source-wrong'
        self.refused()
        self.assertTrue(self.clock.sleeps)
        self.assertEqual([], self.github.archives)

    def test_wrong_workflow_path_refused(self):
        self.github.rows[0]['path'] = '.github/workflows/other.yml'
        self.refused()

    def test_wrong_collector_event_refused(self):
        self.github.rows[0]['event'] = 'push'
        self.refused()

    def test_retried_collector_run_refused(self):
        self.github.rows[0]['run_attempt'] = 2
        self.refused()

    def test_ambiguous_runs_refused(self):
        self.github.rows.append(copy.deepcopy(self.github.rows[0]))
        self.refused()

    def test_completed_failure_is_failure_not_error_or_pass(self):
        self.github.rows[0]['conclusion'] = 'failure'
        self.assertEqual(0, self.run_bridge())
        self.assertEqual(['pending', 'failure'], [x['state'] for x in self.github.statuses()])

    def test_completed_cancelled_never_passes(self):
        self.github.rows[0]['conclusion'] = 'cancelled'
        self.assertEqual(0, self.run_bridge())
        self.assertEqual('failure', self.github.statuses()[-1]['state'])

    def test_timeout_cancels_identified_run_and_records_error(self):
        self.github.rows[0]['status'] = 'in_progress'
        self.refused()
        self.assertTrue(any(c[1].endswith('/runs/71/cancel') for c in self.github.calls))
        self.assertEqual('error', self.github.statuses()[-1]['state'])

    def test_inventory_failure_cancels_and_never_passes(self):
        self.github.fail_inventory = True
        self.refused()
        self.assertTrue(any(c[1].endswith('/runs/71/cancel') for c in self.github.calls))

    def test_dispatch_failure_sets_error_without_cancelling_unknown_run(self):
        self.github.fail_dispatch = True
        self.refused()
        self.assertFalse(any(c[1].endswith('/cancel') for c in self.github.calls))

    def test_wrong_artifact_run_refused(self):
        self.github.artifacts[0]['workflow_run']['id'] = 72
        self.refused()

    def test_expired_artifact_refused(self):
        self.github.artifacts[0]['expired'] = True
        self.refused()

    def test_wrong_artifact_nonce_refused(self):
        self.github.artifacts[0]['name'] = 'other'
        self.refused()

    def test_incomplete_artifact_inventory_refused(self):
        self.github.total = 101
        self.refused()

    def test_required_artifact_missing_refused(self):
        self.github.artifacts = []
        self.github.rows[0]['conclusion'] = 'failure'
        self.refused()

    def test_optional_artifact_missing_exports_empty_directory(self):
        self.plan['artifacts'][0]['ifNoFilesFound'] = 'ignore'
        self.set_plan()
        self.github.artifacts = []
        self.github.rows[0]['conclusion'] = 'failure'
        self.assertEqual(0, self.run_bridge())
        root = Path(self.output_value('artifact_root'))
        self.assertEqual([], list(root.iterdir()))

    def test_success_requires_source_receipt(self):
        self.github.raw = archive_bytes()
        self.refused()

    def test_receipt_repository_identity_pinned(self):
        self.receipt['repository'] = 'outsider/repo'
        self.set_receipt()
        self.refused()

    def test_receipt_head_identity_pinned(self):
        self.receipt['sha'] = 'e' * 40
        self.set_receipt()
        self.refused()

    def test_receipt_workflow_identity_pinned(self):
        self.receipt['workflow'] = '.github/workflows/other.yml'
        self.set_receipt()
        self.refused()

    def test_receipt_job_identity_pinned(self):
        self.receipt['jobId'] = 'other-job'
        self.set_receipt()
        self.refused()

    def test_receipt_version_pinned(self):
        self.receipt['version'] = 2
        self.set_receipt()
        self.refused()

    def test_receipt_configuration_hash_pinned(self):
        self.receipt['configurationSha256'] = 'e' * 64
        self.set_receipt()
        self.refused()

    def test_receipt_configuration_hash_format_checked(self):
        self.env['FACTORY_GUI_CONFIGURATION_SHA256'] = 'not-a-hash'
        self.receipt['configurationSha256'] = 'not-a-hash'
        self.set_receipt()
        self.refused()

    def test_receipt_success_must_be_true(self):
        self.receipt['succeeded'] = False
        self.set_receipt()
        self.refused()

    def test_receipt_success_must_be_boolean(self):
        self.receipt['succeeded'] = 'true'
        self.set_receipt()
        self.refused()

    def test_receipt_size_bounded(self):
        self.receipt['padding'] = 'x' * 16384
        self.set_receipt()
        self.refused()

    def test_failed_run_may_export_logs_without_receipt(self):
        self.github.rows[0]['conclusion'] = 'failure'
        self.github.raw = archive_bytes([('source-step-0.log', b'failed synthetic step')])
        self.assertEqual(0, self.run_bridge())
        self.assertEqual('failure', self.github.statuses()[-1]['state'])
        self.assertEqual('failure', self.output_value('source_conclusion'))

    def test_diagnostic_step_index_bound(self):
        with self.assertRaises(dispatch.Refused):
            dispatch.extract_artifacts(archive_bytes([('source-step-50.log', b'x')]), self.root / 'export', 1)

    def test_arbitrary_root_diagnostic_refused(self):
        with self.assertRaises(dispatch.Refused):
            dispatch.extract_artifacts(archive_bytes([('untrusted.py', b'not executable')]), self.root / 'export', 1)

    def test_pr_controller_error_returns_zero_only_after_head_error_report(self):
        self.github.app['permissions']['actions'] = 'read'
        self.assertEqual(0, self.run_bridge())
        self.assertEqual(['error'], [s['state'] for s in self.github.statuses()])
        self.assertTrue(self.github.calls[-1][1].endswith('/statuses/' + HEAD))

    def test_pr_controller_cannot_hide_unreported_error(self):
        self.github.app['permissions']['actions'] = 'read'
        self.github.fail_status = True
        with self.assertRaises(dispatch.Refused):
            self.run_bridge()

    def test_push_controller_error_is_nonzero(self):
        self.push_event()
        self.github.app['permissions']['actions'] = 'read'
        with self.assertRaises(dispatch.Refused):
            self.run_bridge()
        self.assertEqual('error', self.github.statuses()[-1]['state'])

    def test_push_completed_failure_is_nonzero(self):
        self.push_event()
        self.github.rows[0]['conclusion'] = 'failure'
        self.receipt['sha'] = self.env['GITHUB_SHA']
        self.receipt['succeeded'] = False
        self.set_receipt()
        self.assertEqual(1, self.run_bridge())
        self.assertEqual('failure', self.github.statuses()[-1]['state'])

    def prepare_renewal(self):
        self.github.renewed = copy.deepcopy(self.github.minted)
        self.github.renewed['expires_at'] = datetime.datetime.fromtimestamp(NOW + 7080, datetime.timezone.utc).isoformat()
        self.github.on_dispatch = lambda: setattr(self.clock, 'now', NOW + 3480)

    def test_long_job_renews_only_same_scoped_installation_token(self):
        self.prepare_renewal()
        self.assertEqual(0, self.run_bridge())
        self.assertEqual(2, self.github.mints)
        self.assertEqual([NOW, NOW + 3480], [s[2] for s in self.signatures])
        self.assertTrue(all(s[1] == KEY for s in self.signatures))
        self.assertNotIn('FACTORY_GUI_APP_PRIVATE_KEY', self.env)
        mints = [c[3] for c in self.github.calls if c[1].endswith('/access_tokens')]
        self.assertEqual(mints[0], mints[1])

    def test_renewal_cannot_expand_permissions(self):
        self.prepare_renewal()
        self.github.renewed['permissions']['contents'] = 'write'
        self.refused()

    def test_renewal_cannot_expand_repositories(self):
        self.prepare_renewal()
        self.github.renewed['repositories'].append({'full_name': REPO})
        self.refused()

    def test_renewal_cannot_change_repository(self):
        self.prepare_renewal()
        self.github.renewed['repositories'][0]['full_name'] = REPO
        self.refused()

    def test_renewal_cannot_return_expired_token(self):
        self.prepare_renewal()
        self.github.renewed['expires_at'] = datetime.datetime.fromtimestamp(NOW + 3480, datetime.timezone.utc).isoformat()
        self.refused()

    def test_failed_hosted_artifact_delivery_never_posts_source_pass(self):
        self.assertEqual(0, self.run_bridge())
        self.assertEqual(0, self.deliver_artifacts(failed='true'))
        self.assertEqual(['pending', 'error'], [s['state'] for s in self.github.statuses()])
        self.assertTrue(all(p.endswith('/statuses/' + HEAD) for _, p, _, _ in self.github.calls if '/statuses/' in p))

    def test_missing_source_conclusion_cannot_pass_artifact_delivery(self):
        self.assertEqual(0, self.run_bridge())
        self.assertEqual(0, self.deliver_artifacts(conclusion=''))
        self.assertEqual('error', self.github.statuses()[-1]['state'])

    def test_artifact_delivery_requires_reportable_source_head(self):
        self.run_bridge()
        self.github.fail_status = True
        with self.assertRaises(dispatch.Refused):
            self.deliver_artifacts()

    def test_artifact_delivery_rejects_invalid_outcome(self):
        self.run_bridge()
        with self.assertRaises(dispatch.Refused):
            self.deliver_artifacts(failed='unknown')
        self.assertEqual(['pending'], [s['state'] for s in self.github.statuses()])

    def test_artifact_delivery_push_failure_is_nonzero(self):
        self.push_event()
        self.run_bridge()
        self.assertEqual(1, self.deliver_artifacts(failed='true'))
        self.assertEqual('error', self.github.statuses()[-1]['state'])

    def test_artifact_delivery_rejects_external_result_link(self):
        self.run_bridge()
        self.env['FACTORY_GUI_SOURCE_CONCLUSION'] = 'success'
        self.env['FACTORY_GUI_ARTIFACT_DELIVERY_FAILED'] = 'false'
        self.env['FACTORY_GUI_SOURCE_TARGET_URL'] = 'https://evil.example/actions/runs/71'
        with self.assertRaises(dispatch.Refused):
            dispatch.artifact_delivery(self.env, self.event, self.github)
        self.assertEqual(['pending'], [s['state'] for s in self.github.statuses()])

    def test_zip_symlink_refused(self):
        entry = zipfile.ZipInfo('0/link')
        entry.create_system = 3
        entry.external_attr = (stat.S_IFLNK | 0o777) << 16
        with self.assertRaises(dispatch.Refused):
            dispatch.extract_artifacts(archive_bytes([(entry, b'/outside')]), self.root / 'export', 1)

    def test_zip_traversal_refused(self):
        with self.assertRaises(dispatch.Refused):
            dispatch.extract_artifacts(archive_bytes([('0/../../escape', b'x')]), self.root / 'export', 1)
        self.assertFalse((self.root / 'escape').exists())

    def test_zip_wrong_artifact_index_refused(self):
        with self.assertRaises(dispatch.Refused):
            dispatch.extract_artifacts(archive_bytes([('1/result', b'x')]), self.root / 'export', 1)

    def test_zip_entry_count_bound(self):
        with patch.object(dispatch, 'MAX_ENTRIES', 1), self.assertRaises(dispatch.Refused):
            dispatch.extract_artifacts(archive_bytes([('0/a', b'a'), ('0/b', b'b')]), self.root / 'export', 1)

    def test_zip_expansion_size_bound(self):
        with patch.object(dispatch, 'MAX_UNPACKED_BYTES', 1), self.assertRaises(dispatch.Refused):
            dispatch.extract_artifacts(archive_bytes([('0/a', b'aa')]), self.root / 'export', 1)

    def test_zip_absolute_path_refused(self):
        with self.assertRaises(dispatch.Refused):
            dispatch.extract_artifacts(archive_bytes([('/0/result', b'x')]), self.root / 'export', 1)

    def test_zip_backslash_path_refused(self):
        with self.assertRaises(dispatch.Refused):
            dispatch.extract_artifacts(archive_bytes([('0/dir\\\\result', b'x')]), self.root / 'export', 1)

    def test_zip_directory_entry_and_nested_regular_file_accepted(self):
        raw = archive_bytes([('0/', b''), ('0/nested/', b''), ('0/nested/result', b'evidence')])
        dispatch.extract_artifacts(raw, self.root / 'export', 1)
        self.assertEqual(b'evidence', (self.root / 'export/0/nested/result').read_bytes())

    def test_interrupted_wait_records_error_and_cancels_never_success(self):
        self.github.rows[0]['status'] = 'in_progress'
        def interrupted(seconds):
            raise dispatch.Refused('fake cancelled wait')
        self.clock.sleep = interrupted
        self.refused()
        self.assertTrue(any(c[1].endswith('/runs/71/cancel') for c in self.github.calls))
        self.assertEqual('error', self.github.statuses()[-1]['state'])

    def test_zip_device_entry_refused(self):
        entry = zipfile.ZipInfo('0/device')
        entry.external_attr = (stat.S_IFCHR | 0o600) << 16
        with self.assertRaises(dispatch.Refused):
            dispatch.extract_artifacts(archive_bytes([(entry, b'x')]), self.root / 'export', 1)

    def test_zip_existing_destination_never_overwritten(self):
        destination = self.root / 'export'
        destination.mkdir()
        (destination / 'sentinel').write_text('kept')
        with self.assertRaises(dispatch.Refused):
            dispatch.extract_artifacts(archive_bytes(), destination, 1)
        self.assertEqual('kept', (destination / 'sentinel').read_text())

    def test_signing_key_only_in_pipe_not_argv_environment_or_disk(self):
        calls = []
        def fake_sign(argv, **kwargs):
            calls.append((argv, kwargs))
            self.assertEqual(['/usr/bin/openssl', 'dgst', '-sha256', '-sign'], argv[:4])
            self.assertEqual('/', kwargs['cwd'])
            fd = kwargs['pass_fds'][0]
            self.assertEqual('/dev/fd/' + str(fd), argv[4])
            self.assertEqual(KEY.encode(), os.read(fd, 4096))
            self.assertNotIn(KEY, repr(argv) + repr(kwargs['env']) + repr(kwargs['input']))
            return subprocess.CompletedProcess(argv, 0, stdout=b'fake-signature')
        before = list(self.root.iterdir())
        with patch.object(dispatch.subprocess, 'run', side_effect=fake_sign), \
                patch('builtins.open', side_effect=AssertionError('SIGNING MUST NOT OPEN DISK FILES')), \
                patch.object(dispatch.tempfile, 'NamedTemporaryFile', side_effect=AssertionError('NO KEY FILE')), \
                patch.object(dispatch.tempfile, 'mkstemp', side_effect=AssertionError('NO KEY FILE')):
            jwt = dispatch.app_jwt('123', KEY, NOW)
        self.assertEqual(before, list(self.root.iterdir()))
        self.assertEqual(1, len(calls))
        payload = json.loads(base64.urlsafe_b64decode(jwt.split('.')[1] + '=='))
        self.assertEqual({'iat': NOW - 60, 'exp': NOW + 540, 'iss': '123'}, payload)

    def test_client_call_binds_github_origin_and_bounds_response(self):
        client = dispatch.GitHub()
        response = io.BytesIO(b'{"ok":true}')
        calls = []
        def fake_open(request, **kwargs):
            calls.append((request, kwargs))
            return response
        client.opener.open = fake_open
        self.assertEqual({'ok': True}, client.call('POST', 'repos/o/r/statuses/' + HEAD, 'fake', {'state': 'pending'}))
        request, kwargs = calls[0]
        self.assertEqual('https://api.github.com/repos/o/r/statuses/' + HEAD, request.full_url)
        self.assertEqual('Bearer fake', request.get_header('Authorization'))
        self.assertEqual(30, kwargs['timeout'])
        client.opener.open = lambda *a, **k: io.BytesIO(b'x' * (1024 * 1024 + 1))
        with self.assertRaises(dispatch.Refused):
            client.call('GET', 'repos/o/r', 'fake')

    def test_client_http_errors_do_not_disclose_response_or_token(self):
        client = dispatch.GitHub()
        def fake_open(request, **kwargs):
            raise urllib.error.HTTPError(request.full_url, 403, SOURCE_TOKEN, {}, io.BytesIO(KEY.encode()))
        client.opener.open = fake_open
        with self.assertRaises(dispatch.Refused) as caught:
            client.call('GET', 'repos/o/r', SOURCE_TOKEN)
        self.assertNotIn(KEY, str(caught.exception))
        self.assertNotIn(SOURCE_TOKEN, str(caught.exception))

    def test_artifact_redirect_strips_installation_credential(self):
        client = dispatch.GitHub()
        calls = []
        class Response(io.BytesIO):
            status = 200
            headers = {'Content-Length': '3'}
        def fake_open(request, **kwargs):
            calls.append(request)
            if len(calls) == 1:
                raise urllib.error.HTTPError(request.full_url, 302, 'redirect',
                                             {'Location': 'https://artifact.blob.core.windows.net/evidence'}, None)
            return Response(b'zip')
        client.opener.open = fake_open
        self.assertEqual(b'zip', client.archive('repos/o/r/actions/artifacts/1/zip', APP_TOKEN))
        self.assertEqual('Bearer ' + APP_TOKEN, calls[0].get_header('Authorization'))
        self.assertIsNone(calls[1].get_header('Authorization'))

    def test_artifact_redirect_to_untrusted_host_refused(self):
        client = dispatch.GitHub()
        calls = []
        def fake_open(request, **kwargs):
            calls.append(request)
            raise urllib.error.HTTPError(request.full_url, 302, 'redirect', {'Location': 'https://evil.example/evidence'}, None)
        client.opener.open = fake_open
        with self.assertRaises(dispatch.Refused):
            client.archive('repos/o/r/actions/artifacts/1/zip', APP_TOKEN)
        self.assertEqual(1, len(calls))

    def test_artifact_transport_declared_and_streaming_bounds(self):
        client = dispatch.GitHub()
        class Response(io.BytesIO):
            status = 200
            headers = {}
        for headers, data in (({'Content-Length': '3'}, b''), ({}, b'zip')):
            response = Response(data)
            response.headers = headers
            client.opener.open = lambda *a, **k: response
            with patch.object(dispatch, 'MAX_ZIP_BYTES', 2), self.assertRaises(dispatch.Refused):
                client.archive('repos/o/r/actions/artifacts/1/zip', APP_TOKEN)

    def test_client_no_automatic_redirect(self):
        self.assertIsNone(dispatch.NoRedirect().redirect_request(None, None, 302, '', {}, 'https://evil.example'))

    def test_signing_failure_is_refused(self):
        with patch.object(dispatch.subprocess, 'run', return_value=subprocess.CompletedProcess([], 1, stdout=b'')):
            with self.assertRaises(dispatch.Refused):
                dispatch.app_jwt('123', KEY, NOW)

    def test_signing_pipe_size_bound(self):
        with self.assertRaises(dispatch.Refused):
            dispatch.app_jwt('123', 'x' * 4097, NOW)


MUTATIONS = [
    ('app-permission', "app.get('permissions', {}).get('actions') == 'write'", 'True', 'test_app_actions_write_required'),
    ('installation-approval', "installation.get('permissions', {}).get('actions') == 'write'", 'True', 'test_installation_approval_required'),
    ('expired-token', 'now < expiry <= now + 3660', 'expiry <= now + 3660', 'test_token_expired_refused'),
    ('overlong-token', 'now < expiry <= now + 3660', 'now < expiry', 'test_token_overlong_refused'),
    ('separate-repository', "collector.lower() != repo.lower()", 'True', 'test_collector_must_be_separate_allowlisted_repository'),
    ('protected-ref', "environ['GITHUB_REF'] == 'refs/heads/' + environ['FACTORY_GUI_DEFAULT_BRANCH']", 'True', 'test_source_protected_default_ref_required'),
    ('source-allowlist', "repo.lower() == 'michaeltrefry/lensworks'", 'True', 'test_source_repository_allowlist'),
    ('base-repo', "event['pull_request']['base']['repo']['full_name'].lower() == repo.lower()", 'True', 'test_pr_base_repository_mismatch_refused'),
    ('plan-job', "plan['jobId'] == environ['FACTORY_GUI_SOURCE_JOB']", 'True', 'test_plan_identity_mismatch_refused'),
    ('plan-timeout', "1 <= plan['timeoutMinutes'] <= 180", 'True', 'test_plan_timeout_bound'),
    ('workflow-hash', "hashlib.sha256(base64.b64decode(workflow['content'])).hexdigest() == environ['FACTORY_GUI_COLLECTOR_WORKFLOW_SHA256']", 'True', 'test_workflow_hash_checked_at_immutable_central_commit'),
    ('workflow-file', "workflow['type'] == 'file'", 'True', 'test_workflow_must_be_file'),
    ('moved-head', "result['head_sha'] == tip", 'True', 'test_moved_central_head_refused'),
    ('nonce', "row.get('display_title') == title", 'True', 'test_wrong_nonce_never_accepted'),
    ('workflow-path', "result['path'] == WORKFLOW", 'True', 'test_wrong_workflow_path_refused'),
    ('event', "result['event'] == 'workflow_dispatch'", 'True', 'test_wrong_collector_event_refused'),
    ('run-attempt', "result['run_attempt'] == 1", 'True', 'test_retried_collector_run_refused'),
    ('ambiguous-run', 'len(matches) <= 1', 'True', 'test_ambiguous_runs_refused'),
    ('failed-pass', "status('success' if passed else 'failure'", "status('success' if True else 'failure'", 'test_completed_failure_is_failure_not_error_or_pass'),
    ('cancel', "if run_id is not None:", 'if False:', 'test_timeout_cancels_identified_run_and_records_error'),
    ('artifact-run', "artifact.get('workflow_run', {}).get('id', run_id) == run_id", 'True', 'test_wrong_artifact_run_refused'),
    ('artifact-expired', "not artifact['expired']", 'True', 'test_expired_artifact_refused'),
    ('artifact-nonce', "artifact['name'] == title", 'True', 'test_wrong_artifact_nonce_refused'),
    ('artifact-inventory', "exports['total_count'] == len(artifacts)", 'True', 'test_incomplete_artifact_inventory_refused'),
    ('required-artifact', "not any(a['ifNoFilesFound'] == 'error' for a in plan['artifacts'])", 'True', 'test_required_artifact_missing_refused'),
    ('zip-symlink', 'not stat.S_ISLNK(mode) and (stat.S_IFMT(mode) in (0, stat.S_IFREG, stat.S_IFDIR))', 'True', 'test_zip_symlink_refused'),
    ('zip-canonical-dot', "all(part not in ('', '.', '..') for part in raw_name.split('/'))", 'True', 'test_zip_raw_dot_component_refused'),
    ('zip-canonical-empty', "all(part not in ('', '.', '..') for part in raw_name.split('/'))", 'True', 'test_zip_raw_empty_component_refused'),
    ('collector-private', "metadata.get('private') is True", 'True', 'test_collector_must_be_private'),
    ('collector-identity', "metadata.get('full_name', '').lower() == collector.lower()", 'True', 'test_collector_metadata_identity_pinned'),
    ('collector-fork', "metadata.get('fork', False) is False", 'True', 'test_collector_cannot_be_fork'),
    ('minted-actions', "permissions.get('actions') == 'write'", 'True', 'test_minted_permissions_must_include_actions_write'),
    ('minted-contents', "permissions.get('contents') == 'read'", 'True', 'test_minted_permissions_must_not_expand_to_contents_write'),
    ('minted-extra-permission', "all(name in ('actions', 'contents', 'metadata') and (name != 'metadata' or value == 'read') for name, value in permissions.items())", 'True', 'test_minted_permissions_cannot_include_extra_write'),
    ('minted-repo-count', 'len(repositories) == 1', 'True', 'test_minted_repository_scope_cannot_include_source'),
    ('minted-repo-identity', "repositories[0].get('full_name', '').lower() == collector.lower()", 'True', 'test_minted_repository_scope_cannot_name_other_repository'),
    ('pr-base-default', "event['pull_request']['base']['ref'] == environ['FACTORY_GUI_DEFAULT_BRANCH']", 'True', 'test_pr_base_must_be_protected_default'),
    ('pr-head-repo', "event['pull_request']['head']['repo']['full_name'].lower() == repo.lower()", 'True', 'test_pr_head_repository_must_be_allowlisted'),
    ('push-event-sha', "event['after'] == sha", 'True', 'test_push_event_commit_must_match_authenticated_event'),
    ('push-event-ref', "event['ref'] == environ['GITHUB_REF']", 'True', 'test_push_event_ref_must_match_default'),
    ('push-event-repo', "event['repository']['full_name'].lower() == repo.lower()", 'True', 'test_push_event_repository_must_match_source'),
    ('zip-index', 'int(path.parts[0]) < count', 'True', 'test_zip_wrong_artifact_index_refused'),
    ('zip-count', 'len(archive.infolist()) <= MAX_ENTRIES', 'True', 'test_zip_entry_count_bound'),
    ('zip-size', 'total <= MAX_UNPACKED_BYTES', 'True', 'test_zip_expansion_size_bound'),
    ('pipe-bound', 'len(private_key.encode()) <= 4096', 'True', 'test_signing_pipe_size_bound'),
    ('key-in-argv', "'/dev/fd/' + str(read_fd)", 'private_key', 'test_signing_key_only_in_pipe_not_argv_environment_or_disk'),
    ('sign-failed', 'signed.returncode == 0', 'True', 'test_signing_failure_is_refused'),
    ('mint-permissions', "'permissions': {'actions': 'write', 'contents': 'read'}", "'permissions': {'actions': 'write'}", 'test_token_mint_is_repository_and_permission_scoped'),
    ('client-answer-bound', 'len(result) <= 1024 * 1024', 'True', 'test_client_call_binds_github_origin_and_bounds_response'),
    ('redirect-auth', "if host == 'api.github.com':", 'if True:', 'test_artifact_redirect_strips_installation_credential'),
    ('archive-declared-bound', 'length is None or int(length) <= MAX_ZIP_BYTES', 'True', 'test_artifact_transport_declared_and_streaming_bounds'),
    ('archive-stream-bound', 'output.tell() + len(chunk) <= MAX_ZIP_BYTES', 'True', 'test_artifact_transport_declared_and_streaming_bounds'),
    ('status-head', "status_route = 'repos/' + repo + '/statuses/' + sha", "status_route = 'repos/' + repo + '/statuses/' + environ['GITHUB_SHA']", 'test_success_exports_evidence_and_status_on_actual_pr_head'),
    ('receipt-required', "require(not passed, 'successful collector run", "require(True, 'successful collector run", 'test_success_requires_source_receipt'),
    ('receipt-repository', "receipt.get('repository', '').lower() == repo.lower()", 'True', 'test_receipt_repository_identity_pinned'),
    ('receipt-head', "receipt.get('sha') == sha", 'True', 'test_receipt_head_identity_pinned'),
    ('receipt-workflow', "receipt.get('workflow') == plan['workflow']", 'True', 'test_receipt_workflow_identity_pinned'),
    ('receipt-job', "receipt.get('jobId') == plan['jobId']", 'True', 'test_receipt_job_identity_pinned'),
    ('receipt-version', "receipt.get('version') == 1", 'True', 'test_receipt_version_pinned'),
    ('receipt-configuration', "receipt.get('configurationSha256') == configuration", 'True', 'test_receipt_configuration_hash_pinned'),
    ('receipt-config-format', "re.fullmatch('[0-9a-f]{64}', configuration)", 'True', 'test_receipt_configuration_hash_format_checked'),
    ('receipt-succeeded', "(not passed or receipt['succeeded'])", 'True', 'test_receipt_success_must_be_true'),
    ('receipt-boolean', "type(receipt.get('succeeded')) is bool", 'True', 'test_receipt_success_must_be_boolean'),
    ('receipt-size', 'receipt_path.stat().st_size <= 16384', 'True', 'test_receipt_size_bounded'),
    ('pr-controller-unreported', "if reported and environ['GITHUB_EVENT_NAME'] in ('pull_request_target', 'workflow_run'):", "if environ['GITHUB_EVENT_NAME'] == 'pull_request_target':", 'test_pr_controller_cannot_hide_unreported_error'),
    ('pr-controller-base-poison', "if reported and environ['GITHUB_EVENT_NAME'] in ('pull_request_target', 'workflow_run'):", 'if False:', 'test_pr_controller_error_returns_zero_only_after_head_error_report'),
    ('pr-controller-failed-result', "return 0 if passed or environ['GITHUB_EVENT_NAME'] in ('pull_request_target', 'workflow_run') else 1", 'return 0 if passed else 1', 'test_completed_failure_is_failure_not_error_or_pass'),
    ('artifact-delivery-deferred', "if not passed or not plan['artifacts']:", 'if True:', 'test_success_exports_evidence_and_status_on_actual_pr_head'),
    ('artifact-delivery-failure', "failed == 'true' or not conclusion", 'not conclusion', 'test_failed_hosted_artifact_delivery_never_posts_source_pass'),
    ('artifact-delivery-missing-result', "failed == 'true' or not conclusion", "failed == 'true'", 'test_missing_source_conclusion_cannot_pass_artifact_delivery'),
    ('artifact-delivery-validity', "conclusion in ('success', 'failure', '') and failed in ('true', 'false')", 'True', 'test_artifact_delivery_rejects_invalid_outcome'),
    ('token-renewal', 'if clock() >= expiry - 120:', 'if False:', 'test_long_job_renews_only_same_scoped_installation_token'),
    ('renewal-permissions', "renewed.get('permissions') == permissions", 'True', 'test_renewal_cannot_expand_permissions'),
    ('renewal-repository-count', 'len(renewal_repos) == 1', 'True', 'test_renewal_cannot_expand_repositories'),
    ('renewal-repository-identity', "renewal_repos[0].get('full_name', '').lower() == collector.lower()", 'True', 'test_renewal_cannot_change_repository'),
    ('renewal-expiry', 'renewal_now < renewal_expiry <= renewal_now + 3660', 'True', 'test_renewal_cannot_return_expired_token'),
]


def mutations():
    global dispatch
    failed = errors = executed = 0
    for name, old, new, case in MUTATIONS:
        if old not in TEXT:
            print(name + ': ERROR stale mutation')
            errors += 1
            continue
        try:
            dispatch = load(TEXT.replace(old, new, 1))
            red = unittest.TextTestRunner(stream=io.StringIO()).run(Contracts(case))
            dispatch = load()
            green = unittest.TextTestRunner(stream=io.StringIO()).run(Contracts(case))
            executed += 1
            if red.wasSuccessful() or not green.wasSuccessful():
                print(name + ': FAILED ' + ('SURVIVED' if red.wasSuccessful() else 'restored not GREEN'))
                failed += 1
            else:
                print(name + ': RED (mutation rejected), restored GREEN')
        except Exception as error:
            print(name + ': ERROR ' + type(error).__name__)
            errors += 1
        finally:
            dispatch = load()
    print('GUI_SOURCE_DISPATCH_MUTATION_RESULT ' + json.dumps(dict(executed=executed, skipped=0, failures=failed, errors=errors)))
    return 0 if executed == len(MUTATIONS) and not failed and not errors else 1


if __name__ == '__main__':
    if '--mutations' in sys.argv:
        sys.exit(mutations())
    result = unittest.TextTestRunner(verbosity=1).run(unittest.defaultTestLoader.loadTestsFromTestCase(Contracts))
    print('GUI_SOURCE_DISPATCH_CONTRACT_RESULT ' + json.dumps(dict(executed=result.testsRun, skipped=len(result.skipped),
                                                                 failures=len(result.failures), errors=len(result.errors))))
    sys.exit(0 if result.wasSuccessful() and not result.skipped else 1)
