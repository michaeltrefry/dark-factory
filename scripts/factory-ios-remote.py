# Compiled into the sealed workflow. Never loaded from the target source tree.
# S1 deliberately supports the sandbox's e2e-ios/Tally fixture, not arbitrary
# project layouts. S2 owns discovery/routing. Unprovable evidence fails closed.
import json
import os
import pathlib
import re
import shutil
import subprocess
import tempfile
import zipfile


def unpack(archive, destination):
    destination.mkdir()
    size = 0
    with zipfile.ZipFile(archive) as source:
        entries = source.infolist()
        if len(entries) > 20000:
            raise ValueError('too many source entries')
        roots = {e.filename.split('/')[0] for e in entries}
        if len(roots) != 1:
            raise ValueError('ambiguous archive root')
        for entry in entries:
            parts = pathlib.PurePosixPath(entry.filename).parts[1:]
            if '..' in parts or entry.filename.startswith('/') or ((entry.external_attr >> 16) & 0o170000) == 0o120000:
                raise ValueError('unsafe source archive entry')
            if not parts:
                continue
            size += entry.file_size
            if size > 200 * 1024 * 1024:
                raise ValueError('source archive too large')
            target = destination.joinpath(*parts)
            if entry.is_dir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                with source.open(entry) as data, target.open('xb') as output:
                    shutil.copyfileobj(data, output)


def command(args, cwd):
    # No GitHub/Actions/owner credentials or command-file paths reach repo code.
    env = {k: os.environ[k] for k in ('HOME', 'PATH', 'TMPDIR', 'DEVELOPER_DIR') if k in os.environ}
    with tempfile.TemporaryFile() as output:
        proc = subprocess.run(args, cwd=cwd, env=env, stdout=output, stderr=subprocess.STDOUT, timeout=600)
        if output.tell() > 8 * 1024 * 1024:
            raise ValueError('oversized command output')
        output.seek(0)
        return proc.returncode, output.read().decode('utf-8', errors='strict')


def case_result(tree, identifier, exit_code):
    cases = []
    def visit(node):
        if not isinstance(node, dict):
            raise ValueError('invalid test node')
        if node.get('nodeType') == 'Test Case':
            url = node.get('nodeIdentifierURL', '')
            name = '/'.join(url.split('/')[-3:])
            if name == identifier:
                cases.append(node.get('result'))
        for child in node.get('children', []):
            visit(child)
    for node in tree['testNodes']:
        visit(node)
    if len(cases) != 1 or cases[0] not in ('Passed', 'Failed', 'Skipped'):
        raise ValueError('test not uniquely executed')
    result = {'Passed': 'passed', 'Failed': 'failed', 'Skipped': 'skipped'}[cases[0]]
    if (exit_code == 0) != (result in ('passed', 'skipped')) or exit_code not in (0, 65):
        raise ValueError('test result disagrees with process status')
    return result


def attributable_not_built(log, source, test, path):
    # Only a compiler diagnostic in this exact new test's function can prove it
    # not-built. Helpers, imports, module/type errors and failed hosts cannot.
    # A deliberately small grammar for S1's fixture; ambiguous Swift is error.
    lines = source.splitlines()
    method = test.split('/')[-1]
    start = next((i for i, line in enumerate(lines) if re.fullmatch(r'\s*func ' + re.escape(method) + r'\(\)\s*\{\s*', line)), None)
    if start is None:
        return False
    end = next((i for i in range(start + 1, len(lines)) if lines[i].strip() == '}'), None)
    if end is None or any('{' in line or '}' in line for line in lines[start + 1:end]):
        return False
    if 'Command PhaseScriptExecution failed' in log:
        return False
    diagnostics = [line for line in log.splitlines() if 'error:' in line]
    if not diagnostics:
        return False
    for line in diagnostics:
        match = re.fullmatch(re.escape(str(path)) + r':([0-9]+):[0-9]+: error: .+', line)
        if not match or not start + 1 <= int(match[1]) <= end + 1:
            return False
        if re.search(r'ambiguous|invalid redeclaration|\([A-Za-z_][A-Za-z0-9_]*\)$', line):
            return False
    return True


def main():
    request = {k: os.environ[k] for k in ('NONCE', 'BASE', 'HEAD', 'TESTS', 'SIDE')}
    tests = json.loads(request['TESTS'])
    if request['SIDE'] not in ('base', 'head'):
        raise ValueError('invalid execution side')
    if not re.fullmatch('[0-9a-f]{32}', request['NONCE']) or not all(re.fullmatch('[0-9a-f]{40}', request[k]) for k in ('BASE', 'HEAD')):
        raise ValueError('invalid request identity')
    if not isinstance(tests, list) or not 1 <= len(tests) <= 20 or len(set(tests)) != len(tests) or not all(re.fullmatch(r'TallyTests/TallyTests/test[A-Za-z0-9_]{1,100}', t) for t in tests):
        raise ValueError('invalid test identifiers')
    with tempfile.TemporaryDirectory(prefix='factory-ios-') as directory:
        root = pathlib.Path(directory)
        for side in ('base', 'head'):
            unpack(pathlib.Path('sources') / (side + '.zip'), root / side)
        relative = pathlib.Path('e2e-ios/TallyTests/TallyTests.swift')
        source = (root / 'head' / relative).read_text()
        original = (root / 'base' / relative).read_text()
        # Whole test-only file overlaid, never the project or production code.
        for test in tests:
            method = re.escape(test.split('/')[-1])
            if len(re.findall(r'\bfunc\s+' + method + r'\s*\(', source)) != 1 or re.search(r'\bfunc\s+' + method + r'\s*\(', original):
                raise ValueError('test is not new in the fixture')
        (root / 'base' / relative).write_text(source)
        code, devices = command(['xcrun', 'simctl', 'list', 'devices', 'available', '-j'], root)
        if code:
            raise ValueError('simulator unavailable')
        candidates = [d['udid'] for runtime, values in json.loads(devices)['devices'].items() if '.iOS-' in runtime for d in values if d.get('isAvailable') and d['name'].startswith('iPhone')]
        if not candidates:
            raise ValueError('no available iOS simulator')
        device = sorted(candidates)[0]
        rows = [{'test': test} for test in tests]
        # Each side runs on its own fresh hosted VM. Base repo code cannot
        # alter the head's sources, helper, derived data or results (or vice versa).
        for side in (request['SIDE'],):
            cwd = root / side / 'e2e-ios'
            common = ['xcodebuild', '-project', 'Tally.xcodeproj', '-scheme', 'Tally', '-destination', 'platform=iOS Simulator,id=' + device,
                      '-derivedDataPath', str(root / ('derived-' + side)), 'CODE_SIGNING_ALLOWED=NO']
            code, log = command(common + ['build-for-testing'], cwd)
            if code:
                if side != 'base' or code != 65:
                    raise ValueError('head build or infrastructure failed')
                for row in rows:
                    if not attributable_not_built(log, source, row['test'], root / side / relative):
                        raise ValueError('base compile error not attributable to each new test')
                    row[side] = 'not-built'
                continue
            for index, row in enumerate(rows):
                bundle = root / (side + '-' + str(index) + '.xcresult')
                code, _ = command(common + ['test-without-building', '-only-testing:' + row['test'], '-parallel-testing-enabled', 'NO', '-resultBundlePath', str(bundle)], cwd)
                report_code, report = command(['xcrun', 'xcresulttool', 'get', 'test-results', 'tests', '--path', str(bundle)], root)
                if report_code:
                    raise ValueError('unreadable xcresult')
                row[side] = case_result(json.loads(report), row['test'], code)
        pathlib.Path('result.json').write_text(json.dumps({'version': 1, 'nonce': request['NONCE'], 'base': request['BASE'], 'head': request['HEAD'], 'side': request['SIDE'], 'tests': rows}))


if __name__ == '__main__':
    main()
