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
helper_bytes = pathlib.Path(sys.argv[2]).read_bytes()
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
        marker.write_text(json.dumps({'root': str(previous), 'devices': [device], 'deviceIntents': []}))
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
            principal.run(['/usr/bin/true'], self.root, extra_environment={'RECIPE_FLAG': 'kept', 'TEST_RUNNER_REPO_MODE':'fixture'})
        environment = payloads[-1]['environment']
        self.assertEqual('kept', environment['RECIPE_FLAG'])
        self.assertEqual('fixture', environment['TEST_RUNNER_REPO_MODE'])
        self.assertNotIn('GITHUB_TOKEN', environment)
        self.assertNotIn('ACTIONS_RUNTIME_TOKEN', environment)
        self.assertNotIn('GITHUB_OUTPUT', environment)
        self.assertEqual(str(self.root / 'home'), environment['HOME'])

    def execute(self, platform='macOS', build_failure=False, cleanup_failure=False, trusted=True, empty_inventory_cleanup=False):
        base, head = '1' * 40, '2' * 40
        plan = {'side': 'base', 'commit': base, 'overlayFrom': head, 'overlayPaths': ['Tests/New.swift'], 'deletePaths': [],
                'testEnvironment': {'REPO_MODE': 'fixture', 'REPO_HELPER': '${worktree}/helper'}, 'replacements': {}, 'sourceRepository': 'acme/source', 'builds': [{'flag': '-project', 'container': 'Widget.xcodeproj',
                'scheme': 'Widget', 'tests': [TEST], 'platform': platform}], 'prepare': None}
        request = {'BASE': base, 'HEAD': head, 'SIDE': 'base', 'NONCE': 'a' * 32, 'TESTS': json.dumps([TEST])}
        env = {'runtime': 'runtime', 'deviceType': 'phone', 'xcode': '26.5', 'build': '17F42'}
        class FakePrincipal:
            def __init__(self, root): self.root = root; self.home = root / 'home'; self.home.mkdir(); self.worktree = root / 'base'
            def share(self, path): pass
            def run(inner, args, cwd, **kwargs):
                self.calls.append(args)
                if 'test-without-building' in args:
                    self.assertEqual({'TEST_RUNNER_REPO_MODE':'fixture','TEST_RUNNER_REPO_HELPER':str(inner.worktree.resolve()/'helper')},kwargs.get('extra_environment'))
                else:
                    self.assertFalse(kwargs.get('extra_environment'), 'repository test inputs reached a build or collector')
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
                if empty_inventory_cleanup:
                    with mock.patch.object(subprocess, 'run', side_effect=self.ps_answer()):
                        h['sweep'](502)
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

    @staticmethod
    def ps_answer(code=1, output=b'', error=b''):
        from types import SimpleNamespace
        def answer(args, **kwargs):
            result = SimpleNamespace(returncode=code, stdout=output, stderr=error) if args[0] == '/bin/ps' else SimpleNamespace(returncode=0, stdout=b'', stderr=b'')
            if kwargs.get('check') and result.returncode:
                raise subprocess.CalledProcessError(result.returncode, args, output=result.stdout, stderr=result.stderr)
            return result
        return answer

    def test_ps_no_matches_exit_one_empty_allows_evidence_and_reuse(self):
        result, error = None, None
        try: result = self.execute(empty_inventory_cleanup=True)
        except Exception as caught: error = caught
        self.assertIsNone(error, 'an empty successful UID cleanup must not suppress executed evidence')
        self.assertEqual('ran', result['execution']['status'])
        state = self.root / 'empty-cleanup-state'
        state.mkdir()
        with g['GuiLease']({'stateDirectory': str(state)}): pass
        with g['GuiLease']({'stateDirectory': str(state)}): pass
        self.assertFalse((state / 'active.json').exists())

    def test_malformed_or_failed_process_inventory_keeps_reuse_quarantined(self):
        for index, (code, output, error) in enumerate(((1, b'', b'unknown user'), (2, b'', b'failed'), (0, b'malformed', b''))):
            with self.subTest(code=code, output=output, error=error):
                state = self.root / ('bad-inventory-' + str(index))
                state.mkdir()
                workspace = self.root / ('bad-work-' + str(index))
                workspace.mkdir()
                failure = None
                try:
                    with g['GuiLease']({'stateDirectory': str(state)}) as lease:
                        lease.record(workspace)
                        with mock.patch.object(subprocess, 'run', side_effect=self.ps_answer(code, output, error)):
                            h['sweep'](502)
                except Exception as caught: failure = caught
                self.assertIsInstance(failure, ValueError)
                self.assertTrue((state / 'active.json').exists())
                with mock.patch.dict(g, gui_operation=mock.Mock(side_effect=ValueError('inventory still unknown'))):
                    with self.assertRaises(ValueError):
                        with g['GuiLease']({'stateDirectory': str(state)}): self.fail('replacement admitted')

    def test_creation_effect_with_lost_answer_is_reconciled_by_durable_exact_name_before_new_work(self):
        from types import SimpleNamespace
        state = self.root / 'creation-state'
        state.mkdir()
        workspace = self.root / 'creation-work'
        workspace.mkdir()
        name = 'factory-' + 'c' * 32
        owned = '11111111-1111-1111-1111-111111111111'
        unrelated = {'name': 'unrelated-existing-device', 'udid': '22222222-2222-2222-2222-222222222222'}
        inventory = [dict(unrelated)]
        events = []
        config = {'stateDirectory': str(state), 'sourceUid': os.getuid(), 'developer': '/usr'}
        def source_tool(fixed, root, args):
            self.assertEqual(config['sourceUid'], fixed['sourceUid'])
            if args[2:4] == ['list', 'devices']:
                return SimpleNamespace(returncode=0, stdout=json.dumps({'devices': {'runtime': inventory}}).encode())
            if args[2] == 'delete':
                self.assertEqual(owned, args[3])
                inventory[:] = [d for d in inventory if d['udid'] != args[3]]
                events.append('owned-device-removed')
            return SimpleNamespace(returncode=0, stdout=b'')
        def operation(action, root=None, extra=None, cleanup=False):
            if action == 'device-reconcile':
                with mock.patch.dict(h, source_tool=source_tool, stop_commands=lambda *a: None, sweep=lambda *a: None), \
                        mock.patch.object(sys, 'argv', ['helper', action, str(root), extra]):
                    h['execute_operation'](config, action, root)
            elif action == 'remove':
                shutil.rmtree(root)
                events.append('workspace-removed')
            return ''
        def command(args, cwd, **kwargs):
            payload = json.loads(pathlib.Path(args[-1]).read_text())
            argv = payload['argv']
            if argv == ['/bin/launchctl', 'managername']: return 0, 'Aqua'
            if argv == ['/usr/bin/id', '-u']: return 0, str(os.getuid())
            journal = json.loads((state / 'active.json').read_text())
            self.assertEqual([name], journal.get('deviceIntents'), 'creation must be journaled BEFORE simctl can create')
            inventory.append({'name': name, 'udid': owned})
            events.append('creation-effect-with-lost-answer')
            raise ValueError('collector lost the creation answer')
        with mock.patch.dict(g, GUI_CONFIG=config, gui_operation=operation, command=command):
            with g['GuiLease'](config) as lease:
                with mock.patch.dict(g, GUI_LEASE=lease):
                    principal = g['GuiPrincipal'](workspace)
                    with self.assertRaises(ValueError):
                        principal.run(['/usr/bin/xcrun', 'simctl', 'create', name, 'phone', 'runtime'], workspace)
            self.assertEqual([name], json.loads((state / 'active.json').read_text())['deviceIntents'])
            with g['GuiLease'](config):
                events.append('new-work-admitted')
                self.assertEqual([unrelated], inventory)
        self.assertEqual(['creation-effect-with-lost-answer', 'owned-device-removed', 'workspace-removed', 'new-work-admitted'], events)

    def test_ambiguous_or_unreadable_creation_intent_inventory_never_deletes_a_device(self):
        from types import SimpleNamespace
        name = 'factory-' + 'd' * 32
        inventories = [{'devices': {'runtime': [{'name': name, 'udid': '1' * 8 + '-1111-1111-1111-' + '1' * 12},
                                               {'name': name, 'udid': '2' * 8 + '-2222-2222-2222-' + '2' * 12}]}},
                       {'devices': {'runtime': [{'name': name, 'udid': 'unknown'}]}}, {'devices': ['unknown']}]
        for inventory in inventories:
            with self.subTest(inventory=inventory):
                tool = mock.Mock(return_value=SimpleNamespace(returncode=0, stdout=json.dumps(inventory).encode()))
                with mock.patch.dict(h, source_tool=tool, stop_commands=lambda *a: None, sweep=lambda *a: None), \
                        mock.patch.object(sys, 'argv', ['helper', 'device-reconcile', str(self.root), name]):
                    with self.assertRaises(ValueError): h['execute_operation']({'sourceUid': 502}, 'device-reconcile', self.root)
                self.assertFalse(any(call.args[2][2] == 'delete' for call in tool.call_args_list))

    def test_outer_empty_execution_refuses_source_missing_from_protected_allowlist_before_any_workspace(self):
        from types import SimpleNamespace
        class ProtectedHelper:
            parents = ()
            def lstat(self): return SimpleNamespace(st_mode=0o100755, st_uid=0)
            def read_bytes(self): return helper_bytes
            def __str__(self): return 'protected-helper-fixture'
        config = {'collectorRepository': 'acme/collector', 'runnerName': 'lensworks-guest', 'trustedRepositories': ['acme/other'],
                  'runtime': 'runtime', 'deviceType': 'phone', 'xcode': '26.5', 'build': '17F42', 'hash': 'a' * 64}
        lease = mock.Mock(return_value=contextlib.nullcontext(mock.Mock()))
        legacy = mock.Mock()
        environment = {'SOURCE_REPOSITORY': 'acme/source', 'GITHUB_REPOSITORY': 'acme/collector', 'RUNNER_NAME': 'lensworks-guest',
                       'EXECUTION': '', 'EXECUTION_BUDGET_SECONDS': '60', 'EXECUTION_DEADLINE_UNIX_SECONDS': str(time.time() + 60)}
        error = None
        with mock.patch.dict(os.environ, environment), mock.patch.dict(g, GUI_HELPER=ProtectedHelper(), GUI_CONFIG=None,
                command=lambda args, *a, **k: (0, 'Background' if args[0] == '/bin/launchctl' else 'owned\n'),
                gui_operation=lambda *a, **k: json.dumps(config), GuiLease=lease, _gui_original_main=legacy):
            try: g['main']()
            except ValueError as caught: error = caught
        self.assertIsNotNone(error, 'the protected source allowlist applies before legacy or typed execution')
        lease.assert_not_called()
        legacy.assert_not_called()

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
