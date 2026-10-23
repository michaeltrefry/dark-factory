# Runs the actual embedded runner with fake Xcode command answers, real archives
# and the recorded xcresult shape. No simulator, sandbox helper or network.
import contextlib
import io
import json
import os
import pathlib
import runpy
import sys
import tempfile
import unittest
import zipfile

runner = runpy.run_path(sys.argv[1], run_name='factory_runner')
main = runner['main']
globals_ = main.__globals__
IDENTIFIER = 'TallyTests/TallyTests/testNew'
SOURCE = 'import XCTest\n@testable import Tally\nfinal class TallyTests: XCTestCase {\n    func testNew() {\n        XCTAssertEqual(Counter.start, 0)\n    }\n}\n'


def tree(result):
    return {'testNodes': [{'nodeType': 'Test Case', 'nodeIdentifierURL': 'test://com.apple.xcode/Tally/' + IDENTIFIER, 'result': result}]}


class RunnerTests(unittest.TestCase):
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

    def exercise(self, build_failure=False, foreign_error=False, existing=False):
        with tempfile.TemporaryDirectory() as d:
            before = pathlib.Path.cwd()
            os.chdir(d)
            try:
                pathlib.Path('sources').mkdir()
                for side in ('base', 'head'):
                    with zipfile.ZipFile('sources/' + side + '.zip', 'w') as z:
                        z.writestr('repo/e2e-ios/TallyTests/TallyTests.swift', SOURCE if side == 'head' or existing else 'import XCTest\n')
                        z.writestr('repo/e2e-ios/Tally/Counter.swift', side)
                os.environ.update(NONCE='a'*32, BASE='1'*40, HEAD='2'*40, TESTS=json.dumps([IDENTIFIER]), SIDE='base')
                os.environ['GITHUB_TOKEN'] = 'must-not-reach-build'
                calls = []
                def command(args, cwd):
                    calls.append(args)
                    cwd = pathlib.Path(cwd)
                    if args[0:3] == ['xcrun', 'simctl', 'list']:
                        return 0, json.dumps({'devices': {'com.apple.CoreSimulator.SimRuntime.iOS-18-2': [{'udid': 'device', 'isAvailable': True, 'name': 'iPhone 16'}]}})
                    if args[0] == 'xcodebuild':
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
                original = globals_['command']
                globals_['command'] = command
                try:
                    main()
                    base_result = json.loads(pathlib.Path('result.json').read_text())
                    os.environ['SIDE'] = 'head'
                    main()
                finally:
                    globals_['command'] = original
                result = json.loads(pathlib.Path('result.json').read_text())
                self.assertEqual('not-built' if build_failure else 'failed', base_result['tests'][0]['base'])
                self.assertNotIn('head', base_result['tests'][0])
                self.assertNotIn('base', result['tests'][0])
                self.assertEqual('passed', result['tests'][0]['head'])
                self.assertEqual(2, sum('build-for-testing' in c for c in calls))
            finally:
                os.chdir(before)


suite = unittest.defaultTestLoader.loadTestsFromTestCase(RunnerTests)
result = unittest.TextTestRunner(verbosity=2).run(suite)
sys.exit(not result.wasSuccessful())
