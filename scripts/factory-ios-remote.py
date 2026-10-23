# Compiled into the sealed workflow. Never loaded from the target source tree.
# S1 deliberately supports the sandbox's e2e-ios/Tally fixture, not arbitrary
# project layouts. S2 owns discovery/routing. Unprovable evidence fails closed.
import hashlib
import http.client
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
        '(subpath "/private/etc") (subpath "/private/var/db") (subpath ' + quoted(root / 'artifacts') + ') '
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

    def run(self, args, cwd, extra_environment=None, timeout=COMMAND_TIMEOUT):
        environment = dict(self.env, **(extra_environment or {}))
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
                connection.settimeout(30)
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
    return PinnedHttps(url.hostname, 443, timeout=30, context=ssl.create_default_context())


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
    deadline = time.monotonic() + 600
    for redirect in range(6):
        url = artifact_url(url_text)
        connection = artifact_connection(url)
        try:
            connection.request('GET', urllib.parse.urlunsplit(('', '', url.path or '/', url.query, '')),
                               headers={'Accept-Encoding': 'identity'})
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


class PrepareFailure(ValueError):
    pass


def run_prepare(principal, worktree, recipe, artifacts):
    def expand(text):
        return re.sub(r'\$\{([^}]*)\}', lambda m: str(worktree) if m[1] == 'worktree' else artifacts[m[1][9:]], text)
    log = ''
    for step in recipe['steps']:
        cwd = worktree if step['workingDirectory'] in ('', '.', './') else source_path(worktree, step['workingDirectory'].rstrip('/'), allow_factory=True)
        if not cwd.is_dir():
            raise ValueError('prepare working directory does not exist')
        code, output = principal.run([expand(a) for a in step['argv']], cwd,
            extra_environment={key: expand(value) for key, value in step['environment'].items()},
            timeout=step['timeoutSeconds'] or COMMAND_TIMEOUT)
        log += output + '\n'
        if len(log.encode('utf-8')) > RESULT_LIMIT:
            raise ValueError('prepare log exceeds result bound')
        if code:
            raise ValueError('prepare step failed: ' + step['name'] + '\n' + log)
    return log


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
    if not isinstance(value, dict) or not keys <= set(value) or set(value) - keys - {'prepare'} or value['side'] not in ('base', 'head'):
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
        if not isinstance(build, dict) or set(build) != {'flag', 'container', 'scheme', 'tests'}:
            raise ValueError('invalid execution build')
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


def execution_main(request, text):
    execution = parse_execution(text, request)
    result = {'commit': execution['commit'], 'status': 'unused', 'root': None, 'log': '', 'files': []}
    payload = {'version': 1, 'nonce': request['NONCE'], 'base': request['BASE'], 'head': request['HEAD'],
               'side': request['SIDE'], 'environment': ENVIRONMENT,
               'executionHash': hashlib.sha256(text.encode('utf-8')).hexdigest(), 'execution': result}
    if execution['side'] != request['SIDE']:
        write_execution_result(payload)
        return
    with tempfile.TemporaryDirectory(prefix='factory-ios-', dir='/tmp') as directory:
        root = pathlib.Path(directory)
        for side in ('base', 'head'):
            unpack(pathlib.Path('sources') / (side + '.zip'), root / side)
        # Fetch/cache declared data before any source principal can run. No
        # recipe or source helper is imported/evaluated by this collector.
        recipe = execution.get('prepare')
        artifacts = prepare_artifacts(root, recipe) if recipe is not None else {}
        apply_execution(root, execution)
        cwd = root / execution['side']
        result['root'] = str(cwd)
        principal = Principal(root)
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
                except (ValueError, OSError, subprocess.SubprocessError) as error:
                    raise PrepareFailure(str(error)) from error
            code, version = principal.run(['/usr/bin/xcodebuild', '-version'], root)
            if code or version.strip() != 'Xcode ' + ENVIRONMENT['xcode'] + '\nBuild version ' + ENVIRONMENT['build']:
                raise ValueError('pinned Xcode version unavailable')
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
                        '-destination', 'platform=iOS Simulator,id=' + device,
                        '-derivedDataPath', str(derived), 'CODE_SIGNING_ALLOWED=NO',
                        '-disableAutomaticPackageResolution', '-skipPackageUpdates']
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
                        '-parallel-testing-enabled', 'NO', '-resultBundlePath', str(bundle)], cwd)
                    # Principal.run proves uid quiescence even on exit 0 before
                    # the collector reads anything. Never execute a PR helper.
                    report_code, report = command(['/usr/bin/xcrun', 'xcresulttool', 'get', 'test-results', 'tests', '--path', str(bundle)], root)
                    result['files'].extend([{'name': name + '.out', 'content': output, 'exitCode': code},
                                            {'name': name + '.json', 'content': report, 'exitCode': report_code}])
                    execution_json(payload)
        except PrepareFailure as error:
            result['status'] = 'error'
            result['prepareLog'] = str(error)
        finally:
            principal.close()
        write_execution_result(payload)


def main():
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
