# Compiled into the sealed workflow. Never loaded from the target source tree.
# Typed executions run already-discovered plans; the legacy S1 Tally fixture
# remains available for two-sided walking-skeleton requests. Evidence fails closed.
import hashlib
import math
import http.client
import http.server
import threading
import ipaddress
import json
import socket
import ssl
import tarfile
import urllib.parse
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


EXECUTION_DEADLINE = None


def remaining(timeout=None):
    available = COMMAND_TIMEOUT if EXECUTION_DEADLINE is None else EXECUTION_DEADLINE - time.monotonic()
    if timeout is not None:
        available = min(available, timeout)
    if available <= 0:
        raise ValueError('execution budget exhausted')
    return available


def execution_budget(text, expires):
    if not isinstance(text, str) or not re.fullmatch(r'[0-9]+(?:\.[0-9]+)?', text):
        raise ValueError('invalid execution budget')
    value = float(text)
    if not math.isfinite(value) or not 0 < value <= 21600:
        raise ValueError('invalid execution budget')
    if not isinstance(expires, str) or not re.fullmatch(r'[0-9]+(?:\.[0-9]+)?', expires):
        raise ValueError('invalid execution deadline')
    until = float(expires) - time.time()
    if not math.isfinite(until) or until <= 0:
        raise ValueError('execution deadline exhausted before runner start')
    return time.monotonic() + min(value, until)


def command(args, cwd, env=None, stop=None, timeout=None, cleanup=False):
    timeout = timeout if cleanup else remaining(timeout)
    if timeout is None:
        timeout = 10
    env = env or {'PATH': '/usr/bin:/bin:/usr/sbin:/sbin', 'HOME': os.environ['HOME'],
                      'DEVELOPER_DIR': '/Applications/Xcode_16.4.app/Contents/Developer'}
    proc = subprocess.Popen(args, cwd=cwd, env=env, stdin=subprocess.DEVNULL,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, start_new_session=True)
    output = bytearray()
    reaped = False
    def terminate_group():
        # Never use poll/wait before this signal: the unreaped direct child
        # reserves the pid even when its exited leader has closed the pipe.
        try:
            os.killpg(proc.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
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
            # EOF can precede a successful exit. Give the leader its remaining
            # budget rather than turning that legitimate close into SIGKILL.
            code = proc.wait(timeout=max(0.01, deadline - time.monotonic()))
            reaped = True
            return code, output.decode('utf-8', errors='strict')
    finally:
        # For source commands, ALWAYS sweep the dedicated uid, even after exit 0:
        # a detached child can close stdout and outlive its parent. Collection is
        # forbidden unless the sweep proves the whole principal quiescent.
        try:
            if stop is not None:
                stop()
        finally:
            # A reaped pid is never signalled. On failure it is still reserved
            # by our child. Successful source commands still sweep their fresh
            # dedicated uid through stop(), including detached descendants.
            if not reaped:
                terminate_group()
            try:
                if not reaped:
                    proc.wait(timeout=2)
            finally:
                proc.stdout.close()


def admin(args, cleanup=False):
    code, text = command(['/usr/bin/sudo', '-n', *args], '/', timeout=10 if cleanup else None, cleanup=cleanup)
    if code:
        raise ValueError('remote principal setup/cleanup refused')
    return text


def sandbox_profile(root, writable, mirror_endpoints=()):
    quoted = lambda p: json.dumps(str(p.resolve()))
    if len(mirror_endpoints) > 128:
        raise ValueError('too many registry mirror endpoints')
    network = ''
    for host, port in mirror_endpoints:
        if host not in ('127.0.0.1', '::1') or not bounded_integer(port, 1, 65535):
            raise ValueError('invalid registry mirror endpoint')
        # Seatbelt accepts only localhost or * in an IP filter. Both loopback
        # families at this exact port are reserved by the immutable bridge.
        network += '(allow network-outbound (require-all (remote ip "localhost:' + str(port) + '") (socket-type SOCK_STREAM)))\n'
    return ('(version 1) (deny default)\n' + network +
        '(allow process-fork process-exec signal)\n'
        '(deny process-exec (literal "/usr/bin/sudo") (literal "/usr/bin/su"))\n'
        '(allow sysctl-read mach-lookup ipc-posix* system-socket file-read-metadata)\n'
        '(allow file-read* (literal "/") (literal ' + quoted(root) + ') (subpath "/System") (subpath "/Library") (subpath "/usr") '
        '(subpath "/bin") (subpath "/sbin") (subpath "/Applications") '
        '(subpath "/private/etc") (subpath "/private/var/db") (subpath ' + quoted(root / 'artifacts') + ') (subpath ' + quoted(root / 'packages') + ') '
        '(literal "/dev/null") (literal "/dev/random") (literal "/dev/urandom") '
        + ' '.join('(subpath ' + quoted(p) + ')' for p in writable) + ')\n'
        '(allow file-write* (literal "/dev/null") '
        + ' '.join('(subpath ' + quoted(p) + ')' for p in writable) + ')\n')


def darwin_directory(text, uid, collector_uid, inspect=os.lstat):
    # Foundation downloads use the kernel's per-user TemporaryItems, ignoring
    # TMPDIR. Only the freshly allocated source uid's real Darwin directories
    # may be opened; never an owner/shared directory or a symlink alias.
    if not bounded_integer(uid, 550, 599) or uid == collector_uid:
        raise ValueError('Darwin directory requires the fresh source uid')
    if text.startswith('/var/'):
        text = '/private' + text
    path = pathlib.Path(text.rstrip('/'))
    base = pathlib.Path('/private/var/folders')
    if (not path.is_relative_to(base) or path.name not in ('T', 'C')
            or any(part in ('.', '..') for part in text.split('/'))):
        raise ValueError('Darwin directory is outside the per-user scope')
    current = base
    for part in path.relative_to(base).parts:
        current = current / part
        node = inspect(current)
        if not stat.S_ISDIR(node.st_mode) or node.st_uid not in (0, uid) or node.st_mode & 0o022:
            raise ValueError('Darwin directory has an unsafe ancestor or mode')
    if node.st_uid != uid:
        raise ValueError('Darwin directory is not owned by the fresh source uid')
    return path


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
        self.worktree = root / os.environ['SIDE']
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
                    'TMPDIR': str(self.home / 'tmp'), 'DEVELOPER_DIR': '/Applications/Xcode_16.4.app/Contents/Developer',
                    'xcrun_nocache': '1'}
        # POSIX uid separation is backed by a deny-by-default Seatbelt profile:
        # no user switching, Internet/arbitrary local network or owner access.
        # Only source/build/home and UID-verified Darwin temp/cache are writable;
        # any registry IPC grants reserve exact dual-stack TCP ports, readonly
        # prefetched routes only. System tools remain root/owner-owned.
        self.platform_directories = self.bootstrap_storage()
        writable = [root / os.environ['SIDE'], self.home, root / ('derived-' + os.environ['SIDE']), *self.platform_directories]
        self.profile = root / 'build.sb'
        self.profile.write_text(sandbox_profile(root, writable))

    def bootstrap_storage(self):
        try:
            return self.darwin_directories()
        except BaseException:
            # No repository command has run, but a created uid is still an
            # authority: sweep it and remove its account before reporting a
            # typed setup refusal. Any cleanup failure prevents publication.
            self.sweep()
            admin(['/usr/bin/dscl', '.', '-delete', '/Users/' + self.user], cleanup=True)
            raise

    def darwin_directories(self):
        paths = []
        for setting in ('DARWIN_USER_TEMP_DIR', 'DARWIN_USER_CACHE_DIR'):
            code, text = command(['/usr/bin/sudo', '-n', '-u', self.user, '/usr/bin/env', '-i',
                                  'PATH=/usr/bin:/bin', '/usr/bin/getconf', setting], '/')
            if code:
                raise PrepareFailure('package-storage-unavailable')
            try:
                paths.append(darwin_directory(text.strip(), self.uid, self.collector_uid))
            except (ValueError, OSError) as error:
                raise PrepareFailure('package-storage-unavailable') from error
        return paths

    def registry_endpoints(self, endpoints):
        # A prepare command may replace its own T/C leaf. It has quiesced before
        # this owner-side rewrite; recheck instead of resolving a planted link
        # into an expanded filesystem grant.
        try:
            for path in self.platform_directories:
                darwin_directory(str(path), self.uid, self.collector_uid)
        except (ValueError, OSError) as error:
            raise PrepareFailure('package-storage-unavailable') from error
        writable = [self.root / os.environ['SIDE'], self.home, self.root / ('derived-' + os.environ['SIDE']), *self.platform_directories]
        self.profile.write_text(sandbox_profile(self.root, writable, endpoints))

    def sweep(self):
        # This uid was allocated fresh, is not the owner/root, and only this job
        # ever launches it. Repeat because a child may fork during the first pass.
        deadline = time.monotonic() + 3
        for _ in range(50):
            if time.monotonic() >= deadline:
                break
            code, text = command(['/bin/ps', '-axo', 'uid=,stat='], '/', timeout=min(2, deadline - time.monotonic()), cleanup=True)
            if code:
                raise ValueError('cannot verify build principal quiescence')
            live = any(int(fields[0]) == self.uid and not fields[1].startswith(('Z', 'X'))
                       for line in text.splitlines() if len(fields := line.split()) == 2)
            if not live:
                return
            code, _ = command(['/usr/bin/sudo', '-n', '/usr/bin/pkill', '-KILL', '-u', str(self.uid)], '/', timeout=min(2, max(0.01, deadline - time.monotonic())), cleanup=True)
            if code not in (0, 1):
                raise ValueError('cannot terminate build principal')
            time.sleep(0.1)
        raise ValueError('build descendants survived; evidence refused')

    def run(self, args, cwd, extra_environment=None, timeout=None):
        environment = dict(self.env, **(extra_environment or {}))
        developer = self.env.get('DEVELOPER_DIR')
        if developer:
            # /usr/bin developer-tool shims consult xcrun's system-selected
            # per-user temp cache before honoring TMPDIR. Use the pinned tools
            # directly; never open a shared/owner temp directory to the source.
            tools = {'/usr/bin/python3': developer + '/usr/bin/python3',
                     '/usr/bin/swift': developer + '/Toolchains/XcodeDefault.xctoolchain/usr/bin/swift',
                     '/usr/bin/xcodebuild': developer + '/usr/bin/xcodebuild'}
            args = [tools.get(args[0], args[0]), *args[1:]]
            if args[0] == '/usr/bin/xcrun':
                args = [args[0], '--no-cache', *args[1:]]
        return command(['/usr/bin/sudo', '-n', '-u', self.user, '/usr/bin/env', '-i',
                        *[k + '=' + v for k, v in environment.items()],
                        '/usr/bin/sandbox-exec', '-f', str(self.profile), *args],
                       cwd, env={'PATH': '/usr/bin:/bin:/usr/sbin:/sbin'}, stop=self.sweep, timeout=timeout)

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
        admin(['/bin/rm', '-rf', '--', str(self.root)], cleanup=True)

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


# Execution describes one already-discovered TestRunSpec. The optional typed
# prepare recipe is reviewed source code, run ONLY by the restricted principal.
# It never grants a collector command, script, environment or local cache path.
EXECUTION_LIMIT = 48 * 1024
RESULT_LIMIT = 2 * 1024 * 1024


def repo_path(value, allow_factory=False):
    if (not isinstance(value, str) or not value or len(value) > 1000
            or value.startswith(('/', '-')) or '\\' in value
            or any(ord(c) < 32 or ord(c) == 127 for c in value)
            or any(p in ('', '.', '..') or (not allow_factory and p.lower().startswith('.factory')) for p in value.split('/'))):
        raise ValueError('invalid execution repository path')
    return value


def public_address(text):
    # Mirror ArtifactAddresses, including IPv6 transports carrying IPv4. Python's
    # is_global alone does not classify NAT64/6to4/Teredo embedded addresses.
    address = ipaddress.ip_address(text)
    if '%' in text:
        raise ValueError('scoped artifact address')
    if address.version == 6:
        b = address.packed
        embedded = None
        if address.ipv4_mapped or b[:12] == bytes(12):
            embedded = b[12:]
        elif b[:12] == bytes(8) + b'\xff\xff\0\0':
            raise ValueError('IPv4-translated artifact address')
        elif b[:4] == b'\0\x64\xff\x9b':
            if b[4:12] != bytes(8):
                raise ValueError('local or unknown NAT64 artifact address')
            embedded = b[12:]
        elif b[:2] == b'\x20\x02':
            embedded = b[2:6]
        elif b[:4] == b'\x20\x01\0\0':
            embedded = bytes(x ^ 255 for x in b[12:])
        if embedded is not None:
            public_address(str(ipaddress.IPv4Address(embedded)))
            return address
    forbidden = ('0.0.0.0/8', '10.0.0.0/8', '100.64.0.0/10', '127.0.0.0/8',
        '169.254.0.0/16', '172.16.0.0/12', '192.0.0.0/24', '192.0.2.0/24',
        '192.88.99.0/24', '192.168.0.0/16', '198.18.0.0/15', '198.51.100.0/24',
        '203.0.113.0/24', '224.0.0.0/4', '240.0.0.0/4') if address.version == 4 else (
        '::/128', '::1/128', '100::/64', '2001:db8::/32', 'fc00::/7', 'fe80::/10', 'fec0::/10', 'ff00::/8')
    if any(address in ipaddress.ip_network(network) for network in forbidden):
        raise ValueError('nonpublic artifact address')
    return address


def artifact_url(text):
    if not isinstance(text, str) or len(text) > 2048 or any(ord(c) <= 32 or ord(c) == 127 for c in text):
        raise ValueError('invalid artifact URL')
    url = urllib.parse.urlsplit(text)
    if (url.scheme != 'https' or not url.hostname or url.username is not None or url.password is not None
            or url.fragment or '#' in text or url.port not in (None, 443) or '\\' in text):
        raise ValueError('artifacts require public HTTPS port 443 without credentials or fragment')
    host = url.hostname.rstrip('.').lower()
    if host == 'localhost' or host.endswith('.localhost'):
        raise ValueError('local artifact host')
    try:
        ipaddress.ip_address(host)
    except ValueError:
        pass
    else:
        public_address(host)
    return url


def artifact_addresses(host):
    addresses = list(dict.fromkeys(row[4][0] for row in socket.getaddrinfo(host, 443, type=socket.SOCK_STREAM)))
    if not addresses:
        raise ValueError('artifact host resolves to no address')
    # Check EVERY answer before attempting any connection; a mixed public/private
    # response is refused. Connect to the checked numeric IP, never resolve again.
    return [public_address(address) for address in addresses]


def artifact_connection(url):
    class PinnedHttps(http.client.HTTPSConnection):
        def connect(self):
            last = None
            for address in artifact_addresses(self.host):
                connection = socket.socket(socket.AF_INET if address.version == 4 else socket.AF_INET6, socket.SOCK_STREAM)
                connection.settimeout(remaining(30))
                try:
                    connection.connect((str(address), 443))
                    self.sock = self._context.wrap_socket(connection, server_hostname=self.host)
                    return
                except OSError as error:
                    connection.close()
                    last = error
            raise last or ValueError('artifact connection failed')
    # No proxies, cookies, credentials, transparent decompression or automatic
    # redirects. TLS certificate/name verification remains the stdlib default.
    return PinnedHttps(url.hostname, 443, timeout=remaining(30), context=ssl.create_default_context())


def bounded_integer(value, low, high):
    return type(value) is int and low <= value <= high


def validate_prepare(recipe):
    if not isinstance(recipe, dict) or set(recipe) != {'artifacts', 'steps'}:
        raise ValueError('invalid execution prepare recipe')
    artifacts, steps = recipe['artifacts'], recipe['steps']
    if not isinstance(artifacts, list) or len(artifacts) > 16 or not isinstance(steps, list) or not 1 <= len(steps) <= 32:
        raise ValueError('invalid prepare artifact/step count')
    names = set()
    def name(value):
        return isinstance(value, str) and re.fullmatch(r'[a-z0-9][a-z0-9._-]{0,63}', value)
    for artifact in artifacts:
        if not isinstance(artifact, dict) or set(artifact) != {'name', 'url', 'sha256', 'maxBytes', 'unpack', 'maxUnpackedBytes'}:
            raise ValueError('invalid prepare artifact')
        if not name(artifact['name']) or artifact['name'] in names:
            raise ValueError('invalid or duplicate artifact name')
        names.add(artifact['name'])
        artifact_url(artifact['url'])
        if (not isinstance(artifact['sha256'], str) or not re.fullmatch('[0-9a-fA-F]{64}', artifact['sha256'])
                or not bounded_integer(artifact['maxBytes'], 1, 4 * 1024 ** 3)
                or not bounded_integer(artifact['maxUnpackedBytes'], 1, 16 * 1024 ** 3)
                or artifact['unpack'] not in ('none', 'zip', 'tar', 'tar.gz')):
            raise ValueError('invalid artifact hash, size or unpack format')
    placeholder = re.compile(r'\$\{([^}]*)\}')
    def value(text):
        if not isinstance(text, str) or len(text) > 4096 or any(c in text for c in '\0\r\n'):
            raise ValueError('invalid prepare value')
        for match in placeholder.finditer(text):
            if match[1] != 'worktree' and (not match[1].startswith('artifact:') or match[1][9:] not in names):
                raise ValueError('unknown prepare placeholder')
        if '${' in placeholder.sub('', text):
            raise ValueError('unterminated prepare placeholder')
    step_names = set()
    refused = {'PATH', 'HOME', 'TMPDIR', 'USER', 'LOGNAME', 'SHELL', 'IFS', 'ENV', 'BASH_ENV', 'PS4',
               'SHELLOPTS', 'BASHOPTS', 'MSBUILDDISABLENODEREUSE', 'DOTNET_CLI_USE_MSBUILD_SERVER'}
    for step in steps:
        if not isinstance(step, dict) or set(step) != {'name', 'argv', 'workingDirectory', 'environment', 'timeoutSeconds'}:
            raise ValueError('invalid prepare step')
        if not name(step['name']) or step['name'] in step_names:
            raise ValueError('invalid or duplicate prepare step name')
        step_names.add(step['name'])
        if not isinstance(step['argv'], list) or not 1 <= len(step['argv']) <= 256:
            raise ValueError('invalid prepare argv')
        for argument in step['argv']:
            value(argument)
        program = step['argv'][0]
        if (not (program.startswith(('/', '${')) or re.fullmatch(r'[A-Za-z0-9_+][A-Za-z0-9._+-]{0,127}', program))
                or '=' in program or any(p in ('.', '..') for p in program.split('/'))):
            raise ValueError('invalid prepare program')
        cwd = step['workingDirectory']
        if not isinstance(cwd, str) or len(cwd) > 512:
            raise ValueError('invalid prepare working directory')
        if cwd not in ('', '.', './'):
            if (cwd.startswith('/') or '\\' in cwd or '\0' in cwd or '${' in cwd
                    or any(p in ('', '.', '..') for p in cwd.rstrip('/').split('/'))):
                raise ValueError('invalid prepare working directory')
        environment = step['environment']
        if not isinstance(environment, dict) or len(environment) > 64:
            raise ValueError('invalid prepare environment')
        for key, text in environment.items():
            if (not re.fullmatch('[A-Za-z_][A-Za-z0-9_]{0,127}', key) or key in refused
                    or key.startswith(('DYLD_', 'LD_', 'ANTHROPIC_', 'BASH_FUNC_'))):
                raise ValueError('refused prepare environment variable')
            value(text)
        if step['timeoutSeconds'] is not None and not bounded_integer(step['timeoutSeconds'], 1, 7200):
            raise ValueError('invalid prepare timeout')


def download_artifact(artifact, destination):
    url_text = artifact['url']
    deadline = time.monotonic() + remaining(600)
    for redirect in range(6):
        url = artifact_url(url_text)
        connection = artifact_connection(url)
        try:
            connection.request('GET', urllib.parse.urlunsplit(('', '', url.path or '/', url.query, '')),
                               headers={'Accept-Encoding': 'identity', 'Accept': artifact.get('accept', '*/*')})
            response = connection.getresponse()
            if response.status in (301, 302, 303, 307, 308):
                if redirect == 5 or not response.getheader('Location'):
                    raise ValueError('too many or invalid artifact redirects')
                url_text = urllib.parse.urljoin(url_text, response.getheader('Location'))
                artifact_url(url_text)
                continue
            if response.status != 200:
                raise ValueError('artifact download failed')
            size, digest = 0, hashlib.sha256()
            with destination.open('xb') as output:
                while chunk := response.read(65536):
                    size += len(chunk)
                    if size > artifact['maxBytes'] or time.monotonic() >= deadline:
                        raise ValueError('artifact download exceeds size or time bound')
                    digest.update(chunk)
                    output.write(chunk)
            if digest.hexdigest() != artifact['sha256'].lower():
                raise ValueError('artifact SHA-256 mismatch')
            return
        finally:
            connection.close()
    raise ValueError('artifact redirect limit exceeded')


def unpack_artifact(archive, destination, kind, max_bytes):
    total, count = 0, 0
    def entry(name, size, is_directory, executable, source=None):
        nonlocal total, count
        count += 1
        if count > 200000:
            raise ValueError('too many artifact entries')
        # Artifact content is data, not repository paths: .factory filenames are
        # harmless here, but no absolute/traversal/alias/Windows path is allowed.
        name = name.rstrip('/')
        if name.startswith('./'):
            name = name[2:]
        if not name or name == '.':
            if is_directory:
                return
            raise ValueError('invalid artifact entry')
        if ('\\' in name or name.startswith('/') or ':' in name
                or any(p in ('', '.', '..') for p in name.split('/')) or '\0' in name):
            raise ValueError('unsafe artifact entry')
        total += size
        if total > max_bytes or size < 0:
            raise ValueError('artifact unpack exceeds byte limit')
        path = destination.joinpath(*name.split('/'))
        if is_directory:
            path.mkdir(parents=True, exist_ok=True)
            return
        path.parent.mkdir(parents=True, exist_ok=True)
        written = 0
        with path.open('xb') as output:
            while chunk := source.read(65536):
                written += len(chunk)
                if written > size:
                    raise ValueError('artifact entry exceeds declared size')
                output.write(chunk)
        if written != size:
            raise ValueError('incomplete artifact entry')
        path.chmod(0o555 if executable else 0o444)
    if kind == 'zip':
        with zipfile.ZipFile(archive) as source:
            if len(source.infolist()) > 200000:
                raise ValueError('too many artifact entries')
            for node in source.infolist():
                mode = node.external_attr >> 16
                if stat.S_IFMT(mode) not in (0, stat.S_IFREG, stat.S_IFDIR):
                    raise ValueError('artifact links and special entries refused')
                with source.open(node) as data:
                    entry(node.filename, node.file_size, node.is_dir(), mode & 0o111, data)
    elif kind in ('tar', 'tar.gz'):
        with tarfile.open(archive, mode='r|gz' if kind == 'tar.gz' else 'r|') as source:
            for node in source:
                if not (node.isdir() or node.isreg()) or node.issparse():
                    raise ValueError('artifact links and special entries refused')
                data = source.extractfile(node) if node.isreg() else None
                try:
                    entry(node.name, node.size, node.isdir(), node.mode & 0o111, data)
                finally:
                    if data is not None:
                        data.close()
    else:
        raise ValueError('unknown artifact unpack format')
    for directory, _, _ in os.walk(destination, topdown=False):
        pathlib.Path(directory).chmod(0o555)


def prepare_artifacts(root, recipe):
    cache = root / 'artifacts'
    cache.mkdir(mode=0o755)
    paths = {}
    for artifact in recipe['artifacts']:
        downloaded = root / ('download-' + artifact['name'])
        download_artifact(artifact, downloaded)
        target = cache / artifact['name']
        if artifact['unpack'] == 'none':
            downloaded.rename(target)
            target.chmod(0o444)
        else:
            target.mkdir()
            unpack_artifact(downloaded, target, artifact['unpack'], artifact['maxUnpackedBytes'])
            downloaded.unlink()
        paths[artifact['name']] = str(target)
    cache.chmod(0o555)
    return paths


def validate_package_zip(archive):
    # Inspect central-directory metadata only. Extraction, including framework
    # symlinks, is done later with the source principal's uid and filesystem jail.
    with zipfile.ZipFile(archive) as source:
        entries = source.infolist()
        if len(entries) > 200000 or sum(e.file_size for e in entries) > 400 * 1024 * 1024:
            raise ValueError('package archive exceeds unpack bounds')
        for entry in entries:
            name = entry.filename.rstrip('/')
            kind = stat.S_IFMT(entry.external_attr >> 16)
            if (not name or name.startswith('/') or '\\' in name or '\0' in name
                    or any(p in ('', '.', '..') for p in name.split('/'))
                    or kind not in (0, stat.S_IFREG, stat.S_IFDIR, stat.S_IFLNK)):
                raise ValueError('unsafe package archive entry')
            if kind == stat.S_IFLNK:
                if entry.file_size > 4096:
                    raise ValueError('oversized package symlink')
                target = source.read(entry).decode('utf-8')
                if target.startswith('/') or '\\' in target or '\0' in target:
                    raise ValueError('absolute package symlink refused')
                parts = list(pathlib.PurePosixPath(name).parent.parts)
                for part in target.split('/'):
                    if part == '..':
                        if not parts:
                            raise ValueError('escaping package symlink refused')
                        parts.pop()
                    elif part not in ('', '.'):
                        parts.append(part)


def public_resource(url_text, accept):
    for redirect in range(6):
        url = artifact_url(url_text)
        connection = artifact_connection(url)
        try:
            connection.request('GET', urllib.parse.urlunsplit(('', '', url.path or '/', url.query, '')),
                               headers={'Accept': accept, 'Accept-Encoding': 'identity'})
            response = connection.getresponse()
            if response.status in (301, 302, 303, 307, 308):
                if redirect == 5 or not response.getheader('Location'):
                    raise ValueError('invalid package resource redirect')
                url_text = urllib.parse.urljoin(url_text, response.getheader('Location'))
                artifact_url(url_text)
                continue
            if response.status in (401, 403):
                raise PrepareFailure('package-auth-required')
            if response.status == 404:
                raise PrepareFailure('package-unavailable')
            if response.status != 200:
                raise ValueError('public package resource unavailable')
            data = response.read(1024 * 1024 + 1)
            remaining()
            if len(data) > 1024 * 1024:
                raise ValueError('package resource exceeds size bound')
            headers = {name: response.getheader(name) for name in ('Content-Type', 'Content-Version', 'Link') if response.getheader(name)}
            if any(len(value) > 8192 or '\r' in value or '\n' in value for value in headers.values()):
                raise ValueError('invalid package resource headers')
            return data, headers
        finally:
            connection.close()
    raise ValueError('package resource redirect bound')


PACKAGE_DATA_SCRIPT = '''
import json, os, pathlib, stat, sys
root = pathlib.Path(sys.argv[1]); result = {}; count = total = 0
for directory, dirs, files in os.walk(root, followlinks=False):
    count += len(dirs) + len(files)
    if count > 20000: raise ValueError('package discovery entry bound')
    for name in files:
        if name not in ('Package.resolved', 'registries.json'): continue
        path = pathlib.Path(directory) / name
        with os.fdopen(os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK), 'rb') as source:
            node = os.fstat(source.fileno())
            if not stat.S_ISREG(node.st_mode) or node.st_size > 1024*1024: raise ValueError('unsafe package data')
            body = source.read(1024*1024+1)
        total += len(body)
        if total > 2*1024*1024 or len(result) >= 64: raise ValueError('package discovery byte bound')
        result[str(path.relative_to(root))] = body.decode('utf-8')
print(json.dumps(result))
'''


def package_snapshot(principal, worktree):
    code, output = principal.run(['/usr/bin/python3', '-I', '-c', PACKAGE_DATA_SCRIPT, str(worktree)], worktree)
    if code or len(output.encode('utf-8')) > 3 * 1024 * 1024:
        raise PrepareFailure('package-hydration-failed')
    data = json.loads(output)
    if not isinstance(data, dict) or len(data) > 64:
        raise ValueError('invalid package discovery data')
    for path, text in data.items():
        repo_path(path, allow_factory=True)
        if not isinstance(text, str) or len(text.encode('utf-8')) > 1024 * 1024:
            raise ValueError('invalid package discovery body')
    return data


def package_files(worktree, name, data=None):
    # Archive-only callers may read collector-owned paths. Production after
    # prepare uses the restricted principal's bounded no-follow data snapshot.
    if data is not None:
        return [(worktree / relative, text) for relative, text in data.items() if pathlib.PurePosixPath(relative).name == name]
    result = []
    for path in worktree.rglob(name):
        if path.stat().st_size > 1024 * 1024:
            raise ValueError('package data too large')
        result.append((path, path.read_text(encoding='utf-8')))
    return result


def registry_configuration(worktree, dependencies, data=None):
    configurations = []
    for path, text in package_files(worktree, 'registries.json', data):
        if path.parent.name != 'configuration' or path.parent.parent.name not in ('.swiftpm', 'swiftpm'):
            continue
        if len(text.encode('utf-8')) > 64 * 1024:
            raise ValueError('registry configuration exceeds size bound')
        value = json.loads(text)
        if not isinstance(value, dict) or value.get('version') != 1 or not isinstance(value.get('registries'), dict):
            raise ValueError('unsupported registry configuration')
        configurations.append(value)
    urls = {}
    for identity, version in dependencies:
        scope, name = identity.split('.')
        matches = set()
        for configuration in configurations:
            settings = configuration['registries'].get(scope, configuration['registries'].get('[default]', {}))
            if isinstance(settings, dict) and isinstance(settings.get('url'), str):
                url = settings['url'].rstrip('/')
                parsed = artifact_url(url)
                if parsed.query:
                    raise ValueError('registry URL must have no query')
                matches.add(url)
        if len(matches) != 1:
            raise ValueError('registry package requires one explicit public registry URL')
        urls[scope] = matches.pop()
    policies = [configuration.get('security', {}) for configuration in configurations]
    if any(policy != policies[0] for policy in policies):
        raise ValueError('conflicting registry security policies')
    return urls, policies[0] if policies else {}


def hydrate_registry_packages(root, worktree, dependencies, cache, data=None):
    if not dependencies:
        return
    urls, security = registry_configuration(worktree, dependencies, data)
    entries, routes = [], []
    for index, (identity, version) in enumerate(dependencies):
        scope, name = identity.split('.')
        url = urls[scope] + '/' + scope + '/' + name + '/' + version
        data, headers = public_resource(url, 'application/vnd.swift.registry.v1+json')
        metadata = json.loads(data.decode('utf-8'))
        metadata_file = cache / ('registry-' + str(index) + '.json')
        metadata_file.write_bytes(data)
        route = '/' + scope + '/' + name + '/' + version
        routes.append({'registry': urls[scope], 'path': route, 'file': str(metadata_file), 'headers': headers})
        manifest, manifest_headers = public_resource(url + '/Package.swift', 'application/vnd.swift.registry.v1+swift')
        manifest_file = cache / ('manifest-' + str(index) + '.swift')
        manifest_file.write_bytes(manifest)
        routes.append({'registry': urls[scope], 'path': route + '/Package.swift', 'file': str(manifest_file), 'headers': manifest_headers})
        variants = re.findall(r'swift-tools-version="([0-9]+\.[0-9]+(?:\.[0-9]+)?)"', manifest_headers.get('Link', ''))
        if len(variants) > 16:
            raise ValueError('too many registry manifest variants')
        for variant in set(variants):
            alternate, alternate_headers = public_resource(url + '/Package.swift?swift-version=' + variant, 'application/vnd.swift.registry.v1+swift')
            alternate_file = cache / ('manifest-' + str(index) + '-' + variant + '.swift')
            alternate_file.write_bytes(alternate)
            routes.append({'registry': urls[scope], 'path': route + '/Package.swift?swift-version=' + variant, 'file': str(alternate_file), 'headers': alternate_headers})
        resources = metadata.get('resources', []) if isinstance(metadata, dict) else []
        archives = [r for r in resources if isinstance(r, dict) and r.get('name') == 'source-archive']
        if len(archives) != 1 or not re.fullmatch('[0-9a-fA-F]{64}', archives[0].get('checksum', '')):
            raise ValueError('registry source archive has no SHA-256')
        artifact = {'url': url + '.zip', 'sha256': archives[0]['checksum'], 'maxBytes': 200 * 1024 * 1024, 'accept': 'application/vnd.swift.registry.v1+zip'}
        archive = cache / ('registry-' + str(index) + '.zip')
        download_artifact(artifact, archive)
        # Keep the ZIP opaque to the collector. SwiftPM registry archives can
        # contain legitimate symlinks; extraction belongs to the source principal.
        validate_package_zip(archive)
        entries.append({'identity': identity, 'version': version, 'archive': str(archive), 'registry': urls[scope]})
        routes.append({'registry': urls[scope], 'path': route + '.zip', 'file': str(archive), 'headers': {'Content-Type': 'application/zip', 'Content-Version': '1'}})
    (cache / 'registry-plan.json').write_text(json.dumps({'urls': urls, 'security': security, 'routes': routes, 'entries': entries}))


def package_pins(worktree, versions=None, registries=None, data=None):
    # Read lockfiles as data, not Package.swift or an Xcode project. Every
    # transitive remote must have an immutable revision in the checked-in pins.
    pins = {}
    for path, text in package_files(worktree, 'Package.resolved', data):
        value = json.loads(text)
        if not isinstance(value, dict) or not isinstance(value.get('object', {}), dict):
            raise ValueError('invalid package lockfile')
        rows = value.get('pins') if value.get('version') in (2, 3) else value.get('object', {}).get('pins') if value.get('version') == 1 else None
        if not isinstance(rows, list):
            raise ValueError('unsupported package lockfile')
        for row in rows:
            if not isinstance(row, dict) or not isinstance(row.get('state'), dict):
                raise ValueError('invalid package pin')
            if row.get('kind') == 'registry':
                identity, version = row.get('identity'), row['state'].get('version')
                if (registries is None or not isinstance(identity, str)
                        or not re.fullmatch(r'[a-z0-9][a-z0-9-]{0,38}\.[a-z0-9][a-z0-9-]{0,99}', identity)
                        or not isinstance(version, str) or not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?', version)):
                    raise ValueError('invalid pinned registry dependency')
                if (identity, version) not in registries:
                    registries.append((identity, version))
                if len(registries) > 64:
                    raise ValueError('too many registry dependencies')
                continue
            if row.get('kind', 'remoteSourceControl') != 'remoteSourceControl':
                raise ValueError('unsupported package dependency kind')
            url = row.get('location', row.get('repositoryURL'))
            if isinstance(url, str) and (url.startswith(('ssh:', 'git@')) or url.startswith('https://') and '@' in url.split('/', 3)[2]):
                raise PrepareFailure('package-auth-required')
            parsed = artifact_url(url)
            if parsed.query or not parsed.path or parsed.path.endswith('/'):
                raise ValueError('packages require explicit public HTTPS repository URLs')
            revision = row.get('state', {}).get('revision')
            if not isinstance(revision, str) or not re.fullmatch('[0-9a-fA-F]{40}', revision):
                raise ValueError('packages require immutable revision pins')
            revision = revision.lower()
            if url in pins and pins[url] != revision:
                raise ValueError('conflicting package revision pins')
            pins[url] = revision
            version = row['state'].get('version')
            if version is not None:
                if not isinstance(version, str) or not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?', version):
                    raise ValueError('unsupported package version pin')
                if versions is not None:
                    versions.setdefault(url, set()).add(version)
            if len(pins) > 64:
                raise ValueError('too many pinned packages')
    return pins


def packet(data):
    return ('%04x' % (len(data) + 4)).encode('ascii') + data


def fetch_package(url_text, revision, destination):
    try:
        return fetch_package_pack(url_text, revision, destination)
    except PrepareFailure:
        raise  # credentials/not-found are not negotiation failures
    except ValueError:
        # Ordinary Git servers can refuse an unadvertised locked ancestor. Ask
        # for its advertised tag, or bounded history from the default HEAD,
        # without handing Git a network connection or abandoning the pinned SHA.
        data, _ = public_resource(url_text.rstrip('/') + '/info/refs?service=git-upload-pack', 'application/x-git-upload-pack-advertisement')
        refs, offset = {}, 0
        while offset + 4 <= len(data):
            header = data[offset:offset + 4]
            if not re.fullmatch(b'[0-9a-fA-F]{4}', header):
                raise ValueError('invalid Git reference advertisement')
            length = int(header, 16); offset += 4
            if length == 0:
                continue
            if length < 4 or offset + length - 4 > len(data):
                raise ValueError('truncated Git reference advertisement')
            line = data[offset:offset + length - 4].split(b'\0', 1)[0].strip()
            offset += length - 4
            match = re.fullmatch(b'([0-9a-f]{40}) ([^\r\n ]+)', line)
            if match:
                refs[match[2].decode('utf-8')] = match[1].decode('ascii')
                if len(refs) > 10000:
                    raise ValueError('too many advertised Git references')
        wanted = next((refs[name[:-3]] for name, sha in refs.items() if sha == revision and name.endswith('^{}') and name[:-3] in refs), None)
        wanted = wanted or next((sha for name, sha in refs.items() if sha == revision), None) or refs.get('HEAD')
        if wanted is None:
            raise ValueError('package has no usable advertised reference')
        if destination.exists():
            destination.unlink()
        return fetch_package_pack(url_text, revision, destination, wanted=wanted, depth=4096)


def fetch_package_pack(url_text, revision, destination, wanted=None, depth=1):
    # Smart HTTP is transported here, NOT by git: public-address validation and
    # numeric-address pinning apply to every socket. No proxy, git credential
    # helper, redirect, repo configuration, manifest, macro or plugin can run.
    url = artifact_url(url_text.rstrip('/') + '/git-upload-pack')
    connection = artifact_connection(url)
    body = (packet(('want ' + (wanted or revision) + ' side-band-64k ofs-delta shallow\n').encode('ascii'))
            + packet(('deepen ' + str(depth) + '\n').encode('ascii')) + b'0000' + packet(b'done\n'))
    try:
        connection.request('POST', url.path, body=body, headers={
            'Content-Type': 'application/x-git-upload-pack-request',
            'Accept': 'application/x-git-upload-pack-result', 'Accept-Encoding': 'identity'})
        response = connection.getresponse()
        if response.status in (401, 403):
            raise PrepareFailure('package-auth-required')
        if response.status == 404:
            raise PrepareFailure('package-unavailable')
        if response.status != 200 or response.getheader('Content-Type', '').split(';')[0] != 'application/x-git-upload-pack-result':
            raise ValueError('public pinned package unavailable; private/credentialed dependencies are unsupported')
        size = 0
        def read_exact(count):
            nonlocal size
            value = bytearray()
            while len(value) < count:
                remaining()
                chunk = response.read(count - len(value))
                if not chunk:
                    raise ValueError('incomplete package transport')
                size += len(chunk)
                if size > 200 * 1024 * 1024:
                    raise ValueError('package transport exceeds size bound')
                value.extend(chunk)
            return bytes(value)
        with destination.open('xb') as output:
            started = False
            while True:
                header = read_exact(4)
                if not re.fullmatch(b'[0-9a-fA-F]{4}', header):
                    raise ValueError('invalid package transport packet')
                length = int(header, 16)
                if length == 0:
                    if started:
                        break
                    continue  # shallow-info ends before the side-band pack
                if not 5 <= length <= 65520:
                    raise ValueError('invalid package transport length')
                data = read_exact(length - 4)
                if not started and (data == b'NAK\n' or re.fullmatch(b'shallow [0-9a-f]{40}\n', data)):
                    continue
                if data[0] == 1:
                    if not started and not data[1:].startswith(b'PACK'):
                        raise ValueError('package response has no pack')
                    started = True
                    output.write(data[1:])
                elif data[0] != 2:
                    raise ValueError('package upload-pack refused pinned revision')
            if not started:
                raise ValueError('package response has no pinned objects')
    finally:
        connection.close()


def hydrate_packages(root, worktree, data=None):
    versions = {}
    registries = []
    pins = package_pins(worktree, versions, registries, data)
    cache = root / 'packages'
    cache.mkdir(mode=0o755)
    mirrors = []
    hydrate_registry_packages(root, worktree, registries, cache, data)
    # All git commands are local plumbing on fresh collector-owned bare repos.
    # No source checkout/config is passed to git, and no network protocol is on.
    env = {'PATH': '/usr/bin:/bin:/usr/sbin:/sbin', 'HOME': str(cache),
           'GIT_CONFIG_NOSYSTEM': '1', 'GIT_CONFIG_GLOBAL': '/dev/null',
           'GIT_ALLOW_PROTOCOL': 'file', 'GIT_TERMINAL_PROMPT': '0'}
    for url, revision in pins.items():
        name = hashlib.sha256(url.encode('utf-8')).hexdigest()
        # SwiftPM derives package identity from the mirror's basename. Preserve
        # it exactly, or a locked resolve treats the pinned dependency as new.
        basename = urllib.parse.unquote(artifact_url(url).path.rsplit('/', 1)[-1])
        if not re.fullmatch(r'[A-Za-z0-9_][A-Za-z0-9_.-]{0,199}', basename):
            raise ValueError('unsupported package repository identity')
        bucket = cache / name
        bucket.mkdir()
        mirror = bucket / (basename if basename.endswith('.git') else basename + '.git')
        pack = cache / (name + '.pack')
        fetch_package(url, revision, pack)
        for args in (['init', '--bare', str(mirror)],
                     ['--git-dir=' + str(mirror), 'index-pack', str(pack)]):
            code, indexed = command(['/usr/bin/git', '-c', 'core.hooksPath=/dev/null', '-c', 'gc.auto=0', *args], root, env=env)
            if code:
                raise ValueError('pinned package object verification failed')
        # index-pack outside the object store creates an index beside the pack;
        # move both into the bare mirror only after the whole pack validates.
        objects = mirror / 'objects/pack'
        pack_hash = indexed.strip()
        if not re.fullmatch('[0-9a-f]{40}', pack_hash):
            raise ValueError('invalid package pack hash')
        pack.rename(objects / ('pack-' + pack_hash + '.pack'))
        pack.with_suffix('.idx').rename(objects / ('pack-' + pack_hash + '.idx'))
        (mirror / 'shallow').write_text(revision + '\n')
        refs = ['refs/heads/factory-pin'] + ['refs/tags/' + version for version in sorted(versions.get(url, set()))]
        for args in [['cat-file', '-e', revision + '^{commit}']] + [['update-ref', ref, revision] for ref in refs]:
            code, _ = command(['/usr/bin/git', '--git-dir=' + str(mirror), '-c', 'core.hooksPath=/dev/null', '-c', 'gc.auto=0', *args], root, env=env)
            if code:
                raise ValueError('pinned package object verification failed')
        mirrors.append({'original': url, 'mirror': mirror.resolve().as_uri()})
    for directory, _, files in os.walk(cache, topdown=False):
        for name in files:
            (pathlib.Path(directory) / name).chmod(0o444)
        pathlib.Path(directory).chmod(0o555)
    return mirrors


HOSTS_SCRIPT = r"""
import fcntl, json, os, pathlib, re, stat, sys, tempfile

def edit_hosts(path, action, nonce, names, owner=0):
    if action not in ('install', 'remove') or not re.fullmatch('[0-9a-f]{32}', nonce):
        raise ValueError('invalid sealed host mapping action')
    if not isinstance(names, list) or len(names) > 64 or len(set(names)) != len(names):
        raise ValueError('invalid sealed host mapping count')
    pattern = r'factory-ios-' + nonce + r'-[0-9a-f]{16}\.invalid'
    if any(not isinstance(name, str) or not re.fullmatch(pattern, name) for name in names):
        raise ValueError('only generated hostnames may be installed')
    path = pathlib.Path(path); lock = str(path) + '.factory-ios.lock'
    with os.fdopen(os.open(lock, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW | os.O_NONBLOCK, 0o600), 'r+b') as held:
        node = os.fstat(held.fileno())
        if not stat.S_ISREG(node.st_mode) or node.st_uid != owner or node.st_mode & 0o077:
            raise ValueError('host mapping lock is not private and owned')
        fcntl.flock(held, fcntl.LOCK_EX | fcntl.LOCK_NB)
        with os.fdopen(os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK), 'rb') as source:
            node = os.fstat(source.fileno())
            if not stat.S_ISREG(node.st_mode) or node.st_uid != owner or node.st_mode & 0o022 or node.st_size > 1024*1024:
                raise ValueError('host mapping target is not a bounded owned regular file')
            body = source.read(1024*1024+1)
        if len(body) > 1024*1024 or body and not body.endswith(b'\n'):
            raise ValueError('host mapping target has no safe line boundary')
        marker = ('# factory-ios:' + nonce).encode()
        owned = { (address + ' ' + name + ' # factory-ios:' + nonce + '\n').encode() for name in names for address in ('127.0.0.1', '::1') }
        lines = body.splitlines(keepends=True)
        if any(marker in line and line not in owned for line in lines):
            raise ValueError('refusing an unfamiliar entry with this mapping marker')
        kept = [line for line in lines if line not in owned]
        if action == 'install': kept += sorted(owned)
        replacement = b''.join(kept)
        if replacement == body: return
        fd, staging = tempfile.mkstemp(prefix='.factory-ios-hosts-', dir=str(path.parent))
        try:
            with os.fdopen(fd, 'wb') as output:
                os.fchmod(output.fileno(), stat.S_IMODE(node.st_mode))
                if os.fstat(output.fileno()).st_gid != node.st_gid:
                    os.fchown(output.fileno(), owner, node.st_gid)
                output.write(replacement); output.flush(); os.fsync(output.fileno())
            os.replace(staging, path)
        finally:
            if os.path.exists(staging): os.unlink(staging)

if __name__ == '__main__':
    if os.geteuid() != 0: raise ValueError('host mapping requires the disposable VM root')
    edit_hosts('/private/etc/hosts', sys.argv[1], sys.argv[2], json.loads(sys.argv[3]))
"""


FOUNDATION_HOSTS = '''
import Foundation
let input = try JSONSerialization.jsonObject(with: Data(CommandLine.arguments.last!.utf8)) as! [String]
var result = [String: String]()
for text in input {
    guard let url = URL(string: text), let host = url.host else { exit(1) }
    result[text] = host
}
print(String(data: try JSONSerialization.data(withJSONObject: result, options: [.sortedKeys]), encoding: .utf8)!)
'''


class RegistryHosts:
    def __init__(self, root):
        self.root = root
        self.nonce = hashlib.sha256(str(root).encode('utf-8')).hexdigest()[:32]
        self.names = []

    def original_hosts(self, urls):
        # A sealed literal Foundation expression, NOT a package command. The
        # exact native URL.host determines registryOverrides, including case and
        # Unicode; no repository manifest/macro/plugin or source cwd is loaded.
        developer = '/Applications/Xcode_' + ENVIRONMENT['xcode'] + '.app/Contents/Developer'
        with tempfile.TemporaryDirectory(prefix='native-hosts-', dir=self.root) as directory:
            # Fresh collector-only cwd/cache, no owner or repository settings.
            env = {'PATH': '/usr/bin:/bin:/usr/sbin:/sbin', 'HOME': directory,
                   'CFFIXED_USER_HOME': directory, 'TMPDIR': directory, 'DEVELOPER_DIR': developer}
            code, text = command([developer + '/Toolchains/XcodeDefault.xctoolchain/usr/bin/swift',
                '-module-cache-path', directory + '/modules', '-e', FOUNDATION_HOSTS, json.dumps(urls)], directory, env=env)
        if code:
            raise PrepareFailure('package-ipc-unavailable')
        value = json.loads(text)
        if (not isinstance(value, dict) or set(value) != set(urls)
                or any(not isinstance(host, str) or not host or len(host) > 300 or any(ord(c) < 32 for c in host) for host in value.values())):
            raise ValueError('invalid native registry host identity')
        return value

    def install(self, names):
        self.names = names  # a partially answered install is still cleaned up
        admin(['/usr/bin/python3', '-I', '-c', HOSTS_SCRIPT, 'install', self.nonce, json.dumps(names)])

    def remove(self):
        if self.names:
            admin(['/usr/bin/python3', '-I', '-c', HOSTS_SCRIPT, 'remove', self.nonce, json.dumps(self.names)], cleanup=True)

    def native_host(self, name):
        return name


class RegistryMirror:
    # Finite static endpoint set: per-registry generated hostnames map to the
    # controlled loopback pair on the disposable VM, preserving native host trust
    # identities without alias tricks or a small policy-count cap. Every exact
    # TCP port is bound on BOTH families; requests never forward upstream, carry
    # credentials or select a filesystem path.
    def __init__(self, root, hosts=None):
        plan = json.loads((root / 'packages/registry-plan.json').read_text())
        originals = sorted(set(plan['urls'].values()))
        self.servers, self.threads, self.endpoints = [], [], []
        self.hosts = hosts or RegistryHosts(root)
        identities = self.hosts.original_hosts(originals)
        names = {host: 'factory-ios-' + self.hosts.nonce + '-' + hashlib.sha256(host.encode('utf-8')).hexdigest()[:16] + '.invalid' for host in set(identities.values())}
        if len(set(names.values())) != len(names):
            raise PrepareFailure('package-ipc-unavailable')
        self.configuration = {'version': 1, 'registries': {}}
        security = json.loads(json.dumps(plan['security']))
        overrides = security.get('registryOverrides', {})
        mapped_overrides = {}
        try:
            self.hosts.install(sorted(names.values()))
            for index, original in enumerate(originals):
                host = self.hosts.native_host(names[identities[original]])
                addresses = socket.getaddrinfo(host.strip('[]'), 443, type=socket.SOCK_STREAM)
                if not addresses or any(row[4][0] not in ('127.0.0.1', '::1') for row in addresses):
                    raise PrepareFailure('package-ipc-unavailable')
                bind_host = '127.0.0.1'
                routes = {entry['path']: entry for entry in plan['routes'] if entry['registry'] == original}
                server = self.start_server(bind_host, routes, original, host)
                self.endpoints.extend([(bind_host, server.server_port), ('::1', server.server_port)])
                base = 'http://' + host + ':' + str(server.server_port)
                for scope, url in plan['urls'].items():
                    if url == original:
                        self.configuration['registries'][scope] = {'url': base, 'supportsAvailability': False}
                policy = overrides.get(identities[original])
                if policy is not None:
                    mapped_overrides[host] = policy
                    if host == '[::1]':
                        mapped_overrides['::1'] = policy  # Foundation versions differ in bracket presentation.
            if 'registryOverrides' in security or mapped_overrides:
                security['registryOverrides'] = mapped_overrides
            if security:
                self.configuration['security'] = security
        except BaseException:
            self.close()
            raise

    def start_server(self, host, routes, original, url_host):
        class Server(http.server.ThreadingHTTPServer):
            daemon_threads = True
            block_on_close = False
            request_queue_size = 8
            def __init__(self, address, family=socket.AF_INET):
                self.address_family = family
                self.seats = threading.BoundedSemaphore(8)
                self.requests = 0
                super().__init__(address, Handler)
            def server_bind(self):
                if self.address_family == socket.AF_INET6:
                    self.socket.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 1)
                super().server_bind()
            def process_request(self, request, address):
                self.requests += 1
                if self.requests > 512 or not self.seats.acquire(blocking=False):
                    self.shutdown_request(request)
                    return
                request.settimeout(5)
                super().process_request(request, address)
            def process_request_thread(self, request, address):
                try:
                    super().process_request_thread(request, address)
                finally:
                    self.seats.release()
        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                if (self.headers.get('Host') != url_host + ':' + str(self.server.server_port)
                        or self.headers.get('Authorization') or self.headers.get('Origin')
                        or len(self.path) > 2048 or sum(len(k) + len(v) for k, v in self.headers.items()) > 16384):
                    self.send_error(403)
                    return
                entry = routes.get(self.path)
                if entry is None:
                    self.send_error(404)
                    return
                self.send_response(200)
                self.send_header('Content-Length', str(pathlib.Path(entry['file']).stat().st_size))
                for name, value in entry['headers'].items():
                    if name == 'Link':
                        value = value.replace(original, 'http://' + url_host + ':' + str(self.server.server_port))
                    self.send_header(name, value)
                self.end_headers()
                with open(entry['file'], 'rb') as source:
                    shutil.copyfileobj(source, self.wfile, 65536)
            def log_message(self, *args):
                pass
        server = Server((host, 0))
        try:
            ipv6 = Server(('::1', server.server_port), socket.AF_INET6)
        except BaseException:
            server.server_close()
            raise
        if server.server_address[0] != host or ipv6.server_address[0] != '::1':
            server.server_close()
            ipv6.server_close()
            raise PrepareFailure('package-ipc-unavailable')
        self.servers.extend((server, ipv6))
        for endpoint in (server, ipv6):
            thread = threading.Thread(target=lambda endpoint=endpoint: endpoint.serve_forever(poll_interval=0.1), daemon=True)
            thread.start()
            self.threads.append(thread)
        return server

    def close(self):
        failure = None
        for server in self.servers:
            try:
                server.shutdown()
            except BaseException as error:
                failure = error
            finally:
                try:
                    server.server_close()
                except BaseException as error:
                    failure = error
        for thread in self.threads:
            thread.join(timeout=2)
        try:
            self.hosts.remove()
        except BaseException as error:
            failure = error
        if failure is not None:
            raise CleanupFailure('sealed registry cleanup refused') from failure


CONFIG_SCRIPT = r"""
import json, pathlib, sys
home = pathlib.Path(sys.argv[1]); config = json.loads(sys.argv[2])
for relative in ('.swiftpm/configuration', 'Library/org.swift.swiftpm/configuration'):
    directory = home / relative
    directory.mkdir(parents=True, exist_ok=True)
    (directory / 'mirrors.json').write_text(json.dumps(config['mirrors']))
    if config['registries'] is not None:
        (directory / 'registries.json').write_text(json.dumps(config['registries']))
# Only the collector's exact immutable mirror directories are trusted. Modern
# upload-pack checks ownership too; source UID must not inherit the owner's git
# configuration or a wildcard safe.directory exception.
paths = []
for entry in config['mirrors']['object']:
    import urllib.parse
    parsed = urllib.parse.urlsplit(entry['mirror'])
    path = pathlib.Path(urllib.parse.unquote(parsed.path))
    if parsed.scheme != 'file' or parsed.netloc or not path.is_absolute() or path.resolve() != path or not path.is_dir() or path.stat().st_mode & 0o222:
        raise ValueError('invalid immutable package mirror')
    paths.append(str(path))
(home / '.factory-gitconfig').write_text('[safe]\n' + ''.join('\tdirectory = ' + json.dumps(path) + '\n' for path in paths))
if config['registries'] is not None and len(sys.argv) > 3:
    for path in pathlib.Path(sys.argv[3]).rglob('registries.json'):
        if path.parent.name == 'configuration' and path.parent.parent.name in ('.swiftpm', 'swiftpm'):
            path.write_text(json.dumps(config['registries']))
"""


def install_package_mirrors(principal, mirrors, registry=None):
    config = {'mirrors': {'version': 1, 'object': mirrors}, 'registries': registry.configuration if registry else None}
    code, _ = principal.run(['/usr/bin/python3', '-I', '-c', CONFIG_SCRIPT, str(principal.home), json.dumps(config), str(getattr(principal, 'worktree', principal.home.parent / 'base'))], principal.home.parent)
    if code:
        raise PrepareFailure('package-hydration-failed')
    principal.env.update({'GIT_CONFIG_NOSYSTEM': '1', 'GIT_CONFIG_SYSTEM': '/dev/null',
                          'GIT_CONFIG_GLOBAL': str(principal.home / '.factory-gitconfig')})


BINARY_DISCOVERY_SCRIPT = r"""
import json, os, pathlib, subprocess, sys
root, worktree, home = map(pathlib.Path, sys.argv[1:4]); mirrors = json.loads(sys.argv[4]); checkouts = []
def command(args):
    if args[0] == '/usr/bin/swift' and 'DEVELOPER_DIR' in os.environ:
        args[0] = os.environ['DEVELOPER_DIR'] + '/Toolchains/XcodeDefault.xctoolchain/usr/bin/swift'
    result = subprocess.run(args, capture_output=True)
    if result.returncode:
        sys.stderr.buffer.write(result.stdout + result.stderr)
        raise ValueError('source package discovery failed')
    return result.stdout
for index, mirror in enumerate(mirrors):
    path = home / 'package-discovery' / str(index)
    path.parent.mkdir(parents=True, exist_ok=True)
    command(['/usr/bin/git', 'clone', '--no-checkout', mirror['mirror'], str(path)])
    command(['/usr/bin/git', '-C', str(path), 'checkout', '--detach', 'origin/factory-pin'])
    checkouts.append((path, mirror['original']))
plan = root / 'packages/registry-plan.json'
if plan.is_file():
    for index, entry in enumerate(json.loads(plan.read_text())['entries']):
        path = home / 'registry-discovery' / str(index)
        path.mkdir(parents=True)
        command(['/usr/bin/ditto', '-x', '-k', entry['archive'], str(path)])
        children = list(path.iterdir())
        if not (path / 'Package.swift').is_file() and len(children) == 1 and children[0].is_dir(): path = children[0]
        checkouts.append((path, entry['identity']))
if (worktree / 'Package.swift').is_file(): checkouts.append((worktree, None))
result = []
for checkout, origin in checkouts:
    manifest = json.loads(command(['/usr/bin/swift', 'package', '--disable-sandbox', '--package-path', str(checkout), 'dump-package']))
    for target in manifest.get('targets', []):
        if target.get('type') == 'binary' and target.get('url'):
            result.append({'url': target['url'], 'checksum': target.get('checksum'), 'target': target['name'], 'origin': origin, 'package': manifest['name']})
print(json.dumps(result))
"""


BINARY_CACHE_SCRIPT = r"""
import json, pathlib, shutil, sys
home = pathlib.Path(sys.argv[1]); entries = json.loads(sys.argv[2])
for cache in (home / 'Library/Caches/org.swift.swiftpm/artifacts', home / '.cache/org.swift.swiftpm/artifacts'):
    cache.mkdir(parents=True, exist_ok=True)
    for entry in entries: shutil.copyfile(entry['file'], cache / entry['key'])
"""


INDEX_CACHE_SCRIPT = r"""
import json, os, pathlib, subprocess, sys
scratch = pathlib.Path(sys.argv[1]); entries = json.loads(sys.argv[2]); scratch.mkdir(parents=True, exist_ok=True)
state = scratch / 'workspace-state.json'
data = json.loads(state.read_text()) if state.exists() else {'version': 7, 'object': {'dependencies': [], 'artifacts': [], 'prebuilts': []}}
for entry in entries:
    destination = scratch / 'artifacts' / entry['identity'] / entry['target']
    destination.mkdir(parents=True)
    subprocess.run(['/usr/bin/ditto', '-x', '-k', entry['file'], str(destination)], check=True)
    artifacts = []; count = 0
    for directory, dirs, files in os.walk(destination, followlinks=False):
        count += len(dirs) + len(files)
        if count > 200000: raise ValueError('artifact entry bound')
        for name in list(dirs):
            if name.endswith(('.xcframework', '.artifactbundle')):
                artifacts.append(pathlib.Path(directory) / name); dirs.remove(name)
    if len(artifacts) != 1: raise ValueError('ambiguous indexed binary artifact')
    path = artifacts[0]
    package = {'identity': entry['identity'], 'kind': entry['packageKind'], 'location': entry['origin'], 'name': entry['package']}
    artifact = {'packageRef': package, 'targetName': entry['target'], 'source': {'type': 'remote', 'url': entry['url'], 'checksum': entry['checksum']}, 'path': str(path), 'kind': {'xcframework' if path.name.endswith('.xcframework') else 'artifactsArchive': {}}}
    data['object']['artifacts'] = [old for old in data['object']['artifacts'] if (old['packageRef']['identity'], old['targetName']) != (entry['identity'], entry['target'])] + [artifact]
state.write_text(json.dumps(data))
"""


def hydrate_binary_packages(principal, root, worktree, mirrors, scratch=None):
    code, output = principal.run(['/usr/bin/python3', '-I', '-c', BINARY_DISCOVERY_SCRIPT, str(root), str(worktree), str(principal.home), json.dumps(mirrors)], root)
    if code:
        raise PrepareFailure('package-hydration-failed')
    declarations = json.loads(output)
    if not isinstance(declarations, list) or len(declarations) > 128:
        raise ValueError('invalid binary package discovery')
    if not declarations:
        return False
    cache = root / 'packages'
    cache.chmod(0o755)  # collector-owned; source profile grants no writes here
    staged = cache / 'binary-cache'
    staged.mkdir()
    entries, indexed = [], []
    try:
        for index, declaration in enumerate(declarations):
            if not isinstance(declaration, dict) or set(declaration) != {'url', 'checksum', 'target', 'origin', 'package'}:
                raise ValueError('invalid binary dependency declaration')
            url = artifact_url(declaration['url'])
            checksum = declaration['checksum']
            if not isinstance(checksum, str) or not re.fullmatch('[0-9a-fA-F]{64}', checksum):
                raise ValueError('binary dependency requires SHA-256')
            archive = staged / (str(index) + '.zip')
            download_artifact({'url': declaration['url'], 'sha256': checksum, 'maxBytes': 200 * 1024 * 1024}, archive)
            if url.path.lower().endswith('.artifactbundleindex'):
                if archive.stat().st_size > 1024 * 1024:
                    raise ValueError('binary artifact index too large')
                index_data = json.loads(archive.read_text(encoding='utf-8'))
                code, target = principal.run(['/usr/bin/swift', '-print-target-info'], root)
                if code:
                    raise ValueError('cannot identify binary artifact host triple')
                target = json.loads(target)['target']
                triples = {target.get('triple'), target.get('unversionedTriple')}
                choices = index_data.get('archives', []) if isinstance(index_data, dict) else []
                if not isinstance(choices, list) or len(choices) > 128:
                    raise ValueError('invalid binary artifact index')
                selected = next((entry for entry in choices if isinstance(entry, dict)
                    and isinstance(entry.get('fileName'), str) and entry['fileName'].lower().endswith('.zip')
                    and isinstance(entry.get('supportedTriples'), list) and all(isinstance(triple, str) for triple in entry['supportedTriples'])
                    and any(triple in triples for triple in entry['supportedTriples'])), None)
                if selected is None or not isinstance(selected.get('checksum'), str) or not re.fullmatch('[0-9a-fA-F]{64}', selected['checksum']):
                    raise ValueError('binary artifact index has no pinned compatible archive')
                repo_path(selected['fileName'], allow_factory=True)
                selected_url = urllib.parse.urljoin(declaration['url'], selected['fileName'])
                artifact_url(selected_url)
                selected_archive = staged / (str(index) + '-selected.zip')
                download_artifact({'url': selected_url, 'sha256': selected['checksum'], 'maxBytes': 200 * 1024 * 1024}, selected_archive)
                validate_package_zip(selected_archive)
                selected_archive.chmod(0o444)
                origin = declaration['origin']
                if origin is None:
                    identity, package_kind, origin = worktree.name.lower(), 'root', str(worktree)
                elif re.fullmatch(r'[a-z0-9][a-z0-9-]*\.[a-z0-9][a-z0-9-]*', origin):
                    identity, package_kind = origin, 'registry'
                else:
                    origin_url = artifact_url(origin)
                    identity, package_kind = origin_url.path.rsplit('/', 1)[-1].removesuffix('.git').lower(), 'remoteSourceControl'
                if not isinstance(declaration['target'], str) or '/' in declaration['target'] or len(declaration['target']) > 300:
                    raise ValueError('invalid indexed artifact target')
                repo_path(identity, allow_factory=True); repo_path(declaration['target'], allow_factory=True)
                indexed.append(dict(declaration, file=str(selected_archive), identity=identity, packageKind=package_kind, origin=origin))
            elif url.path.lower().endswith('.zip'):
                validate_package_zip(archive)
                entries.append({'file': str(archive), 'key': re.sub(r'[^A-Za-z0-9_]', '_', declaration['url'])})
            else:
                raise ValueError('unsupported binary artifact URL format')
            archive.chmod(0o444)
    finally:
        staged.chmod(0o555)
        cache.chmod(0o555)
    code, _ = principal.run(['/usr/bin/python3', '-I', '-c', BINARY_CACHE_SCRIPT, str(principal.home), json.dumps(entries)], root)
    if code:
        raise PrepareFailure('package-hydration-failed')
    if indexed:
        scratch = scratch or root / ('derived-' + os.environ.get('SIDE', 'base')) / 'SourcePackages'
        code, _ = principal.run(['/usr/bin/python3', '-I', '-c', INDEX_CACHE_SCRIPT, str(scratch), json.dumps(indexed)], root)
        if code:
            raise PrepareFailure('package-hydration-failed')
    return True


def package_resolution_failure(output):
    # Classification only: remote/model/compiler prose is never transported as
    # an authoritative diagnostic. Native trust refusal remains actionable.
    if any(word in output.lower() for word in ('signature', 'signing', 'not signed', 'untrusted certificate')):
        return 'package-signature-refused'
    return 'package-hydration-failed'


class CleanupFailure(RuntimeError):
    pass


class PrepareFailure(ValueError):
    def __init__(self, code, step=None):
        super().__init__(code)
        self.code, self.step = code, step


def run_prepare(principal, worktree, recipe, artifacts):
    def expand(text):
        return re.sub(r'\$\{([^}]*)\}', lambda m: str(worktree) if m[1] == 'worktree' else artifacts[m[1][9:]], text)
    log = ''
    for index, step in enumerate(recipe['steps']):
        cwd = worktree if step['workingDirectory'] in ('', '.', './') else source_path(worktree, step['workingDirectory'].rstrip('/'), allow_factory=True)
        if not cwd.is_dir():
            raise PrepareFailure('prepare-step-failed', index)
        try:
            code, output = principal.run([expand(a) for a in step['argv']], cwd,
                extra_environment={key: expand(value) for key, value in step['environment'].items()},
                timeout=remaining(step['timeoutSeconds']))
        except (ValueError, OSError, subprocess.SubprocessError) as error:
            raise PrepareFailure('prepare-step-timeout', index) from error
        log += output + '\n'
        if len(log.encode('utf-8')) > RESULT_LIMIT:
            raise PrepareFailure('prepare-step-failed', index)
        if code:
            raise PrepareFailure('prepare-step-failed', index)
    return log


def validate_test_environment(values):
    if not isinstance(values, dict) or len(values) > 32:
        raise ValueError('test_env: at most 32 variables')
    reserved = set('PATH HOME TMPDIR TMP TEMP USER LOGNAME SHELL IFS ENV BASH_ENV PS4 SHELLOPTS BASHOPTS CDPATH PWD OLDPWD ZDOTDIR DEVELOPER_DIR SDKROOT XCODE_XCCONFIG_FILE CFFIXED_USER_HOME MSBUILDDISABLENODEREUSE DOTNET_CLI_USE_MSBUILD_SERVER USESHAREDCOMPILATION'.split())
    prefixes = ('DYLD_', 'LD_', 'ANTHROPIC_', 'OPENAI_', 'ROUTER_', 'FACTORY_', 'GITHUB_', 'ACTIONS_', 'RUNNER_', 'TEST_RUNNER_', 'BASH_FUNC_')
    secrets = ('TOKEN', 'SECRET', 'PASSWORD', 'PASSWD', 'CREDENTIAL', 'AUTH', 'API_KEY', 'PRIVATE_KEY', 'ACCESS_KEY')
    size = 0
    for key, value in values.items():
        if not isinstance(key, str) or not re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]{0,127}', key) or key.upper() in reserved or key.upper().startswith(prefixes) or any(s in key.upper() for s in secrets):
            raise ValueError('test_env.' + str(key) + ': forbidden name')
        if not isinstance(value, str):
            raise ValueError('test_env.' + key + ': expected string')
        size += len(key.encode('utf-8')) + len(value.encode('utf-8'))
        valid = bool(re.fullmatch(r'(?:(?:/usr/bin|/bin|/usr/local/bin|/opt/homebrew/bin)/[A-Za-z0-9_+.-]+|[A-Za-z0-9 _.,:+@%=-]*)', value))
        if value.startswith('${worktree}/'):
            path = value[len('${worktree}/'):]
            valid = bool(re.fullmatch(r'[A-Za-z0-9_. /-]+', path)) and all(p not in ('', '.', '..') for p in path.split('/'))
        if size > 8192 or len(value) > 1024 or any(ord(c) < 32 or ord(c) > 126 for c in value) or '__CURRENT_VALUE__' in value or not valid or value.endswith(('/.', '/..')):
            raise ValueError('test_env.' + key + ': forbidden value or bounds')
    return values


TRUSTED_EXECUTABLE_ROOTS = ('/bin', '/usr/bin', '/usr/local/bin', '/usr/local/libexec', '/usr/local/Cellar', '/usr/local/share/dotnet', '/opt/homebrew/bin', '/opt/homebrew/Cellar', '/opt/homebrew/libexec', '/opt/homebrew/Library/Homebrew', '/System/Cryptexes/App/usr/bin', '/System/Volumes/Preboot/Cryptexes/OS/usr/bin')


def resolve_test_environment(values, worktree, canonical_executable=None, trusted_executable_roots=TRUSTED_EXECUTABLE_ROOTS):
    validate_test_environment(values)
    result = {}
    root = worktree.resolve()
    for key, value in values.items():
        expanded = value.replace('${worktree}', str(root))
        if value.startswith('${worktree}/'):
            probe = root
            for part in value[len('${worktree}/'):].split('/'):
                probe = probe / part
                if probe.is_symlink():
                    raise ValueError('test_env.' + key + ': symbolic link')
            if not pathlib.Path(expanded).resolve().is_relative_to(root):
                raise ValueError('test_env.' + key + ': outside workspace')
        elif expanded.startswith('/'):
            try:
                canonical = pathlib.Path(canonical_executable(expanded) if canonical_executable else pathlib.Path(expanded).resolve(strict=True))
                if not any(str(canonical).startswith(trusted.rstrip('/') + '/') for trusted in trusted_executable_roots):
                    raise ValueError('test_env.' + key + ': executable resolves outside trusted system/tool installation roots')
                if not canonical.is_file() or not os.access(canonical, os.X_OK):
                    raise ValueError('test_env.' + key + ': executable unavailable')
                expanded = str(canonical)  # forward the verified destination, never a mutable owner-path indirection
            except (OSError, RuntimeError) as error:
                raise ValueError('test_env.' + key + ': executable cannot be resolved and verified') from error
        result['TEST_RUNNER_' + key] = expanded
    return result


def parse_execution(text, request):
    if len(text.encode('utf-8')) > EXECUTION_LIMIT:
        raise ValueError('execution descriptor exceeds 48 KiB')
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError('duplicate execution property')
            result[key] = value
        return result
    value = json.loads(text, object_pairs_hook=unique)
    keys = {'side', 'commit', 'overlayFrom', 'overlayPaths', 'deletePaths', 'replacements', 'builds'}
    if not isinstance(value, dict) or not keys <= set(value) or set(value) - keys - ({'prepare', 'sourceRepository', 'testEnvironment'} if globals().get('GUI_CONFIG') is not None else {'prepare', 'testEnvironment'}) or value['side'] not in ('base', 'head'):
        raise ValueError('invalid execution descriptor')
    if value['commit'] != request[value['side'].upper()]:
        raise ValueError('execution commit does not match its side')
    if value['overlayFrom'] is not None and value['overlayFrom'] not in (request['BASE'], request['HEAD']):
        raise ValueError('unknown execution overlay revision')
    for field in ('overlayPaths', 'deletePaths'):
        paths = value[field]
        if not isinstance(paths, list) or len(paths) > 2000:
            raise ValueError('invalid execution path list')
        for path in paths:
            repo_path(path)
        if len(paths) != len(set(paths)):
            raise ValueError('duplicate execution path')
    if value['overlayPaths'] and value['overlayFrom'] is None:
        raise ValueError('execution overlay has no source revision')
    if not isinstance(value['replacements'], dict) or len(value['replacements']) > 2000:
        raise ValueError('invalid execution replacements')
    for path, content in value['replacements'].items():
        repo_path(path)
        if not isinstance(content, str):
            raise ValueError('invalid execution replacement text')
        content.encode('utf-8', errors='strict')
    builds = value['builds']
    if not isinstance(builds, list) or not 1 <= len(builds) <= 100:
        raise ValueError('invalid execution builds')
    seen = set()
    for build in builds:
        if not isinstance(build, dict) or set(build) != ({'flag', 'container', 'scheme', 'tests', 'platform'} if globals().get('GUI_CONFIG') is not None else {'flag', 'container', 'scheme', 'tests'}):
            raise ValueError('invalid execution build')
        if globals().get('GUI_CONFIG') is not None and build['platform'] not in ('macOS', 'iOS'):
            raise ValueError('invalid GUI build platform')
        if build['flag'] not in ('-project', '-workspace'):
            raise ValueError('invalid execution container flag')
        container = repo_path(build['container'])
        if not container.endswith('.xcodeproj' if build['flag'] == '-project' else '.xcworkspace'):
            raise ValueError('invalid execution container')
        scheme = build['scheme']
        if (not isinstance(scheme, str) or not scheme or len(scheme) > 300
                or scheme.startswith('-') or any(ord(c) < 32 or ord(c) == 127 for c in scheme)):
            raise ValueError('invalid execution scheme')
        tests = build['tests']
        if not isinstance(tests, list) or not tests:
            raise ValueError('invalid execution tests')
        for test in tests:
            # Swift Testing includes nested suite names, parameter labels and
            # Unicode identifiers; XCTest/UI identifiers share the target prefix.
            if (not isinstance(test, str) or len(test) > 1000 or len(test.split('/')) < 2
                    or any(not p or p.startswith('-') or p in ('.', '..') for p in test.split('/'))
                    or any(ord(c) < 32 or ord(c) == 127 for c in test) or test in seen):
                raise ValueError('invalid or duplicate execution test')
            seen.add(test)
            if len(seen) > 200:
                raise ValueError('too many execution tests')
    validate_test_environment(value.get('testEnvironment', {}))
    if value.get('prepare') is not None:
        validate_prepare(value['prepare'])
    return value


def source_path(root, relative, allow_factory=False):
    # All mutations precede the first principal launch. Archives contain regular
    # files/directories only; check again rather than following any link.
    path = root
    for part in repo_path(relative, allow_factory=allow_factory).split('/'):
        path = path / part
        try:
            node = path.lstat()
        except FileNotFoundError:
            continue
        if not (stat.S_ISDIR(node.st_mode) or stat.S_ISREG(node.st_mode)):
            raise ValueError('unsafe execution source path')
    return path


def execution_sides(execution):
    # A side needs the opposite archive only when its overlay copies files from the other revision. Same-revision overlays,
    # replacements, deletions and the required side itself never consult it, so an unused archive is neither unpacked nor read.
    sides = [execution['side']]
    if execution['overlayPaths'] and execution['overlayFrom'] != execution['commit']:
        sides.append('head' if execution['side'] == 'base' else 'base')
    return sides


def apply_execution(root, execution):
    target = root / execution['side']
    for relative in execution['deletePaths']:
        path = source_path(target, relative)
        if path.exists():
            if not path.is_file():
                raise ValueError('execution deletion is not a file')
            path.unlink()
    source_side = execution['side'] if execution['overlayFrom'] == execution['commit'] else ('head' if execution['side'] == 'base' else 'base')
    for relative in execution['overlayPaths']:
        source = source_path(root / source_side, relative)
        if not source.is_file():
            raise ValueError('execution overlay is not a regular file')
        destination = source_path(target, relative)
        destination.parent.mkdir(parents=True, exist_ok=True)
        if source != destination:
            shutil.copyfile(source, destination)
    for relative, content in execution['replacements'].items():
        path = source_path(target, relative)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding='utf-8')


def execution_json(payload):
    text = json.dumps(payload, ensure_ascii=True)
    if len(text.encode('utf-8')) > RESULT_LIMIT:
        raise ValueError('execution result exceeds 2 MiB; incomplete evidence refused')
    return text


def write_execution_result(payload):
    pathlib.Path('result.json').write_text(execution_json(payload), encoding='utf-8')


class RemoteWorkspace:
    def __enter__(self):
        self.root = pathlib.Path(tempfile.mkdtemp(prefix='factory-ios-', dir='/tmp'))
        self.claimed = False
        return self

    def handoff(self):
        # No TemporaryDirectory finalizer may traverse a tree source owns.
        self.claimed = True

    def __exit__(self, *error):
        if not self.claimed:
            shutil.rmtree(self.root)
        # Once handed off, Principal.close is the ONLY cleanup authority. A
        # refusal leaves the disposable VM workspace intact and publishes none.


def execution_main(request, text):
    execution = parse_execution(text, request)
    result = {'commit': execution['commit'], 'status': 'unused', 'root': None, 'log': '', 'files': []}
    payload = {'version': 1, 'nonce': request['NONCE'], 'base': request['BASE'], 'head': request['HEAD'],
               'side': request['SIDE'], 'environment': ENVIRONMENT,
               'executionHash': hashlib.sha256(text.encode('utf-8')).hexdigest(), 'execution': result}
    if execution['side'] != request['SIDE']:
        write_execution_result(payload)
        return
    setup_failure = None
    with RemoteWorkspace() as workspace:
        root = workspace.root
        for side in execution_sides(execution):
            unpack(pathlib.Path('sources') / (side + '.zip'), root / side)
        # Fetch/cache declared data before any source principal can run. No
        # recipe or source helper is imported/evaluated by this collector.
        recipe = execution.get('prepare')
        apply_execution(root, execution)
        cwd = root / execution['side']
        result['root'] = str(cwd)
        test_environment = resolve_test_environment(execution.get('testEnvironment', {}), cwd)
        try:
            artifacts = prepare_artifacts(root, recipe) if recipe is not None else {}
        except (ValueError, OSError, subprocess.SubprocessError):
            setup_failure = PrepareFailure('artifact-fetch-failed', 0)
        if setup_failure is None:
            workspace.handoff()
            try:
                principal = Principal(root)
            except PrepareFailure as error:
                setup_failure = error
        if setup_failure is None:
            registry = None
            try:
                principal.share(cwd)
                derived = root / ('derived-' + execution['side'])
                derived.mkdir()
                principal.share(derived)
                if recipe is not None:
                    try:
                        # Successful prepare output is not compiler evidence. Keep
                        # execution.log BUILD-only, exactly as the local runner does.
                        run_prepare(principal, cwd, recipe, artifacts)
                    except PrepareFailure:
                        raise
                    except (ValueError, OSError, subprocess.SubprocessError) as error:
                        raise PrepareFailure('prepare-step-failed', 0) from error
                try:
                    # Prepare may generate lockfiles; only a bounded no-follow read
                    # by the restricted principal crosses back as untrusted data.
                    data = package_snapshot(principal, cwd)
                    mirrors = hydrate_packages(root, cwd, data)
                    has_registry = (root / 'packages/registry-plan.json').is_file()
                    if has_registry and globals().get('GUI_CONFIG') is None:
                        try:
                            registry = RegistryMirror(root)
                            principal.registry_endpoints(registry.endpoints)
                        except OSError as error:
                            raise PrepareFailure('package-ipc-unavailable') from error
                    if mirrors or registry:
                        install_package_mirrors(principal, mirrors, registry) if registry else install_package_mirrors(principal, mirrors)
                    has_binary = hydrate_binary_packages(principal, root, cwd, mirrors, derived / 'SourcePackages')
                except (ValueError, OSError, subprocess.SubprocessError) as error:
                    raise PrepareFailure(error.code if isinstance(error, PrepareFailure) else 'package-hydration-failed') from error
                code, version = principal.run(['/usr/bin/xcodebuild', '-version'], root)
                if code or version.strip() != 'Xcode ' + ENVIRONMENT['xcode'] + '\nBuild version ' + ENVIRONMENT['build']:
                    raise ValueError('pinned Xcode version unavailable')
                device = None
                if any(build.get('platform', 'iOS') == 'iOS' for build in execution['builds']):
                    code, inventory = principal.run(['/usr/bin/xcrun', 'simctl', 'list', '-j'], root)
                    if code:
                        raise ValueError('simulator unavailable')
                    device_type, runtime = simulator(json.loads(inventory))
                    code, device = principal.run(['/usr/bin/xcrun', 'simctl', 'create', 'factory-' + request['NONCE'], device_type, runtime], root)
                    device = device.strip()
                    if code or not re.fullmatch('[0-9A-Fa-f-]{36}', device):
                        raise ValueError('simulator creation failed')
                def arguments(build):
                    return ['/usr/bin/xcodebuild', build['flag'], build['container'], '-scheme', build['scheme'],
                            '-destination', 'platform=macOS' if build.get('platform') == 'macOS' else 'platform=iOS Simulator,id=' + device,
                            '-derivedDataPath', str(derived), '-clonedSourcePackagesDirPath', str(derived / 'SourcePackages'),
                            *(['CODE_SIGN_IDENTITY=-', 'DEVELOPMENT_TEAM=', 'PROVISIONING_PROFILE=', 'PROVISIONING_PROFILE_SPECIFIER='] if globals().get('GUI_CONFIG') is not None else ['CODE_SIGNING_ALLOWED=NO']),
                            '-disableAutomaticPackageResolution', '-skipPackageUpdates',
                            '-skipMacroValidation', '-skipPackagePluginValidation']
                if mirrors or registry or has_binary or has_registry:
                    for build in execution['builds']:
                        # Only this restricted principal evaluates Package.swift.
                        # Local mirrors contain the exact lockfile commits.
                        # Internet and ungranted local endpoints stay denied;
                        # missing transitive pins or native trust refusals fail closed.
                        resolve = [arg for arg in arguments(build) if arg != '-disableAutomaticPackageResolution']
                        code, output = principal.run(resolve + ['-resolvePackageDependencies', '-onlyUsePackageVersionsFromResolvedFile'], cwd)
                        if code:
                            raise PrepareFailure(package_resolution_failure(output))
                result['status'] = 'ran'
                # Like the local runner, complete every build before any test. A later target's failed build must not
                # leave partial executed cases that mask compiler attribution/retries for the other targets.
                for build in execution['builds']:
                    code, log = principal.run(arguments(build) + ['build-for-testing'], cwd)
                    # Standardize only the workspace's macOS /private/tmp alias;
                    # C# can attribute paths without access to this remote filesystem.
                    result['log'] += log.replace('/private' + str(cwd), str(cwd)) + '\n'
                    execution_json(payload)
                    if code:
                        result['status'] = 'build-failed' if code == 65 else 'error'
                        break
                for build in execution['builds'] if result['status'] == 'ran' else []:
                    common = arguments(build)
                    for test in build['tests']:
                        name = 'xcode-test-' + hashlib.sha256(test.encode('utf-8')).hexdigest()[:16]
                        bundle = derived / (name + '.xcresult')
                        code, output = principal.run(common + ['test-without-building', '-only-testing:' + test,
                            '-parallel-testing-enabled', 'NO', '-resultBundlePath', str(bundle)], cwd, extra_environment=resolve_test_environment(execution.get('testEnvironment', {}), cwd))
                        # Principal.run proves uid quiescence even on exit 0 before
                        # the collector reads anything. Never execute a PR helper.
                        report_code, report = principal.run(['/usr/bin/xcrun', 'xcresulttool', 'get', 'test-results', 'tests', '--path', str(bundle)], root)
                        result['files'].extend([{'name': name + '.out', 'content': output, 'exitCode': code},
                                                {'name': name + '.json', 'content': report, 'exitCode': report_code}])
                        execution_json(payload)
            except PrepareFailure as error:
                result.update(status='prepare-failed', failure={'code': error.code, 'step': error.step})
            finally:
                try:
                    principal.close()
                finally:
                    if registry is not None:
                        registry.close()
        if setup_failure is not None:
            result.update(status='prepare-failed', failure={'code': setup_failure.code, 'step': None})
    write_execution_result(payload)

def main():
    global EXECUTION_DEADLINE
    # Legacy two-sided fixture requests predate the transported budget. General
    # executions require it; a missing value must never silently become 600 s.
    budget = os.environ.get('EXECUTION_BUDGET_SECONDS', '')
    if os.environ.get('EXECUTION') or budget:
        EXECUTION_DEADLINE = execution_budget(budget, os.environ.get('EXECUTION_DEADLINE_UNIX_SECONDS', ''))
    request = {k: os.environ[k] for k in ('NONCE', 'BASE', 'HEAD', 'TESTS', 'SIDE')}
    if request['SIDE'] not in ('base', 'head'):
        raise ValueError('invalid execution side')
    if not re.fullmatch('[0-9a-f]{32}', request['NONCE']) or not all(re.fullmatch('[0-9a-f]{40}', request[k]) for k in ('BASE', 'HEAD')):
        raise ValueError('invalid request identity')
    execution = os.environ.get('EXECUTION', '')
    if execution:
        execution_main(request, execution)
        return
    tests = json.loads(request['TESTS'])
    if request['SIDE'] not in ('base', 'head'):
        raise ValueError('invalid execution side')
    if not re.fullmatch('[0-9a-f]{32}', request['NONCE']) or not all(re.fullmatch('[0-9a-f]{40}', request[k]) for k in ('BASE', 'HEAD')):
        raise ValueError('invalid request identity')
    if not isinstance(tests, list) or not 1 <= len(tests) <= 20 or len(set(tests)) != len(tests) or not all(re.fullmatch(r'TallyTests/TallyTests/test[A-Za-z0-9_]{1,100}', t) for t in tests):
        raise ValueError('invalid test identifiers')
    with RemoteWorkspace() as workspace:
        root = workspace.root
        for side in ('base', 'head'):
            unpack(pathlib.Path('sources') / (side + '.zip'), root / side)
        # Legacy fixture protocol cannot represent a commit-owned recipe. Never silently ignore one.
        for side in ('base', 'head'):
            if (root / side / '.factory/prepare.yaml').exists():
                raise ValueError('test_env: legacy fixture execution requires the validated recipe descriptor')
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
        workspace.handoff()
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
                    report_code, report = principal.run(['/usr/bin/xcrun', 'xcresulttool', 'get', 'test-results', 'tests', '--path', str(bundle)], root)
                    if report_code:
                        raise ValueError('unreadable xcresult')
                    row[side] = case_result(json.loads(report), row['test'], code)
            payload = {'version': 1, 'nonce': request['NONCE'], 'base': request['BASE'], 'head': request['HEAD'], 'side': request['SIDE'], 'environment': ENVIRONMENT, 'tests': rows}
        finally:
            principal.close()
        pathlib.Path('result.json').write_text(json.dumps(payload))


def interrupt(signum, frame):
    # Actions cancellation must unwind the source command and principal finally
    # blocks. Repeated TERM/INT cannot interrupt their bounded uid teardown.
    signal.signal(signal.SIGINT, signal.SIG_IGN)
    signal.signal(signal.SIGTERM, signal.SIG_IGN)
    raise KeyboardInterrupt('remote execution cancelled')


if __name__ == '__main__':
    signal.signal(signal.SIGINT, interrupt)
    signal.signal(signal.SIGTERM, interrupt)
    main()
