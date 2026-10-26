"""Safe installer contracts: filesystem in TemporaryDirectory, ALL host commands faked.
Run: python3 -I tests/test_setup_gui_collector.py [--mutations]
No sudo, dscl, launchctl, downloaded executable, runner or network is run.
"""
import copy
import hashlib
import io
import json
import os
from pathlib import Path
import sys
import tarfile
import tempfile
import time
import types
import unittest

SOURCE = Path(__file__).resolve().parents[1] / 'scripts/setup-gui-collector.py'
TEXT = SOURCE.read_text()


def load(text):
    module = types.ModuleType('collector_installer')
    exec(compile(text, str(SOURCE), 'exec'), module.__dict__)
    return module


setup = load(TEXT)


class FakeHost:
    def __init__(self, root):
        self.root = root
        self.accounts = {'lwrunner': {'uid': 502}}
        self.calls = []
        self.acls = set()
        self.used = {0, 501, 502}
        self.fail = None
        self.crash_after = None
        self.groups = {}
        self.fields = {}
        self.services = set()
        self.unremovable = set()
        self.processes = {}
        self.next_pid = 900
        self.spawn_identity = None
        self.ambiguous_pids = set()
        self.gated = []

    def process_identity(self, pid, uid=None):
        if pid in self.ambiguous_pids:
            raise setup.Refused('process state is unknown')
        identity = self.processes.get(pid)
        if identity is None:
            return None
        require = setup.require
        require(identity.startswith(str(uid) + ' '), 'process identity is ambiguous')
        return identity

    def kill(self, pid):
        self.processes.pop(pid, None)

    def start_gated(self, argv, **kwargs):
        self.calls.append(('gated_start', argv, kwargs))
        if self.fail == 'gate-spawn':
            raise setup.Refused('simulated spawn failure')
        self.next_pid += 1
        self.processes[self.next_pid] = self.spawn_identity if self.spawn_identity is not None else ('503 collector ' + str(self.next_pid))
        gate = FakeGate(self, self.next_pid, argv, kwargs)
        self.gated.append(gate)
        return gate

    def account(self, name):
        return self.accounts.get(name)

    def ids(self):
        return self.used | set(self.accounts[x]['uid'] for x in self.accounts), self.used | set(self.groups.values()) - {502}

    def group(self, name):
        return self.groups.get(name)

    def group_state(self, name):
        return None if name not in self.groups else (self.groups[name], set())

    def uid_free(self, uid):
        users, groups = self.ids()
        return uid not in users and uid not in groups

    def _before(self, label):
        if self.fail == label:
            raise setup.Refused('simulated failure')

    def _after(self, label):
        if self.crash_after and self.crash_after(label):
            self.crash_after = None
            raise setup.Refused('simulated crash after effect')

    def create_group(self, name, uid):
        self.calls.append(('create_group', name, uid))
        self._before('group')
        self.groups[name] = uid
        self._after('group')

    def create_account(self, name, uid, home):
        if name not in self.groups:
            self.create_group(name, uid)
        self.accounts[name] = dict(uid=uid, gid=uid, home=home, shell='/usr/bin/false')
        self.fields[name] = {key: value(self.setup_plan()) for key, value in setup.ACCOUNT_FIELDS}

    def setup_plan(self):
        return getattr(self, 'plan', {'uid': 503, 'home': '/Users/_factorygui'})

    def account_fields(self, name):
        return None if name not in self.fields else dict(self.fields[name])

    def write_account_field(self, name, key, value):
        self.calls.append(('write_field', name, key, value))
        self._before('field:' + key)
        self.fields.setdefault(name, {})[key] = value
        values = self.fields[name]
        if all(key in values for key, _ in setup.ACCOUNT_FIELDS):
            self.accounts[name] = dict(uid=int(values['UniqueID']), gid=int(values['PrimaryGroupID']),
                                       home=values['NFSHomeDirectory'], shell=values['UserShell'])
        else:
            self.accounts.pop(name, None)
        self._after('field:' + key)

    def service_absent(self, label):
        self.calls.append(('service_absent', label))
        return label not in self.services

    def verify_account(self, name, uid, home):
        setup.require(self.accounts[name] == dict(uid=uid, gid=uid, home=str(home), shell='/usr/bin/false'), 'account mismatch')

    def acl_free(self, path):
        return str(path) not in self.acls

    def run(self, argv, **kwargs):
        self.calls.append((argv, kwargs))
        if self.fail and self.fail in argv[0]:
            raise setup.Refused('simulated failure')
        if argv[0].endswith('config.sh'):
            runner = Path(kwargs['cwd'])
            info = {'agentName': argv[argv.index('--name') + 1], 'agentId': 71,
                    'gitHubUrl': 'https://github.com/o/central'}
            for name, data in (('.runner', json.dumps(info)), ('.credentials', 'private-key'),
                               ('.credentials_rsaparams', 'private-rsa')):
                (runner / name).write_text(data)
                (runner / name).chmod(0o600)
        return ''

    def start(self, plist):
        self.calls.append(('start', str(plist)))

    def stop(self, label):
        self.calls.append(('stop', label))
        self._before('stop')
        if label not in self.unremovable:
            self.services.discard(label)
        self._after('stop')


class FakeGate:
    def __init__(self, host, pid, argv, kwargs):
        self.host, self.pid, self.argv, self.kwargs = host, pid, argv, kwargs
        self.start_identity = host.processes[pid]
        self.released = False

    def release(self):
        self.released = True
        self.host.calls.append(('gate_release', self.pid))
        self.host.run(self.argv, **self.kwargs)

    def abort(self):
        self.host.kill(self.pid)

    def wait(self):
        return 0


class Contracts(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.archive = self.root / 'runner.tar.gz'
        with tarfile.open(self.archive, 'w:gz') as archive:
            for name in ('config.sh', 'run.sh'):
                entry = tarfile.TarInfo(name)
                entry.mode = 0o755
                entry.size = 4
                archive.addfile(entry, io.BytesIO(b'fake'))
        self.helper = self.root / 'helper'
        self.helper.write_text('trusted helper')
        self.source_job = self.root / 'source-job'
        self.source_job.write_text('trusted non-root source client')
        self.host = FakeHost(self.root)
        self.plan = dict(version=1, user='_factorygui', uid=503, home='/Users/_factorygui',
                         runnerName='factory-gui-unique-1234', archive=str(self.archive),
                         archiveSha256=hashlib.sha256(self.archive.read_bytes()).hexdigest(), helper=str(self.helper),
                         helperSha256=setup.digest(self.helper.read_bytes()), sourceJob=str(self.source_job),
                         sourceJobSha256=setup.digest(self.source_job.read_bytes()),
                         configuration=dict(collectorUid=503, sourceUid=502, sourceUser='lwrunner',
                                            runnerRoot='/Users/_factorygui/actions-runner',
                                            runnerName='factory-gui-unique-1234', collectorRepository='o/central',
                                            stateDirectory='/Users/_factorygui/gui-state',
                                            retiredRunnerRoots=['/Users/lwrunner/actions-runner'],
                                            developer='/Applications/Xcode.app/Contents/Developer', xcode='26.5',
                                            build='17F42', runtime='iOS-26-5', deviceType='iPhone-17', trustedRepositories=['o/trusted']),
                         old=dict(runnerRoot='/Users/lwrunner/actions-runner', runnerId=19,
                                  runnerName='lensworks-guest', serviceLabel='actions.runner.old',
                                  servicePlist='/Library/LaunchAgents/actions.runner.old.plist', files={}))
        old = self.root / 'Users/lwrunner/actions-runner'
        old.mkdir(parents=True, mode=0o700)
        for name in setup.AUTH_FILES:
            (old / name).write_text(json.dumps({'agentId': 19, 'agentName': 'lensworks-guest', 'gitHubUrl': 'https://github.com/o/old-repo'}) if name == '.runner' else 'old-' + name)
        service = self.root / 'Library/LaunchAgents/actions.runner.old.plist'
        service.parent.mkdir(parents=True)
        (old / '.service').write_text('actions.runner.old')
        service.write_bytes(setup.plistlib.dumps({'Label': 'actions.runner.old', 'UserName': 'lwrunner',
                                                'WorkingDirectory': '/Users/lwrunner/actions-runner'}))
        self.plan['old']['files'] = {name: setup.digest((old / name).read_bytes()) for name in setup.AUTH_FILES}
        self.plan['old']['files']['servicePlist'] = setup.digest(service.read_bytes())
        self.authority = dict(expiresAt=time.time() + 600, appPermissionsVerified=True, newNameAbsent=True,
                              migratedWorkflows=[dict(path=path, sha256='a' * 64, collectorProtocol=True) for path in sorted(setup.MIGRATED_WORKFLOWS)])
        self.host.plan = self.plan
        self.host.services.add('actions.runner.old')
        self.bind()
        self.installer = setup.Installer(self.plan, self.host, self.root)

    def bind(self):
        self.authority['planSha256'] = setup.digest(json.dumps(self.plan, sort_keys=True).encode())

    def apply(self):
        return self.installer.apply(self.authority, lambda: 'SHORT_LIVED_TOKEN_12345')

    def online(self):
        self.authority['online'] = dict(runnerId=71, runnerName=self.plan['runnerName'], status='online', labels=[setup.bootstrap_label(self.authority['planSha256'])])
        self.authority['removedOldRunnerId'] = None
        self.apply()  # online proof protects backups BEFORE exact remote removal
        self.authority['removedOldRunnerId'] = 19

    def test_dryrun_has_no_mutations(self):
        before = sorted(str(x) for x in self.root.rglob('*'))
        self.installer.validate(self.authority)
        self.assertEqual(before, sorted(str(x) for x in self.root.rglob('*')))
        self.assertEqual([], self.host.calls)

    def test_uid_must_be_free_and_not_lwrunner(self):
        for uid in (499, 502, 501):
            self.plan['uid'] = uid
            self.plan['configuration']['collectorUid'] = uid
            self.bind()
            with self.assertRaises(setup.Refused):
                self.installer.validate(self.authority)

    def test_existing_group_name_is_not_reassigned(self):
        self.host.groups['_factorygui'] = 777
        with self.assertRaises(setup.Refused):
            self.installer.validate(self.authority)
        self.assertEqual([], self.host.calls)

    def test_private_home_never_adopted(self):
        (self.root / 'Users/_factorygui').mkdir()
        with self.assertRaises(setup.Refused):
            self.installer.validate(self.authority)

    def test_symlink_home_refused(self):
        (self.root / 'Users/_factorygui').symlink_to(self.root / 'Users/lwrunner')
        with self.assertRaises(setup.Refused):
            self.installer.validate(self.authority)

    def test_collector_registration_cannot_be_in_source_repository(self):
        self.plan['configuration']['trustedRepositories'] = ['O/CENTRAL']
        self.bind()
        with self.assertRaises(setup.Refused):
            self.installer.validate(self.authority)
        self.assertEqual([], self.host.calls)

    def test_reviewed_source_client_is_installed_fixed_root_program(self):
        result = self.apply()
        installed = self.installer.path(setup.SOURCE_JOB)
        self.assertEqual(self.source_job.read_bytes(), installed.read_bytes())
        self.assertEqual(0o755, installed.stat().st_mode & 0o777)
        self.assertEqual(self.plan['sourceJobSha256'], result['sourceJobSha256'])
        self.assertFalse(any(isinstance(x[0], list) and x[0][0] == str(installed) for x in self.host.calls))
        self.assertNotIn(str(setup.SOURCE_JOB), (self.root / 'etc/sudoers.d/dark-factory-gui-collector').read_text())

    def test_source_client_checksum_is_plan_bound(self):
        self.source_job.write_text('modified after review')
        with self.assertRaises(setup.Refused):
            self.installer.validate(self.authority)
        self.assertEqual([], self.host.calls)

    def test_privileged_helper_checksum_is_plan_bound(self):
        self.helper.write_text('modified privileged helper')
        with self.assertRaises(setup.Refused):
            self.installer.validate(self.authority)
        self.assertEqual([], self.host.calls)

    def test_checksum_mismatch_refused(self):
        self.plan['archiveSha256'] = '0' * 64
        self.bind()
        with self.assertRaises(setup.Refused):
            self.installer.validate(self.authority)

    def test_root_inventory_is_readonly_and_nonsecret(self):
        before = {str(path): path.read_bytes() for path in self.root.rglob('*') if path.is_file()}
        result = setup.inventory('/Users/lwrunner/actions-runner', self.host, self.root)
        after = {str(path): path.read_bytes() for path in self.root.rglob('*') if path.is_file()}
        self.assertEqual(before, after)
        self.assertEqual('inventory', result['status'])
        self.assertEqual('o/old-repo', result['repository'])
        self.assertEqual(self.plan['old'], result['old'])
        self.assertNotIn('old-.credentials', json.dumps(result))
        self.assertEqual([], self.host.calls)

    def test_root_inventory_cannot_leave_source_home(self):
        with self.assertRaises(setup.Refused):
            setup.inventory('/Users/owner/actions-runner', self.host, self.root)

    def test_root_inventory_never_follows_credential_symlink(self):
        path = self.root / 'Users/lwrunner/actions-runner/.credentials'
        path.unlink()
        path.symlink_to(self.helper)
        with self.assertRaises(setup.Refused):
            setup.inventory('/Users/lwrunner/actions-runner', self.host, self.root)

    def test_root_inventory_binds_service_to_source_directory(self):
        path = self.root / 'Library/LaunchAgents/actions.runner.old.plist'
        path.write_bytes(setup.plistlib.dumps({'Label': 'actions.runner.old', 'WorkingDirectory': '/Users/owner/runner'}))
        with self.assertRaises(setup.Refused):
            setup.inventory('/Users/lwrunner/actions-runner', self.host, self.root)

    def test_root_inventory_refuses_wrong_source_uid(self):
        self.host.accounts['lwrunner']['uid'] = 501
        with self.assertRaises(setup.Refused):
            setup.inventory('/Users/lwrunner/actions-runner', self.host, self.root)

    def runTest(self):
        pass

    def fresh(self):
        case = type(self)('runTest')
        case.setUp()
        self.addCleanup(case.doCleanups)
        return case

    def test_crash_after_each_account_creation_effect_resumes_exact(self):
        labels = ['group'] + ['field:' + key for key, _ in setup.ACCOUNT_FIELDS]
        self.assertEqual(7, len(labels))
        for label in labels:
            with self.subTest(after=label):
                case = self.fresh()
                case.host.crash_after = lambda step: step == label
                with self.assertRaises(setup.Refused):
                    case.apply()
                case.host.crash_after = None
                self.assertEqual('awaiting-owner-online-verification-old-untouched', case.apply()['status'])
                name = case.plan['user']
                self.assertEqual(1, len([c for c in case.host.calls if c[0] == 'create_group']))
                self.assertEqual({key for key, _ in setup.ACCOUNT_FIELDS}, set(case.host.fields[name]))
                for key, _ in setup.ACCOUNT_FIELDS:
                    self.assertEqual(1, len([c for c in case.host.calls if c[0] == 'write_field' and c[2] == key]))
                self.assertEqual(503, case.host.accounts[name]['uid'])
                self.assertEqual('/Users/_factorygui', case.host.accounts[name]['home'])
                self.assertTrue((case.root / 'Users/_factorygui').is_dir())

    def test_partial_account_conflicting_attribute_is_refused_not_overwritten(self):
        self.host.crash_after = lambda step: step == 'group'
        with self.assertRaises(setup.Refused):
            self.apply()
        self.host.crash_after = None
        self.host.fields['_factorygui'] = {'IsHidden': '0'}
        with self.assertRaises(setup.Refused):
            self.apply()
        self.assertEqual('0', self.host.fields['_factorygui']['IsHidden'])
        self.assertFalse(any(c[0] == 'write_field' and c[2] == 'IsHidden' for c in self.host.calls))

    def test_unrelated_group_with_reserved_name_is_not_adopted(self):
        self.installer.mkdir(self.installer.path(setup.STATE))
        self.installer.receipt.update(planSha256=self.authority['planSha256'], uid=503)
        self.installer.save()
        self.host.groups['_factorygui'] = 900
        with self.assertRaises(setup.Refused):
            self.apply()
        self.assertEqual(900, self.host.groups['_factorygui'])

    def test_reserved_account_before_home_creation_resumes(self):
        self.installer.mkdir(self.installer.path(setup.STATE))
        self.installer.receipt.update(planSha256=self.authority['planSha256'], uid=503)
        self.installer.save()
        self.host.create_account('_factorygui', 503, '/Users/_factorygui')
        self.assertFalse((self.root / 'Users/_factorygui').exists())
        self.assertEqual('awaiting-owner-online-verification-old-untouched', self.apply()['status'])
        self.assertEqual(0o700, (self.root / 'Users/_factorygui').stat().st_mode & 0o777)

    def test_nullable_owner_authority_resumes_as_pending(self):
        self.authority.update(kind='gui-collector-authority-v1', online=None, removedOldRunnerId=None)
        self.assertEqual('awaiting-owner-online-verification-old-untouched', self.apply()['status'])

    def test_secret_bearing_authority_is_never_persisted(self):
        self.authority['registrationToken'] = 'SECRET_MUST_NOT_PERSIST'
        with self.assertRaises(setup.Refused):
            self.apply()
        self.assertFalse(self.installer.path(setup.STATE / 'receipt.json').exists())

    def test_owner_handoff_private_file_can_be_promoted(self):
        source = self.root / 'authority.json'
        source.write_bytes(b'{"owner":true}')
        source.chmod(0o600)
        data = setup.handoff_bytes(source, os.getuid(), self.host, 65536)
        self.installer.mkdir(self.installer.path(setup.STATE))
        self.installer.atomic_state('authority.json', data)
        target = self.installer.path(setup.STATE / 'authority.json')
        self.assertEqual(b'{"owner":true}', target.read_bytes())
        self.assertEqual(0o600, target.stat().st_mode & 0o777)
        self.assertEqual([], [x for x in self.host.calls if x[0] == 'account'])

    def test_handoff_symlink_is_not_followed(self):
        source = self.root / 'handoff-link'
        source.symlink_to(self.helper)
        with self.assertRaises(setup.Refused):
            setup.handoff_bytes(source, os.getuid(), self.host, 65536)

    def test_handoff_wrong_mode_and_acl_refused(self):
        source = self.root / 'handoff.json'
        source.write_text('{}')
        source.chmod(0o644)
        with self.assertRaises(setup.Refused):
            setup.handoff_bytes(source, os.getuid(), self.host, 65536)
        source.chmod(0o600)
        self.host.acls.add(str(source))
        with self.assertRaises(setup.Refused):
            setup.handoff_bytes(source, os.getuid(), self.host, 65536)

    def test_raw_plan_file_hash_is_authority_binding(self):
        raw = json.dumps(self.plan, indent=2).encode() + b'\n'
        self.installer.raw_plan_sha256 = setup.digest(raw)
        self.authority['planSha256'] = setup.digest(raw)
        self.assertEqual(setup.digest(raw), self.installer.validate(self.authority))
        self.authority['planSha256'] = setup.digest(raw.rstrip())
        with self.assertRaises(setup.Refused):
            self.installer.validate(self.authority)

    def test_exact_four_workflow_paths_not_arbitrary_four(self):
        self.authority['migratedWorkflows'][0]['path'] = '.github/workflows/unrelated.yml'
        with self.assertRaises(setup.Refused):
            self.installer.validate(self.authority)

    def test_four_migrations_required(self):
        self.authority['migratedWorkflows'].pop()
        with self.assertRaises(setup.Refused):
            self.installer.validate(self.authority)

    def test_app_permissions_required(self):
        self.authority['appPermissionsVerified'] = False
        with self.assertRaises(setup.Refused):
            self.installer.validate(self.authority)

    def test_authority_plan_binding(self):
        self.authority['planSha256'] = '0' * 64
        with self.assertRaises(setup.Refused):
            self.installer.validate(self.authority)

    def test_native_token_environment_and_no_default_labels(self):
        result = self.apply()
        call, = [x for x in self.host.calls if isinstance(x[0], list) and x[0][0].endswith('config.sh')]
        argv, context = call
        self.assertIn('--no-default-labels', argv)
        self.assertEqual('factory-gui-bootstrap-' + self.authority['planSha256'][:16], argv[argv.index('--labels') + 1])
        self.assertEqual('factory-gui-bootstrap-' + self.authority['planSha256'][:16], result['bootstrapLabel'])
        self.assertNotIn('factory-gui-collector', argv)
        self.assertNotIn('--replace', argv)
        self.assertNotIn('SHORT_LIVED_TOKEN_12345', str(argv))
        self.assertEqual(503, context['uid'])
        self.assertEqual(503, context['gid'])
        self.assertNotIn('SHORT_LIVED_TOKEN_12345', json.dumps(result))
        # Real adapter tested with a fake subprocess; no command executes.
        captured = []
        original = setup.subprocess.run
        setup.subprocess.run = lambda argv, **kw: (captured.append(kw) or types.SimpleNamespace(returncode=0, stdout=b''))
        try:
            setup.Host().run(['fake'], uid=503, gid=503, home='/Users/_factorygui', token='one-time')
        finally:
            setup.subprocess.run = original
        self.assertEqual('one-time', captured[0]['env']['ACTIONS_RUNNER_INPUT_TOKEN'])
        self.assertEqual('/Users/_factorygui', captured[0]['env']['HOME'])
        self.assertNotIn('GH_TOKEN', captured[0]['env'])
        self.assertNotIn('VSTS_AGENT_INPUT_TOKEN', captured[0]['env'])

    def test_final_label_before_old_retirement_cannot_authorize_backup(self):
        self.apply()
        self.authority['online'] = dict(runnerId=71, runnerName=self.plan['runnerName'], status='online',
                                        labels=[setup.CUSTOM_LABEL])
        result = self.apply()
        self.assertEqual('awaiting-owner-online-verification-old-untouched', result['status'])
        self.assertNotIn('old-backup-protected', result['stages'])
        self.assertTrue((self.root / 'Users/lwrunner/actions-runner/.credentials').exists())
        self.assertFalse(any(x[0] == 'stop' for x in self.host.calls))

    def test_final_label_after_durable_retirement_is_valid_on_resume(self):
        self.apply()
        self.online()
        self.assertEqual('complete', self.apply()['status'])
        self.authority['online']['labels'] = [setup.CUSTOM_LABEL]
        self.assertEqual('complete', self.apply()['status'])
        self.assertIn('old-retired', self.installer.receipt['stages'])
        self.assertEqual(1, len([x for x in self.host.calls if isinstance(x[0], list) and x[0][0].endswith('config.sh')]))

    def test_online_stage_protects_backup_before_remote_removal(self):
        self.apply()
        self.authority['online'] = dict(runnerId=71, runnerName=self.plan['runnerName'], status='online', labels=[setup.bootstrap_label(self.authority['planSha256'])])
        result = self.apply()
        self.assertEqual('awaiting-owner-exact-old-registration-removal', result['status'])
        self.assertIn('old-backup-protected', result['stages'])
        self.assertTrue((self.root / 'var/db/dark-factory/gui-collector/rollback/.credentials').exists())
        self.assertFalse(any(x[0] == 'stop' for x in self.host.calls))
        self.assertTrue((self.root / 'Users/lwrunner/actions-runner/.credentials').exists())

    def test_remote_removal_before_backup_is_refused(self):
        self.apply()
        self.authority.update(online=dict(runnerId=71, runnerName=self.plan['runnerName'], status='online', labels=[setup.bootstrap_label(self.authority['planSha256'])]),
                              removedOldRunnerId=19)
        with self.assertRaises(setup.Refused):
            self.apply()
        self.assertFalse(any(x[0] == 'stop' for x in self.host.calls))
        self.assertFalse((self.root / 'var/db/dark-factory/gui-collector/rollback').exists())

    def test_online_required_before_old_retirement(self):
        self.online()
        self.authority['online']['status'] = 'offline'
        result = self.apply()
        self.assertEqual('awaiting-owner-online-verification-old-untouched', result['status'])
        self.assertTrue((self.root / 'Users/lwrunner/actions-runner/.credentials').exists())
        self.assertFalse(any(x[0] == 'stop' for x in self.host.calls))

    def test_idempotent_resume_never_registers_again(self):
        self.apply()
        self.online()
        self.assertEqual('complete', self.apply()['status'])
        self.assertEqual('complete', self.apply()['status'])
        calls = [x for x in self.host.calls if isinstance(x[0], list) and x[0][0].endswith('config.sh')]
        self.assertEqual(1, len(calls))
        self.assertFalse((self.root / 'Users/lwrunner/actions-runner/.credentials').exists())
        backup = self.root / 'var/db/dark-factory/gui-collector/rollback/.credentials'
        self.assertEqual(0o600, backup.stat().st_mode & 0o777)

    def interrupted_registration(self):
        original = self.installer.stage
        def crash(name):
            original(name)
            if name == 'registration-attempted':
                raise setup.Refused('simulated crash after durable intent')
        self.installer.stage = crash
        try:
            with self.assertRaises(setup.Refused):
                self.apply()
        finally:
            self.installer.stage = original
        self.assertFalse((self.root / 'Users/_factorygui/actions-runner/.runner').exists())
        return self.installer.receipt['registrationAttempt']

    def proof(self, **changes):
        attempt = self.installer.receipt['registrationAttempt']
        proof = dict(attemptNonce=attempt['nonce'], runnerName=self.plan['runnerName'],
                     checkedAt=time.time(), status='absent', appPermissionsVerified=True)
        proof.update(changes)
        return proof

    def test_provably_unstarted_registration_resumes_with_fresh_absence_proof(self):
        attempt = self.interrupted_registration()
        self.assertEqual('reserved', attempt['state'])
        self.authority['registrationAbsence'] = self.proof()
        self.assertEqual('awaiting-owner-online-verification-old-untouched', self.apply()['status'])
        configs = [c for c in self.host.calls if isinstance(c[0], list) and c[0][0].endswith('config.sh')]
        self.assertEqual(1, len(configs))
        self.assertEqual(2, self.installer.receipt['registrationAttempts'])
        self.assertEqual('released', self.installer.receipt['registrationAttempt']['state'])

    def test_unstarted_registration_without_absence_proof_stays_refused(self):
        self.interrupted_registration()
        with self.assertRaises(setup.Refused):
            self.apply()
        self.assertFalse(any(isinstance(c[0], list) and c[0][0].endswith('config.sh') for c in self.host.calls))

    def test_absence_proof_must_match_the_exact_attempt_and_name(self):
        for change in ({'attemptNonce': 'f' * 32}, {'runnerName': 'factory-gui-other-9999'},
                       {'status': 'present'}, {'appPermissionsVerified': False}):
            with self.subTest(proof=change):
                case = self.fresh()
                case.interrupted_registration()
                case.authority['registrationAbsence'] = case.proof(**change)
                with self.assertRaises(setup.Refused):
                    case.apply()
                self.assertFalse(any(isinstance(c[0], list) and c[0][0].endswith('config.sh')
                                     for c in case.host.calls))

    def test_absence_proof_must_be_fresh(self):
        for checked in (time.time() - 301, time.time() + 60):
            with self.subTest(checkedAt=checked):
                case = self.fresh()
                attempt = case.interrupted_registration()
                # Keep the attempt older than the stale proof so only the freshness rule can reject it.
                attempt['startedAt'] = time.time() - 600
                case.installer.save()
                case.authority['registrationAbsence'] = case.proof(checkedAt=checked)
                with self.assertRaises(setup.Refused):
                    case.apply()

    def test_released_or_unknown_registration_is_never_retried(self):
        for change in ({'state': 'released'}, {'state': 'unknown'}):
            with self.subTest(attempt=change):
                case = self.fresh()
                case.interrupted_registration()
                case.host.processes[777] = 'collector 777'
                case.installer.receipt['registrationAttempt'].update(
                    change, pid=777, startIdentity='collector 777')
                case.host.kill(777)
                case.installer.save()
                case.authority['registrationAbsence'] = case.proof()
                with self.assertRaises(setup.Refused):
                    case.apply()

    def crash_after_state(self, state):
        original = self.installer.save
        def crash():
            original()
            attempt = self.installer.receipt.get('registrationAttempt') or {}
            if attempt.get('state') == state:
                raise setup.Refused('simulated crash after ' + state)
        self.installer.save = crash
        try:
            with self.assertRaises(setup.Refused):
                self.apply()
        finally:
            self.installer.save = original
        self.assertEqual(state, self.installer.receipt['registrationAttempt']['state'])
        return self.installer.receipt['registrationAttempt']

    def test_unreleased_child_alive_blocks_retry_then_dead_allows_one(self):
        attempt = self.crash_after_state('unreleased')
        self.authority['registrationAbsence'] = self.proof()
        with self.assertRaises(setup.Refused):
            self.apply()
        self.assertFalse(any(c[0] == 'gate_release' for c in self.host.calls))
        self.host.kill(attempt['pid'])
        self.assertEqual('awaiting-owner-online-verification-old-untouched', self.apply()['status'])
        self.assertEqual(2, self.installer.receipt['registrationAttempts'])
        self.assertEqual(1, len([c for c in self.host.calls if c[0] == 'gate_release']))

    def test_missing_proof_for_unstarted_intent_is_a_structured_challenge(self):
        self.interrupted_registration()
        with self.assertRaises(setup.ReconciliationRequired):
            self.apply()
        challenge = self.installer.registration_status(self.authority['planSha256'])
        self.assertEqual('registration-reconciliation-required', challenge['status'])
        self.assertIs(True, challenge['registrationIntent']['localUnstartedVerified'])
        self.assertLess(challenge['attempts'], 2)

    def test_released_state_is_a_generic_refusal_not_a_retry_challenge(self):
        self.crash_after_state('released')
        self.host.kill(self.installer.receipt['registrationAttempt']['pid'])
        self.authority['registrationAbsence'] = self.proof()
        with self.assertRaises(setup.Refused) as caught:
            self.apply()
        self.assertNotIsInstance(caught.exception, setup.ReconciliationRequired)

    def test_released_gated_child_is_never_retried_even_if_dead(self):
        attempt = self.crash_after_state('released')
        self.assertFalse(any(c[0] == 'gate_release' for c in self.host.calls))
        self.host.kill(attempt['pid'])
        self.authority['registrationAbsence'] = self.proof()
        with self.assertRaises(setup.Refused):
            self.apply()
        self.assertFalse(any(c[0] == 'gate_release' for c in self.host.calls))

    def test_reserved_intent_with_a_pid_is_refused(self):
        self.interrupted_registration()
        self.installer.receipt['registrationAttempt'].update(pid=1234, startIdentity='collector 1234')
        self.installer.save()
        self.host.processes[1234] = 'collector 1234'
        self.authority['registrationAbsence'] = self.proof()
        with self.assertRaises(setup.Refused):
            self.apply()

    def native_identity(self, answer):
        original = setup.subprocess.run
        setup.subprocess.run = lambda argv, **kw: answer
        try:
            return setup.Host().process_identity(1234, 503)
        finally:
            setup.subprocess.run = original

    def test_process_identity_distinguishes_exact_dead_live_and_ambiguous(self):
        def answer(code, out='', err=''):
            return types.SimpleNamespace(returncode=code, stdout=out.encode(), stderr=err.encode())
        self.assertIsNone(self.native_identity(answer(1)))
        self.assertEqual('503 Mon Oct 26 10:00:00 2026',
                         self.native_identity(answer(0, '503  Mon Oct 26 10:00:00 2026\n')))
        for invalid in (answer(1, '', 'ps: permission denied'), answer(2), answer(0, '504 Mon Oct'),
                        answer(0, '503'), answer(0, '')):
            with self.subTest(answer=invalid):
                with self.assertRaises(setup.Refused):
                    self.native_identity(invalid)

    def test_ambiguous_process_state_never_authorizes_dead_unreleased(self):
        attempt = self.crash_after_state('unreleased')
        self.host.ambiguous_pids.add(attempt['pid'])
        self.authority['registrationAbsence'] = self.proof()
        with self.assertRaises(setup.Refused) as caught:
            self.apply()
        self.assertNotIsInstance(caught.exception, setup.ReconciliationRequired)

    def test_reused_pid_with_a_different_identity_allows_one_retry(self):
        attempt = self.crash_after_state('unreleased')
        self.host.processes[attempt['pid']] = '503 different-start'
        self.authority['registrationAbsence'] = self.proof()
        self.assertEqual('awaiting-owner-online-verification-old-untouched', self.apply()['status'])

    def test_unverifiable_child_identity_never_releases_or_registers(self):
        self.host.spawn_identity = ''
        with self.assertRaises(setup.Refused):
            self.apply()
        self.assertFalse(any(c[0] == 'gate_release' for c in self.host.calls))
        self.assertFalse(any(isinstance(c[0], list) and c[0][0].endswith('config.sh') for c in self.host.calls))
        self.assertEqual('reserved', self.installer.receipt['registrationAttempt']['state'])

    def test_registration_status_challenge_is_readonly_and_omits_runner_id(self):
        attempt = self.interrupted_registration()
        before = self.root.joinpath('var/db/dark-factory/gui-collector/receipt.json').read_bytes()
        status = self.installer.registration_status(self.authority['planSha256'])
        after = self.root.joinpath('var/db/dark-factory/gui-collector/receipt.json').read_bytes()
        self.assertEqual(before, after)
        self.assertEqual('registration-reconciliation-required', status['status'])
        self.assertEqual('reserved', status['registrationIntent']['state'])
        self.assertIs(True, status['registrationIntent']['localUnstartedVerified'])
        self.assertEqual(attempt['nonce'], status['registrationIntent']['attemptNonce'])
        self.assertNotIn('runnerId', status)
        self.assertEqual(self.plan['helperSha256'], status['helperSha256'])
        self.assertEqual(self.plan['sourceJobSha256'], status['sourceJobSha256'])

    def test_registration_retry_budget_and_window_are_bounded(self):
        case = self.fresh()
        case.interrupted_registration()
        case.installer.receipt['registrationAttempts'] = 2
        case.installer.save()
        case.authority['registrationAbsence'] = case.proof()
        with self.assertRaises(setup.Refused):
            case.apply()
        case = self.fresh()
        case.interrupted_registration()
        case.installer.receipt['registrationFirstStartedAt'] = time.time() - 3601
        case.installer.save()
        case.authority['registrationAbsence'] = case.proof()
        with self.assertRaises(setup.Refused):
            case.apply()

    def test_local_credentials_contradicting_absence_are_refused(self):
        self.interrupted_registration()
        (self.root / 'Users/_factorygui/actions-runner/.credentials').write_text('uncertain local')
        self.authority['registrationAbsence'] = self.proof()
        with self.assertRaises(setup.Refused):
            self.apply()
        self.assertFalse(any(isinstance(c[0], list) and c[0][0].endswith('config.sh') for c in self.host.calls))

    def test_local_registration_with_remote_absence_mismatch_is_refused(self):
        self.apply()
        self.authority['registrationAbsence'] = self.proof()
        with self.assertRaises(setup.Refused):
            self.apply()

    def test_partial_registration_is_not_blindly_repeated(self):
        self.host.fail = 'config.sh'
        with self.assertRaises(setup.Refused):
            self.apply()
        self.host.fail = None
        with self.assertRaises(setup.Refused):
            self.apply()
        calls = [x for x in self.host.calls if isinstance(x[0], list) and x[0][0].endswith('config.sh')]
        self.assertEqual(1, len(calls))

    def test_acl_rejected_on_resume(self):
        self.apply()
        self.host.acls.add(str(self.root / 'Users/_factorygui'))
        with self.assertRaises(setup.Refused):
            self.apply()

    def test_sudoers_is_exact_fixed_command(self):
        self.apply()
        rule = (self.root / 'etc/sudoers.d/dark-factory-gui-collector').read_text()
        self.assertEqual('_factorygui ALL=(root) NOPASSWD: /usr/local/libexec/dark-factory/factory-gui-exec\n', rule)
        self.assertTrue(any(isinstance(x[0], list) and x[0][0] == '/usr/sbin/visudo' for x in self.host.calls))
        self.assertEqual(0o600, (self.root / 'etc/dark-factory/gui-executor.json').stat().st_mode & 0o777)
        self.assertEqual(setup.digest((self.root / 'etc/dark-factory/gui-executor.json').read_bytes()),
                         self.installer.receipt['configurationSha256'])
        self.assertEqual(0o755, (self.root / 'usr/local/libexec/dark-factory/factory-gui-exec').stat().st_mode & 0o777)

    def test_launchdaemon_is_non_gui_service_user(self):
        self.apply()
        plist = setup.plistlib.loads((self.root / 'Library/LaunchDaemons/net.dark-factory.gui-collector.plist').read_bytes())
        self.assertEqual('_factorygui', plist['UserName'])
        self.assertEqual('_factorygui', plist['GroupName'])
        self.assertEqual({'HOME': '/Users/_factorygui', 'PATH': '/usr/bin:/bin:/usr/sbin:/sbin'}, plist['EnvironmentVariables'])
        self.assertEqual('/Users/_factorygui/actions-runner', plist['WorkingDirectory'])

    def test_exact_old_id_required_for_retirement(self):
        self.apply()
        self.online()
        self.authority['removedOldRunnerId'] = 20
        with self.assertRaises(setup.Refused):
            self.apply()
        self.assertTrue((self.root / 'Users/lwrunner/actions-runner/.credentials').exists())

    def test_old_hash_mismatch_preserves_service_and_credentials(self):
        self.apply()
        self.online()
        (self.root / 'Users/lwrunner/actions-runner/.credentials').write_text('someone changed it')
        with self.assertRaises(setup.Refused):
            self.apply()
        self.assertFalse(any(x[0] == 'stop' for x in self.host.calls))

    def test_crash_after_bootout_effect_resumes_without_second_bootout(self):
        self.apply()
        self.online()
        self.host.crash_after = lambda step: step == 'stop'
        with self.assertRaises(setup.Refused):
            self.apply()
        self.host.crash_after = None
        self.assertNotIn('old-service-stopped', self.installer.receipt['stages'])
        self.assertEqual('complete', self.apply()['status'])
        self.assertEqual(1, len([c for c in self.host.calls if c[0] == 'stop']))
        self.assertIn('old-service-stopped', self.installer.receipt['stages'])
        self.assertFalse((self.root / 'Users/lwrunner/actions-runner/.runner').exists())

    def test_real_service_absence_proof_checks_every_domain(self):
        original = setup.subprocess.run
        def fake(answer):
            setup.subprocess.run = lambda argv, **kw: answer(argv[-1])
            try:
                return setup.Host().service_absent('actions.runner.old')
            finally:
                setup.subprocess.run = original
        def result(code, error=''):
            return types.SimpleNamespace(returncode=code, stderr=error.encode(), stdout=b'')
        def notfound(target):
            # The literal native shape: "Bad request." then the quoted service and its domain,
            # with NO full target string appended. Exit status is 113 for a missing service.
            domain = {'gui/502/actions.runner.old': 'user gui: 502',
                      'user/502/actions.runner.old': 'uid: 502',
                      'system/actions.runner.old': 'system'}[target]
            return result(113, 'Bad request.\nCould not find service "actions.runner.old" in domain for %s' % domain)
        self.assertTrue(fake(lambda target: result(0) if target == 'gui/502' else notfound(target)))
        self.assertFalse(fake(lambda target: result(0)))
        self.assertFalse(fake(lambda target: result(0) if target == 'gui/502' else result(113, 'Operation not permitted')))
        self.assertFalse(fake(lambda target: result(113, 'Bad request.\nCould not find service "other" in domain for user gui: 502') if target != 'gui/502' else result(0)))
        self.assertFalse(fake(lambda target: result(113, 'Bad request.\nCould not find service "actions.runner.old" in domain for uid: 501') if target != 'gui/502' else result(0)))
        self.assertFalse(fake(lambda target: result(113, 'Bad request.\nCould not find service "actions.runner.old" in domain for user user: 502') if target != 'gui/502' else result(0)))
        with self.assertRaises(setup.Refused):
            fake(lambda target: result(1, 'Bad request.\nCould not find domain for gui: 502'))

    def test_unknown_service_state_blocks_and_preserves_old_files(self):
        self.apply()
        self.online()
        self.host.unremovable.add('actions.runner.old')
        with self.assertRaises(setup.Refused):
            self.apply()
        self.assertTrue((self.root / 'Users/lwrunner/actions-runner/.runner').exists())
        self.assertTrue((self.root / 'var/db/dark-factory/gui-collector/rollback/.runner').exists())
        self.assertIn('old-backup-protected', self.installer.receipt['stages'])
        self.assertNotIn('old-retired', self.installer.receipt['stages'])
        self.assertNotEqual('complete', self.installer.receipt['status'])

    def test_retirement_intent_mismatch_is_refused(self):
        self.apply()
        self.online()
        receipt = self.root / 'var/db/dark-factory/gui-collector/receipt.json'
        data = json.loads(receipt.read_text())
        data['retirement'] = {'serviceLabel': 'actions.runner.another', 'domain': 'gui/502',
                              'userId': 502, 'runnerRoot': '/Users/lwrunner/actions-runner'}
        receipt.write_text(json.dumps(data))
        with self.assertRaises(setup.Refused):
            self.apply()
        self.assertFalse(any(c[0] == 'stop' for c in self.host.calls))
        self.assertTrue((self.root / 'Users/lwrunner/actions-runner/.runner').exists())

    def test_retirement_failure_keeps_protected_backup_and_old_files(self):
        self.apply()
        self.online()
        self.host.fail = 'stop'
        with self.assertRaises(setup.Refused):
            self.apply()
        self.assertTrue((self.root / 'Users/lwrunner/actions-runner/.credentials').exists())
        self.assertTrue((self.root / 'var/db/dark-factory/gui-collector/rollback/.credentials').exists())
        self.assertEqual('retirement-blocked-local-files-restored-registration-requires-owner', self.installer.receipt['status'])

    def test_partial_retirement_restores_deleted_credential(self):
        self.apply()
        self.online()
        original = Path.unlink
        target = self.root / 'Users/lwrunner/actions-runner/.credentials'
        def fail_once(path, *args, **kwargs):
            if path == target:
                raise OSError('fake deletion failure')
            return original(path, *args, **kwargs)
        Path.unlink = fail_once
        try:
            with self.assertRaises(OSError):
                self.apply()
        finally:
            Path.unlink = original
        restored = self.root / 'Users/lwrunner/actions-runner/.runner'
        self.assertTrue(restored.exists())
        self.assertEqual(self.plan['old']['files']['.runner'], setup.digest(restored.read_bytes()))

    def test_credential_reappearing_is_not_complete(self):
        self.apply()
        self.online()
        self.apply()
        (self.root / 'Users/lwrunner/actions-runner/.runner').write_text('new registration')
        with self.assertRaises(setup.Refused):
            self.apply()

    def test_wrong_private_home_mode_refuses_resume(self):
        self.apply()
        (self.root / 'Users/_factorygui').chmod(0o755)
        with self.assertRaises(setup.Refused):
            self.apply()

    def test_sudoers_symlink_never_followed(self):
        sudoers = self.root / 'etc/sudoers.d/dark-factory-gui-collector'
        sudoers.parent.mkdir(parents=True)
        victim = self.root / 'victim'
        victim.write_text('untouched')
        sudoers.symlink_to(victim)
        with self.assertRaises(setup.Refused):
            self.apply()
        self.assertEqual('untouched', victim.read_text())

    def test_existing_account_without_reservation_is_not_adopted(self):
        self.host.create_account('_factorygui', 503, '/Users/_factorygui')
        (self.root / 'Users/_factorygui').mkdir(mode=0o700)
        with self.assertRaises(setup.Refused):
            self.installer.validate(self.authority)

    def test_archive_symlink_refused_without_execution(self):
        with tarfile.open(self.archive, 'w:gz') as archive:
            entry = tarfile.TarInfo('config.sh')
            entry.type = tarfile.SYMTYPE
            entry.linkname = '/etc/sudoers'
            archive.addfile(entry)
        self.plan['archiveSha256'] = setup.digest(self.archive.read_bytes())
        self.bind()
        with self.assertRaises(setup.Refused):
            self.apply()
        self.assertFalse(any(isinstance(x[0], list) and x[0][0].endswith('config.sh') for x in self.host.calls))


# Named RED mutations, restored GREEN, entirely in-memory and only this installer's code.
MUTATIONS = [
    ('uid-floor', "p['uid'] >= 500 and p['uid'] != 502", "p['uid'] >= 1", 'test_uid_must_be_free_and_not_lwrunner'),
    ('adopt-home', "require(authorized, 'refusing to adopt an existing HOME')", "require(True, 'refusing to adopt an existing HOME')", 'test_private_home_never_adopted'),
    ('checksum', "file_digest(archive) == p['archiveSha256']", 'True', 'test_checksum_mismatch_refused'),
    ('migration-four', "len(workflows) == 4 and {w['path'] for w in workflows} == MIGRATED_WORKFLOWS", 'True', 'test_four_migrations_required'),
    ('app-permission', "authority.get('appPermissionsVerified') is True", 'True', 'test_app_permissions_required'),
    ('plan-binding', "authority.get('planSha256') == binding", 'True', 'test_authority_plan_binding'),
    ('generic-labels', "'--no-default-labels', '--labels'", "'--labels'", 'test_native_token_environment_and_no_default_labels'),
    ('native-token-protocol', "env['ACTIONS_RUNNER_INPUT_TOKEN']", "env['VSTS_AGENT_INPUT_TOKEN']", 'test_native_token_environment_and_no_default_labels'),
    ('registration-root', "uid=p['uid'], gid=p['uid'], home=home", "uid=None, gid=None, home=home", 'test_native_token_environment_and_no_default_labels'),
    ('online-order', "online.get('status') == 'online'", 'True', 'test_online_required_before_old_retirement'),
    ('reregister', "if not (runner / '.runner').exists():", 'if True:', 'test_idempotent_resume_never_registers_again'),
    ('acl', 'and self.host.acl_free(path)', 'and True', 'test_acl_rejected_on_resume'),
    ('sudo-command', "str(HELPER) + '\\n'", "'/bin/sh' + '\\n'", 'test_sudoers_is_exact_fixed_command'),
    ('daemon-user', "'UserName': p['user']", "'UserName': 'lwrunner'", 'test_launchdaemon_is_non_gui_service_user'),
    ('old-id', "removed == old['runnerId']", 'True', 'test_exact_old_id_required_for_retirement'),
    ('old-inventory', "digest(source.read_bytes()) == old['files'][name]", 'True', 'test_old_hash_mismatch_preserves_service_and_credentials'),
    ('rollback-status', "'retirement-blocked-local-files-restored-registration-requires-owner'", "'complete'", 'test_retirement_failure_keeps_protected_backup_and_old_files'),
    ('archive-link', '(member.isfile() or member.isdir())', 'True', 'test_archive_symlink_refused_without_execution'),
    ('rollback-file', 'if not source.exists():', 'if False:', 'test_partial_retirement_restores_deleted_credential'),
    ('retired-reappears', "not old_file.exists() and not old_file.is_symlink()", 'True', 'test_credential_reappearing_is_not_complete'),
    ('private-mode', 'stat.S_IMODE(node.st_mode) == mode', 'True', 'test_wrong_private_home_mode_refuses_resume'),
    ('reservation', "require(authorized, 'existing group/account has no installer reservation receipt')", "require(True, 'existing group/account has no installer reservation receipt')", 'test_existing_account_without_reservation_is_not_adopted'),
    ('dryrun-write', 'p = self.p\n        require(set(p)', "p = self.p\n        self.host.run(['mutation-write'])\n        require(set(p)", 'test_dryrun_has_no_mutations'),
    ('handoff-promotion', 'output.write(data)', "output.write(b'{}')", 'test_owner_handoff_private_file_can_be_promoted'),
    ('handoff-acl', 'and host.acl_free(path)', 'and True', 'test_handoff_wrong_mode_and_acl_refused'),
    ('raw-plan-binding', 'self.raw_plan_sha256 or digest', 'digest', 'test_raw_plan_file_hash_is_authority_binding'),
    ('exact-workflows', "{w['path'] for w in workflows} == MIGRATED_WORKFLOWS", 'True', 'test_exact_four_workflow_paths_not_arbitrary_four'),
    ('authority-secrets', 'require(set(authority) <=', 'require(set() <=', 'test_secret_bearing_authority_is_never_persisted'),
    ('nullable-online', "authority.get('online') or {}", "authority.get('online', {})", 'test_nullable_owner_authority_resumes_as_pending'),
    ('partial-home-resume', 'if home.exists() or home.is_symlink():', 'if True:', 'test_reserved_account_before_home_creation_resumes'),
    ('installed-config-hash', "self.receipt['configurationSha256'] = digest(config)", "self.receipt['configurationSha256'] = '0' * 64", 'test_sudoers_is_exact_fixed_command'),
    ('backup-stage', "'awaiting-owner-exact-old-registration-removal'", "'complete'", 'test_online_stage_protects_backup_before_remote_removal'),
    ('backup-before-delete', "require('old-backup-protected' in self.receipt['stages'],", 'require(True,', 'test_remote_removal_before_backup_is_refused'),
    ('inventory-nonsecret', "'files': {name: digest(data) for name, data in contents.items()}", "'files': {name: data.decode() for name, data in contents.items()}", 'test_root_inventory_is_readonly_and_nonsecret'),
    ('inventory-home-scope', "logical.is_relative_to('/Users/lwrunner')", 'True', 'test_root_inventory_cannot_leave_source_home'),
    ('inventory-symlink', 'physical = canonical(mapped(path))', 'physical = mapped(path)', 'test_root_inventory_never_follows_credential_symlink'),
    ('inventory-service', "info.get('WorkingDirectory') == str(logical)", 'True', 'test_root_inventory_binds_service_to_source_directory'),
    ('inventory-source-uid', "source['uid'] == 502", 'True', 'test_root_inventory_refuses_wrong_source_uid'),
    ('source-client-install', "self.put(self.path(SOURCE_JOB), pathlib.Path(self.inputs.get('sourceJob', p['sourceJob'])).read_bytes(), 0o755)", 'pass', 'test_reviewed_source_client_is_installed_fixed_root_program'),
    ('source-client-hash', "file_digest(source_job) == p['sourceJobSha256']", 'True', 'test_source_client_checksum_is_plan_bound'),
    ('root-helper-hash', "file_digest(helper) == p['helperSha256']", 'True', 'test_privileged_helper_checksum_is_plan_bound'),
    ('separate-collector-repo', "c['collectorRepository'].lower() not in {repo.lower() for repo in repositories}", 'True', 'test_collector_registration_cannot_be_in_source_repository'),
    ('premature-final-label', "if 'old-retired' in self.receipt['stages']:", 'if True:', 'test_final_label_before_old_retirement_cannot_authorize_backup'),
    ('promoted-label-resume', 'allowed_labels.append([CUSTOM_LABEL])', 'pass', 'test_final_label_after_durable_retirement_is_valid_on_resume'),
    ('account-attribute-conflict', "require(fields[key] == expected(p), 'existing account attribute conflicts with reservation: ' + key)", "require(True, 'existing account attribute conflicts with reservation: ' + key)", 'test_partial_account_conflicting_attribute_is_refused_not_overwritten'),
    ('service-absence-proof', "require(self.host.service_absent(old['serviceLabel']),\n                            'old service is still present", "require(True,\n                            'old service is still present", 'test_unknown_service_state_blocks_and_preserves_old_files'),
    ('retirement-intent', 'if intent is not None:', 'if False:', 'test_retirement_intent_mismatch_is_refused'),
    ('resume-skip-bootout', "if not self.host.service_absent(old['serviceLabel']):", 'if True:', 'test_crash_after_bootout_effect_resumes_without_second_bootout'),
    ('duplicate-field-write', "self.host.write_account_field(name, key, value)", "self.host.write_account_field(name, key, value)\n            self.host.write_account_field(name, key, value)", 'test_crash_after_each_account_creation_effect_resumes_exact'),
    ('service-domain-proof', 'if not _absent_diagnostic(error, label, domain_text):', 'if False:', 'test_real_service_absence_proof_checks_every_domain'),
    ('registration-absence-status', "if not (absence is not None and absence.get('status') == 'absent'", 'if not (True', 'test_absence_proof_must_match_the_exact_attempt_and_name'),
    ('registration-absence-name', "if absence.get('runnerName') != p['runnerName']:", 'if False:', 'test_absence_proof_must_match_the_exact_attempt_and_name'),
    ('registration-absence-nonce', "if absence.get('attemptNonce') != attempt.get('nonce'):", 'if False:', 'test_absence_proof_must_match_the_exact_attempt_and_name'),
    ('registration-absence-fresh', "if not (type(checked) in (int, float) and attempt.get('startedAt') <= checked <= time.time()", 'if not (True', 'test_absence_proof_must_be_fresh'),
    ('registration-budget', "require(attempts < MAX_REGISTRATION_ATTEMPTS, 'registration attempt budget is exhausted')", "require(True, 'registration attempt budget is exhausted')", 'test_registration_retry_budget_and_window_are_bounded'),
    ('registration-window', "require(now <= first + REGISTRATION_RETRY_WINDOW, 'registration retry window has expired')", "require(True, 'registration retry window has expired')", 'test_registration_retry_budget_and_window_are_bounded'),
    ('registration-local-creds', "if (runner / name).exists() or (runner / name).is_symlink():", 'if False:', 'test_local_credentials_contradicting_absence_are_refused'),
    ('registration-local-mismatch', "require(authority.get('registrationAbsence') is None,\n                    'remote absence was proven but a local registration exists: reconcile before retrying')", "require(True, 'remote absence was proven but a local registration exists: reconcile before retrying')", 'test_local_registration_with_remote_absence_mismatch_is_refused'),    ('registration-state', "require(state in ('reserved', 'unreleased'),", "require(True,", 'test_released_or_unknown_registration_is_never_retried'),
    ('registration-unreleased-alive', "require(self.host.process_identity(pid, self.p['uid']) != identity,", "require(True,", 'test_unreleased_child_alive_blocks_retry_then_dead_allows_one'),
    ('registration-reserved-pid', "require(attempt.get('pid') is None and attempt.get('startIdentity') is None,", "require(True,", 'test_reserved_intent_with_a_pid_is_refused'),
    ('registration-release-order', "self.receipt['registrationAttempt']['state'] = 'released'\n        self.save()\n        gate.release()", "gate.release()\n        self.receipt['registrationAttempt']['state'] = 'released'\n        self.save()", 'test_released_gated_child_is_never_retried_even_if_dead'),
    ('registration-challenge-type', 'raise ReconciliationRequired(reason)', 'raise Refused(reason)', 'test_missing_proof_for_unstarted_intent_is_a_structured_challenge'),
    ('process-ambiguous', "if result.returncode == 1 and not output.strip() and not errors.strip():", 'if True:', 'test_process_identity_distinguishes_exact_dead_live_and_ambiguous'),
    ('process-identity-fields', "require(len(parts) == 2 and parts[0] == str(uid) and parts[1].strip(),", 'require(True,', 'test_process_identity_distinguishes_exact_dead_live_and_ambiguous'),
    ('child-identity-check', "if not (isinstance(gate.start_identity, str) and gate.start_identity):", 'if False:', 'test_unverifiable_child_identity_never_releases_or_registers'),

]


def mutations():
    global setup
    for name, old, new, case in MUTATIONS:
        if old not in TEXT:
            raise RuntimeError('stale mutation: ' + name)
        setup = load(TEXT.replace(old, new))
        result = unittest.TextTestRunner(stream=io.StringIO()).run(Contracts(case))
        if result.wasSuccessful():
            raise RuntimeError('SURVIVED: ' + name)
        setup = load(TEXT)
        restored = unittest.TextTestRunner(stream=io.StringIO()).run(Contracts(case))
        if not restored.wasSuccessful():
            raise RuntimeError('restored test failed: ' + name)
        print(name + ': RED (mutation rejected), restored GREEN')


if __name__ == '__main__':
    if '--mutations' in sys.argv:
        mutations()
    else:
        suite = unittest.defaultTestLoader.loadTestsFromTestCase(Contracts)
        result = unittest.TextTestRunner(verbosity=1).run(suite)
        print('GUI_SETUP_CONTRACT_RESULT ' + json.dumps(dict(executed=result.testsRun, skipped=len(result.skipped),
                                                           failures=len(result.failures), errors=len(result.errors))))
        sys.exit(0 if result.wasSuccessful() and not result.skipped else 1)
