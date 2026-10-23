# Compiled into the sealed workflow. Never loaded from the target source tree.
# S1 deliberately supports the sandbox's e2e-ios/Tally fixture, not arbitrary
# project layouts. S2 owns discovery/routing. Unprovable evidence fails closed.
import json
import os
import pathlib
import re
import selectors
import signal
import stat
import time
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
            kind = stat.S_IFMT(entry.external_attr >> 16)
            if '..' in parts or entry.filename.startswith('/') or kind not in (0, stat.S_IFREG, stat.S_IFDIR):
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
                target.chmod(0o755 if (entry.external_attr >> 16) & 0o111 else 0o644)


# Trusted commands never search a source-controlled PATH. Repository commands use
# Principal.run, not this collector account. Every pipe is bounded while draining.
OUTPUT_LIMIT = 8 * 1024 * 1024
COMMAND_TIMEOUT = 600
ENVIRONMENT = {'runtime': 'com.apple.CoreSimulator.SimRuntime.iOS-18-5',
               'deviceType': 'com.apple.CoreSimulator.SimDeviceType.iPhone-16',
               'xcode': '16.4', 'build': '16F6'}


def command(args, cwd, env=None, stop=None, timeout=COMMAND_TIMEOUT):
    env = env or {'PATH': '/usr/bin:/bin:/usr/sbin:/sbin', 'HOME': os.environ['HOME'],
                      'DEVELOPER_DIR': '/Applications/Xcode_16.4.app/Contents/Developer'}
    proc = subprocess.Popen(args, cwd=cwd, env=env, stdin=subprocess.DEVNULL,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, start_new_session=True)
    output = bytearray()
    deadline = time.monotonic() + timeout
    try:
        with selectors.DefaultSelector() as ready:
            ready.register(proc.stdout, selectors.EVENT_READ)
            while ready.get_map():
                if time.monotonic() >= deadline:
                    raise ValueError('command timed out')
                for key, _ in ready.select(min(0.1, max(0, deadline - time.monotonic()))):
                    chunk = os.read(key.fileobj.fileno(), 65536)
                    if not chunk:
                        ready.unregister(key.fileobj)
                    elif len(output) + len(chunk) > OUTPUT_LIMIT:
                        raise ValueError('oversized command output')
                    else:
                        output.extend(chunk)
            return proc.wait(timeout=max(0.01, deadline - time.monotonic())), output.decode('utf-8', errors='strict')
    finally:
        # For source commands, ALWAYS sweep the dedicated uid, even after exit 0:
        # a detached child can close stdout and outlive its parent. Collection is
        # forbidden unless the sweep proves the whole principal quiescent.
        if stop is not None:
            stop()
        elif proc.poll() is None:
            os.killpg(proc.pid, signal.SIGKILL)
        proc.wait(timeout=10)
        proc.stdout.close()


def admin(args):
    code, text = command(['/usr/bin/sudo', '-n', *args], '/')
    if code:
        raise ValueError('remote principal setup/cleanup refused')
    return text


def sandbox_profile(root, writable):
    quoted = lambda p: json.dumps(str(p.resolve()))
    return ('(version 1) (deny default)\n'
        '(allow process-fork process-exec signal)\n'
        '(deny process-exec (literal "/usr/bin/sudo") (literal "/usr/bin/su"))\n'
        '(allow sysctl-read mach-lookup ipc-posix* system-socket file-read-metadata)\n'
        '(allow file-read* (literal "/") (literal ' + quoted(root) + ') (subpath "/System") (subpath "/Library") (subpath "/usr") '
        '(subpath "/bin") (subpath "/sbin") (subpath "/Applications") '
        '(subpath "/private/etc") (subpath "/private/var/db") '
        '(literal "/dev/null") (literal "/dev/random") (literal "/dev/urandom") '
        + ' '.join('(subpath ' + quoted(p) + ')' for p in writable) + ')\n'
        '(allow file-write* (literal "/dev/null") '
        + ' '.join('(subpath ' + quoted(p) + ')' for p in writable) + ')\n')


class Principal:
    # Exists only on a disposable hosted VM. This code is never installed or run
    # against the factory host's sandbox accounts. Refuse an existing identity.
    user = '_factory_ios_build'

    def __init__(self, root):
        self.root = root
        self.collector_uid = os.getuid()
        self.root_identity = self.identity(root, self.collector_uid)
        parent = root.parent.stat()
        self.parent_identity = (parent.st_dev, parent.st_ino, parent.st_uid)
        self.home = root / 'home'
        code, _ = command(['/usr/bin/id', self.user], '/')
        if code == 0:
            raise ValueError('build principal already exists')
        used = admin(['/usr/bin/dscl', '.', '-list', '/Users', 'UniqueID'])
        uids = {int(line.split()[-1]) for line in used.splitlines()}
        self.uid = next(uid for uid in range(550, 600) if uid not in uids)
        # No password, admin/sudo group, login shell, owner home, owner env or
        # launch agent. The collector retains sudo; source code never does.
        for key, value in [('UniqueID', str(self.uid)), ('PrimaryGroupID', '20'),
                           ('NFSHomeDirectory', str(self.home)), ('UserShell', '/usr/bin/false'),
                           ('Password', '*')]:
            admin(['/usr/bin/dscl', '.', '-create', '/Users/' + self.user, key, value])
        groups = admin(['/usr/bin/id', '-Gn', self.user]).split()
        if set(groups) - {'staff', 'everyone', 'localaccounts'}:
            raise ValueError('build principal has unexpected groups')
        code, _ = command(['/usr/bin/sudo', '-n', '-l', '-U', self.user], '/')
        if code == 0:
            raise ValueError('build principal can use sudo')
        self.home.mkdir()
        (self.home / 'tmp').mkdir()
        # Close all owner credentials and Actions command/publication files at
        # their ancestor, not by trying to enumerate current secret filenames.
        owner = pathlib.Path(os.environ['HOME']).resolve()
        workspace = pathlib.Path.cwd().resolve()
        if owner == pathlib.Path('/') or not workspace.is_relative_to(owner):
            raise ValueError('hosted workspace is outside owner home')
        # Remove inherited ACL grants as well as group/other mode rights.
        # All three are collector-owned, never directories a source user made.
        admin(['/bin/chmod', '-N', str(owner), str(workspace), str(root)])
        owner.chmod(0o700)
        workspace.chmod(0o700)
        root.chmod(0o755)
        admin(['/usr/sbin/chown', '-R', self.user + ':staff', str(self.home)])
        self.env = {'PATH': '/usr/bin:/bin:/usr/sbin:/sbin', 'HOME': str(self.home),
                    'TMPDIR': str(self.home / 'tmp'), 'DEVELOPER_DIR': '/Applications/Xcode_16.4.app/Contents/Developer'}
        # POSIX uid separation is backed by a deny-by-default Seatbelt profile:
        # no user switching, network, owner reads/writes, or writes outside this
        # principal's source/build/home. System tools remain root/owner-owned.
        writable = [root / os.environ['SIDE'], self.home, root / ('derived-' + os.environ['SIDE'])]
        self.profile = root / 'build.sb'
        self.profile.write_text(sandbox_profile(root, writable))

    def sweep(self):
        # This uid was allocated fresh, is not the owner/root, and only this job
        # ever launches it. Repeat because a child may fork during the first pass.
        for _ in range(50):
            code, text = command(['/bin/ps', '-axo', 'uid=,stat='], '/')
            if code:
                raise ValueError('cannot verify build principal quiescence')
            live = any(int(fields[0]) == self.uid and not fields[1].startswith(('Z', 'X'))
                       for line in text.splitlines() if len(fields := line.split()) == 2)
            if not live:
                return
            code, _ = command(['/usr/bin/sudo', '-n', '/usr/bin/pkill', '-KILL', '-u', str(self.uid)], '/')
            if code not in (0, 1):
                raise ValueError('cannot terminate build principal')
            time.sleep(0.1)
        raise ValueError('build descendants survived; evidence refused')

    def run(self, args, cwd):
        return command(['/usr/bin/sudo', '-n', '-u', self.user, '/usr/bin/env', '-i',
                        *[k + '=' + v for k, v in self.env.items()],
                        '/usr/bin/sandbox-exec', '-f', str(self.profile), *args],
                       cwd, env={'PATH': '/usr/bin:/bin:/usr/sbin:/sbin'}, stop=self.sweep)

    @staticmethod
    def identity(path, owner):
        node = path.lstat()  # never follow a swapped workspace symlink
        if not stat.S_ISDIR(node.st_mode) or node.st_uid != owner or node.st_mode & 0o022:
            raise ValueError('refusing cleanup: workspace is not collector-owned and closed to writers')
        return node.st_dev, node.st_ino, node.st_uid

    def verify_workspace(self):
        if (self.root.parent.resolve() != pathlib.Path('/tmp').resolve()
                or not self.root.name.startswith('factory-ios-')):
            raise ValueError('refusing cleanup outside the remote workspace')
        parent = self.root.parent.stat()
        if ((parent.st_dev, parent.st_ino, parent.st_uid) != self.parent_identity
                or parent.st_uid != 0 or not parent.st_mode & stat.S_ISVTX
                or self.identity(self.root, self.collector_uid) != self.root_identity):
            raise ValueError('refusing cleanup: workspace identity or sticky parent changed')

    def close(self):
        # No collector-side traversal of worker-owned content. A remote root rm
        # removes exactly the fresh workspace, physically (no symlink following),
        # only after quiescence and a second identity/ownership check. The build
        # uid cannot replace its root entry in root-owned sticky /tmp.
        self.verify_workspace()
        self.sweep()
        self.verify_workspace()
        admin(['/bin/rm', '-rf', '--', str(self.root)])

    def share(self, path):
        admin(['/usr/sbin/chown', '-R', self.user + ':staff', str(path)])


def fixture_membership(project):
    # S1 accepts only the fixture's synchronized layout. Any explicit sources,
    # exceptions, unknown targets/groups or alternate path is unprovable, not a
    # best-effort guess. Parse both revisions BEFORE the first source execution.
    objects = project['objects']
    root = objects[project['rootObject']]
    if root['isa'] != 'PBXProject' or root.get('projectDirPath', '') or root.get('projectRoot', ''):
        raise ValueError('unknown fixture project root')
    main = objects[root['mainGroup']]
    if main['isa'] != 'PBXGroup' or main.get('path') or main.get('sourceTree') != '<group>':
        raise ValueError('unknown fixture main group')
    targets = [objects[t] for t in root['targets']]
    expected = {'Tally': ('Tally', 'com.apple.product-type.application'),
                'TallyTests': ('TallyTests', 'com.apple.product-type.bundle.unit-test'),
                'TallyUITests': ('TallyUITests', 'com.apple.product-type.bundle.ui-testing')}
    if (len(targets) != 3 or {t.get('name') for t in targets} != set(expected)
            or sum(o.get('isa', '').endswith('Target') for o in objects.values()) != 3
            or root.get('projectReferences')):
        raise ValueError('unknown fixture target ownership')
    seen = set()
    for target in targets:
        path, kind = expected[target['name']]
        if target['isa'] != 'PBXNativeTarget' or target['productType'] != kind or target.get('buildRules'):
            raise ValueError('unknown fixture target')
        groups = target.get('fileSystemSynchronizedGroups', [])
        if len(groups) != 1 or groups[0] in seen or groups[0] not in main['children']:
            raise ValueError('mixed fixture target membership')
        seen.update(groups)
        group = objects[groups[0]]
        if group != {'isa': 'PBXFileSystemSynchronizedRootGroup', 'path': path, 'sourceTree': '<group>'}:
            raise ValueError('unknown synchronized fixture ownership')
        phases = [objects[p] for p in target['buildPhases']]
        if sum(p['isa'] == 'PBXSourcesBuildPhase' for p in phases) != 1 or any(
                p.get('files') for p in phases if p['isa'] == 'PBXSourcesBuildPhase'):
            raise ValueError('explicit or unknown fixture sources')
    if any(o.get('isa') in ('PBXBuildFile', 'PBXFileSystemSynchronizedBuildFileExceptionSet',
                            'PBXFileSystemSynchronizedGroupBuildPhaseMembershipExceptionSet') for o in objects.values()):
        raise ValueError('unknown or mixed fixture ownership')


def simulator(inventory):
    runtimes = [r for r in inventory['runtimes'] if r.get('identifier') == ENVIRONMENT['runtime'] and r.get('isAvailable')]
    types = [d for d in inventory['devicetypes'] if d.get('identifier') == ENVIRONMENT['deviceType']]
    if len(runtimes) != 1 or len(types) != 1:
        raise ValueError('pinned simulator specification is unavailable')
    return ENVIRONMENT['deviceType'], ENVIRONMENT['runtime']


def case_result(tree, identifier, exit_code):
    cases = []
    def visit(node, in_case=False):
        if not isinstance(node, dict):
            raise ValueError('invalid test node')
        if node.get('nodeType') == 'Test Case':
            url = node.get('nodeIdentifierURL', '')
            name = '/'.join(url.split('/')[-3:])
            if name != identifier:
                raise ValueError('unexpected executed test')
            cases.append(node.get('result'))
        elif node.get('nodeType') == 'Failure Message' and in_case and not node.get('children') and isinstance(node.get('name'), str):
            return
        elif node.get('nodeType') not in ('Test Plan', 'Unit test bundle', 'UI test bundle', 'Test Suite'):
            raise ValueError('host error or unknown test report node')
        elif node.get('result') not in (None, 'Passed', 'Failed', 'Skipped'):
            raise ValueError('unusable test report suite')
        for child in node.get('children', []):
            visit(child, node.get('nodeType') == 'Test Case')
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
    def diagnostic_path(value):
        # Xcode canonicalizes macOS's /private/var and /private/tmp aliases in
        # diagnostics differently from Python's temporary-directory spelling.
        return value[8:] if value.startswith(('/private/var/', '/private/tmp/')) else value
    for line in diagnostics:
        match = re.fullmatch(r'(.+):([0-9]+):[0-9]+: error: .+', line)
        if not match or diagnostic_path(match[1]) != diagnostic_path(str(path)) or not start + 1 <= int(match[2]) <= end + 1:
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
    with tempfile.TemporaryDirectory(prefix='factory-ios-', dir='/tmp') as directory:
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
        for side in ('base', 'head'):
            project = root / side / 'e2e-ios/Tally.xcodeproj/project.pbxproj'
            code, data = command(['/usr/bin/plutil', '-convert', 'json', '-o', '-', str(project)], root)
            if code:
                raise ValueError('unreadable fixture project')
            fixture_membership(json.loads(data))
        (root / 'base' / relative).write_text(source)
        principal = Principal(root)
        try:
            principal.share(root / request['SIDE'])
            derived = root / ('derived-' + request['SIDE'])
            derived.mkdir()
            principal.share(derived)
            code, version = principal.run(['/usr/bin/xcodebuild', '-version'], root)
            if code or version.strip() != 'Xcode ' + ENVIRONMENT['xcode'] + '\nBuild version ' + ENVIRONMENT['build']:
                raise ValueError('pinned Xcode version unavailable')
            code, devices = principal.run(['/usr/bin/xcrun', 'simctl', 'list', '-j'], root)
            if code:
                raise ValueError('simulator unavailable')
            device_type, runtime = simulator(json.loads(devices))
            code, device = principal.run(['/usr/bin/xcrun', 'simctl', 'create', 'factory-' + request['NONCE'], device_type, runtime], root)
            device = device.strip()
            if code or not re.fullmatch('[0-9A-Fa-f-]{36}', device):
                raise ValueError('simulator creation failed')
            rows = [{'test': test} for test in tests]
            # Each side runs on its own fresh hosted VM. Base repo code cannot
            # alter the head's sources, helper, derived data or results (or vice versa).
            for side in (request['SIDE'],):
                cwd = root / side / 'e2e-ios'
                common = ['/usr/bin/xcodebuild', '-project', 'Tally.xcodeproj', '-scheme', 'Tally', '-destination', 'platform=iOS Simulator,id=' + device,
                          '-derivedDataPath', str(root / ('derived-' + side)), 'CODE_SIGNING_ALLOWED=NO']
                code, log = principal.run(common + ['build-for-testing'], cwd)
                if code:
                    if side != 'base' or code != 65:
                        raise ValueError('head build or infrastructure failed')
                    for row in rows:
                        if not attributable_not_built(log, source, row['test'], root / side / relative):
                            raise ValueError('base compile error not attributable to each new test')
                        row[side] = 'not-built'
                    continue
                for index, row in enumerate(rows):
                    bundle = derived / (side + '-' + str(index) + '.xcresult')
                    code, _ = principal.run(common + ['test-without-building', '-only-testing:' + row['test'], '-parallel-testing-enabled', 'NO', '-resultBundlePath', str(bundle)], cwd)
                    report_code, report = command(['/usr/bin/xcrun', 'xcresulttool', 'get', 'test-results', 'tests', '--path', str(bundle)], root)
                    if report_code:
                        raise ValueError('unreadable xcresult')
                    row[side] = case_result(json.loads(report), row['test'], code)
            payload = {'version': 1, 'nonce': request['NONCE'], 'base': request['BASE'], 'head': request['HEAD'], 'side': request['SIDE'], 'environment': ENVIRONMENT, 'tests': rows}
        finally:
            principal.close()
        pathlib.Path('result.json').write_text(json.dumps(payload))


if __name__ == '__main__':
    main()
