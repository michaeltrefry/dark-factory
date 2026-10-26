#!/usr/bin/python3 -I
"""Explicit local macOS installer. Never fetches a token/archive or runs gh/sudo.
Authority is supplied by the owner's separate, read-only verified preflight.
All runner code runs with collector uid/gid and a new allowlisted environment.
"""
import argparse
import contextlib
import fcntl
import hashlib
import json
import os
import pathlib
import plistlib
import pwd
import grp
import re
import secrets
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import time

HELPER = pathlib.Path('/usr/local/libexec/dark-factory/factory-gui-exec')
SOURCE_JOB = pathlib.Path('/usr/local/libexec/dark-factory/factory-gui-source-job')
CONFIG = pathlib.Path('/etc/dark-factory/gui-executor.json')
SUDOERS = pathlib.Path('/etc/sudoers.d/dark-factory-gui-collector')
DAEMON = pathlib.Path('/Library/LaunchDaemons/net.dark-factory.gui-collector.plist')
STATE = pathlib.Path('/var/db/dark-factory/gui-collector')
LABEL = 'net.dark-factory.gui-collector'
CUSTOM_LABEL = 'factory-gui-collector'
MIGRATED_WORKFLOWS = {'.github/workflows/' + name for name in (
    'inference-adapter.yml', 'live-raw-acceptance.yml', 'rust-catalog-tests.yml', 's47-procedural-ui-tour.yml')}
AUTH_FILES = ('.runner', '.credentials', '.credentials_rsaparams', '.service')
HEX = re.compile(r'[a-f0-9]{64}')
MAX_REGISTRATION_ATTEMPTS = 2
REGISTRATION_RETRY_WINDOW = 3600
ABSENCE_FRESH_SECONDS = 300
GATE_SCRIPT = ("import os,sys\n"
               "try:\n    byte=os.read(int(sys.argv[1]),1)\nexcept OSError:\n    byte=b''\n"
               "if byte != b'g':\n    os._exit(125)\n"
               "os.execv(sys.argv[2], sys.argv[2:])\n")
ACCOUNT_FIELDS = (('UniqueID', lambda plan: str(plan['uid'])),
                  ('PrimaryGroupID', lambda plan: str(plan['uid'])),
                  ('NFSHomeDirectory', lambda plan: plan['home']),
                  ('UserShell', lambda plan: '/usr/bin/false'),
                  ('Password', lambda plan: '*'),
                  ('IsHidden', lambda plan: '1'))


def _absent_diagnostic(stderr, label, domain_text):
    """True only for launchctl's literal "no such service in this domain" message.

    Apple's launchctl prints "Bad request." on its own line followed by
    ``Could not find service "<label>" in domain for <domain_text>`` and exits 113.
    Any extra or differing text — permission failures, unknown domains, other services —
    means the state is unknown and must never be treated as absence.
    """
    lines = [line.strip() for line in stderr.splitlines() if line.strip()]
    expected = 'Could not find service "%s" in domain for %s' % (label, domain_text)
    return bool(lines) and lines[-1] == expected and all(line == 'Bad request.' for line in lines[:-1])


class Refused(Exception):
    pass


class ReconciliationRequired(Refused):
    """A retryable unstarted intent needs the owner gateway's fresh absence proof."""


def require(condition, reason):
    if not condition:
        raise Refused(reason)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def bootstrap_label(plan_sha256):
    require(HEX.fullmatch(plan_sha256) is not None, 'invalid bootstrap plan binding')
    return 'factory-gui-bootstrap-' + plan_sha256[:16]


def canonical(path):
    path = pathlib.Path(path)
    require(path.is_absolute() and '..' not in path.parts and path.resolve() == path,
            'path is relative, noncanonical or contains a symlink')
    return path


@contextlib.contextmanager
def handoff(path, owner_uid, host, private=False):
    """Read an owner-to-root handoff without following file or parent symlinks."""
    path = canonical(path)
    parent_fd = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    fd = None
    try:
        parent = os.fstat(parent_fd)
        require(parent.st_uid in {0, owner_uid} and not parent.st_mode & 0o022
                and host.acl_free(path.parent), 'handoff parent owner/mode/ACL refused')
        if parent.st_uid != 0:
            require(stat.S_IMODE(parent.st_mode) == 0o700, 'owner handoff parent must be private 0700')
        fd = os.open(path.name, os.O_RDONLY | os.O_NONBLOCK | os.O_NOFOLLOW, dir_fd=parent_fd)
        node = os.fstat(fd)
        require(stat.S_ISREG(node.st_mode) and node.st_uid in {0, owner_uid}
                and not node.st_mode & 0o022 and host.acl_free(path), 'handoff file owner/mode/ACL refused')
        if private:
            require(stat.S_IMODE(node.st_mode) == 0o600, 'private handoff file must be 0600')
        with os.fdopen(fd, 'rb') as source:
            fd = None
            yield source
    finally:
        if fd is not None:
            os.close(fd)
        os.close(parent_fd)


def handoff_bytes(path, owner_uid, host, limit):
    with handoff(path, owner_uid, host, private=True) as source:
        data = source.read(limit + 1)
    require(len(data) <= limit, 'handoff input over bounds')
    return data


def file_digest(path):
    checksum = hashlib.sha256()
    with pathlib.Path(path).open('rb') as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b''):
            checksum.update(chunk)
    return checksum.hexdigest()


class GatedProcess:
    """A registration child that cannot exec (or POST) until the gate byte is written."""

    def __init__(self, process, write_fd, pid, start_identity):
        self.process, self.write_fd, self.pid, self.start_identity = process, write_fd, pid, start_identity

    def release(self):
        os.write(self.write_fd, b'g')
        os.close(self.write_fd)

    def abort(self):
        os.close(self.write_fd)

    def wait(self):
        return self.process.wait(timeout=120)


class Host:
    """Only this adapter runs machine commands; tests replace it completely."""

    def env(self, home, token):
        env = {'PATH': '/usr/bin:/bin:/usr/sbin:/sbin', 'HOME': str(home or '/var/empty'),
               'LANG': 'en_US.UTF-8'}
        if token:
            # Native actions/runner CommandSettings protocol. Never --token or PAT.
            env['ACTIONS_RUNNER_INPUT_TOKEN'] = token
        return env

    def process_identity(self, pid, uid):
        """Exact uid+lstart for a live pid, None only for ps's confirmed no-such-process.

        Any other ps failure (permission, unknown code, malformed output) is ambiguous and
        refused: an unreadable process must never authorize a dead-child retry."""
        require(type(pid) is int and pid > 1 and type(uid) is int, 'invalid process identity request')
        result = subprocess.run(['/bin/ps', '-p', str(pid), '-o', 'uid=', '-o', 'lstart='],
                                stdin=subprocess.DEVNULL, capture_output=True, timeout=30)
        output = result.stdout.decode(errors='replace')
        errors = result.stderr.decode(errors='replace')
        if result.returncode != 0:
            if result.returncode == 1 and not output.strip() and not errors.strip():
                return None
            raise Refused('process state is unknown (ps refused or answered unexpectedly)')
        parts = ' '.join(output.split()).split(' ', 1)
        require(len(parts) == 2 and parts[0] == str(uid) and parts[1].strip(),
                'process identity is ambiguous')
        return parts[0] + ' ' + parts[1]

    def start_gated(self, argv, *, uid, gid, home, cwd='/', token=None):
        read_fd, write_fd = os.pipe()
        def demote():
            os.setgroups([gid])
            os.setgid(gid)
            os.setuid(uid)
            os.umask(0o077)
        try:
            process = subprocess.Popen([sys.executable, '-I', '-c', GATE_SCRIPT, str(read_fd), *argv],
                                       cwd=cwd, env=self.env(home, token), stdin=subprocess.DEVNULL,
                                       stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                       pass_fds=(read_fd,), preexec_fn=demote)
        finally:
            os.close(read_fd)
        try:
            identity = self.process_identity(process.pid, uid)
            require(isinstance(identity, str) and identity, 'child start identity is unverifiable')
        except Exception:
            # Close the gate (the child exits on EOF) and wait a bounded time; never signal it.
            os.close(write_fd)
            try:
                process.wait(timeout=120)
            except subprocess.TimeoutExpired:
                pass
            raise
        return GatedProcess(process, write_fd, process.pid, identity)

    def run(self, argv, *, uid=None, gid=None, home=None, cwd='/', token=None):
        env = self.env(home, token)
        def demote():
            os.setgroups([gid])
            os.setgid(gid)
            os.setuid(uid)
            os.umask(0o077)
        result = subprocess.run(argv, cwd=cwd, env=env, stdin=subprocess.DEVNULL,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                preexec_fn=demote if uid is not None else None, timeout=120)
        # Never copy runner output/errors to the receipt or terminal (may contain secrets).
        require(result.returncode == 0, 'command failed: ' + pathlib.Path(argv[0]).name)
        return result.stdout.decode().strip()

    def acl_free(self, path):
        return len(self.run(['/bin/ls', '-led', str(path)]).splitlines()) == 1

    def account(self, name):
        try:
            value = pwd.getpwnam(name)
            return dict(uid=value.pw_uid, gid=value.pw_gid, home=value.pw_dir, shell=value.pw_shell)
        except KeyError:
            return None

    def ids(self):
        return {x.pw_uid for x in pwd.getpwall()}, {x.gr_gid for x in grp.getgrall()}

    def group(self, name):
        try:
            return grp.getgrnam(name).gr_gid
        except KeyError:
            return None

    def group_state(self, name):
        try:
            group = grp.getgrnam(name)
            return group.gr_gid, set(group.gr_mem)
        except KeyError:
            return None

    def uid_free(self, uid):
        users, groups = self.ids()
        return uid not in users and uid not in groups

    def create_group(self, name, uid):
        self.run(['/usr/sbin/dseditgroup', '-o', 'create', '-i', str(uid), name])

    def account_fields(self, name):
        try:
            listing = self.run(['/usr/bin/dscl', '.', '-read', '/Users/' + name])
        except Refused:
            return None
        values = {}
        for line in listing.splitlines():
            key, separator, value = line.partition(': ')
            if separator and key in dict(ACCOUNT_FIELDS):
                values[key] = value
        return values

    def write_account_field(self, name, key, value):
        self.run(['/usr/bin/dscl', '.', '-create', '/Users/' + name, key, value])

    def service_absent(self, label):
        # Absent means launchctl proved absence in every domain this label could be loaded in.
        # A missing/unknown domain, wrong-domain presence or any other error is not absence.
        require(isinstance(label, str) and label and '"' not in label and '\n' not in label,
                'invalid service label')
        domain = subprocess.run(['/bin/launchctl', 'print', 'gui/502'], stdin=subprocess.DEVNULL,
                                capture_output=True, timeout=120)
        require(domain.returncode == 0, 'lwrunner GUI launchd domain is not readable; service state unknown')
        # launchctl's actual "not found" diagnostic names the quoted service and its domain
        # separately; the full target string never appears in the message. Anything else —
        # permission denied, an unknown domain, a wrong uid/kind or a present service — is not
        # absence. See _absent_diagnostic for the exact accepted native forms.
        for target, domain_text in (('gui/502/' + label, 'user gui: 502'),
                                    ('user/502/' + label, 'uid: 502'),
                                    ('system/' + label, 'system')):
            result = subprocess.run(['/bin/launchctl', 'print', target],
                                    stdin=subprocess.DEVNULL, capture_output=True, timeout=120)
            if result.returncode == 0:
                return False
            error = result.stderr.decode(errors='replace')
            if not _absent_diagnostic(error, label, domain_text):
                return False
        return True

    def verify_account(self, name, uid, home):
        require(self.account(name) == dict(uid=uid, gid=uid, home=str(home), shell='/usr/bin/false'),
                'existing collector account differs from the reserved identity')
        group = grp.getgrnam(name)
        require(group.gr_gid == uid and not set(group.gr_mem) - {name}, 'collector group is not private')
        require(self.run(['/usr/bin/id', '-G', name]).split() == [str(uid)],
                'collector has supplementary groups')
        require(self.run(['/usr/bin/dscl', '.', '-read', '/Users/' + name, 'Password']) == 'Password: *',
                'collector account password is not disabled')

    def start(self, plist):
        # bootstrap reports EEXIST on resume; print is read-only and disambiguates.
        try:
            self.run(['/bin/launchctl', 'print', 'system/' + LABEL])
        except Refused:
            self.run(['/bin/launchctl', 'bootstrap', 'system', str(plist)])
        self.run(['/bin/launchctl', 'kickstart', 'system/' + LABEL])

    def stop(self, label):
        # Stop exactly this service, never the Aqua domain or user processes.
        # An ambiguous/missing service is an operator reconciliation, not proof it stopped.
        self.run(['/bin/launchctl', 'bootout', 'gui/502/' + label])


class Installer:
    def __init__(self, plan, host=None, root=None, raw_plan_sha256=None, inputs=None):
        self.p = plan
        self.raw_plan_sha256 = raw_plan_sha256
        self.inputs = inputs or {}
        self.host = host or Host()
        # Test-only filesystem mapping, never a CLI option or environment override.
        self.root = pathlib.Path(root) if root else None
        self.receipt = {'version': 1, 'stages': [], 'status': 'planned'}

    def path(self, path):
        if self.root:
            return self.root / str(path).lstrip('/')
        path = pathlib.Path(path)
        # macOS's two fixed system aliases only; never resolve an arbitrary planted link.
        if path.parts[1:2] in (('var',), ('etc',)):
            return pathlib.Path('/private') / path.relative_to('/')
        return path

    def secure(self, path, uid=0, mode=None):
        path = canonical(path)
        node = path.lstat()
        require(node.st_uid == (os.getuid() if self.root else uid), 'unexpected path owner')
        require(stat.S_ISDIR(node.st_mode) or stat.S_ISREG(node.st_mode), 'not a regular file or directory')
        require(not node.st_mode & 0o022 and self.host.acl_free(path), 'path has writable mode or ACL')
        if mode is not None:
            require(stat.S_IMODE(node.st_mode) == mode, 'path has wrong private mode')
        return path

    def mkdir(self, path, uid=0):
        if path.exists() or path.is_symlink():
            return self.secure(path, uid, 0o700)
        # Every existing ancestor is root-owned and non-writable, except the collector HOME.
        self.parents(path.parent)
        path.mkdir(mode=0o700)
        if not self.root:
            os.chown(path, uid, self.p['uid'] if uid else 0)
        self.host.run(['/bin/chmod', '-N', str(path)])
        return self.secure(path, uid, 0o700)

    def parents(self, path):
        canonical(path)
        if self.root and path == self.root:
            return
        if not path.exists():
            self.parents(path.parent)
            path.mkdir(mode=0o755)
        node = path.lstat()
        allowed = {os.getuid()} if self.root else {0}
        if not self.root and path.is_relative_to(pathlib.Path(self.p['home'])):
            allowed.add(self.p['uid'])
        require(node.st_uid in allowed and stat.S_ISDIR(node.st_mode) and not node.st_mode & 0o022
                and self.host.acl_free(path), 'unsafe installation ancestor')
        if path.parent != path and (not self.root or path != self.root):
            self.parents(path.parent)

    def put(self, path, data, mode, uid=0):
        if uid == 502:
            require(path.parent == self.path(self.p['old']['runnerRoot']) or
                    (path == self.path(self.p['old']['servicePlist']) and
                     path.is_relative_to(self.path('/Users/lwrunner'))), 'rollback path is outside exact old runner')
            self.secure(path.parent, 502)
        else:
            self.parents(path.parent)
        if path.exists() or path.is_symlink():
            self.secure(path, uid)
            require(path.read_bytes() == data, 'existing installed file differs; refusing overwrite')
            require(stat.S_IMODE(path.stat().st_mode) == mode, 'existing installed mode differs')
            return
        fd, temporary = tempfile.mkstemp(prefix='.install-', dir=path.parent)
        try:
            with os.fdopen(fd, 'wb') as output:
                output.write(data)
                output.flush()
                os.fsync(output.fileno())
            os.chmod(temporary, mode)
            if not self.root:
                os.chown(temporary, uid, self.p['uid'] if uid else 0)
            os.replace(temporary, path)
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)
        self.secure(path, uid, mode)

    def save(self):
        data = json.dumps(self.receipt, sort_keys=True, indent=2).encode() + b'\n'
        self.atomic_state('receipt.json', data)

    def atomic_state(self, name, data):
        require(name in ('receipt.json', 'authority.json', 'plan.json'), 'unknown protected state input')
        path = self.path(STATE / name)
        if path.exists() or path.is_symlink():
            self.secure(path, 0, 0o600)
        fd, temporary = tempfile.mkstemp(dir=path.parent, prefix='.receipt-')
        with os.fdopen(fd, 'wb') as output:
            output.write(data)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)

    def stage(self, name):
        if name not in self.receipt['stages']:
            self.receipt['stages'].append(name)
        self.save()

    def validate(self, authority):
        p = self.p
        require(set(p) == {'version', 'user', 'uid', 'home', 'runnerName', 'archive', 'archiveSha256',
                           'helper', 'helperSha256', 'sourceJob', 'sourceJobSha256', 'configuration', 'old'},
                'unknown or missing setup plan field')
        require(p['version'] == 1 and re.fullmatch(r'_factorygui[a-z0-9_]*', p['user']), 'invalid service account name')
        require(type(p['uid']) is int and p['uid'] >= 500 and p['uid'] != 502, 'collector UID must be free >=500, never lwrunner 502')
        require(p['home'] == '/Users/' + p['user'], 'collector HOME must be its dedicated /Users directory')
        canonical(self.path(p['home']))
        require(re.fullmatch(r'factory-gui-[a-z0-9-]{8,60}', p['runnerName']) is not None,
                'use one unique new runner name, never lensworks-guest or --replace')
        require(HEX.fullmatch(p['archiveSha256']) is not None, 'invalid archive checksum')
        archive = canonical(self.inputs.get('archive', p['archive']))
        require(archive.is_file() and file_digest(archive) == p['archiveSha256'], 'runner archive checksum mismatch')
        helper = canonical(self.inputs.get('helper', p['helper']))
        require(helper.is_file() and HEX.fullmatch(p['helperSha256']) and file_digest(helper) == p['helperSha256'],
                'trusted helper checksum mismatch')
        source_job = canonical(self.inputs.get('sourceJob', p['sourceJob']))
        require(source_job.is_file() and HEX.fullmatch(p['sourceJobSha256']) and
                file_digest(source_job) == p['sourceJobSha256'], 'trusted source client checksum mismatch')
        c = p['configuration']
        require(c['collectorUid'] == p['uid'] and c['sourceUid'] == 502 and c['sourceUser'] == 'lwrunner'
                and c['runnerName'] == p['runnerName'], 'configuration identity mismatch')
        require(c['runnerRoot'] == p['home'] + '/actions-runner' and c['stateDirectory'] == p['home'] + '/gui-state',
                'runner and state must be under the private HOME')
        require(set(c) == {'collectorUid', 'sourceUid', 'sourceUser', 'runnerRoot', 'runnerName',
                          'collectorRepository', 'stateDirectory', 'retiredRunnerRoots', 'developer',
                          'xcode', 'build', 'runtime', 'deviceType', 'trustedRepositories'}, 'invalid helper configuration fields')
        require(re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', c['collectorRepository']) is not None,
                'invalid central repository')
        repositories = c['trustedRepositories']
        require(isinstance(repositories, list) and 0 < len(repositories) <= 100 and all(
            isinstance(repo, str) and re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repo) for repo in repositories),
                'invalid trusted source repository allowlist')
        require(c['collectorRepository'].lower() not in {repo.lower() for repo in repositories},
                'collector must be registered separately from every source repository; PR jobs can bypass source wrappers')
        require(self.host.account('lwrunner')['uid'] == 502, 'existing lwrunner identity differs')
        binding = self.raw_plan_sha256 or digest(json.dumps(p, sort_keys=True).encode())
        receipt_path = self.path(STATE / 'receipt.json')
        authorized = False
        if receipt_path.exists() or receipt_path.is_symlink():
            self.secure(receipt_path, 0, 0o600)
            previous = json.loads(receipt_path.read_text())
            require(previous.get('planSha256') == binding and previous.get('uid') == p['uid'],
                    'existing installation reservation belongs to another plan/identity')
            authorized = True
        account = self.host.account(p['user'])
        fields = self.host.account_fields(p['user'])
        group = self.host.group_state(p['user'])
        uids, gids = self.host.ids()
        if group is not None or fields is not None or account is not None:
            require(authorized, 'existing group/account has no installer reservation receipt')
        if group is not None:
            require(p['uid'] in gids and group[0] == p['uid'] and not group[1] - {p['user']},
                    'group name/UUID belongs to an unrelated group')
        # Only a durable reservation may adopt the exact intent this plan authored. Anything else
        # (an unrelated account, group, HOME or UID) is refused rather than clobbered.
        if account is None:
            if group is None:
                require(p['uid'] not in uids and p['uid'] not in gids, 'reserved UID/GID already in use')
            require(authorized or p['uid'] not in uids, 'reserved UID belongs to another account')
            if self.path(p['home']).exists() or self.path(p['home']).is_symlink():
                require(authorized, 'refusing to adopt an existing HOME')
        else:
            require(account['uid'] == p['uid'] and account['home'] == p['home'], 'existing account differs from reservation')
            for key, expected in ACCOUNT_FIELDS:
                if key in (fields or {}):
                    require(fields[key] == expected(p), 'existing account attribute conflicts with reservation: ' + key)
            home = self.path(p['home'])
            if home.exists() or home.is_symlink():
                self.secure(home, p['uid'], 0o700)
        require(set(authority) <= {'version', 'kind', 'planSha256', 'expiresAt', 'appPermissionsVerified',
                                   'newNameAbsent', 'migratedWorkflows', 'online', 'removedOldRunnerId',
                                   'registrationAbsence'},
                'unknown authority fields; secret-bearing input must never be persisted')
        require(authority.get('version', 1) == 1 and authority.get('kind', 'gui-collector-authority-v1') ==
                'gui-collector-authority-v1', 'unknown authority receipt kind/version')
        online = authority.get('online') or {}
        require(not online or set(online) == {'runnerId', 'runnerName', 'status', 'labels'}, 'unknown online authority fields')
        absence = authority.get('registrationAbsence')
        require(absence is None or set(absence) == {'attemptNonce', 'runnerName', 'checkedAt', 'status',
                                                   'appPermissionsVerified'},
                'unknown registration absence fields')
        require(authority.get('planSha256') == binding, 'authority does not bind this exact plan')
        require(type(authority.get('expiresAt')) in (int, float) and time.time() < authority['expiresAt'] <= time.time() + 3600,
                'authority receipt expired or not short lived')
        require(authority.get('appPermissionsVerified') is True, 'owner-side App permission preflight required')
        workflows = authority.get('migratedWorkflows', [])
        require(len(workflows) == 4 and {w['path'] for w in workflows} == MIGRATED_WORKFLOWS
                and all(set(w) == {'path', 'sha256', 'collectorProtocol'} for w in workflows)
                and all(w.get('collectorProtocol') is True and HEX.fullmatch(w.get('sha256', '')) for w in workflows),
                'all four existing LensWorks workflows must have verified immutable migration evidence')
        require(authority.get('newNameAbsent') is True, 'new runner name was not proven unique')
        old = p['old']
        require(set(old) == {'runnerRoot', 'runnerId', 'runnerName', 'serviceLabel', 'servicePlist', 'files'}, 'invalid old inventory')
        require(old['runnerName'] != p['runnerName'] and type(old['runnerId']) is int and old['runnerId'] > 0,
                'old registration must be a different exact ID/name')
        require(old['runnerRoot'] in c['retiredRunnerRoots'] and old['runnerRoot'].startswith('/Users/lwrunner/'),
                'old runner must be an inventoried lwrunner directory')
        require(re.fullmatch(r'actions\.runner\.[A-Za-z0-9_.-]+', old['serviceLabel']) is not None
                and old['servicePlist'] in [prefix + old['serviceLabel'] + '.plist' for prefix in
                    ('/Library/LaunchAgents/', '/Users/lwrunner/Library/LaunchAgents/')], 'unscoped old service')
        require(set(old['files']) == set(AUTH_FILES) | {'servicePlist'}
                and all(HEX.fullmatch(x) for x in old['files'].values()), 'old credential/service hashes required')
        return binding

    def unpack(self, runner):
        # Archive is untrusted DATA, in a fresh empty staging directory only. No tar subprocess.
        with tarfile.open(self.inputs.get('archive', self.p['archive']), 'r:gz') as archive:
            members = archive.getmembers()
            require(len(members) <= 100000 and sum(m.size for m in members) <= 4 * 1024**3, 'archive over bounds')
            names = set()
            for member in members:
                name = pathlib.PurePosixPath(member.name)
                require(not name.is_absolute() and '..' not in name.parts and member.name not in names
                        and (member.isfile() or member.isdir()), 'archive link, duplicate or unsafe member')
                names.add(member.name)
            for member in members:
                target = runner / member.name
                if member.isdir():
                    target.mkdir(parents=True, exist_ok=True, mode=0o700)
                else:
                    target.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
                    with archive.extractfile(member) as source, target.open('xb') as output:
                        shutil.copyfileobj(source, output)
                    target.chmod(0o700 if member.mode & 0o111 else 0o600)
            for target in [runner, *runner.rglob('*')]:
                if not self.root:
                    os.chown(target, self.p['uid'], self.p['uid'])
        require((runner / 'config.sh').is_file() and (runner / 'run.sh').is_file(), 'runner archive missing entrypoints')

    def registration(self, runner):
        for name in AUTH_FILES[:3]:
            self.secure(runner / name, self.p['uid'], 0o600)
        info = json.loads((runner / '.runner').read_text())
        require(info.get('agentName') == self.p['runnerName'] and type(info.get('agentId')) is int
                and info['agentId'] > 0 and str(info.get('gitHubUrl', '')).rstrip('/') ==
                'https://github.com/' + self.p['configuration']['collectorRepository'], 'new registration metadata mismatch')
        return info['agentId']

    def registration_absence(self, authority):
        """Accept a retry only when the previous attempt provably never started and the
        owner-side gateway freshly proved the exact new name absent for that attempt."""
        p = self.p
        attempt = self.receipt.get('registrationAttempt')
        require(isinstance(attempt, dict), 'no persisted registration attempt to reconcile')
        state = attempt.get('state')
        require(state in ('reserved', 'unreleased'),
                'registration was released or its state is unknown; nothing is retried')
        if state == 'reserved':
            require(attempt.get('pid') is None and attempt.get('startIdentity') is None,
                    'a registration process exists although the intent is reserved')
        else:
            pid, identity = attempt.get('pid'), attempt.get('startIdentity')
            require(type(pid) is int and isinstance(identity, str) and identity,
                    'unreleased registration has no verifiable child identity')
            require(self.host.process_identity(pid, self.p['uid']) != identity,
                    'the gated registration child is still running')
        def blocked(reason):
            # A retryable unstarted intent is reported to the engine as a structured challenge
            # (exit 0), never a generic root failure it might blindly retry.
            raise ReconciliationRequired(reason)
        absence = authority.get('registrationAbsence')
        if not (absence is not None and absence.get('status') == 'absent'
                and absence.get('appPermissionsVerified') is True):
            blocked('a fresh owner-side proof that the exact new runner name is absent is required')
        if absence.get('runnerName') != p['runnerName']:
            blocked('absence proof is for another runner name')
        if absence.get('attemptNonce') != attempt.get('nonce'):
            blocked('absence proof is not bound to this exact registration attempt')
        checked = absence.get('checkedAt')
        if not (type(checked) in (int, float) and attempt.get('startedAt') <= checked <= time.time()
                and time.time() - checked <= ABSENCE_FRESH_SECONDS):
            blocked('absence proof is stale or predates this registration attempt')
        runner = self.path(p['configuration']['runnerRoot'])
        for name in AUTH_FILES[:3]:
            if (runner / name).exists() or (runner / name).is_symlink():
                blocked('local registration credentials exist although the attempt was never launched')
        return True

    def begin_registration(self, authority, token_reader, runner, home, binding):
        p = self.p
        if 'registration-attempted' in self.receipt['stages']:
            self.registration_absence(authority)
        attempts = self.receipt.get('registrationAttempts', 0)
        require(attempts < MAX_REGISTRATION_ATTEMPTS, 'registration attempt budget is exhausted')
        now = time.time()
        first = self.receipt.setdefault('registrationFirstStartedAt', now)
        require(now <= first + REGISTRATION_RETRY_WINDOW, 'registration retry window has expired')
        self.receipt['registrationAttempts'] = attempts + 1
        self.receipt['registrationAttempt'] = {'nonce': secrets.token_hex(16), 'number': attempts + 1,
                                               'startedAt': now, 'createdAt': now, 'state': 'reserved',
                                               'pid': None, 'startIdentity': None}
        self.save()
        self.stage('registration-attempted')
        token = token_reader()
        require(isinstance(token, str) and re.fullmatch(r'[A-Za-z0-9_\-.]{10,2048}', token),
                'invalid short-lived registration token')
        gate = self.host.start_gated([str(runner / 'config.sh'), '--unattended', '--url',
                                      'https://github.com/' + p['configuration']['collectorRepository'],
                                      '--name', p['runnerName'], '--no-default-labels', '--labels',
                                      bootstrap_label(binding), '--work', '_work', '--disableupdate'],
                                     uid=p['uid'], gid=p['uid'], home=home, cwd=runner, token=token)
        token = None
        if not (isinstance(gate.start_identity, str) and gate.start_identity):
            gate.abort()
            require(False, 'child start identity is unverifiable; registration not released')
        self.receipt['registrationAttempt'].update(state='unreleased', pid=gate.pid,
                                                   startIdentity=gate.start_identity)
        self.save()
        # Durable BEFORE the gate opens: after this point no proof can authorize a retry.
        self.receipt['registrationAttempt']['state'] = 'released'
        self.save()
        gate.release()
        require(gate.wait() == 0, 'registration command failed')

    def registration_status(self, binding):
        """Read-only root challenge for the owner-side gateway."""
        attempt = self.receipt.get('registrationAttempt')
        require('registration-attempted' in self.receipt.get('stages', [])
                and isinstance(attempt, dict), 'no registration reconciliation is pending')
        runner = self.path(self.p['configuration']['runnerRoot'])
        require(not (runner / '.runner').exists(), 'a local registration already exists')
        state = attempt.get('state')
        if state == 'reserved':
            unstarted = attempt.get('pid') is None and attempt.get('startIdentity') is None
        elif state == 'unreleased':
            unstarted = (type(attempt.get('pid')) is int and isinstance(attempt.get('startIdentity'), str)
                         and self.host.process_identity(attempt['pid'], self.p['uid']) != attempt['startIdentity'])
        else:
            unstarted = False
        config = json.dumps(self.p['configuration'], sort_keys=True, indent=2).encode() + b'\n'
        return {'version': 1, 'status': 'registration-reconciliation-required', 'planSha256': binding,
                'runnerName': self.p['runnerName'], 'uid': self.p['uid'],
                'helperSha256': self.p['helperSha256'], 'sourceJobSha256': self.p['sourceJobSha256'],
                'configurationSha256': digest(config), 'attempts': self.receipt.get('registrationAttempts', 0),
                'firstStartedAt': self.receipt.get('registrationFirstStartedAt'),
                'registrationIntent': {'attemptNonce': attempt.get('nonce'),
                                       'createdAt': attempt.get('createdAt'),
                                       'state': state, 'pid': attempt.get('pid'),
                                       'startIdentity': attempt.get('startIdentity'),
                                       'localUnstartedVerified': unstarted}}

    def retire(self, authority):
        old = self.p['old']
        # External removal is explicit owner orchestration, not gh/PAT/root network here.
        removed = authority.get('removedOldRunnerId')
        if removed is not None:
            require(removed == old['runnerId'], 'owner must remove only the exact old registration after new runner online verification')
            require('old-backup-protected' in self.receipt['stages'],
                    'protected backup must precede remote registration removal; reconcile premature deletion')
        backup = self.mkdir(self.path(STATE / 'rollback'))
        oldroot = canonical(self.path(old['runnerRoot']))
        service = canonical(self.path(old['servicePlist']))
        sources = {name: oldroot / name for name in AUTH_FILES}
        sources['servicePlist'] = service
        # Reserve every backup BEFORE touching service/credentials. Already backed files are resumable.
        for name, source in sources.items():
            target = backup / name
            if target.exists():
                self.secure(target, 0, 0o600)
                require(digest(target.read_bytes()) == old['files'][name], 'rollback backup mismatch')
                if source.exists() or source.is_symlink():
                    require(not source.is_symlink() and source.is_file() and digest(source.read_bytes()) == old['files'][name],
                            'old credential/service inventory changed since backup')
            else:
                require(source.is_file() and not source.is_symlink() and digest(source.read_bytes()) == old['files'][name],
                        'old credential/service inventory changed')
                self.put(target, source.read_bytes(), 0o600)
        info = json.loads((backup / '.runner').read_text())
        require(info.get('agentId') == old['runnerId'] and info.get('agentName') == old['runnerName'], 'old registration ID/name mismatch')
        require((backup / '.service').read_text().strip() == old['serviceLabel'], 'old service binding mismatch')
        service_info = plistlib.loads((backup / 'servicePlist').read_bytes())
        require(service_info.get('Label') == old['serviceLabel'] and service_info.get('UserName', 'lwrunner') == 'lwrunner'
                and service_info.get('WorkingDirectory') == old['runnerRoot'], 'old plist identity/path mismatch')
        self.stage('old-backup-protected')
        if removed is None:
            self.receipt['status'] = 'awaiting-owner-exact-old-registration-removal'
            self.save()
            return False
        try:
            if 'old-service-stopped' not in self.receipt['stages']:
                intent = self.receipt.get('retirement')
                if intent is not None:
                    require(intent == {'serviceLabel': old['serviceLabel'], 'domain': 'gui/502',
                                       'userId': 502, 'runnerRoot': old['runnerRoot']},
                            'protected retirement intent does not match this exact service')
                else:
                    self.receipt['retirement'] = {'serviceLabel': old['serviceLabel'], 'domain': 'gui/502',
                                                  'userId': 502, 'runnerRoot': old['runnerRoot']}
                    self.save()
                # Absence of exactly this service in lwrunner's domain is the only success
                # evidence. A crash after bootout can therefore resume; wrong-domain or
                # unknown launchctl errors stay blocked, never "ignored".
                if not self.host.service_absent(old['serviceLabel']):
                    self.host.stop(old['serviceLabel'])
                    require(self.host.service_absent(old['serviceLabel']),
                            'old service is still present (or its state is unknowable) after bootout')
                self.stage('old-service-stopped')
            for name, source in sources.items():
                if source.exists():
                    require(not source.is_symlink() and digest(source.read_bytes()) == old['files'][name], 'old file changed before retirement')
                    source.unlink()
            self.stage('old-retired')
        except Exception:
            # Restore exact bytes locally; GitHub deletion is not reversible by a file restore.
            for name, source in sources.items():
                if not source.exists():
                    owner = 502 if name != 'servicePlist' or source.is_relative_to(self.path('/Users/lwrunner')) else 0
                    self.put(source, (backup / name).read_bytes(), 0o644 if name == 'servicePlist' else 0o600, owner)
            self.receipt['status'] = 'retirement-blocked-local-files-restored-registration-requires-owner'
            self.save()
            raise

    def provision_account(self):
        """Finish the exact reserved identity across crashes, never adopting anything else."""
        p = self.p
        name, uid, home = p['user'], p['uid'], p['home']
        journal = self.receipt.setdefault('account', {'group': 'pending', 'fields': {}})
        group = self.host.group_state(name)
        if group is not None:
            require(group[0] == uid and not group[1] - {name}, 'group name/UUID belongs to an unrelated group')
            journal['group'] = 'done'
        else:
            require(self.host.uid_free(uid), 'reserved UID/GID already belongs to another account')
            journal['group'] = 'pending'
            self.save()
            self.host.create_group(name, uid)
            journal['group'] = 'done'
            self.save()
        fields = self.host.account_fields(name)
        for key, expected in ACCOUNT_FIELDS:
            value = expected(p)
            if fields is not None and key in fields:
                require(fields[key] == expected(p), 'existing account attribute conflicts with reservation: ' + key)
                journal['fields'][key] = 'done'
                continue
            # Intent is durable BEFORE the side effect, so a crash cannot make the record
            # look like an unknown account or leave a field unaccounted for.
            journal['fields'][key] = 'pending'
            self.save()
            self.host.write_account_field(name, key, value)
            journal['fields'][key] = 'done'
            fields = self.host.account_fields(name)
            self.save()
        self.save()

    def apply(self, authority, token_reader):
        # Serialize all local apply/resume operations, including across root processes.
        self.validate(authority)
        self.mkdir(self.path(STATE))
        lock = self.path(STATE / 'setup.lock')
        fd = os.open(lock, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
        try:
            self.secure(lock, 0, 0o600)
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            return self.apply_locked(authority, token_reader)
        finally:
            os.close(fd)

    def apply_locked(self, authority, token_reader):
        binding = self.validate(authority)
        state = self.path(STATE)
        receipt = state / 'receipt.json'
        if receipt.exists():
            self.secure(receipt, 0, 0o600)
            self.receipt = json.loads(receipt.read_text())
            require(self.receipt['planSha256'] == binding, 'another plan already owns this installation')
        else:
            self.receipt.update(planSha256=binding, runnerName=self.p['runnerName'], uid=self.p['uid'])
            self.save()
        self.receipt['bootstrapLabel'] = bootstrap_label(binding)
        for name in ('plan', 'authority'):
            if name + 'Bytes' in self.inputs:
                self.atomic_state(name + '.json', self.inputs[name + 'Bytes'])
        p = self.p
        home = self.path(p['home'])
        self.provision_account()
        self.host.verify_account(p['user'], p['uid'], p['home'])
        self.mkdir(home, p['uid'])
        self.mkdir(self.path(p['configuration']['stateDirectory']), p['uid'])
        self.stage('account-private')
        self.put(self.path(HELPER), pathlib.Path(self.inputs.get('helper', p['helper'])).read_bytes(), 0o755)
        self.put(self.path(SOURCE_JOB), pathlib.Path(self.inputs.get('sourceJob', p['sourceJob'])).read_bytes(), 0o755)
        self.receipt['helperSha256'] = p['helperSha256']
        self.receipt['sourceJobSha256'] = p['sourceJobSha256']
        config = json.dumps(p['configuration'], sort_keys=True, indent=2).encode() + b'\n'
        self.put(self.path(CONFIG), config, 0o600)
        self.receipt['configurationSha256'] = digest(config)
        rule = (p['user'] + ' ALL=(root) NOPASSWD: ' + str(HELPER) + '\n').encode()
        # Validate a temporary sudoers file before publishing the exact fixed-command rule.
        with tempfile.NamedTemporaryFile(dir=state) as check:
            check.write(rule)
            check.flush()
            self.host.run(['/usr/sbin/visudo', '-cf', check.name])
        self.put(self.path(SUDOERS), rule, 0o440)
        self.stage('helper-config-sudoers')
        runner = self.path(p['configuration']['runnerRoot'])
        if not runner.exists():
            staging = pathlib.Path(tempfile.mkdtemp(prefix='.runner-staging-', dir=home))
            try:
                self.unpack(staging)
                os.rename(staging, runner)
            finally:
                if staging.exists():
                    shutil.rmtree(staging)
        self.secure(runner, p['uid'], 0o700)
        self.stage('runner-staged')
        if not (runner / '.runner').exists():
            if 'registration-attempted' not in self.receipt['stages']:
                require(authority.get('registrationAbsence') is None,
                        'unexpected absence proof for a registration that was never attempted')
            self.begin_registration(authority, token_reader, runner, home, binding)
        else:
            require(authority.get('registrationAbsence') is None,
                    'remote absence was proven but a local registration exists: reconcile before retrying')
        runner_id = self.registration(runner)
        self.receipt['runnerId'] = runner_id
        self.stage('registered')
        daemon = plistlib.dumps({'Label': LABEL, 'UserName': p['user'], 'GroupName': p['user'],
                                 'ProgramArguments': [str(pathlib.Path(p['configuration']['runnerRoot']) / 'run.sh')],
                                 'WorkingDirectory': p['configuration']['runnerRoot'], 'RunAtLoad': True, 'KeepAlive': True,
                                 'Umask': 0o077, 'EnvironmentVariables': {'HOME': p['home'], 'PATH': '/usr/bin:/bin:/usr/sbin:/sbin'}})
        self.put(self.path(DAEMON), daemon, 0o644)
        self.host.start(self.path(DAEMON))
        self.stage('daemon-started')
        online = authority.get('online') or {}
        allowed_labels = [[bootstrap_label(binding)]]
        if 'old-retired' in self.receipt['stages']:
            allowed_labels.append([CUSTOM_LABEL])
        if not (online.get('runnerId') == runner_id and online.get('runnerName') == p['runnerName']
                and online.get('status') == 'online' and online.get('labels') in allowed_labels):
            self.receipt['status'] = 'awaiting-owner-online-verification-old-untouched'
            self.save()
            return self.receipt
        self.stage('new-online-verified')
        if 'old-retired' not in self.receipt['stages']:
            if self.retire(authority) is False:
                return self.receipt
        for name in AUTH_FILES:
            old_file = self.path(self.p['old']['runnerRoot']) / name
            require(not old_file.exists() and not old_file.is_symlink(), 'old authentication reappeared after retirement')
        require(not self.path(self.p['old']['servicePlist']).exists(), 'old service reappeared after retirement')
        self.receipt['status'] = 'complete'
        self.save()
        return self.receipt


def inventory(old_root, host=None, root=None):
    """Read only exact old-runner inventory. Never return credential bodies."""
    host = host or Host()
    source = host.account('lwrunner')
    require(source is not None and source['uid'] == 502, 'existing lwrunner identity differs')
    logical = pathlib.Path(old_root)
    require(logical.is_absolute() and logical != pathlib.Path('/Users/lwrunner') and
            logical.is_relative_to('/Users/lwrunner') and '..' not in logical.parts, 'old runner must be inside lwrunner HOME')
    def mapped(path):
        return pathlib.Path(root) / str(path).lstrip('/') if root else pathlib.Path(path)
    def read(path, limit=1024 * 1024):
        physical = canonical(mapped(path))
        parent_fd = os.open(physical.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        fd = None
        try:
            parent = os.fstat(parent_fd)
            owners = {os.getuid()} if root else {0, 502}
            require(parent.st_uid in owners and not parent.st_mode & 0o022 and host.acl_free(physical.parent),
                    'old inventory parent owner/mode/ACL refused')
            fd = os.open(physical.name, os.O_RDONLY | os.O_NONBLOCK | os.O_NOFOLLOW, dir_fd=parent_fd)
            node = os.fstat(fd)
            require(stat.S_ISREG(node.st_mode) and node.st_uid in owners and not node.st_mode & 0o022
                    and host.acl_free(physical), 'old inventory file owner/mode/ACL refused')
            with os.fdopen(fd, 'rb') as file:
                fd = None
                data = file.read(limit + 1)
            require(len(data) <= limit, 'old inventory file over bounds')
            return data
        finally:
            if fd is not None:
                os.close(fd)
            os.close(parent_fd)
    contents = {name: read(logical / name) for name in AUTH_FILES}
    registration = json.loads(contents['.runner'])
    runner_id, runner_name = registration.get('agentId'), registration.get('agentName')
    require(type(runner_id) is int and runner_id > 0 and isinstance(runner_name, str)
            and re.fullmatch(r'[A-Za-z0-9_.-]{1,100}', runner_name), 'old runner metadata invalid')
    repository_match = re.fullmatch(r'https://github\.com/([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)/?',
                                   registration.get('gitHubUrl', ''))
    require(repository_match is not None, 'old registration repository is not known')
    label = contents['.service'].decode().strip()
    require(re.fullmatch(r'actions\.runner\.[A-Za-z0-9_.-]{1,180}', label) is not None, 'old service label invalid')
    services = [pathlib.Path(prefix) / (label + '.plist') for prefix in
                ('/Library/LaunchAgents', '/Users/lwrunner/Library/LaunchAgents')]
    services = [path for path in services if mapped(path).exists() or mapped(path).is_symlink()]
    require(len(services) == 1, 'old service plist is absent or ambiguous')
    service = services[0]
    contents['servicePlist'] = read(service, 65536)
    info = plistlib.loads(contents['servicePlist'])
    require(info.get('Label') == label and info.get('UserName', 'lwrunner') == 'lwrunner' and
            info.get('WorkingDirectory') == str(logical), 'old service plist does not bind lwrunner runner root')
    return {'version': 1, 'status': 'inventory', 'repository': repository_match.group(1),
            'old': {'runnerRoot': str(logical), 'runnerId': runner_id, 'runnerName': runner_name,
                    'serviceLabel': label, 'servicePlist': str(service),
                    'files': {name: digest(data) for name, data in contents.items()}}}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_mutually_exclusive_group(required=True)
    for mode in ('preflight', 'dry-run', 'apply', 'inventory', 'registration-status'):
        modes.add_argument('--' + mode, action='store_true')
    parser.add_argument('--plan')
    parser.add_argument('--authority')
    parser.add_argument('--old-root', default='/Users/lwrunner/actions-runner')
    args = parser.parse_args()
    try:
        if args.inventory:
            require(sys.platform == 'darwin' and os.geteuid() == 0,
                    'inventory requires explicitly authorized root on macOS; reads only and never invokes sudo')
            print(json.dumps(inventory(args.old_root), sort_keys=True))
            return 0
        if args.registration_status:
            require(sys.platform == 'darwin' and os.geteuid() == 0,
                    'registration status requires explicitly authorized root on macOS; reads only')
            require(args.plan is not None and args.authority is not None, 'plan and authority are required')
            owner_uid = int(os.environ.get('SUDO_UID', os.getuid()))
            host = Host()
            plan_bytes = handoff_bytes(args.plan, owner_uid, host, 65536)
            authority_bytes = handoff_bytes(args.authority, owner_uid, host, 65536)
            plan, authority = json.loads(plan_bytes), json.loads(authority_bytes)
            installer = Installer(plan, host, raw_plan_sha256=digest(plan_bytes))
            binding = installer.validate(authority)
            state = installer.path(STATE / 'receipt.json')
            require(state.is_file(), 'no protected installation receipt')
            installer.secure(state, 0, 0o600)
            installer.receipt = json.loads(state.read_text())
            print(json.dumps(installer.registration_status(binding), sort_keys=True))
            return 0
        require(args.plan is not None and args.authority is not None, 'plan and authority are required for setup modes')
        if args.apply:
            require(sys.platform == 'darwin' and os.geteuid() == 0, 'apply requires an explicitly authorized root invocation on macOS; never invokes sudo')
        owner_uid = int(os.environ.get('SUDO_UID', os.getuid())) if args.apply else os.getuid()
        require(owner_uid == 0 or owner_uid >= 500 and owner_uid != 502, 'handoff must be from root or the owner, never lwrunner')
        host = Host()
        plan_bytes = handoff_bytes(args.plan, owner_uid, host, 65536)
        authority_bytes = handoff_bytes(args.authority, owner_uid, host, 65536)
        plan, authority = json.loads(plan_bytes), json.loads(authority_bytes)
        installer = Installer(plan, host, raw_plan_sha256=digest(plan_bytes))
        if args.apply:
            # Only trusted installer code runs as root. Archive/helper are inert bytes here.
            # Staging is outside every downloaded file's directory and root-private.
            with tempfile.TemporaryDirectory(prefix='gui-collector-setup-', dir='/private/var/db') as staging:
                installer.secure(pathlib.Path(staging), 0, 0o700)
                inputs = {'planBytes': plan_bytes, 'authorityBytes': authority_bytes}
                for field, limit in (('helper', 1024 * 1024), ('sourceJob', 1024 * 1024), ('archive', 1024**3)):
                    target = pathlib.Path(staging) / field
                    with handoff(plan[field], owner_uid, host) as source, target.open('xb') as output:
                        target.chmod(0o600)
                        total = 0
                        while True:
                            block = source.read(1024 * 1024)
                            if not block:
                                break
                            total += len(block)
                            require(total <= limit, 'handoff artifact over bounds')
                            output.write(block)
                    inputs[field] = str(target)
                installer.inputs = inputs
                result = installer.apply(authority, lambda: sys.stdin.readline(2050).rstrip('\n'))
        else:
            binding = installer.validate(authority)
            result = {'version': 1, 'status': 'dry-run' if args.dry_run else 'preflight', 'planSha256': binding,
                      'mutations': False, 'requiresExplicitRootApply': True,
                      'stages': ['account-private', 'helper-config-sudoers', 'runner-staged', 'registered',
                                 'daemon-started', 'new-online-verified', 'old-backup-protected', 'old-retired'],
                      'note': 'Owner authority attestation is required; this tool does not migrate workflows or mint tokens.'}
        print(json.dumps(result, sort_keys=True))
        return 0
    except ReconciliationRequired as error:
        # The engine gets a real read-only challenge (exit 0), so it never blindly retries root.
        if 'installer' in locals() and 'args' in locals() and args.apply:
            try:
                challenge = installer.registration_status(installer.raw_plan_sha256)
            except Refused:
                pass
            else:
                print(json.dumps(challenge, sort_keys=True))
                return 0
        print(json.dumps({'version': 1, 'status': 'refused', 'reason': str(error)}))
        return 2
    except Refused as error:
        # Only our fixed refusal strings are public; never raw command output or input errors.
        print(json.dumps({'version': 1, 'status': 'refused', 'reason': str(error)}))
        return 2
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError):
        print(json.dumps({'version': 1, 'status': 'refused', 'reason': 'unreadable input or command failure; reconcile protected receipt'}))
        return 2


if __name__ == '__main__':
    sys.exit(main())
