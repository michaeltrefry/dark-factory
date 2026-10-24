# Real sealed GUI collector contracts over fake privileged operations. This file
# never invokes a native helper, changes an account, launches a GUI or sends signals.
import contextlib
import json
import os
import pathlib
import runpy
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import zipfile
from unittest import mock

module = runpy.run_path(sys.argv[1], run_name='sealed_gui_fixture')
g = module['main'].__globals__
helper = runpy.run_path(sys.argv[2], run_name='root_helper_fixture')
h = helper['main'].__globals__
TEST = 'WidgetTests/Suite/newTest()'


class GuiContracts(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.directory.name)
        self.calls = []
        self.environ = mock.patch.dict(os.environ, SIDE='base')
        self.environ.start()
        g['EXECUTION_DEADLINE'] = time.monotonic() + 10

    def tearDown(self):
        self.environ.stop()
        self.directory.cleanup()

    def operation(self, action, root=None, extra=None, cleanup=False):
        self.calls.append((action, root, extra))
        return ''

    def test_machine_lease_waits_for_other_job(self):
        config = {'stateDirectory': str(self.root)}
        entered = threading.Event()
        finished = threading.Event()
        def second():
            with g['GuiLease'](config):
                entered.set()
            finished.set()
        with g['GuiLease'](config):
            thread = threading.Thread(target=second)
            thread.start()
            self.assertFalse(entered.wait(0.1))
        self.assertTrue(finished.wait(3))
        thread.join()
        self.assertTrue(entered.is_set())

    def test_privileged_operations_require_the_held_lease_and_its_exact_workspace(self):
        config = {'stateDirectory': str(self.root), 'collectorUid': os.getuid()}
        workspace = self.root / 'fresh'
        with mock.patch.dict(h, protected=lambda *a: None):
            with g['GuiLease'](config) as lease:
                lease.record(workspace)
                h['require_lease'](config, workspace)
                with self.assertRaises(ValueError): h['require_lease'](config, self.root / 'another')
            with self.assertRaises(ValueError): h['require_lease'](config, workspace)

    def test_unknown_cleanup_blocks_reuse_then_next_job_reconciles_automatically(self):
        previous = self.root / 'old-work'
        previous.mkdir()
        marker = self.root / 'active.json'
        device = '11111111-1111-1111-1111-111111111111'
        marker.write_text(json.dumps({'root': str(previous), 'devices': [device]}))
        with mock.patch.dict(g, gui_operation=mock.Mock(side_effect=ValueError('not stopped'))):
            with self.assertRaises(ValueError):
                with g['GuiLease']({'stateDirectory': str(self.root)}):
                    self.fail('replacement admitted')
        self.assertTrue(marker.exists())
        with mock.patch.dict(g, gui_operation=self.operation):
            with g['GuiLease']({'stateDirectory': str(self.root)}):
                pass
        self.assertEqual(['device-delete', 'remove'], [c[0] for c in self.calls])
        self.assertFalse(marker.exists())

    def test_fresh_home_workspace_and_derived_data_do_not_reuse_prior_job(self):
        persistent = self.root / 'persistent-account-state'
        persistent.write_text('accepted trusted CI state')
        config = {'sourceUid': os.getuid(), 'developer': '/Applications/Fixture.app/Contents/Developer'}
        homes = []
        with mock.patch.dict(g, GUI_CONFIG=config, gui_operation=self.operation), mock.patch.object(g['GuiPrincipal'], 'run', side_effect=[(0, 'Aqua'), (0, str(os.getuid()))] * 2):
            for number in range(2):
                job = self.root / ('job-' + str(number))
                job.mkdir()
                lease = mock.Mock()
                with mock.patch.dict(g, GUI_LEASE=lease):
                    principal = g['GuiPrincipal'](job)
                    homes.append(principal.home)
                    self.assertEqual([], list((principal.home / 'tmp').iterdir()))
                    (principal.home / 'old-cache').write_text('not inherited')
                    principal.close()
                    self.assertTrue(lease.cleaned)
        self.assertNotEqual(homes[0], homes[1])
        self.assertEqual('accepted trusted CI state', persistent.read_text())
        self.assertEqual(2, len([c for c in self.calls if c[0] == 'remove']))

    def test_recipe_environment_is_preserved_but_collector_environment_is_not_forwarded(self):
        config = {'sourceUid': os.getuid(), 'developer': '/Applications/Fixture.app/Contents/Developer'}
        payloads = []
        def command(args, cwd, **kwargs):
            payload = json.loads(pathlib.Path(args[-1]).read_text())
            payloads.append(payload)
            return 0, 'Aqua' if payload['argv'] == ['/bin/launchctl', 'managername'] else str(os.getuid()) if payload['argv'] == ['/usr/bin/id', '-u'] else 'ok'
        with mock.patch.dict(g, GUI_CONFIG=config, GUI_LEASE=mock.Mock(), gui_operation=self.operation, command=command):
            principal = g['GuiPrincipal'](self.root)
            principal.run(['/usr/bin/true'], self.root, extra_environment={'RECIPE_FLAG': 'kept'})
        environment = payloads[-1]['environment']
        self.assertEqual('kept', environment['RECIPE_FLAG'])
        self.assertNotIn('GITHUB_TOKEN', environment)
        self.assertNotIn('ACTIONS_RUNTIME_TOKEN', environment)
        self.assertNotIn('GITHUB_OUTPUT', environment)
        self.assertEqual(str(self.root / 'home'), environment['HOME'])

    def execute(self, platform='macOS', build_failure=False, cleanup_failure=False, trusted=True):
        base, head = '1' * 40, '2' * 40
        plan = {'side': 'base', 'commit': base, 'overlayFrom': head, 'overlayPaths': ['Tests/New.swift'], 'deletePaths': [],
                'replacements': {}, 'sourceRepository': 'acme/source', 'builds': [{'flag': '-project', 'container': 'Widget.xcodeproj',
                'scheme': 'Widget', 'tests': [TEST], 'platform': platform}], 'prepare': None}
        request = {'BASE': base, 'HEAD': head, 'SIDE': 'base', 'NONCE': 'a' * 32, 'TESTS': json.dumps([TEST])}
        env = {'runtime': 'runtime', 'deviceType': 'phone', 'xcode': '26.5', 'build': '17F42'}
        class FakePrincipal:
            def __init__(self, root): self.root = root; self.home = root / 'home'; self.home.mkdir(); self.worktree = root / 'base'
            def share(self, path): pass
            def run(inner, args, cwd, **kwargs):
                self.calls.append(args)
                if args == ['/usr/bin/xcodebuild', '-version']: return 0, 'Xcode 26.5\nBuild version 17F42'
                if args[:3] == ['/usr/bin/xcrun', 'simctl', 'list']:
                    return 0, json.dumps({'runtimes': [{'identifier': 'runtime', 'isAvailable': True}], 'devicetypes': [{'identifier': 'phone'}]})
                if args[:3] == ['/usr/bin/xcrun', 'simctl', 'create']: return 0, '11111111-1111-1111-1111-111111111111'
                if 'build-for-testing' in args:
                    self.assertEqual('from head', (cwd / 'Tests/New.swift').read_text())
                    self.assertEqual('base production', (cwd / 'Production.swift').read_text())
                    return (65, str(cwd / 'Tests/New.swift') + ':1:1: error: missing symbol') if build_failure else (0, '')
                if 'test-without-building' in args: return 65, 'test output'
                return 0, json.dumps({'testNodes': [{'nodeType': 'Unit test bundle', 'name': 'WidgetTests', 'children': [
                    {'nodeType': 'Test Case', 'nodeIdentifier': 'Suite/newTest()', 'result': 'Failed'}]}]})
            def close(inner):
                shutil.rmtree(inner.root)
                if cleanup_failure: raise ValueError('cleanup failed')
        (self.root / 'sources').mkdir()
        for side in ('base', 'head'):
            with zipfile.ZipFile(self.root / 'sources' / (side + '.zip'), 'w') as archive:
                archive.writestr('repository/Tests/New.swift', 'from head' if side == 'head' else 'old test')
                archive.writestr('repository/Production.swift', side + ' production')
        prior = pathlib.Path.cwd()
        os.chdir(self.root)
        try:
            with mock.patch.dict(os.environ, SOURCE_REPOSITORY='acme/source'), mock.patch.dict(g,
                    GUI_CONFIG={'trustedRepositories': ['acme/source'] if trusted else [], 'hash': 'a' * 64}, GUI_LEASE=mock.Mock(), ENVIRONMENT=env,
                    Principal=FakePrincipal, package_snapshot=lambda *a: {}, hydrate_packages=lambda *a: {}, hydrate_binary_packages=lambda *a: False):
                g['execution_main'](request, json.dumps(plan))
            return json.loads((self.root / 'result.json').read_text())
        finally:
            os.chdir(prior)

    def test_unallowlisted_source_never_reaches_execution(self):
        with self.assertRaises(ValueError): self.execute(trusted=False)
        self.assertEqual([], self.calls)

    def test_actual_mac_gui_execution_preserves_exact_overlay_and_per_test_output(self):
        result = self.execute()
        self.assertEqual('ran', result['execution']['status'])
        self.assertEqual('a' * 64, result['guiConfigurationHash'])
        self.assertEqual(2, len(result['execution']['files']))
        self.assertTrue(any('platform=macOS' in command for command in self.calls))
        self.assertFalse(any('simctl' in command for command in self.calls))

    def test_actual_ios_gui_execution_selects_pinned_simulator(self):
        result = self.execute('iOS')
        self.assertEqual('ran', result['execution']['status'])
        self.assertTrue(any('phone' in command and 'runtime' in command for command in self.calls))

    def test_build_failure_is_ordinary_compiler_evidence_without_test_reports(self):
        result = self.execute(build_failure=True)
        self.assertEqual('build-failed', result['execution']['status'])
        self.assertEqual([], result['execution']['files'])
        self.assertIn('missing symbol', result['execution']['log'])

    def test_failed_cleanup_publishes_no_evidence(self):
        with self.assertRaises(ValueError): self.execute(cleanup_failure=True)
        self.assertFalse((self.root / 'result.json').exists())

    def configuration(self, same_uid=False, source_sudo=False, source_credential=False, unknown_key=False, cleanup=False):
        from types import SimpleNamespace
        collector = self.root.resolve() / 'collector'
        source = self.root.resolve() / 'lwrunner'
        runner = collector / 'runner'
        state = collector / 'state'
        retired = source / 'old-runner'
        for path in (runner, state, retired): path.mkdir(parents=True, exist_ok=True)
        (runner / '.runner').write_text(json.dumps({'agentName': 'lensworks-guest', 'gitHubUrl': 'https://github.com/acme/collector'}))
        if source_credential: (retired / '.credentials').write_text('must refuse')
        config = {'collectorUid': 501, 'sourceUid': 501 if same_uid else 502, 'sourceUser': 'lwrunner',
                  'runnerRoot': str(runner), 'stateDirectory': str(state), 'retiredRunnerRoots': [str(retired)],
                  'developer': '/usr', 'xcode': '26.5', 'build': '17F42', 'runtime': 'runtime', 'deviceType': 'phone',
                  'trustedRepositories': ['acme/source'], 'runnerName': 'lensworks-guest', 'collectorRepository': 'acme/collector'}
        if unknown_key: config['bypass'] = True
        path = self.root / 'configuration.json'
        path.write_text(json.dumps(config))
        def user(uid): return SimpleNamespace(pw_dir=str(collector if uid == 501 else source))
        def run(args, **kwargs):
            return SimpleNamespace(returncode=0 if args[0].endswith('/id') or source_sudo else 1, stdout=b'staff')
        with mock.patch.dict(h, CONFIG=path, protected=lambda *a, **k: None), mock.patch.object(os, 'geteuid', return_value=0), \
                mock.patch.dict(os.environ, SUDO_UID='501'), mock.patch.object(h['pwd'], 'getpwnam', return_value=SimpleNamespace(pw_uid=config['sourceUid'])), \
                mock.patch.object(h['pwd'], 'getpwuid', side_effect=user), mock.patch.object(subprocess, 'run', side_effect=run):
            return h['configuration'](cleanup=cleanup)

    def test_configuration_accepts_only_separate_collector_and_existing_source_identity(self):
        self.assertEqual('lwrunner', self.configuration()['sourceUser'])
        with self.assertRaises(ValueError): self.configuration(same_uid=True)

    def test_execution_account_with_old_runner_authentication_is_refused(self):
        with self.assertRaises(ValueError): self.configuration(source_credential=True)

    def test_execution_account_with_sudo_authority_is_refused(self):
        with self.assertRaises(ValueError): self.configuration(source_sudo=True)

    def test_cleanup_only_can_reconcile_when_new_source_execution_is_refused(self):
        self.assertEqual(502, self.configuration(source_sudo=True, source_credential=True, cleanup=True)['sourceUid'])
        with self.assertRaises(ValueError): self.configuration(source_sudo=True, source_credential=True)

    def test_unknown_configuration_keys_cannot_widen_authority(self):
        with self.assertRaises(ValueError): self.configuration(unknown_key=True)

    def test_root_helper_never_executes_source_as_collector_or_root(self):
        request = self.root / ('command-' + 'a' * 32 + '.json')
        request.write_text(json.dumps({'argv': ['/usr/bin/true'], 'cwd': str(self.root), 'environment': {'HOME': str(self.root / 'home')}}))
        config = {'collectorUid': 501, 'sourceUid': 502, 'hash': 'a' * 64}
        executed = []
        def run(args, **kwargs):
            executed.append((args, kwargs))
            return mock.Mock(returncode=0)
        with mock.patch.dict(h, configuration=lambda **kwargs: config, workspace=lambda *a, **k: self.root, protected=lambda *a: None, require_lease=lambda *a: None,
                register_command=lambda *a: contextlib.nullcontext()), \
                mock.patch.object(sys, 'argv', ['helper', 'run', str(self.root), str(request)]), \
                mock.patch.object(subprocess, 'run', side_effect=run):
            self.assertEqual(0, h['main']())
        self.assertEqual('/bin/launchctl', executed[0][0][0])
        self.assertEqual(['asuser', '502', '/usr/bin/sudo', '-n', '-u', 'lwrunner', '/usr/bin/env', '-i'], executed[0][0][1:9])
        self.assertEqual({'PATH': '/usr/bin:/bin:/usr/sbin:/sbin'}, executed[0][1]['env'])
        self.assertEqual(self.root, executed[0][1]['cwd'])

    def test_cleanup_proves_privileged_command_group_identity_before_stopping_it(self):
        record = self.root / 'gui-command-400.json'
        record.write_text(json.dumps({'group': 400, 'start': 'known start'}))
        with mock.patch.dict(h, protected=lambda *a: None, process_groups=mock.Mock(side_effect=[[(400, 400, 'known start')], []])), \
                mock.patch.object(os, 'getpgrp', return_value=999), mock.patch.object(os, 'killpg') as signal_fake:
            h['stop_commands'](self.root)
        signal_fake.assert_called_once()
        self.assertFalse(record.exists())

    def test_ambiguous_privileged_command_identity_quarantines_without_signalling(self):
        record = self.root / 'gui-command-400.json'
        record.write_text(json.dumps({'group': 400, 'start': 'known start'}))
        with mock.patch.dict(h, protected=lambda *a: None, process_groups=lambda: [(400, 400, 'different start')]), \
                mock.patch.object(os, 'getpgrp', return_value=999), mock.patch.object(os, 'killpg') as signal_fake:
            with self.assertRaises(ValueError): h['stop_commands'](self.root)
        signal_fake.assert_not_called()
        self.assertTrue(record.exists())


if __name__ == '__main__':
    sys.argv = [sys.argv[0]]
    unittest.main()
