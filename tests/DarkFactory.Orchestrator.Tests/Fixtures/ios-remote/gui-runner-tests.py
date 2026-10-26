# Real sealed GUI collector contracts over fake privileged operations. This file
# never invokes a native helper, changes an account, launches a GUI or sends signals.
import contextlib
import json
import os
import pathlib
import runpy
import shutil
import stat
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import zipfile
from unittest import mock
import uuid

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
        state = self.root / 'collector-state'
        state.mkdir(mode=0o700)
        marker = state / 'active.json'
        marker.write_text(json.dumps({'root': str(self.root.resolve()), 'devices': [], 'deviceIntents': []}))
        marker.chmod(0o600)
        self.gui_config = {'collectorUid': os.getuid(), 'sourceUid': 502, 'hash': 'a' * 64, 'stateDirectory': str(state)}
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
                        h['sweep'](502, self.root, self.gui_config)
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
                    GUI_CONFIG={'trustedRepositories': ['acme/source'] if trusted else [], 'hash': 'a' * 64}, GUI_LEASE=mock.Mock(evidence=mock.Mock(return_value={})), ENVIRONMENT=env,
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
                            h['sweep'](502, self.root, self.gui_config)
                except Exception as caught: failure = caught
                self.assertIsInstance(failure, ValueError)
                self.assertTrue((state / 'active.json').exists())
                with mock.patch.dict(g, gui_operation=mock.Mock(side_effect=ValueError('inventory still unknown'))):
                    with self.assertRaises(ValueError):
                        with g['GuiLease']({'stateDirectory': str(state)}): self.fail('replacement admitted')

    @contextlib.contextmanager
    def os_agent_fixture(self):
        real_fstat = os.fstat
        def root_stat(fd):
            node = list(real_fstat(fd))
            node[4] = 0  # fake only st_uid; preserve inode/device so temporary-tree deletion still checks its own descriptors
            return os.stat_result(node)
        binaries = {'/System/Library/FixtureAgent', '/System/Library/NewAgent', '/usr/sbin/distnoted', '/usr/libexec/lsd', '/usr/sbin/cfprefsd'}
        # Only metadata is faked: actual temporary-file bytes, exclusive creation, modes and parser are exercised. No native ps,
        # pkill, sudo, GUI, UID inventory or signal is invoked by these tests.
        with mock.patch.dict(h, protected=lambda *a: None, root_os_binary=lambda binary: binary in binaries), \
                mock.patch.object(os, 'fstat', side_effect=root_stat), \
                mock.patch.object(time, 'sleep'):
            yield

    def test_os_agent_baseline_precedes_every_source_command_and_failure_starts_none(self):
        events = []
        config = {'sourceUid': os.getuid(), 'developer': '/usr'}
        def operation(action, *args, **kwargs): events.append(action)
        def run(args, *a, **k):
            events.append('source')
            return 0, 'Aqua' if args[0] == '/bin/launchctl' else str(os.getuid())
        with mock.patch.dict(g, GUI_CONFIG=config, GUI_LEASE=mock.Mock(), gui_operation=operation), \
                mock.patch.object(g['GuiPrincipal'], 'run', side_effect=run):
            g['GuiPrincipal'](self.root)
        self.assertEqual(['baseline', 'share', 'source', 'source'], events)
        failed = self.root / 'failed-baseline'
        failed.mkdir()
        events.clear()
        error = None
        with mock.patch.dict(g, GUI_CONFIG=config, GUI_LEASE=mock.Mock(), gui_operation=mock.Mock(side_effect=ValueError('baseline unknown'))), \
                mock.patch.object(g['GuiPrincipal'], 'run', side_effect=run):
            try: g['GuiPrincipal'](failed)
            except ValueError as caught: error = caught
        self.assertTrue(isinstance(error, ValueError) and not events, 'a failed baseline cannot run source commands')

    def test_root_baseline_captures_only_pre_source_root_os_commands_once(self):
        command = '/System/Library/FixtureAgent --session Aqua'
        inventory = ('41 1 /System/Library/FixtureAgent ' + command + '\n'
                     '42 2 /System/Library/NewAgent /System/Library/NewAgent\n'
                     '43 1 /tmp/source /tmp/source --persist\n').encode()
        with self.os_agent_fixture(), mock.patch.object(subprocess, 'run', side_effect=self.ps_answer(0, inventory)):
            h['baseline'](self.root, 502, self.gui_config)
            record = self.root / h['BASELINE_NAME']
            value = json.loads(record.read_text())
            self.assertEqual(({'version': 1, 'uid': 502, 'root': str(self.root.resolve()), 'configurationHash': 'a' * 64,
                               'agents': [{'executable': '/System/Library/FixtureAgent', 'command': command}]},
                              0o600, {('/System/Library/FixtureAgent', command)}),
                             (value, stat.S_IMODE(record.stat().st_mode), h['read_baseline'](self.root, 502, self.gui_config)))
            with self.assertRaises(FileExistsError): h['baseline'](self.root, 502, self.gui_config)

    def test_fixed_root_baseline_operation_dispatches_under_the_workspace_lease(self):
        config = self.gui_config
        command = '/System/Library/FixtureAgent --session Aqua'
        error = None
        with self.os_agent_fixture(), mock.patch.dict(h, configuration=lambda **k: config, workspace=lambda *a, **k: self.root,
                require_lease=lambda *a: None, register_command=lambda *a: contextlib.nullcontext()), \
                mock.patch.object(sys, 'argv', ['helper', 'baseline', str(self.root)]), \
                mock.patch.object(subprocess, 'run', side_effect=self.ps_answer(0, ('41 1 /System/Library/FixtureAgent ' + command + '\n').encode())):
            try: h['main']()
            except ValueError as caught: error = caught
            self.assertTrue(error is None and h['read_baseline'](self.root, 502, self.gui_config) == {('/System/Library/FixtureAgent', command)},
                            'the fixed baseline operation must capture the leased workspace before source execution')

    def test_only_exact_baselined_full_commands_may_respawn_with_new_pids(self):
        command = '/System/Library/FixtureAgent --session Aqua'
        with self.os_agent_fixture(), mock.patch.object(subprocess, 'run', side_effect=self.ps_answer(0,
                ('41 1 /System/Library/FixtureAgent ' + command + '\n').encode())):
            h['baseline'](self.root, 502, self.gui_config)
        error = None
        with self.os_agent_fixture(), mock.patch.object(subprocess, 'run', side_effect=self.ps_answer(0,
                ('999 1 /System/Library/FixtureAgent ' + command + '\n').encode())):
            try: h['sweep'](502, self.root, self.gui_config)
            except ValueError as caught: error = caught
        self.assertIsNone(error, 'an exact baseline OS command respawned under launchd is allowed, independent of pid')

    def test_new_system_binary_changed_arguments_parent_or_ownership_quarantines_cleanup(self):
        command = '/System/Library/FixtureAgent --session Aqua'
        with self.os_agent_fixture(), mock.patch.object(subprocess, 'run', side_effect=self.ps_answer(0,
                ('41 1 /System/Library/FixtureAgent ' + command + '\n').encode())):
            h['baseline'](self.root, 502, self.gui_config)
        rows = ['99 1 /System/Library/NewAgent /System/Library/NewAgent',
                '99 1 /System/Library/FixtureAgent /System/Library/FixtureAgent --session Other',
                '99 2 /System/Library/FixtureAgent ' + command,
                '99 1 /usr/sbin/distnoted /usr/sbin/distnoted --source-arguments',
                '99 1 /usr/libexec/lsd /usr/libexec/lsd --source-arguments',
                '99 1 /usr/sbin/cfprefsd /usr/sbin/cfprefsd agent --source-arguments']
        for row in rows:
            with self.subTest(row=row), self.os_agent_fixture(), \
                    mock.patch.object(subprocess, 'run', side_effect=self.ps_answer(0, (row + '\n').encode())):
                with self.assertRaises(ValueError): h['sweep'](502, self.root, self.gui_config)
        with self.os_agent_fixture(), mock.patch.dict(h, root_os_binary=lambda binary: False), \
                mock.patch.object(subprocess, 'run', side_effect=self.ps_answer(0, ('99 1 /System/Library/FixtureAgent ' + command + '\n').encode())):
            with self.assertRaises(ValueError): h['sweep'](502, self.root, self.gui_config)

    def test_three_observed_agents_require_their_exact_full_commands_and_root_binary(self):
        commands = ['/usr/sbin/distnoted agent', '/usr/libexec/lsd', '/usr/sbin/cfprefsd agent']
        for command in commands:
            binary = command.split()[0]
            with self.subTest(command=command), self.os_agent_fixture(), \
                    mock.patch.object(subprocess, 'run', side_effect=self.ps_answer(0, ('99 1 ' + binary + ' ' + command + '\n').encode())):
                error = None
                try: h['sweep'](502, self.root, self.gui_config)
                except ValueError as caught: error = caught
                self.assertIsNone(error, 'only the exact observed launchd agent command is allowed without a baseline entry')
                with mock.patch.dict(h, root_os_binary=lambda binary: False):
                    with self.assertRaises(ValueError): h['sweep'](502, self.root, self.gui_config)

    def test_baseline_read_refuses_wrong_schema_uid_modes_owners_links_and_bounds(self):
        from types import SimpleNamespace
        record = self.root / h['BASELINE_NAME']
        valid = {'version': 1, 'uid': 502, 'root': str(self.root.resolve()), 'configurationHash': 'a' * 64, 'agents': []}
        invalid = [dict(valid, uid=503), dict(valid, root=str(self.root / 'other')), dict(valid, configurationHash='b' * 64),
                   dict(valid, version=True), dict(valid, bypass=True), dict(valid, agents=[{}]),
                   dict(valid, agents=[{'executable': '/System/Library/FixtureAgent', 'command': '/System/Library/FixtureAgent ' + 'x' * 4096}]),
                   dict(valid, agents=[{'executable': '/System/Library/FixtureAgent', 'command': '/tmp/source'}]),
                   dict(valid, agents=[{'executable': '/System/Library/FixtureAgent', 'command': '/System/Library/FixtureAgent'}] * 257)]
        with self.os_agent_fixture():
            for value in invalid:
                with self.subTest(value=str(value)[:100]):
                    record.write_text(json.dumps(value)); record.chmod(0o600)
                    with self.assertRaises(ValueError): h['read_baseline'](self.root, 502, self.gui_config)
            record.write_bytes(b'x' * (h['MAX_BASELINE_BYTES'] + 1)); record.chmod(0o600)
            with self.subTest(case='bytes'), self.assertRaises(ValueError): h['read_baseline'](self.root, 502, self.gui_config)
            record.write_text(json.dumps(valid)); record.chmod(0o644)
            with self.subTest(case='mode'), self.assertRaises(ValueError): h['read_baseline'](self.root, 502, self.gui_config)
            record.chmod(0o600)
            with self.subTest(case='owner'), mock.patch.object(os, 'fstat', return_value=SimpleNamespace(st_uid=502, st_mode=stat.S_IFREG | 0o600)):
                with self.assertRaises(ValueError): h['read_baseline'](self.root, 502, self.gui_config)
            with self.subTest(case='ACL'), mock.patch.dict(h, protected=mock.Mock(side_effect=ValueError('unsafe ACL'))):
                with self.assertRaises(ValueError): h['read_baseline'](self.root, 502, self.gui_config)
            record.unlink(); record.symlink_to(self.root / 'outside-baseline')
            with self.subTest(case='symlink'), self.assertRaises(OSError): h['read_baseline'](self.root, 502, self.gui_config)

    def test_root_os_binary_requires_root_regular_unwritable_resolved_os_path(self):
        from types import SimpleNamespace
        for owner, mode, resolved, expected in [(0, stat.S_IFREG | 0o755, '/System/Library/FixtureAgent', True),
                (502, stat.S_IFREG | 0o755, '/System/Library/FixtureAgent', False),
                (0, stat.S_IFREG | 0o777, '/System/Library/FixtureAgent', False),
                (0, stat.S_IFLNK | 0o755, '/System/Library/FixtureAgent', False),
                (0, stat.S_IFREG | 0o755, '/tmp/source', False)]:
            with self.subTest(owner=owner, mode=mode, resolved=resolved), \
                    mock.patch.object(pathlib.Path, 'lstat', return_value=SimpleNamespace(st_uid=owner, st_mode=mode)), \
                    mock.patch.object(pathlib.Path, 'resolve', return_value=pathlib.Path(resolved)):
                self.assertEqual(expected, h['root_os_binary']('/System/Library/FixtureAgent'))
        self.assertFalse(h['root_os_binary']('/tmp/source'))

    def test_run_operation_refuses_missing_baseline_before_any_source_execution(self):
        calls = []
        request = self.root / ('command-' + 'a' * 32 + '.json')
        request.write_text(json.dumps({'argv': ['/usr/bin/true'], 'cwd': str(self.root), 'environment': {}}))
        def source_call(*a, **k):
            calls.append(a)
            return mock.Mock(returncode=0)
        with mock.patch.object(sys, 'argv', ['helper', 'run', str(self.root), str(request)]), \
                mock.patch.dict(h, protected=lambda *a: None, register_command=lambda *a: contextlib.nullcontext()), \
                mock.patch.object(subprocess, 'run', side_effect=source_call):
            error = None
            try: h['execute_operation'](dict(self.gui_config, runnerRoot='/collector'), 'run', self.root)
            except FileNotFoundError as caught: error = caught
        self.assertTrue(isinstance(error, FileNotFoundError) and not calls, 'root run requires the pre-source baseline before executing')

    def test_interrupted_baseline_publication_retries_only_the_original_complete_snapshot(self):
        command = '/System/Library/FixtureAgent --session Aqua'
        real_link = os.link
        for interrupted_side in ('durable', 'workspace'):
            with self.subTest(interrupted_side=interrupted_side):
                workspace = self.root / ('publication-' + interrupted_side)
                workspace.mkdir()
                marker = pathlib.Path(self.gui_config['stateDirectory']) / 'active.json'
                marker.write_text(json.dumps({'root': str(workspace.resolve()), 'devices': [], 'deviceIntents': []}))
                def link(source, target, **kwargs):
                    if (pathlib.Path(target).name == h['BASELINE_NAME']) == (interrupted_side == 'workspace'):
                        raise InterruptedError('publication interrupted after data fsync')
                    return real_link(source, target, **kwargs)
                with self.os_agent_fixture(), mock.patch.object(subprocess, 'run', side_effect=self.ps_answer(0,
                        ('41 1 /System/Library/FixtureAgent ' + command + '\n').encode())):
                    error = None
                    with mock.patch.object(os, 'link', side_effect=link):
                        try: h['baseline'](workspace, 502, self.gui_config)
                        except InterruptedError as caught: error = caught
                    directory = h['baseline_directory'](self.gui_config)
                    durable = directory / h['baseline_name'](workspace)
                    before = (durable.exists(), (workspace / h['BASELINE_NAME']).exists())
                    candidates = list((workspace if interrupted_side == 'workspace' else directory).glob('.*.pending'))
                    captured = candidates[0].read_bytes() if len(candidates) == 1 else None
                    # No new source/OS inventory may be captured on retry: it must publish/reuse the original snapshot bytes.
                    agents = None
                    with mock.patch.object(subprocess, 'run', side_effect=AssertionError('post-interruption process recapture')):
                        try:
                            h['baseline'](workspace, 502, self.gui_config)
                            agents = h['read_baseline'](workspace, 502, self.gui_config, required=True)
                        except Exception:
                            pass  # the assertion below must reject an unreadable/re-captured retry, not count a setup error as RED
                    self.assertEqual((True, (interrupted_side == 'workspace', False), {('/System/Library/FixtureAgent', command)}, captured),
                                     (isinstance(error, InterruptedError), before, agents, durable.read_bytes() if durable.exists() else None),
                                     'an interrupted publication leaves no partial final and retries the exact pre-source material')

    def test_baseline_publication_fsyncs_data_before_exclusive_publish_and_then_the_directory(self):
        events = []
        record = self.root / 'atomic-baseline.json'
        raw = b'{"fixture":"original"}'
        real_sync, real_link = os.fsync, os.link
        def sync(fd):
            events.append('directory' if stat.S_ISDIR(os.fstat(fd).st_mode) else 'data')
            return real_sync(fd)
        def link(source, target, **kwargs):
            events.append('publish')
            return real_link(source, target, **kwargs)
        with self.os_agent_fixture(), mock.patch.object(os, 'fsync', side_effect=sync), mock.patch.object(os, 'link', side_effect=link):
            h['write_baseline_record'](record, raw)
            first = list(events)
            error = None
            try: h['write_baseline_record'](record, b'{"fixture":"replacement"}')
            except FileExistsError as caught: error = caught
            self.assertEqual((['data', 'publish', 'directory'], True, raw),
                             (first, isinstance(error, FileExistsError), record.read_bytes()),
                             'a complete flushed snapshot is published exclusively and an existing final is never overwritten')

    def test_incomplete_pending_baseline_cannot_execute_source_or_publish_a_final_record(self):
        directory = None
        calls = []
        request = self.root / ('command-' + 'e' * 32 + '.json')
        request.write_text(json.dumps({'argv': ['/usr/bin/true'], 'cwd': str(self.root), 'environment': {}}))
        def source_call(*a, **k):
            calls.append(a)
            return mock.Mock(returncode=0)
        with self.os_agent_fixture():
            directory = h['baseline_directory'](self.gui_config, create=True)
            durable = directory / h['baseline_name'](self.root)
            pending = durable.with_name('.' + durable.name + '.' + 'f' * 32 + '.pending')
            pending.write_text('{"version":'); pending.chmod(0o600)
            local = self.root / h['BASELINE_NAME']
            local.write_text(json.dumps({'version': 1, 'uid': 502, 'root': str(self.root.resolve()), 'configurationHash': 'a' * 64, 'agents': []}))
            local.chmod(0o600)  # a valid workspace copy alone is not durable authority to start source execution
            error = None
            with mock.patch.object(sys, 'argv', ['helper', 'run', str(self.root), str(request)]), \
                    mock.patch.dict(h, register_command=lambda *a: contextlib.nullcontext()), \
                    mock.patch.object(subprocess, 'run', side_effect=source_call):
                try: h['execute_operation'](dict(self.gui_config, runnerRoot='/collector'), 'run', self.root)
                except Exception as caught: error = caught
            self.assertTrue(error is not None and not calls and not durable.exists() and pending.exists(),
                            'unknown partial material remains quarantined, never a source execution or invented cleanup proof')

    def test_crash_after_workspace_removal_reuses_only_the_same_durable_baseline(self):
        from types import SimpleNamespace
        command = '/System/Library/FixtureAgent --session Aqua'
        for unknown in (False, True):
            with self.subTest(unknown=unknown):
                workspace = self.root / ('removed-' + str(unknown))
                workspace.mkdir()
                marker = pathlib.Path(self.gui_config['stateDirectory']) / 'active.json'
                marker.write_text(json.dumps({'root': str(workspace.resolve()), 'devices': [], 'deviceIntents': []}))
                def native(args, **kwargs):
                    if args[0] == '/bin/rm':
                        if workspace.exists(): shutil.rmtree(workspace)
                        return SimpleNamespace(returncode=0, stdout=b'', stderr=b'')
                    return self.ps_answer(0, ('999 1 /System/Library/FixtureAgent ' + command + '\n').encode())(args, **kwargs)
                with self.os_agent_fixture(), mock.patch.object(subprocess, 'run', side_effect=native), \
                        mock.patch.dict(h, stop_commands=lambda *a: None):
                    h['baseline'](workspace, 502, self.gui_config)
                    durable = h['baseline_directory'](self.gui_config) / h['baseline_name'](workspace)
                    saved = durable.read_bytes() if durable.exists() else None
                    with mock.patch.object(sys, 'argv', ['helper', 'remove', str(workspace)]):
                        h['execute_operation'](self.gui_config, 'remove', workspace)
                    # Simulate process death here: rm completed, but active.json was NOT cleared by the collector.
                    def operation(action, root=None, extra=None, cleanup=False):
                        with mock.patch.object(sys, 'argv', ['helper', action, str(root)]):
                            return h['execute_operation'](self.gui_config, action, root)
                    inventory = ('1000 1 /System/Library/NewAgent /System/Library/NewAgent\n' if unknown
                                 else '1000 1 /System/Library/FixtureAgent ' + command + '\n').encode()
                    admitted, error = False, None
                    with mock.patch.dict(g, gui_operation=operation), \
                            mock.patch.object(subprocess, 'run', side_effect=lambda args, **kwargs:
                                native(args, **kwargs) if args[0] == '/bin/rm' else self.ps_answer(0, inventory)(args, **kwargs)):
                        try:
                            with g['GuiLease'](self.gui_config): admitted = True
                        except ValueError as caught: error = caught
                    self.assertEqual((not unknown, unknown, unknown, False, saved),
                                     (admitted, isinstance(error, ValueError), marker.exists(), workspace.exists(), durable.read_bytes() if durable.exists() else None),
                                     'a removal crash must retain the original proof, admit exact respawns, and quarantine new OS programs')

    def test_durable_baseline_is_bound_to_exact_workspace_uid_and_configuration(self):
        workspace = self.root / 'bound-workspace'
        workspace.mkdir()
        command = '/System/Library/FixtureAgent --session Aqua'
        with self.os_agent_fixture(), mock.patch.object(subprocess, 'run', side_effect=self.ps_answer(0,
                ('41 1 /System/Library/FixtureAgent ' + command + '\n').encode())):
            h['baseline'](workspace, 502, self.gui_config)
            directory = h['baseline_directory'](self.gui_config)
            durable = directory / h['baseline_name'](workspace)
            shutil.rmtree(workspace)
            other = self.root / 'different-workspace'
            (directory / h['baseline_name'](other)).write_bytes(durable.read_bytes())
            (directory / h['baseline_name'](other)).chmod(0o600)
            for root, uid, config in [(workspace, 503, self.gui_config), (workspace, 502, dict(self.gui_config, hash='b' * 64)),
                                      (other, 502, self.gui_config)]:
                with self.subTest(root=root, uid=uid, hash=config['hash']), self.assertRaises(ValueError):
                    h['read_baseline'](root, uid, config)

    def test_stale_baseline_reclamation_keeps_active_or_existing_workspaces(self):
        workspace = self.root / 'old-root'
        workspace.mkdir()
        live = self.root / 'still-existing-root'
        live.mkdir()
        new = self.root / 'new-root'
        new.mkdir()
        marker = pathlib.Path(self.gui_config['stateDirectory']) / 'active.json'
        command = '/System/Library/FixtureAgent --session Aqua'
        with self.os_agent_fixture(), mock.patch.object(subprocess, 'run', side_effect=self.ps_answer(0,
                ('41 1 /System/Library/FixtureAgent ' + command + '\n').encode())):
            marker.write_text(json.dumps({'root': str(workspace.resolve()), 'devices': [], 'deviceIntents': []}))
            h['baseline'](workspace, 502, self.gui_config)
            directory = h['baseline_directory'](self.gui_config)
            old_record = directory / h['baseline_name'](workspace)
            live_record = directory / h['baseline_name'](live)
            value = json.loads(old_record.read_text()); value['root'] = str(live.resolve())
            h['write_baseline_record'](live_record, json.dumps(value).encode())
            shutil.rmtree(workspace)
            h['sweep_baseline_records'](self.gui_config, directory)
            self.assertEqual((True, True), (old_record.exists(), live_record.exists()),
                             'the active removed root and any still-existing workspace retain their proof')
            marker.write_text(json.dumps({'root': str(new.resolve()), 'devices': [], 'deviceIntents': []}))
            h['baseline'](new, 502, self.gui_config)
            self.assertEqual((False, True, True, 0o700),
                             (old_record.exists(), live_record.exists(), (directory / h['baseline_name'](new)).exists(), stat.S_IMODE(directory.stat().st_mode)),
                             'only a gone root no longer owned by the active journal can be reclaimed')

    def test_durable_baseline_inventory_is_bounded_and_metadata_verified_before_reclamation(self):
        command = '/System/Library/FixtureAgent --session Aqua'
        with self.os_agent_fixture(), mock.patch.object(subprocess, 'run', side_effect=self.ps_answer(0,
                ('41 1 /System/Library/FixtureAgent ' + command + '\n').encode())):
            h['baseline'](self.root, 502, self.gui_config)
            directory = h['baseline_directory'](self.gui_config)
            record = directory / h['baseline_name'](self.root)
            other = self.root / 'other-root'
            other.mkdir()
            value = json.loads(record.read_text()); value['root'] = str(other.resolve())
            second = directory / h['baseline_name'](other)
            h['write_baseline_record'](second, json.dumps(value).encode())
            with self.subTest(case='bounded'), mock.patch.dict(h, MAX_AGENTS=1), self.assertRaises(ValueError):
                h['sweep_baseline_records'](self.gui_config, directory)
            second.chmod(0o644)
            with self.subTest(case='metadata'), self.assertRaises(ValueError): h['sweep_baseline_records'](self.gui_config, directory)
            second.chmod(0o600)
            with self.subTest(case='private-state'), mock.patch.dict(h, protected=mock.Mock(side_effect=ValueError('unsafe state'))), \
                    self.assertRaises(ValueError): h['read_baseline'](other, 502, self.gui_config)

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
        config = {'collectorRepository': 'acme/collector', 'runnerName': 'factory-gui-12345678', 'trustedRepositories': ['acme/other'],
                  'runtime': 'runtime', 'deviceType': 'phone', 'xcode': '26.5', 'build': '17F42', 'hash': 'a' * 64}
        lease = mock.Mock(return_value=contextlib.nullcontext(mock.Mock()))
        legacy = mock.Mock()
        environment = {'SOURCE_REPOSITORY': 'acme/source', 'GITHUB_REPOSITORY': 'acme/collector', 'RUNNER_NAME': 'factory-gui-12345678',
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

    def configuration(self, same_uid=False, source_sudo=False, source_credential=False, unknown_key=False, cleanup=False, runner_name='factory-gui-12345678'):
        from types import SimpleNamespace
        collector = self.root.resolve() / 'collector'
        source = self.root.resolve() / 'lwrunner'
        runner = collector / 'runner'
        state = collector / 'state'
        retired = source / 'old-runner'
        for path in (runner, state, retired): path.mkdir(parents=True, exist_ok=True)
        (runner / '.runner').write_text(json.dumps({'agentName': runner_name, 'gitHubUrl': 'https://github.com/acme/collector'}))
        if source_credential: (retired / '.credentials').write_text('must refuse')
        config = {'collectorUid': 501, 'sourceUid': 501 if same_uid else 502, 'sourceUser': 'lwrunner',
                  'runnerRoot': str(runner), 'stateDirectory': str(state), 'retiredRunnerRoots': [str(retired)],
                  'developer': '/usr', 'xcode': '26.5', 'build': '17F42', 'runtime': 'runtime', 'deviceType': 'phone',
                  'trustedRepositories': ['acme/source'], 'runnerName': runner_name, 'collectorRepository': 'acme/collector'}
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

    def test_registration_is_fresh_unique_name_not_old_replacement_or_unknown_name(self):
        self.assertEqual('factory-gui-unique-12345678', self.configuration(runner_name='factory-gui-unique-12345678')['runnerName'])
        for name in ('lensworks-guest', 'factory-gui-short', 'unrelated'):
            with self.subTest(name=name), self.assertRaises(ValueError): self.configuration(runner_name=name)

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
        config = {'collectorUid': 501, 'sourceUid': 502, 'hash': 'a' * 64, 'runnerRoot': '/collector/runner'}
        executed = []
        bindings = []
        def run(args, **kwargs):
            executed.append((args, kwargs))
            variables = dict(arg.split('=', 1) for arg in args[9:] if '=' in arg)
            bindings.append(json.loads(pathlib.Path(variables['FACTORY_GUI_TEST_BINDING']).read_text()))
            return mock.Mock(returncode=0)
        with mock.patch.dict(h, configuration=lambda **kwargs: config, workspace=lambda *a, **k: self.root, protected=lambda *a: None, require_lease=lambda *a: None,
                register_command=lambda *a: contextlib.nullcontext(), read_baseline=lambda *a, **k: set()), \
                mock.patch.object(sys, 'argv', ['helper', 'run', str(self.root), str(request)]), \
                mock.patch.object(subprocess, 'run', side_effect=run):
            self.assertEqual(0, h['main']())
        self.assertEqual('/bin/launchctl', executed[0][0][0])
        self.assertEqual(['asuser', '502', '/usr/bin/sudo', '-n', '-u', 'lwrunner', '/usr/bin/env', '-i'], executed[0][0][1:9])
        self.assertEqual({'PATH': '/usr/bin:/bin:/usr/sbin:/sbin'}, executed[0][1]['env'])
        self.assertEqual(self.root, executed[0][1]['cwd'])
        self.assertEqual('/collector/runner/.credentials', bindings[0].get('collectorCredentialProbe'))
        forwarded = dict(arg.split('=', 1) for arg in executed[0][0][9:] if '=' in arg)
        self.assertEqual(forwarded['FACTORY_GUI_TEST_BINDING'], forwarded.get('TEST_RUNNER_FACTORY_GUI_TEST_BINDING'))
        self.assertEqual('a' * 64, forwarded.get('TEST_RUNNER_FACTORY_GUI_CONFIGURATION_HASH'))

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


    def test_short_and_full_core_simulator_identifiers_normalize_once_and_wrong_inventory_is_refused(self):
        short = g['gui_environment']({'runtime': 'iOS-26-5', 'deviceType': 'iPhone-17', 'xcode': '26.5', 'build': '17F42'})
        full = g['gui_environment']({'runtime': 'com.apple.CoreSimulator.SimRuntime.iOS-26-5',
                                     'deviceType': 'com.apple.CoreSimulator.SimDeviceType.iPhone-17', 'xcode': '26.5', 'build': '17F42'})
        expected = ('com.apple.CoreSimulator.SimRuntime.iOS-26-5', 'com.apple.CoreSimulator.SimDeviceType.iPhone-17')
        self.assertEqual(expected, (short['runtime'], short['deviceType']))
        self.assertEqual(short, full)  # a full identifier gains no second prefix
        self.assertNotIn('com.apple.CoreSimulator.SimRuntime.com.apple', full['runtime'])
        with mock.patch.dict(g, ENVIRONMENT=short):
            self.assertEqual((expected[1], expected[0]), g['simulator']({'runtimes': [{'identifier': expected[0], 'isAvailable': True}],
                                                                         'devicetypes': [{'identifier': expected[1]}]}))
            for inventory in ({'runtimes': [{'identifier': 'iOS-26-5', 'isAvailable': True}], 'devicetypes': [{'identifier': expected[1]}]},
                              {'runtimes': [{'identifier': expected[0], 'isAvailable': True}], 'devicetypes': [{'identifier': 'iPhone-17'}]},
                              {'runtimes': [{'identifier': expected[0], 'isAvailable': False}], 'devicetypes': [{'identifier': expected[1]}]},
                              {'runtimes': [{'identifier': 'com.apple.CoreSimulator.SimRuntime.iOS-18-5', 'isAvailable': True}],
                               'devicetypes': [{'identifier': expected[1]}]}):
                with self.subTest(inventory=inventory), self.assertRaises(ValueError): g['simulator'](inventory)

    def test_gui_prepare_failure_publishes_typed_result_only_after_cleanup_proof(self):
        class Lease:
            def __init__(self): self.cleaned, self.roots = False, []
            def record(self, root, devices=None, intents=None): self.roots.append(root)
            def evidence(self):
                root = self.roots[-1] if self.roots else None
                if not self.cleaned or root is None or root.exists():
                    raise ValueError('GUI cleanup has not been proven')
                return {'cleaned': True, 'workspaceGone': True}
        class RefusedPrincipal:
            def __init__(self, root): raise AssertionError('no source principal before artifacts')
        plan = self.execution_plan()
        plan['prepare'] = {'artifacts': [], 'steps': [{'name': 'noop', 'argv': ['/usr/bin/true'], 'workingDirectory': '',
                                                       'environment': {}, 'timeoutSeconds': 5}]}
        for cleanup_failure in (False, True):
            with self.subTest(cleanup_failure=cleanup_failure):
                lease = Lease()
                with self.execution_directory({'base': 'base', 'head': 'head'}) as directory:
                    (self.root / 'result.json').unlink(missing_ok=True)
                    real_rmtree = g['shutil'].rmtree
                    def rmtree(path, *a, **k):
                        if pathlib.Path(path).name.startswith('factory-ios-') and cleanup_failure:
                            raise OSError('cleanup refused')
                        return real_rmtree(path, *a, **k)
                    error = None
                    with mock.patch.dict(g, GUI_CONFIG={'hash': 'a' * 64, 'trustedRepositories': ['acme/source']}, GUI_LEASE=lease, Principal=RefusedPrincipal), \
                            mock.patch.dict(os.environ, SOURCE_REPOSITORY='acme/source'), \
                            mock.patch.dict(g, prepare_artifacts=mock.Mock(side_effect=OSError('download refused'))), \
                            mock.patch.object(g['shutil'], 'rmtree', side_effect=rmtree):
                        try: g['execution_main'](self.execution_request('base'), json.dumps(plan))
                        except Exception as caught: error = caught
                        published = (directory / 'result.json').exists()
                        if not cleanup_failure:
                            result = json.loads((directory / 'result.json').read_text())
                    if cleanup_failure:
                        # The cleanup failure itself must surface (not be masked by the lease-evidence gate), and nothing publishes.
                        self.assertTrue(isinstance(error, OSError) and not published,
                                        f'cleanup failure must surface and suppress evidence: {error!r}, published={published}')
                    else:
                        self.assertEqual(('prepare-failed', 'artifact-fetch-failed', True),
                                         (result['execution']['status'], result['execution']['failure']['code'],
                                          (result.get('guiLease') or {}).get('cleaned')),
                                         'the typed prepare refusal is published only once the lease proves the workspace gone')

    def execution_plan(self):
        base, head = '1' * 40, '2' * 40
        return {'side': 'base', 'commit': base, 'overlayFrom': head, 'overlayPaths': ['Tests/New.swift'], 'deletePaths': [],
                'testEnvironment': {}, 'replacements': {}, 'sourceRepository': 'acme/source',
                'builds': [{'flag': '-project', 'container': 'Widget.xcodeproj', 'scheme': 'Widget', 'tests': [TEST], 'platform': 'macOS'}],
                'prepare': None}

    def execution_request(self, side):
        return {'BASE': '1' * 40, 'HEAD': '2' * 40, 'SIDE': side, 'NONCE': 'a' * 32, 'TESTS': json.dumps([TEST])}

    @contextlib.contextmanager
    def execution_directory(self, sources):
        # sources: side name -> a zip payload marker ('base'/'head') or raw bytes; a missing key means no archive at all.
        directory = self.root / ('execution-' + uuid.uuid4().hex)
        directory.mkdir()
        (directory / 'sources').mkdir()
        for name, content in sources.items():
            if isinstance(content, bytes):
                (directory / 'sources' / (name + '.zip')).write_bytes(content)
                continue
            with zipfile.ZipFile(directory / 'sources' / (name + '.zip'), 'w') as archive:
                archive.writestr('repository/Tests/New.swift', 'from head' if name == 'head' else 'old test')
                archive.writestr('repository/Production.swift', name + ' production')
        prior = pathlib.Path.cwd()
        os.chdir(directory)
        try:
            yield directory
        finally:
            os.chdir(prior)

    def test_head_only_execution_never_reads_an_unused_base_archive_and_required_bad_side_fails(self):
        class SimplePrincipal:
            def __init__(self, root): self.root, self.home, self.worktree = root, root / 'home', root / 'head'; self.home.mkdir()
            def share(self, path): pass
            def run(self, args, cwd, **kwargs):
                if args == ['/usr/bin/xcodebuild', '-version']: return 0, 'Xcode 26.5\nBuild version 17F42'
                if 'build-for-testing' in args: return 0, ''
                if 'test-without-building' in args: return 0, 'ran'
                return 0, json.dumps({'testNodes': [{'nodeType': 'Test Case', 'nodeIdentifier': TEST, 'result': 'Passed'}]})
            def close(self): shutil.rmtree(self.root)
        plan = self.execution_plan()
        plan.update(side='head', commit='2' * 40, overlayFrom='2' * 40, overlayPaths=[])
        environment = {'runtime': 'runtime', 'deviceType': 'phone', 'xcode': '26.5', 'build': '17F42'}
        for absent in (False, True):
            archives = {'head': 'head', **({} if absent else {'base': b'not an archive'})}
            with self.subTest(absent=absent), self.execution_directory(archives):
                with mock.patch.dict(g, GUI_CONFIG={'hash': 'a' * 64, 'trustedRepositories': ['acme/source']}, GUI_LEASE=mock.Mock(evidence=mock.Mock(return_value={})),
                        ENVIRONMENT=environment, Principal=SimplePrincipal, package_snapshot=lambda *a: {},
                        hydrate_packages=lambda *a: {}, hydrate_binary_packages=lambda *a: False), \
                        mock.patch.dict(os.environ, SOURCE_REPOSITORY='acme/source'):
                    outcome, failure = None, None
                    try:
                        g['execution_main'](self.execution_request('head'), json.dumps(plan))
                        outcome = json.loads(pathlib.Path('result.json').read_text())['execution']['status']
                    except Exception as caught:
                        failure = caught
                self.assertEqual('ran', outcome,
                                 f'a same-revision overlay needs no opposite archive, so an invalid unused base is never read: {failure!r}')
        required = dict(plan, overlayFrom='1' * 40, overlayPaths=['Tests/New.swift'])
        with self.execution_directory({'head': 'head', 'base': b'not an archive'}):
            error = None
            with mock.patch.dict(g, GUI_CONFIG={'hash': 'a' * 64, 'trustedRepositories': ['acme/source']}, GUI_LEASE=mock.Mock(evidence=mock.Mock(return_value={})),
                    ENVIRONMENT=environment, Principal=SimplePrincipal), \
                    mock.patch.dict(os.environ, SOURCE_REPOSITORY='acme/source'):
                try: g['execution_main'](self.execution_request('head'), json.dumps(required))
                except Exception as caught: error = caught
            self.assertTrue(isinstance(error, zipfile.BadZipFile) and not pathlib.Path('result.json').exists(),
                            f'an overlay that does need the opposite revision fails reading that archive: {error!r}')

if __name__ == '__main__':
    sys.argv = [sys.argv[0]]
    unittest.main()
