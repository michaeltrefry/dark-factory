# Runs the actual embedded runner with fake Xcode command answers, real archives
# and the recorded xcresult shape. No simulator, sandbox helper or network.
import contextlib
import io
import json
import os
import pathlib
import copy
import subprocess
import stat
import time
import runpy
import sys
import tempfile
import unittest
from unittest import mock
import zipfile

runner = runpy.run_path(sys.argv[1], run_name='factory_runner')
main = runner['main']
globals_ = main.__globals__
IDENTIFIER = 'TallyTests/TallyTests/testNew'
FIXTURES = pathlib.Path(__file__).resolve().parent.parent
PROJECT_FILE = FIXTURES / 'xcode-ios/Tally.xcodeproj/project.pbxproj'
def portable_project_fixture():
    # Replay only ownership facts used by this fixture. Linux CI has no plutil;
    # on macOS the very same tests also consume the real recorded pbxproj.
    objects = {'project': {'isa': 'PBXProject', 'mainGroup': 'main', 'targets': []},
               'main': {'isa': 'PBXGroup', 'sourceTree': '<group>', 'children': []}}
    for name, kind in [('Tally', 'application'), ('TallyTests', 'bundle.unit-test'),
                       ('TallyUITests', 'bundle.ui-testing')]:
        target, group, phase = name + '-target', name + '-group', name + '-sources'
        objects['project']['targets'].append(target)
        objects['main']['children'].append(group)
        objects[target] = {'isa': 'PBXNativeTarget', 'name': name,
                           'productType': 'com.apple.product-type.' + kind,
                           'fileSystemSynchronizedGroups': [group], 'buildPhases': [phase]}
        objects[group] = {'isa': 'PBXFileSystemSynchronizedRootGroup', 'path': name, 'sourceTree': '<group>'}
        objects[phase] = {'isa': 'PBXSourcesBuildPhase', 'files': []}
    return {'rootObject': 'project', 'objects': objects}


PROJECT = (json.loads(subprocess.check_output(['/usr/bin/plutil', '-convert', 'json', '-o', '-', str(PROJECT_FILE)]))
           if sys.platform == 'darwin' and not os.environ.get('IOS_FIXTURE_PORTABLE') else portable_project_fixture())
SOURCE = 'import XCTest\n@testable import Tally\nfinal class TallyTests: XCTestCase {\n    func testNew() {\n        XCTAssertEqual(Counter.start, 0)\n    }\n}\n'


def tree(result):
    return {'testNodes': [{'nodeType': 'Test Case', 'nodeIdentifierURL': 'test://com.apple.xcode/Tally/' + IDENTIFIER, 'result': result}]}


def owned_group_stop(pid, sig, records, stop):
    # This seam never invokes a native signal, even for a valid own-tree request.
    record = records.read_text().splitlines()[0]
    if pid != int(record.split('|', 1)[0]) or sig != globals_['signal'].SIGKILL:
        raise AssertionError('foreign signal refused by owned process seam')
    stop()


class RunnerTests(unittest.TestCase):
    def setUp(self):
        # Production now tears down every command group, including a completed
        # collector command. Unit fixtures never deliver a host signal.
        signals = mock.patch.object(globals_['os'], 'killpg')
        signals.start()
        self.addCleanup(signals.stop)

    def test_base_fails_head_passes_and_only_test_file_is_overlaid(self):
        self.exercise()

    def test_attributable_new_test_compile_error_is_not_built(self):
        self.exercise(build_failure=True)

    def test_failed_build_outside_test_never_counts_as_not_built(self):
        with self.assertRaises(ValueError):
            self.exercise(build_failure=True, foreign_error=True)

    def test_plugin_ambiguity_and_failed_script_are_not_compile_evidence(self):
        attribute = globals_['attributable_not_built']
        path = pathlib.Path('/fixture/TallyTests.swift')
        for error in ('ambiguous use of value', 'invalid redeclaration of value', 'too long (line_length)'):
            self.assertFalse(attribute(str(path) + ':5:1: error: ' + error, SOURCE, IDENTIFIER, path))
        self.assertFalse(attribute(str(path) + ':5:1: error: no such member\nCommand PhaseScriptExecution failed with a nonzero exit code', SOURCE, IDENTIFIER, path))

    def test_repo_process_receives_no_github_credential_or_command_file_path(self):
        os.environ['GITHUB_TOKEN'] = 'owner-secret-must-not-reach-repo'
        os.environ['GITHUB_ENV'] = '/must-not-reach-repo'
        code, output = globals_['command']([sys.executable, '-I', '-c', 'import os; print(os.environ.get("GITHUB_TOKEN")); print(os.environ.get("GITHUB_ENV"))'], tempfile.gettempdir())
        self.assertEqual(0, code)
        self.assertEqual('None\nNone\n', output)

    def test_xcode_private_temporary_path_alias_is_the_same_file(self):
        attribute = globals_['attributable_not_built']
        self.assertTrue(attribute('/var/tmp/fixture/TallyTests.swift:5:1: error: no member', SOURCE, IDENTIFIER, pathlib.Path('/private/var/tmp/fixture/TallyTests.swift')))
        self.assertFalse(attribute('/var/tmp/other/TallyTests.swift:5:1: error: no member', SOURCE, IDENTIFIER, pathlib.Path('/private/var/tmp/fixture/TallyTests.swift')))

    def test_existing_test_is_not_new(self):
        with self.assertRaises(ValueError):
            self.exercise(existing=True)

    def test_unreported_test_is_error(self):
        with self.assertRaises(ValueError):
            globals_['case_result']({'testNodes': []}, IDENTIFIER, 0)

    def test_duplicate_result_is_error(self):
        data = tree('Passed')
        data['testNodes'] *= 2
        with self.assertRaises(ValueError):
            globals_['case_result'](data, IDENTIFIER, 0)

    def test_failed_host_cannot_be_mistaken_for_passing_test(self):
        data = {'testNodes': [{'nodeType': 'Failure Message', 'result': 'Failed', 'name': 'Tally encountered an error'}]}
        with self.assertRaises(ValueError):
            globals_['case_result'](data, IDENTIFIER, 65)

    def test_exit_disagreement_is_error(self):
        with self.assertRaises(ValueError):
            globals_['case_result'](tree('Passed'), IDENTIFIER, 65)

    def test_unsafe_archive_never_extracts(self):
        with tempfile.TemporaryDirectory() as d:
            root = pathlib.Path(d)
            with zipfile.ZipFile(root / 'bad.zip', 'w') as z:
                z.writestr('repo/../../escape', 'bad')
            with self.assertRaises(ValueError):
                globals_['unpack'](root / 'bad.zip', root / 'output')
            self.assertFalse((root.parent / 'escape').exists())

    def test_requested_failure_plus_genuine_host_error_is_error(self):
        data = tree('Failed')
        host = json.loads((FIXTURES / 'xcresult/host-error.json').read_text())
        data['testNodes'].extend(host['testNodes'])
        with self.assertRaisesRegex(ValueError, 'unexpected executed test'):
            globals_['case_result'](data, IDENTIFIER, 65)

    def test_unexpected_executed_case_is_error(self):
        data = tree('Failed')
        data['testNodes'].append(dict(data['testNodes'][0], nodeIdentifierURL='test://other/testOther'))
        with self.assertRaisesRegex(ValueError, 'unexpected executed test'):
            globals_['case_result'](data, IDENTIFIER, 65)

    def test_real_assertion_message_is_usable_failure(self):
        data = tree('Failed')
        data['testNodes'][0]['children'] = [{'nodeType': 'Failure Message', 'name': 'TallyTests.swift:5: XCTAssertEqual failed'}]
        self.assertEqual('failed', globals_['case_result'](data, IDENTIFIER, 65))

    def test_mixed_app_source_membership_at_either_revision_prevents_execution(self):
        for side in ('base', 'head'):
            with self.subTest(side=side), self.assertRaisesRegex(ValueError, 'explicit or unknown fixture sources'):
                self.exercise(mixed=side)

    def test_unknown_synchronized_ownership_is_refused(self):
        for kind in ('exceptions', 'path', 'duplicate'):
            data = copy.deepcopy(PROJECT)
            objects = data['objects']
            target = next(o for o in objects.values() if o.get('name') == 'TallyTests' and o['isa'] == 'PBXNativeTarget')
            group = objects[target['fileSystemSynchronizedGroups'][0]]
            if kind == 'duplicate':
                target['fileSystemSynchronizedGroups'] *= 2
            else:
                group[kind] = ['unknown'] if kind == 'exceptions' else '../Tally'
            with self.subTest(kind=kind), self.assertRaises(ValueError):
                globals_['fixture_membership'](data)

    def test_explicit_platform_does_not_depend_on_random_device_uuids(self):
        environment = globals_['ENVIRONMENT']
        inventory = {'runtimes': [{'identifier': environment['runtime'], 'isAvailable': True},
                                  {'identifier': 'com.apple.CoreSimulator.SimRuntime.iOS-99-0', 'isAvailable': True}],
                     'devicetypes': [{'identifier': environment['deviceType']}],
                     'devices': [{'udid': 'a', 'runtime': 'iOS-99-0'}, {'udid': 'z', 'runtime': environment['runtime']}]}
        reverse = copy.deepcopy(inventory)
        reverse['devices'].reverse()
        self.assertEqual((environment['deviceType'], environment['runtime']), globals_['simulator'](inventory))
        self.assertEqual(globals_['simulator'](inventory), globals_['simulator'](reverse))
        inventory['runtimes'][0]['isAvailable'] = False
        with self.assertRaises(ValueError):
            globals_['simulator'](inventory)

    def test_tracked_executable_mode_survives_safe_extraction(self):
        with tempfile.TemporaryDirectory() as d:
            root = pathlib.Path(d)
            with zipfile.ZipFile(root / 'source.zip', 'w') as z:
                entry = zipfile.ZipInfo('repo/Scripts/generate.sh')
                entry.external_attr = (stat.S_IFREG | 0o755) << 16
                z.writestr(entry, '#!/bin/sh\nprintf generated')
            globals_['unpack'](root / 'source.zip', root / 'out')
            self.assertEqual(b'generated', subprocess.check_output([str(root / 'out/Scripts/generate.sh')]))

    def test_archive_special_file_is_refused(self):
        with tempfile.TemporaryDirectory() as d:
            root = pathlib.Path(d)
            with zipfile.ZipFile(root / 'source.zip', 'w') as z:
                entry = zipfile.ZipInfo('repo/fifo')
                entry.external_attr = stat.S_IFIFO << 16
                z.writestr(entry, '')
            with self.assertRaises(ValueError):
                globals_['unpack'](root / 'source.zip', root / 'out')

    @unittest.skipUnless(sys.platform == 'darwin', 'executable Seatbelt fixture requires macOS')
    def test_adversarial_source_cannot_write_publication_or_tools_or_read_credentials(self):
        # Same profile as production, current uid only. No sudo, user creation,
        # installed helper, real credential read, or signal is run on this host.
        with tempfile.TemporaryDirectory() as d:
            root = pathlib.Path(d)
            source = root / 'source'
            source.mkdir()
            evidence, tool, secret = [root / n for n in ('result.json', 'xcrun', 'owner-credential')]
            evidence.write_text('trusted evidence')
            tool.write_text('trusted tool')
            secret.write_text('fixture secret')
            profile = root / 'build.sb'
            policy = globals_['sandbox_profile'](root, [source])
            self.assertIn('(deny process-exec (literal "/usr/bin/sudo") (literal "/usr/bin/su"))', policy)
            profile.write_text(policy)
            attack = source / 'attack.sh'
            attack.write_text('printf started\\n; for target in "$@"; do printf tampered > "$target"; done\n/bin/cat "$3"\n')
            result = subprocess.run(['/usr/bin/sandbox-exec', '-f', str(profile), '/bin/sh', str(attack),
                                     str(evidence), str(tool), str(secret)], capture_output=True, cwd=source)
            self.assertEqual((False, 'trusted evidence', 'trusted tool', 'fixture secret'),
                             (result.returncode == 0, evidence.read_text(), tool.read_text(), secret.read_text()))
            self.assertIn(b'started', result.stdout)
            self.assertNotIn(b'fixture secret', result.stdout)
            # Probe forbidden executables with harmless --version: if exec were
            # allowed this would not switch identity, consult credentials or sudo.
            for executable in ('/usr/bin/sudo', '/usr/bin/su'):
                result = subprocess.run(['/usr/bin/sandbox-exec', '-f', str(profile), executable, '--version'], capture_output=True, cwd=source)
                self.assertIn(b'Operation not permitted', result.stderr)

    def test_successful_bootstrap_closes_collector_paths_without_host_administration(self):
        with tempfile.TemporaryDirectory() as d, tempfile.TemporaryDirectory(prefix='factory-ios-', dir='/tmp') as r:
            owner, root = pathlib.Path(d), pathlib.Path(r)
            owner.chmod(0o755)
            workspace = owner / 'workspace'
            workspace.mkdir(mode=0o755)
            previous_cwd = pathlib.Path.cwd()
            old_command, old_admin = globals_['command'], globals_['admin']
            calls = []
            def admin(args):
                calls.append(args)
                if '-list' in args:
                    return 'root 0\nrunner ' + str(os.getuid())
                return 'staff' if args[0] == '/usr/bin/id' else ''
            globals_['admin'] = admin
            globals_['command'] = lambda args, cwd: (1, '')
            try:
                os.chdir(workspace)
                with mock.patch.dict(os.environ, HOME=str(owner), SIDE='base'), mock.patch.object(globals_['Principal'], 'darwin_directories', return_value=[]):
                    globals_['Principal'](root)
                self.assertIn(['/bin/chmod', '-N', str(owner.resolve()), str(workspace.resolve()), str(root)], calls)
                self.assertEqual((0o700, 0o700, 0o755), tuple(stat.S_IMODE(path.stat().st_mode) for path in (owner, workspace, root)))
            finally:
                globals_['command'], globals_['admin'] = old_command, old_admin
                os.chdir(previous_cwd)

    def test_cleanup_quiesces_before_removing_only_its_remote_workspace(self):
        # Every path is freshly allocated by this test; admin remains a fake.
        with tempfile.TemporaryDirectory(prefix='factory-ios-', dir='/tmp') as d:
            root = pathlib.Path(d)
            principal = globals_['Principal'].__new__(globals_['Principal'])
            principal.root = root
            principal.collector_uid = os.getuid()
            principal.root_identity = principal.identity(root, principal.collector_uid)
            parent = root.parent.stat()
            principal.parent_identity = parent.st_dev, parent.st_ino, parent.st_uid
            events = []
            principal.sweep = lambda: events.append('quiescent')
            previous = globals_['admin']
            globals_['admin'] = lambda args, **kwargs: events.append(args)
            saved = root.with_name(root.name + '-saved')
            try:
                principal.close()
                self.assertEqual(['quiescent', ['/bin/rm', '-rf', '--', str(root)]], events)
                for unsafe in ('/', '/tmp', '/tmp/not-a-factory-workspace'):
                    principal.root = pathlib.Path(unsafe)
                    events.clear()
                    with self.subTest(unsafe=unsafe), self.assertRaisesRegex(ValueError, 'refusing cleanup'):
                        principal.close()
                    self.assertEqual([], events)
                principal.root = root
                for changed in ('swapped-directory', 'symlink', 'writable', 'wrong-owner', 'during-sweep'):
                    events.clear()
                    if changed in ('swapped-directory', 'symlink'):
                        root.rename(saved)
                        if changed == 'symlink':
                            root.symlink_to(saved, target_is_directory=True)
                        else:
                            root.mkdir()
                    elif changed == 'writable':
                        root.chmod(0o777)
                    elif changed == 'wrong-owner':
                        principal.collector_uid = -1
                    else:
                        def swap():
                            root.rename(saved)
                            root.mkdir()
                        principal.sweep = swap
                    try:
                        with self.subTest(changed=changed), self.assertRaisesRegex(ValueError, 'refusing cleanup'):
                            principal.close()
                        self.assertEqual([], events)
                    finally:
                        principal.collector_uid = os.getuid()
                        principal.sweep = lambda: events.append('quiescent')
                        if root.is_symlink():
                            root.unlink()
                        elif saved.exists():
                            root.rmdir()
                        if saved.exists():
                            saved.rename(root)
                        root.chmod(0o700)
            finally:
                globals_['admin'] = previous

    def test_failed_cleanup_publishes_no_evidence(self):
        with self.assertRaisesRegex(ValueError, 'cleanup refused'):
            self.exercise(cleanup_failure=True)

    def test_successful_source_command_still_quiesces_before_returning(self):
        # Exercise refusal before any process integration. A foreign pid cannot
        # even request the OwnProcess handshake, much less reach a native sink.
        with tempfile.TemporaryDirectory() as d:
            records = pathlib.Path(d) / 'owned'
            records.write_text('43210|fixture start\n')
            stopped = mock.Mock()
            with self.assertRaisesRegex(AssertionError, 'foreign signal'):
                owned_group_stop(43211, globals_['signal'].SIGKILL, records, stopped)
            self.assertFalse(stopped.called)
        stopped = []
        code, output = globals_['command'](['/bin/echo', 'finished'], '/', stop=lambda: stopped.append(True))
        self.assertEqual((0, 'finished\n', [True]), (code, output, stopped))

    def test_uid_sweep_includes_detached_descendants_and_refuses_survivors(self):
        principal = globals_['Principal'].__new__(globals_['Principal'])
        principal.uid = 550
        old_command = globals_['command']
        for survives in (False, True):
            calls = []
            def fake(args, cwd, **kwargs):
                if args[0] == '/bin/ps':
                    return 0, '501 R\n' + ('550 R\n550 S\n' if survives or not calls else '550 Z\n')
                calls.append(args)
                return 0, ''
            globals_['command'] = fake
            try:
                with mock.patch.object(globals_['time'], 'sleep'):
                    if survives:
                        with self.assertRaisesRegex(ValueError, 'descendants survived'):
                            principal.sweep()
                    else:
                        principal.sweep()
                self.assertEqual([['/usr/bin/sudo', '-n', '/usr/bin/pkill', '-KILL', '-u', '550']] * (50 if survives else 1), calls)
            finally:
                globals_['command'] = old_command

    def test_bootstrap_refuses_existing_identity_privileged_group_or_sudo_grant(self):
        # Exercise the real bootstrap with all administrative calls faked and
        # chmod intercepted: no host account, owner mode or permission changes.
        principal = globals_['Principal']
        for refusal in ('existing', 'group', 'sudo'):
            with tempfile.TemporaryDirectory() as d:
                root = pathlib.Path(d)
                before_command, before_admin = globals_['command'], globals_['admin']
                def command(args, cwd):
                    return (0 if refusal == 'existing' else 1, '') if args[0] == '/usr/bin/id' else (0, '')
                def admin(args):
                    if '-list' in args:
                        return 'runner 501\nroot 0'
                    if args[0] == '/usr/bin/id':
                        return 'staff admin' if refusal == 'group' else 'staff'
                    return ''
                globals_['command'], globals_['admin'] = command, admin
                try:
                    with mock.patch.object(pathlib.Path, 'chmod'), self.subTest(refusal=refusal), self.assertRaisesRegex(
                            ValueError, {'existing': 'already exists', 'group': 'unexpected groups', 'sudo': 'can use sudo'}[refusal]):
                        principal(root)
                finally:
                    globals_['command'], globals_['admin'] = before_command, before_admin

    def test_source_commands_always_use_dedicated_principal_and_quiesce(self):
        principal = globals_['Principal'].__new__(globals_['Principal'])
        principal.env = {'HOME': '/build/home', 'PATH': '/usr/bin:/bin'}
        principal.profile = pathlib.Path('/collector/build.sb')
        stopped = []
        principal.sweep = lambda: stopped.append(True)
        before = globals_['command']
        def fake(args, cwd, **kw):
            # Assert the exact real argv, not a test-only recreation of policy.
            self.assertEqual(['/usr/bin/sudo', '-n', '-u', '_factory_ios_build', '/usr/bin/env', '-i',
                              'HOME=/build/home', 'PATH=/usr/bin:/bin', '/usr/bin/sandbox-exec', '-f',
                              '/collector/build.sb', '/usr/bin/xcodebuild'], args)
            kw['stop']()
            return 0, ''
        globals_['command'] = fake
        try:
            principal.run(['/usr/bin/xcodebuild'], '/')
        finally:
            globals_['command'] = before
        self.assertEqual([True], stopped)

    def exercise(self, build_failure=False, foreign_error=False, existing=False, mixed=None, cleanup_failure=False):
        with tempfile.TemporaryDirectory() as d:
            before = pathlib.Path.cwd()
            workspaces = []
            os.chdir(d)
            try:
                pathlib.Path('sources').mkdir()
                for side in ('base', 'head'):
                    with zipfile.ZipFile('sources/' + side + '.zip', 'w') as z:
                        z.writestr('repo/e2e-ios/TallyTests/TallyTests.swift', SOURCE if side == 'head' or existing else 'import XCTest\n')
                        z.writestr('repo/e2e-ios/Tally/Counter.swift', side)
                        z.writestr('repo/e2e-ios/Tally.xcodeproj/project.pbxproj', side)
                os.environ.update(NONCE='a'*32, BASE='1'*40, HEAD='2'*40, TESTS=json.dumps([IDENTIFIER]), SIDE='base')
                os.environ['GITHUB_TOKEN'] = 'must-not-reach-build'
                calls = []
                def command(args, cwd):
                    calls.append(args)
                    cwd = pathlib.Path(cwd)
                    if args[0] == '/usr/bin/plutil':
                        data = copy.deepcopy(PROJECT)
                        if pathlib.Path(args[-1]).read_text() == mixed:
                            objects = data['objects']
                            app = next(o for o in objects.values() if o.get('name') == 'Tally' and o['isa'] == 'PBXNativeTarget')
                            phase = next(objects[p] for p in app['buildPhases'] if objects[p]['isa'] == 'PBXSourcesBuildPhase')
                            phase['files'] = ['TEST_FILE_NOW_PRODUCTION']
                        return 0, json.dumps(data)
                    if args[:3] == ['/usr/bin/xcrun', 'simctl', 'list']:
                        environment = globals_['ENVIRONMENT']
                        return 0, json.dumps({'runtimes': [{'identifier': environment['runtime'], 'isAvailable': True}],
                                              'devicetypes': [{'identifier': environment['deviceType']}]})
                    if args[:3] == ['/usr/bin/xcrun', 'simctl', 'create']:
                        return 0, '00000000-0000-4000-8000-000000000001'
                    if args[:2] == ['/usr/bin/xcodebuild', '-version']:
                        return 0, 'Xcode 16.4\nBuild version 16F6\n'
                    if args[0] == '/usr/bin/xcodebuild':
                        side = cwd.parent.name
                        self.assertEqual(side, (cwd / 'Tally/Counter.swift').read_text())
                        self.assertEqual(SOURCE, (cwd / 'TallyTests/TallyTests.swift').read_text())
                        if 'build-for-testing' in args:
                            if build_failure and side == 'base':
                                path = cwd / ('Tally/Counter.swift' if foreign_error else 'TallyTests/TallyTests.swift')
                                return 65, str(path) + ':5:1: error: no such member\n'
                            return 0, ''
                        self.assertIn('-only-testing:' + IDENTIFIER, args)
                        return (65 if side == 'base' else 0), ''
                    side = pathlib.Path(args[args.index('--path') + 1]).name.split('-')[0]
                    return 0, json.dumps(tree('Failed' if side == 'base' else 'Passed'))
                class FakePrincipal:
                    def __init__(self, root):
                        self.root = root
                        workspaces.append(root)
                    def share(self, path):
                        pass
                    def run(self, args, cwd):
                        return command(args, cwd)
                    def close(self):
                        calls.append(['principal-close'])
                        if cleanup_failure:
                            raise ValueError('cleanup refused')
                        tempfile.TemporaryDirectory._rmtree(self.root)
                originals = globals_['command'], globals_['Principal']
                globals_['command'], globals_['Principal'] = command, FakePrincipal
                try:
                    try:
                        main()
                    except ValueError:
                        if cleanup_failure:
                            self.assertFalse(pathlib.Path('result.json').exists())
                        raise
                    base_result = json.loads(pathlib.Path('result.json').read_text())
                    os.environ['SIDE'] = 'head'
                    main()
                finally:
                    globals_['command'], globals_['Principal'] = originals
                result = json.loads(pathlib.Path('result.json').read_text())
                self.assertEqual('not-built' if build_failure else 'failed', base_result['tests'][0]['base'])
                self.assertNotIn('head', base_result['tests'][0])
                self.assertNotIn('base', result['tests'][0])
                self.assertEqual('passed', result['tests'][0]['head'])
                self.assertEqual(2, sum('build-for-testing' in c for c in calls))
                self.assertEqual(globals_['ENVIRONMENT'], result['environment'])
                self.assertEqual(2, calls.count(['principal-close']))
            finally:
                os.chdir(before)
                # No real principal/process ran in exercise(): explicit cleanup
                # of these known fixture trees is safe even after fake refusal.
                for path in workspaces:
                    if path.exists():
                        tempfile.TemporaryDirectory._rmtree(path)


if len(sys.argv) > 2 and sys.argv[2] == '--process':
    directory, script, mode = pathlib.Path(sys.argv[3]), sys.argv[4], sys.argv[5]
    globals_['OUTPUT_LIMIT'] = 4096
    setattr(globals_['os'], 'killpg', lambda pid, sig: owned_group_stop(pid, sig, directory / 'owned', stop_owned_tree))
    def stop_owned_tree():
        (directory / 'stop').write_text('stop requested')
        for _ in range(1000):
            if (directory / 'stopped').exists():
                return
            time.sleep(0.01)
        raise ValueError('owned-tree stop was not acknowledged')
    try:
        globals_['command'](['/bin/sh', script], directory, stop=stop_owned_tree, timeout=0.25 if mode == 'timeout' else 10)
    except ValueError as error:
        print(str(error))
        sys.exit(0)
    raise AssertionError('unbounded command accepted')

suite = unittest.defaultTestLoader.loadTestsFromTestCase(RunnerTests)
if len(sys.argv) > 2:
    suite = unittest.TestSuite([RunnerTests(sys.argv[2])])
result = unittest.TextTestRunner(verbosity=2).run(suite)
sys.exit(not result.wasSuccessful())
