# Inserted before the sealed hosted runner's entrypoint; never loaded from source.
# Dedicated trusted CI, NOT containment of malicious code. Persistent lwrunner
# account/GUI state is accepted. Collector credentials remain a different uid's.
import fcntl
import uuid

GUI_HELPER = pathlib.Path('/usr/local/libexec/dark-factory/factory-gui-exec')
GUI_LEASE = None
GUI_CONFIG = None


def gui_operation(operation, root=None, extra=None, cleanup=False):
    args = ['/usr/bin/sudo', '-n', str(GUI_HELPER), operation]
    if root is not None:
        args.append(str(root))
    if extra is not None:
        args.append(str(extra))
    code, output = command(args, '/', timeout=40 if cleanup else None, cleanup=cleanup)
    if code:
        raise ValueError('protected GUI operation refused')
    return output


class GuiLease:
    def __init__(self, config):
        self.directory = pathlib.Path(config['stateDirectory'])
        self.stream = None
        self.marker = self.directory / 'active.json'
        self.root = None
        self.cleaned = False

    def __enter__(self):
        self.stream = open(self.directory / 'machine.lock', 'a+b')
        os.chmod(self.directory / 'machine.lock', 0o600)
        try:
            while True:
                try:
                    fcntl.flock(self.stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                    break
                except BlockingIOError:
                    time.sleep(min(0.1, remaining()))
            if self.marker.exists():
                # An OS-released lock with a marker is a crashed job, never
                # permission to overlap it. Reconcile through the same bounded
                # fixed-uid cleanup before reusing the machine, automatically.
                if self.marker.stat().st_size > 1024:
                    raise ValueError('invalid GUI cleanup journal')
                previous = json.loads(self.marker.read_text())
                if set(previous) != {'root', 'devices', 'deviceIntents'}:
                    raise ValueError('invalid GUI cleanup journal')
                if not isinstance(previous['devices'], list) or len(previous['devices']) > 10 or any(not isinstance(d, str) or not re.fullmatch('[0-9A-Fa-f-]{36}', d) for d in previous['devices']):
                    raise ValueError('invalid GUI device journal')
                intents = previous['deviceIntents']
                if (not isinstance(intents, list) or len(intents) > 10 or len(set(intents)) != len(intents)
                        or any(not isinstance(name, str) or not re.fullmatch('factory-[0-9a-f]{32}', name) for name in intents)):
                    raise ValueError('invalid GUI device creation journal')
                if previous['root'] is None and (previous['devices'] or intents):
                    raise ValueError('GUI device creation has no workspace authority')
                if previous['root'] is not None:
                    old = pathlib.Path(previous['root'])
                    # Intent survives a create whose answer/UDID never arrived.
                    # Stop the old commands, then reconcile the exact nonce-name
                    # in the fixed source user's device set before admitting work.
                    for name in intents:
                        gui_operation('device-reconcile', old, name, cleanup=True)
                    for device in previous['devices']:
                        gui_operation('device-delete', old, device, cleanup=True)
                    gui_operation('remove', old, cleanup=True)
                self.marker.unlink()
            self.record(None)
            return self
        except BaseException:
            self.stream.close()
            self.stream = None
            raise

    def record(self, root, devices=None, device_intents=None):
        temporary = self.directory / ('journal-' + uuid.uuid4().hex)
        fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, 'w') as output:
            json.dump({'root': str(root) if root is not None else None, 'devices': devices or [], 'deviceIntents': device_intents or []}, output)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, self.marker)
        self.root = root

    def __exit__(self, *failure):
        try:
            if self.root is None or self.cleaned:
                self.marker.unlink(missing_ok=True)
            # Failure leaves a durable quarantine. Next invocation automatically
            # retries bounded cleanup; it never silently starts a replacement.
        finally:
            if self.stream is not None:
                self.stream.close()


class GuiWorkspace(RemoteWorkspace):
    def __enter__(self):
        workspace = super().__enter__()
        workspace.root = workspace.root.resolve()  # macOS /tmp is /private/tmp; share checks compare physical paths
        GUI_LEASE.record(workspace.root)
        return workspace

    def __exit__(self, *failure):
        super().__exit__(*failure)
        if not self.claimed:
            GUI_LEASE.cleaned = True


RemoteWorkspace = GuiWorkspace


class GuiPrincipal:
    user = 'lwrunner'

    def __init__(self, root):
        self.root = root
        self.uid = GUI_CONFIG['sourceUid']
        self.devices = []
        self.device_intents = []
        self.home = root / 'home'
        self.worktree = root / os.environ['SIDE']
        self.home.mkdir(mode=0o700)
        (self.home / 'tmp').mkdir(mode=0o700)
        root.chmod(0o755)
        GUI_LEASE.record(root)
        self.share(self.home)
        self.env = {'PATH': '/usr/bin:/bin:/usr/sbin:/sbin', 'HOME': str(self.home),
                    'TMPDIR': str(self.home / 'tmp'), 'DEVELOPER_DIR': GUI_CONFIG['developer'], 'xcrun_nocache': '1'}
        code, manager = self.run(['/bin/launchctl', 'managername'], root)
        if code or manager.strip() != 'Aqua':
            raise ValueError('existing lwrunner GUI login is unavailable')
        code, identity = self.run(['/usr/bin/id', '-u'], root)
        if code or identity.strip() != str(self.uid):
            raise ValueError('GUI execution did not adopt lwrunner identity')

    def share(self, path):
        gui_operation('share', self.root, path)

    def registry_endpoints(self, endpoints):
        # Exact prefetched mirrors/native validation are reused. Trusted CI is
        # allowed network access; this is not an advertised network sandbox.
        if any(host not in ('127.0.0.1', '::1') or not bounded_integer(port, 1, 65535) for host, port in endpoints):
            raise ValueError('invalid GUI package endpoint')

    def run(self, args, cwd, extra_environment=None, timeout=None):
        environment = dict(self.env)
        if extra_environment:
            environment.update(extra_environment)  # preserve declared recipe env; S5 owns extension
        if args[:3] == ['/usr/bin/xcrun', 'simctl', 'create'] and len(args) > 3 and re.fullmatch('factory-[0-9a-f]{32}', args[3]):
            self.device_intents.append(args[3])
            GUI_LEASE.record(self.root, self.devices, self.device_intents)  # durable BEFORE creation can take effect
        request = self.root / ('command-' + uuid.uuid4().hex + '.json')
        fd = os.open(request, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        try:
            with os.fdopen(fd, 'w') as output:
                json.dump({'argv': args, 'cwd': str(cwd), 'environment': environment}, output)
            try:
                result = command(['/usr/bin/sudo', '-n', str(GUI_HELPER), 'run', str(self.root), str(request)], '/', timeout=timeout)
            except BaseException:
                gui_operation('sweep', self.root, cleanup=True)
                raise
            if args[:3] == ['/usr/bin/xcrun', 'simctl', 'create'] and result[0] == 0:
                device = result[1].strip()
                if not re.fullmatch('[0-9A-Fa-f-]{36}', device):
                    raise ValueError('unreadable created GUI device')
                self.devices.append(device)
                GUI_LEASE.record(self.root, self.devices, self.device_intents)
            return result
        finally:
            request.unlink(missing_ok=True)

    def close(self):
        for name in self.device_intents:
            gui_operation('device-reconcile', self.root, name, cleanup=True)
        for device in self.devices:
            gui_operation('device-delete', self.root, device, cleanup=True)
        gui_operation('remove', self.root, cleanup=True)
        GUI_LEASE.cleaned = True


# The one execution engine still constructs overlays, hydration and native
# validation, builds all targets, and reads each xcresult separately. Only the
# execution principal and platform destination change.
Principal = GuiPrincipal
_gui_original_parse = parse_execution
_gui_original_write = write_execution_result
_gui_original_main = main


def parse_execution(text, request):
    value = _gui_original_parse(text, request)
    repo = value.get('sourceRepository')
    if repo != os.environ['SOURCE_REPOSITORY'] or repo.lower() not in [r.lower() for r in GUI_CONFIG['trustedRepositories']]:
        raise ValueError('GUI source repository is not allowlisted')
    return value


def write_execution_result(payload):
    payload['guiConfigurationHash'] = GUI_CONFIG['hash']
    _gui_original_write(payload)


def main():
    global GUI_CONFIG, GUI_LEASE, ENVIRONMENT, EXECUTION_DEADLINE
    # Verify the installed root-owned helper before ever invoking it. Parent
    # directories must be root-owned and closed to group/other writers too.
    for path in [GUI_HELPER, *GUI_HELPER.parents]:
        node = path.lstat()
        if stat.S_ISLNK(node.st_mode) or node.st_uid != 0 or node.st_mode & 0o022:
            raise ValueError('unprotected installed GUI helper')
        code, listing = command(['/bin/ls', '-led', str(path)], '/')
        if code:
            raise ValueError('unreadable installed GUI helper permissions')
        for line in listing.splitlines()[1:]:
            if ' deny ' in line:
                continue
            entry = re.fullmatch(r'\s*[0-9]+: \S+ (?:inherited )?allow (.+)', line)
            allowed = {'read', 'list', 'search', 'execute', 'readattr', 'readextattr', 'readsecurity', 'file_inherit', 'directory_inherit', 'only_inherit', 'limit_inherit'}
            if entry is None or set(entry[1].split(',')) - allowed:
                raise ValueError('installed GUI helper has a writable or unknown ACL')
    if hashlib.sha256(GUI_HELPER.read_bytes()).hexdigest() != 'GUI_HELPER_HASH':
        raise ValueError('stale installed GUI helper')
    EXECUTION_DEADLINE = execution_budget(os.environ['EXECUTION_BUDGET_SECONDS'], os.environ['EXECUTION_DEADLINE_UNIX_SECONDS'])
    code, collector_manager = command(['/bin/launchctl', 'managername'], '/')
    if code or collector_manager.strip() not in ('Background', 'System'):
        raise ValueError('credentialed GUI collector must run in a non-GUI service domain')
    GUI_CONFIG = json.loads(gui_operation('check'))
    if GUI_CONFIG['collectorRepository'].lower() != os.environ.get('GITHUB_REPOSITORY', '').lower() or GUI_CONFIG['runnerName'] != os.environ.get('RUNNER_NAME'):
        raise ValueError('actual GUI collector registration does not match this workflow job')
    source = os.environ.get('SOURCE_REPOSITORY')
    if not isinstance(source, str) or source.lower() not in [repo.lower() for repo in GUI_CONFIG['trustedRepositories']]:
        raise ValueError('GUI source repository is not in the protected allowlist')
    ENVIRONMENT = {key: GUI_CONFIG[key] for key in ('runtime', 'deviceType', 'xcode', 'build')}
    pathlib.Path('result.json').unlink(missing_ok=True)
    with GuiLease(GUI_CONFIG) as lease:
        GUI_LEASE = lease
        _gui_original_main()
