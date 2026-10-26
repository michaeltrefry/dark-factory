#!/usr/bin/env python3
"""Safe contracts: no GUI, root, real runner account, network or native signals."""
import ast
import base64
import importlib.util
import io
import hashlib
import json
import os
import re
from pathlib import Path
import subprocess
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
def load_source(mutation=None):
    module = types.ModuleType('sealed_source')
    for name in ('factory-ios-remote.py', 'factory-gui-remote.py', 'factory-gui-source-job.py'):
        text = (ROOT / 'scripts' / name).read_text()
        if mutation and mutation[0] == name:
            for old, new in mutation[1]:
                if old not in text: raise ValueError('stale production mutation')
                text = text.replace(old, new, 1)
        code = ast.parse(text.replace('GUI_HELPER_HASH', hashlib.sha256(b'trusted helper').hexdigest()))
        code.body = [n for n in code.body if not (isinstance(n, ast.If) and ast.unparse(n.test) == "__name__ == '__main__'")]
        exec(compile(code, name, 'exec'), module.__dict__)
    return module

r = load_source()


def plan():
    return dict(version=1, workflow='.github/workflows/rust-catalog-tests.yml', jobId='cargo-test', timeoutMinutes=30,
                submodules='false', env={'CARGO_BUILD_JOBS': '2'}, steps=[dict(name='Run', id='', run='cargo test', condition='success()', env={}, timeoutMinutes=30)], artifacts=[])


def encoded(value):
    return base64.b64encode(json.dumps(value).encode()).decode()


class Contracts(unittest.TestCase):
    def test_plan_preserves_scripts_and_exact_workflow(self):
        value = plan()
        self.assertEqual(value, r.source_plan(encoded(value)))
        value['workflow'] = '.github/workflows/attacker.yml'
        with self.assertRaisesRegex(ValueError, 'allowlisted'):
            r.source_plan(encoded(value))

    def test_plan_accepts_missing_or_bash_shell_and_refuses_every_other_shell(self):
        value = plan()
        self.assertEqual('implicit-bash', r.source_plan(encoded(value))['steps'][0].get('shell', 'implicit-bash'))
        value = plan(); value['steps'][0]['shell'] = 'bash'
        self.assertEqual('bash', r.source_plan(encoded(value))['steps'][0]['shell'])
        for shell in ('sh', 'zsh', '/bin/sh', 'bash -x', ''):  # '', 'sh' and 'bash -x': different argv or unknown
            value = plan(); value['steps'][0]['shell'] = shell
            with self.subTest(shell=shell), self.assertRaisesRegex(ValueError, 'source shell'):
                r.source_plan(encoded(value))

    def _job_patches(self, config, Principal):
        return [patch.object(r, 'verify_gui_helper'), patch.object(r, 'command', return_value=(0, 'Background')),
                patch.object(r, 'gui_operation', side_effect=lambda op, *a, **k: json.dumps(config) if op == 'check' else ''),
                patch.object(r, 'source_archive', side_effect=lambda repo, sha, destination, path: destination.mkdir(exist_ok=True)),
                patch.object(r, 'source_git_metadata', lambda *a, **k: None),
                patch.object(r, 'GuiPrincipal', Principal),
                patch.object(r, 'GUI_HELPER', types.SimpleNamespace(read_bytes=lambda: b'trusted helper')),
                patch.object(r, 'reconcile_source_exports', lambda config: None)]

    def test_job_run_uses_pipefail_only_when_the_plan_names_bash(self):
        outer = self
        recorded = []
        class Principal:
            def __init__(self, workspace):
                self.root = workspace; self.worktree = workspace / 'base'
            def share(self, path): pass
            def run(self, args, cwd, extra_environment=None, timeout=None):
                recorded.append(args)
                if args[0] == '/usr/bin/touch': Path(args[1]).touch(); return 0, ''
                if args[0] == '/usr/bin/python3': return 0, json.dumps({'env': '', 'path': ''})
                outer.assertEqual('/bin/bash', args[0]); outer.assertEqual('--noprofile', args[1]); outer.assertEqual('--norc', args[2])
                return 0, ''
            def close(self): pass
        def archive(repo, sha, destination, path):
            destination.mkdir(exist_ok=True)
        for shell, expect_pipefail in ((None, False), ('bash', True)):
            with self.subTest(shell=shell):
                recorded.clear()
                value = plan(); value['steps'] = [dict(name='Run', id='', run='cargo test', condition='success()', env={}, timeoutMinutes=30)]
                if shell: value['steps'][0]['shell'] = shell
                with tempfile.TemporaryDirectory() as directory:
                    root = Path(directory).resolve(); state = root / 'state'; state.mkdir(); output = root / 'output'; output.touch()
                    config = dict(collectorUid=os.geteuid(), collectorRepository='acme/control', runnerName='factory-gui-12345678', trustedRepositories=['acme/source'], stateDirectory=str(state), hash='a' * 64)
                    env = {'FACTORY_GUI_SOURCE_PROTOCOL': r.SOURCE_PROTOCOL, 'FACTORY_GUI_SOURCE_PLAN': encoded(value), 'FACTORY_GUI_SOURCE_REPOSITORY': 'acme/source', 'FACTORY_GUI_SOURCE_SHA': '1' * 40,
                           'FACTORY_GUI_SOURCE_RUN_ID': '12', 'FACTORY_GUI_SOURCE_RUN_ATTEMPT': '1', 'GITHUB_REPOSITORY': 'acme/control', 'RUNNER_NAME': 'factory-gui-12345678', 'GITHUB_OUTPUT': str(output), 'HOME': str(root)}
                    context = patch.dict(os.environ, env)
                    patches = [patch.object(sys, 'argv', ['installed-client'])] + self._job_patches(config, Principal)
                    with context:
                        try:
                            for p in patches: p.start()
                            self.assertEqual(0, r.source_job_main())
                        finally:
                            for p in patches: p.stop()
                    runs = [args for args in recorded if args[0] == '/bin/bash']
                    self.assertEqual(1, len(runs))
                    self.assertEqual(['-e', '-o', 'pipefail', str(runs[0][-1])], ['-e', '-o', 'pipefail', runs[0][-1]])
                    self.assertEqual(expect_pipefail, '-o' in runs[0])
                    script = Path(runs[0][-1]).read_text()
                    self.assertEqual('cargo test', script)

    def test_source_credentials_and_loader_environment_are_never_forwarded(self):
        for key in ('GITHUB_TOKEN', 'ACTIONS_RUNTIME_TOKEN', 'FACTORY_ROUTER_KEY', 'DYLD_INSERT_LIBRARIES', 'LD_PRELOAD', 'HOME', 'PATH', 'BASH_ENV', 'USER_SECRET', 'MY_PASSWORD'):
            with self.subTest(key=key), self.assertRaises(ValueError):
                r.source_environment({key: 'bad'}, '/source', '/temp')
        self.assertEqual({'TEST_RUNNER_HELPER': '/source/bin/helper'}, r.source_environment({'TEST_RUNNER_HELPER': '${source}/bin/helper'}, '/source', '/temp'))

    def test_declared_source_placeholders_and_only_known_variable_expand(self):
        with patch.dict(os.environ, {'FACTORY_GUI_VAR_LW_REQUIRE_REAL': '1'}):
            self.assertEqual({'LW_REQUIRE_REAL': '1', 'OUT': '/temp/out'}, r.source_environment({'LW_REQUIRE_REAL': '${var:LW_REQUIRE_REAL}', 'OUT': '${temp}/out'}, '/source', '/temp'))
        with self.assertRaises(ValueError):
            r.source_environment({'OUT': '${owner}/secrets'}, '/source', '/temp')

    def test_unknown_plan_fields_conditions_and_unbounded_time_refused(self):
        for update in ({'version': 2}, {'version': True}, {'timeoutMinutes': 999}, {'submodules': 'true'}, {'extra': True}):
            value = plan(); value.update(update)
            with self.subTest(update=update), self.assertRaises(ValueError):
                r.source_plan(encoded(value))
        value = plan(); value['steps'][0]['condition'] = 'arbitrary()'
        with self.assertRaises(ValueError):
            r.source_plan(encoded(value))

    def test_source_command_files_persist_env_and_workspace_only_path(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            p = types.SimpleNamespace(root=root, worktree=root / 'base')
            p.run = lambda *a, **k: (0, json.dumps({'env': 'LW_INFERENCE_REAL=1\nMULTI<<END\na\nb\nEND\n', 'path': str(root / 'bin')}))
            env, paths = r.source_command_state(p, root)
            self.assertEqual({'LW_INFERENCE_REAL': '1', 'MULTI': 'a\nb'}, env)
            self.assertEqual([str(root / 'bin')], paths)
            p.run = lambda *a, **k: (0, json.dumps({'env': 'GITHUB_TOKEN=bad\n', 'path': ''}))
            with self.assertRaises(ValueError):
                r.source_command_state(p, root)
            p.run = lambda *a, **k: (0, json.dumps({'env': '', 'path': '/Users/owner/bin'}))
            with self.assertRaises(ValueError):
                r.source_command_state(p, root)

    def test_source_artifacts_are_bounded_regular_files_not_credential_links(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); source = root / 'source'; source.mkdir(); output = root / 'out'; output.mkdir()
            (source / 'proof.txt').write_text('proof')
            self.assertEqual(1, r.source_export(source, ['*.txt'], output, [0]))
            self.assertEqual('proof', (output / 'proof.txt').read_text())
            outside = root / 'private'; outside.write_text('DO NOT READ')
            (source / 'leak').symlink_to(outside)
            with self.assertRaises((ValueError, OSError)):
                r.source_export(source, ['leak'], output, [0])
            for path in ('../private', '/Users/owner/secret'):
                with self.assertRaises(ValueError):
                    r.source_export(source, [path], output, [0])
            with self.assertRaisesRegex(ValueError, 'byte bound'):
                r.source_export(source, ['*.txt'], root / 'over', [100 * 1024 * 1024])

    def test_full_job_uses_one_lease_fresh_source_identity_and_real_cleanup_before_publication(self):
        import shutil
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve(); state = root / 'state'; state.mkdir(); output = root / 'output'; output.touch()
            events = []; roots = []; fail_cleanup = [False]; fail_source = [False]
            config = dict(collectorUid=os.geteuid(), sourceUid=502, collectorRepository='acme/control', runnerName='factory-gui-12345678', trustedRepositories=['acme/source'], stateDirectory=str(state), hash='a'*64, developer='/Applications/Xcode.app/Contents/Developer')
            value = plan(); value['artifacts'] = [dict(name='proof', paths=['proof.txt'], condition='always()', ifNoFilesFound='error', retentionDays=1)]
            value['steps'] += [dict(name='Only if enabled',id=None,run='conditional',condition="env.LW_INFERENCE_REAL == '1'",env={},timeoutMinutes=10),dict(name='Always',id=None,run='always',condition='always()',env={},timeoutMinutes=10)]
            def operation(op, workspace=None, extra=None, cleanup=False):
                if op == 'check': return json.dumps(config)
                self.assertTrue((state/'active.json').exists())
                events.append(op)
                if op == 'remove':
                    if fail_cleanup[0]: raise ValueError('cleanup unknown')
                    shutil.rmtree(workspace, ignore_errors=True)
                return ''
            def archive(repo, sha, destination, path):
                self.assertEqual(('acme/source','1'*40),(repo,sha)); self.addCleanup(shutil.rmtree,destination.parent,True); destination.mkdir(exist_ok=True); (destination/'proof.txt').write_text('fresh'); roots.append(destination)
            class Principal:
                def __init__(self, workspace):
                    self.root=workspace; self.worktree=workspace/'base'; self.home=workspace/'home'; self.home.mkdir(); self.env={}
                def share(self,path): events.append('share')
                def run(self,args,cwd,extra_environment=None,timeout=None):
                    if args[0]=='/usr/bin/touch': Path(args[1]).touch(); return 0,''
                    if args[0]=='/usr/bin/python3': return 0,json.dumps({'env':'LW_INFERENCE_REAL=1\n','path':''})
                    self_outer.assertEqual('/bin/bash',args[0]); self_outer.assertEqual(str(self.worktree),extra_environment['GITHUB_WORKSPACE'])
                    self_outer.assertNotIn('GITHUB_TOKEN',extra_environment)
                    script=Path(args[-1]).read_text(); events.append(script); return (1 if fail_source[0] and script=='cargo test' else 0),'source output ::not-an-action-command::'
                def close(self):
                    operation('remove',self.root,cleanup=True); r.GUI_LEASE.cleaned=True
            self_outer=self
            env={'FACTORY_GUI_SOURCE_PROTOCOL':r.SOURCE_PROTOCOL,'FACTORY_GUI_SOURCE_PLAN':encoded(value),'FACTORY_GUI_SOURCE_REPOSITORY':'acme/source','FACTORY_GUI_SOURCE_SHA':'1'*40,'FACTORY_GUI_SOURCE_RUN_ID':'12','FACTORY_GUI_SOURCE_RUN_ATTEMPT':'1','GITHUB_REPOSITORY':'acme/control','RUNNER_NAME':'factory-gui-12345678','GITHUB_OUTPUT':str(output),'GITHUB_TOKEN':'MUST_NOT_REACH_SOURCE','HOME':str(root)}
            with patch.dict(os.environ,env), patch.object(sys,'argv',['installed-client']), patch.object(r,'GUI_HELPER',types.SimpleNamespace(read_bytes=lambda:b'trusted helper')), patch.object(r,'verify_gui_helper'), patch.object(r,'command',return_value=(0,'Background')), patch.object(r,'gui_operation',side_effect=operation), patch.object(r,'source_archive',side_effect=archive), patch.object(r,'source_git_metadata',lambda *a,**k:None), patch.object(r,'GuiPrincipal',Principal):
                self.assertEqual(0,r.source_job_main())
                self.assertEqual(['cargo test','conditional','always'],[e for e in events if e in ('cargo test','conditional','always')])
                self.assertFalse((state/'active.json').exists()); self.assertFalse(roots[0].exists())
                exports=output.read_text().splitlines(); self.assertEqual(1,len(exports)); export=Path(exports[0].split('=',1)[1]); self.assertEqual('fresh',(export/'0/proof.txt').read_text()); self.assertTrue(json.loads((export/'receipt.json').read_text())['succeeded'])
                # Failed teardown never publishes and leaves the exact durable
                # journal; next invocation reconciles it under the same lease.
                output.write_text(''); fail_cleanup[0]=True
                with self.assertRaisesRegex(ValueError,'cleanup unknown'): r.source_job_main()
                self.assertEqual('',output.read_text()); self.assertTrue((state/'active.json').exists())
                fail_cleanup[0]=False
                self.assertEqual(0,r.source_job_main()); self.assertFalse((state/'active.json').exists()); self.assertEqual(3,len(set(roots)))
                output.write_text(''); events.clear(); fail_source[0]=True
                self.assertEqual(1,r.source_job_main())
                self.assertEqual(['cargo test','always'],[e for e in events if e in ('cargo test','conditional','always')])
                failed_export=Path(output.read_text().strip().split('=',1)[1]); self.assertFalse(json.loads((failed_export/'receipt.json').read_text())['succeeded'])

    def test_fixed_recovery_probe_blocks_new_source_on_failed_cleanup_then_restores_only_own_fixture(self):
        import shutil
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory).resolve();state=root/'state';state.mkdir();roots=[];commands=[]
            config=dict(collectorUid=os.geteuid(),collectorRepository='acme/control',runnerName='factory-gui-12345678',stateDirectory=str(state),hash='a'*64)
            def operation(op,workspace=None,extra=None,cleanup=False):
                if op=='check':return json.dumps(config)
                self.assertTrue((state/'active.json').is_file())
                if op=='remove':
                    if (workspace/'gui-command-0.json').exists(): raise ValueError('fixture uncertainty refused by protected cleanup')
                    shutil.rmtree(workspace)
                return ''
            class Workspace:
                def __enter__(self):
                    self.root=root/('job-'+str(len(roots)));self.root.mkdir();roots.append(self.root);self.claimed=False;r.GUI_LEASE.record(self.root);return self
                def handoff(self):self.claimed=True
                def __exit__(self,*a):
                    if not self.claimed:shutil.rmtree(self.root)
            class Principal:
                def __init__(self,workspace):self.root=workspace
                def run(self,args,cwd):
                    commands.append(args);return 0,''
                def close(self):operation('remove',self.root,cleanup=True);r.GUI_LEASE.cleaned=True
            with patch.dict(os.environ,GITHUB_REPOSITORY='acme/control',RUNNER_NAME='factory-gui-12345678'),patch.object(r,'verify_gui_helper'),patch.object(r,'command',return_value=(0,'Background')),patch.object(r,'gui_operation',side_effect=operation),patch.object(r,'GuiWorkspace',Workspace),patch.object(r,'GuiPrincipal',Principal):
                result=r.exercise_source_recovery()
            self.assertFalse(result['literalCrash']);self.assertTrue(result['validUncleanJournalReconciled']);self.assertTrue(result['failedCleanupBlockedReuse']);self.assertFalse(result['sourceStartedWhileBlocked']);self.assertTrue(result['restoredOnlyOwnFixture']);self.assertTrue(result['normalReconciliationRestoredReuse']);self.assertTrue(result['freshWorkspace']);self.assertEqual('a'*64,result['configurationSha256'])
            self.assertEqual([['/usr/bin/true'],['/usr/bin/true']],commands);self.assertEqual(3,len(roots));self.assertTrue(all(not path.exists() for path in roots));self.assertFalse((state/'active.json').exists());self.assertTrue(result['lease']['cleaned']);self.assertTrue(result['lease']['reconciled'])
            self.assertLessEqual(result['lease']['startedUtcSeconds'],result['lease']['completedUtcSeconds'])

    def test_interrupted_probe_fixture_is_repaired_only_when_its_probe_lock_is_free(self):
        import fcntl,shutil
        with tempfile.TemporaryDirectory() as directory, tempfile.TemporaryDirectory(prefix='factory-ios-',dir=tempfile.gettempdir()) as workspace:
            state=Path(directory).resolve();root=Path(workspace).resolve();probe=root/'gui-command-0.json';probe.write_text(r.PROBE_UNCERTAINTY_BODY);probe.chmod(0o600)
            config=dict(stateDirectory=str(state),collectorUid=os.geteuid(),hash='b'*64)
            (state/'active.json').write_text(json.dumps({'root':str(root),'devices':[],'deviceIntents':[]}))
            witness=state/'probe-uncertainty.json';witness.write_text(json.dumps({'version':1,'root':str(root),'configurationSha256':'b'*64}));witness.chmod(0o600)
            removals=[]
            def operation(op,old,extra=None,cleanup=False):
                self.assertEqual('remove',op)
                if probe.exists(): raise ValueError('protected cleanup refuses uncertain record')
                removals.append(str(old));shutil.rmtree(old)
            with open(state/'probe.lock','a+b') as owner,patch.object(r,'GUI_TEMP_ROOT',root.parent),patch.object(r,'gui_operation',side_effect=operation):
                fcntl.flock(owner.fileno(),fcntl.LOCK_EX|fcntl.LOCK_NB)
                with self.assertRaises(ValueError):
                    with r.GuiLease(config):self.fail('live intentional refusal was bypassed')
                self.assertTrue(probe.exists());self.assertTrue(witness.exists());self.assertEqual([],removals)
                fcntl.flock(owner.fileno(),fcntl.LOCK_UN)
                probe.write_text('not our fixture')
                with self.assertRaisesRegex(ValueError,'fixture changed'):
                    with r.GuiLease(config):self.fail('unknown record was erased')
                self.assertEqual('not our fixture',probe.read_text());self.assertTrue(witness.exists());self.assertEqual([],removals)
                probe.write_text(r.PROBE_UNCERTAINTY_BODY)
                try:
                    with r.GuiLease(config) as lease:self.assertTrue(lease.reconciled)
                except ValueError as error:self.fail('an interrupted OWN fixture must reconcile automatically: '+str(error))
            self.assertFalse(probe.exists());self.assertFalse(witness.exists());self.assertFalse((state/'active.json').exists());self.assertEqual([str(root)],removals)

    def test_source_submodules_follow_exact_public_gitlink_pins_without_git_credentials(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory); source=root/'source'; source.mkdir(); staging=root/'archives'; staging.mkdir()
            (source/'.gitmodules').write_text('[submodule "vendor/Compositor"]\npath = vendor/Compositor\nurl = https://github.com/robbietilton/Compositor.git\n')
            calls=[]
            def tree(repo,sha):
                calls.append(('tree',repo,sha))
                return [{'path':'vendor','type':'tree','sha':'2'*40}] if sha=='1'*40 else [{'path':'Compositor','mode':'160000','type':'commit','sha':'3'*40}]
            def archive(repo,sha,destination,path): calls.append(('archive',repo,sha)); destination.mkdir(exist_ok=True); (destination/'source.swift').write_text('pinned')
            with patch.object(r,'source_public_json',side_effect=tree),patch.object(r,'source_archive',side_effect=archive):
                r.source_submodules('acme/source','1'*40,source,staging)
            self.assertEqual([('tree','acme/source','1'*40),('tree','acme/source','2'*40),('archive','robbietilton/Compositor','3'*40)],calls)
            self.assertTrue((source/'vendor/Compositor/source.swift').is_file())
            for value in ('../../private','/Users/owner/private'):
                (source/'.gitmodules').write_text('[submodule "bad"]\npath = '+value+'\nurl = https://github.com/o/r.git\n')
                with patch.object(r,'source_public_json',return_value=[]),self.assertRaises(ValueError): r.source_submodules('acme/source','1'*40,source,staging)
            (source/'.gitmodules').write_text('[submodule "bad"]\npath = x\nurl = ssh://private/repo\n')
            with patch.object(r,'source_public_json',return_value=[]),self.assertRaises(ValueError):r.source_submodules('acme/source','1'*40,source,staging)

    def test_export_cleanup_reclaims_only_old_exact_collector_roots_and_preserves_live_uploads(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory).resolve(); state=root/'state';state.mkdir(); private=root/'unrelated';private.mkdir()
            config=dict(stateDirectory=str(state),collectorUid=os.geteuid())
            old=state/'factory-source-artifacts-12345678';old.mkdir(mode=0o700); recent=state/'factory-source-artifacts-87654321';recent.mkdir(mode=0o700)
            os.utime(old,(r.time.time()-22000,r.time.time()-22000))
            with patch.object(r,'verify_gui_helper'),patch.object(r,'gui_operation',return_value=json.dumps(config)):
                r.reconcile_source_exports(config)
                self.assertFalse(old.exists());self.assertTrue(recent.exists());self.assertTrue(private.exists())
                with self.assertRaises(ValueError): r.cleanup_source_export(str(private))
                link=state/'factory-source-artifacts-aaaaaaaa';link.symlink_to(private)
                with self.assertRaises(ValueError):r.cleanup_source_export(str(link))
                with self.assertRaises(ValueError):r.reconcile_source_exports(config)
                link.unlink();r.cleanup_source_export(str(recent));self.assertFalse(recent.exists())

    def test_exact_mirror_paths_are_safe_for_modern_upload_pack_no_wildcard(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve(); home = root / 'home'; home.mkdir()
            listed = root / 'listed.git'; unlisted = root / 'unlisted.git'
            env = {'PATH': '/usr/bin:/bin', 'HOME': str(home), 'GIT_CONFIG_NOSYSTEM': '1', 'GIT_CONFIG_GLOBAL': '/dev/null'}
            for path in (listed, unlisted):
                subprocess.run(['/usr/bin/git', 'init', '--bare', str(path)], env=env, capture_output=True, check=True)
                for entry in sorted(path.rglob('*'), reverse=True):
                    entry.chmod(0o555 if entry.is_dir() else 0o444)
                path.chmod(0o555)
            try:
                p = types.SimpleNamespace(home=home, env={})
                def source_run(args, cwd):
                    result = subprocess.run([sys.executable, *args[1:]], cwd=cwd, env=env, capture_output=True)
                    return result.returncode, result.stdout.decode() + result.stderr.decode()
                p.run = source_run
                r.install_package_mirrors(p, [{'original': 'https://public.example/listed', 'mirror': listed.as_uri()}])
                expected = str(listed)
                config = home / '.factory-gitconfig'
                self.assertEqual([expected], subprocess.run(['/usr/bin/git', 'config', '--file', str(config), '--get-all', 'safe.directory'], capture_output=True, text=True, check=True).stdout.splitlines())
                different = dict(env, **p.env, GIT_TEST_ASSUME_DIFFERENT_OWNER='1')
                allowed = subprocess.run(['/usr/bin/git', 'upload-pack', '--advertise-refs', str(listed)], env=different, capture_output=True)
                self.assertEqual(0, allowed.returncode, allowed.stderr.decode())
                # Apple Git does not apply GIT_TEST_ASSUME_DIFFERENT_OWNER to
                # upload-pack. Read the ACTUAL inherited protected config and
                # model a different-owner upload-pack's exact allowlist decision;
                # no root/chown/live account is used to manufacture ownership.
                inherited_result = subprocess.run(['/usr/bin/git', 'config', '--global', '--get-all', 'safe.directory'], env=different, capture_output=True, text=True)
                self.assertEqual(0, inherited_result.returncode, 'source child must inherit isolated mirror authorization')
                inherited = inherited_result.stdout.splitlines()
                def different_owner_upload(path):
                    if str(path) not in inherited:
                        raise PermissionError('dubious ownership')
                    return subprocess.run(['/usr/bin/git', 'upload-pack', '--advertise-refs', str(path)], env=different, capture_output=True).returncode
                self.assertEqual(0, different_owner_upload(listed))
                with self.assertRaisesRegex(PermissionError, 'dubious ownership'):
                    different_owner_upload(unlisted)
                self.assertNotIn('*', config.read_text())
                self.assertEqual(str(config), p.env['GIT_CONFIG_GLOBAL'])
            finally:
                for path in (listed, unlisted):
                    path.chmod(0o755)
                    for entry in path.rglob('*'):
                        entry.chmod(0o755 if entry.is_dir() else 0o644)

    def test_mirror_config_rejects_writable_remote_and_symlink_paths(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve(); home = root / 'home'; home.mkdir(); writable = root / 'writable.git'; writable.mkdir(); link = root / 'link.git'; link.symlink_to(writable)
            for url in (writable.as_uri(), link.as_uri(), 'https://public.example/repo.git', 'file://foreign/repo.git'):
                result = subprocess.run([sys.executable, '-I', '-c', r.CONFIG_SCRIPT, str(home), json.dumps({'mirrors': {'version': 1, 'object': [{'original': 'x', 'mirror': url}]}, 'registries': None}), str(root)], capture_output=True)
                self.assertNotEqual(0, result.returncode, url)


    def test_source_git_metadata_builds_a_sanitized_self_contained_object_db_before_handoff(self):
        """Real git fixture (no network): the sanitized .git answers rev-parse HEAD,
        describe --tags, and submodule status against the actual git binary with
        the pinned commit, its tags, and no other refs, no hooks, no alternates."""
        sha = {}
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            origin = root / 'origin'; (origin / 'sub').mkdir(parents=True)
            (origin / 'file.txt').write_text('pinned source')
            env = {'PATH': '/usr/bin:/bin', 'HOME': str(root), 'GIT_CONFIG_NOSYSTEM': '1', 'GIT_CONFIG_GLOBAL': '/dev/null'}
            subprocess.run(['/usr/bin/git', 'init', str(origin)], env=env, capture_output=True, check=True)
            subprocess.run(['/usr/bin/git', '-C', str(origin), 'add', '.'], env=env, capture_output=True, check=True)
            subprocess.run(['/usr/bin/git', '-C', str(origin, ), '-c', 'user.email=t@t', '-c', 'user.name=t', 'commit', '-m', 'source'], env=env, capture_output=True, check=True)
            subprocess.run(['/usr/bin/git', '-C', str(origin), 'tag', 'v1.2.3'], env=env, capture_output=True, check=True)
            sha['commit'] = subprocess.run(['/usr/bin/git', '-C', str(origin), 'rev-parse', 'HEAD'], env=env, capture_output=True, text=True, check=True).stdout.strip()
            (origin / 'other.txt').write_text('later')
            subprocess.run(['/usr/bin/git', '-C', str(origin), 'add', '.'], env=env, capture_output=True, check=True)
            subprocess.run(['/usr/bin/git', '-C', str(origin), '-c', 'user.email=t@t', '-c', 'user.name=t', 'commit', '-m', 'later'], env=env, capture_output=True, check=True)
            packdir = root / 'packs'; packdir.mkdir()
            pack = packdir / 'metadata.pack'
            from unittest.mock import patch as _patch
            captured = {}
            def fake_fetch(url_text, revision, destination, depth=1, wanted=None):
                captured['url'] = url_text; captured['revision'] = revision; captured['depths'] = captured.get('depths', []) + [depth]
                packresult = subprocess.run(['/usr/bin/git', '--git-dir=' + str(origin / '.git'), 'pack-objects', '--revs', str(packdir) + '/p'],
                                            input=sha['commit'] + '\n', env=env, capture_output=True, text=True)
                self.assertEqual(0, packresult.returncode, packresult.stderr)
                name = packresult.stdout.strip().splitlines()[-1]
                import shutil
                shutil.copyfile(packdir / ('p-' + name + '.pack'), Path(str(destination)).resolve())
                shutil.copyfile(packdir / ('p-' + name + '.idx'), Path(str(destination)).resolve().with_suffix('.idx'))
                return None
            def fake_refs(url_text, accept):
                self.assertEqual('application/x-git-upload-pack-advertisement', accept)
                result = subprocess.run(['/usr/bin/git', '--git-dir=' + str(origin / '.git'), 'for-each-ref', '--format=%(objectname) %(refname)'],
                                         env=env, capture_output=True, text=True, check=True)
                # pkt-line: 4-hex length includes the 4-byte prefix and the
                # trailing newline of each line, matching smart HTTP adverts.
                body = b''
                for line in result.stdout.splitlines():
                    body += ('%04x' % (len(line) + 5)).encode() + line.encode() + b'\n'
                head = subprocess.run(['/usr/bin/git', '--git-dir=' + str(origin / '.git'), 'rev-parse', 'HEAD'],
                                      env=env, capture_output=True, text=True, check=True).stdout.strip()
                body += ('%04x' % (len(head + ' HEAD') + 5)).encode() + (head + ' HEAD').encode() + b'\n'
                body += b'0000'
                return body, {}
            destination = root / 'checkout'; destination.mkdir()
            (destination / 'file.txt').write_text('pinned source')
            (destination / 'sub').mkdir()
            staging = root / 'staging'; staging.mkdir()
            with _patch.object(r, 'fetch_package_pack', side_effect=fake_fetch), _patch.object(r, 'public_resource', side_effect=fake_refs):
                r.source_git_metadata('acme/source', sha['commit'], destination, staging)
            admin = destination / '.git'
            self.assertTrue((admin / 'HEAD').read_text().strip() == sha['commit'])
            self.assertFalse((admin / 'hooks').exists())
            self.assertFalse((admin / 'objects/info/alternates').exists())
            self.assertIn('bare = false', (admin / 'config').read_text())
            self.assertFalse((admin / 'objects' / 'info' / 'alternates').exists())
            self.assertFalse((admin / 'hooks').exists())
            self.assertFalse((admin / 'logs').exists())
            refs = subprocess.run(['/usr/bin/git', '--git-dir=' + str(admin), 'for-each-ref', '--format=%(refname)'],
                                  env=env, capture_output=True, text=True, check=True).stdout.split()
            self.assertIn('refs/tags/v1.2.3', refs)
            self.assertNotIn('refs/heads/main', refs)
            self.assertEqual(sha['commit'], subprocess.run(['/usr/bin/git', '--git-dir=' + str(admin), 'rev-parse', 'HEAD'],
                                  env=env, capture_output=True, text=True, check=True).stdout.strip())
            described = subprocess.run(['/usr/bin/git', '--git-dir=' + str(admin), 'describe', '--tags'],
                                        env=env, capture_output=True, text=True)
            self.assertEqual(0, described.returncode, described.stderr)
            self.assertEqual('v1.2.3', described.stdout.strip())
            status = subprocess.run(['/usr/bin/git', '--git-dir=' + str(admin), '--work-tree=' + str(destination), 'submodule', 'status'],
                                    env=env, capture_output=True, text=True)
            # No .gitmodules: git-submodule reports nothing and exits 0 only from
            # a real work tree; with no work tree it refuses (exit 1) — accept the
            # exact refusal, proving the command ran against sanitized metadata
            # and no source-owned configuration.
            self.assertIn(status.returncode, (0, 1))
            self.assertNotIn('fatal: not a git repository', status.stderr)
            for entry in admin.rglob('*'):
                mode = entry.stat().st_mode & 0o777
                self.assertEqual(0o555 if entry.is_dir() else 0o444, mode, str(entry))
            config_text = (admin / 'config').read_text()
            self.assertNotIn('remote', config_text)
            self.assertNotIn('url', config_text)


    def test_source_git_metadata_carries_the_pinned_gitlink_graph_for_real_submodule_status(self):
        """Submodules PRESENT in the fixture: the sanitized .git answers the real
        `git submodule status` with the exact pinned gitlink revision, proving the
        recursive public gitlink graph is carried through the sanitized object db."""
        sha = {}
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            env = {'PATH': '/usr/bin:/bin', 'HOME': str(root), 'GIT_CONFIG_NOSYSTEM': '1', 'GIT_CONFIG_GLOBAL': '/dev/null'}
            vendee = root / 'Compositor'; vendee.mkdir()
            (vendee / 'inside.swift').write_text('pinned dependency')
            subprocess.run(['/usr/bin/git', 'init', str(vendee)], env=env, capture_output=True, check=True)
            subprocess.run(['/usr/bin/git', '-C', str(vendee), 'add', '.'], env=env, capture_output=True, check=True)
            subprocess.run(['/usr/bin/git', '-C', str(vendee), '-c', 'user.email=t@t', '-c', 'user.name=t', 'commit', '-qm', 'dependency'], env=env, capture_output=True, check=True)
            dep = subprocess.run(['/usr/bin/git', '-C', str(vendee), 'rev-parse', 'HEAD'], env=env, capture_output=True, text=True, check=True).stdout.strip()
            origin = root / 'origin'; origin.mkdir()
            (origin / 'file.txt').write_text('pinned source')
            subprocess.run(['/usr/bin/git', 'init', str(origin)], env=env, capture_output=True, check=True)
            subprocess.run(['/usr/bin/git', '-C', str(origin), 'add', '.'], env=env, capture_output=True, check=True)
            subprocess.run(['/usr/bin/git', '-C', str(origin), '-c', 'protocol.file.allow=always', 'submodule', 'add', '-b', 'master', str(vendee / '.git'), 'vendor/Compositor'],
                            env=env, capture_output=True, check=True)
            subprocess.run(['/usr/bin/git', '-C', str(origin), '-c', 'user.email=t@t', '-c', 'user.name=t', 'commit', '-qm', 'source'], env=env, capture_output=True, check=True)
            subprocess.run(['/usr/bin/git', '-C', str(origin), 'tag', 'v2.0.0'], env=env, capture_output=True, check=True)
            sha['commit'] = subprocess.run(['/usr/bin/git', '-C', str(origin), 'rev-parse', 'HEAD'], env=env, capture_output=True, text=True, check=True).stdout.strip()
            packdir = root / 'packs'; packdir.mkdir()
            staging = root / 'staging'; staging.mkdir()
            destination = root / 'checkout'; destination.mkdir()
            # The checkout carries the source tree WITHOUT the real submodule
            # contents, exactly as source_archive+source_submodules populate it.
            (destination / 'file.txt').write_text('pinned source')
            (destination / 'vendor' / 'Compositor').mkdir(parents=True)
            (destination / 'vendor' / 'Compositor' / 'inside.swift').write_text('pinned dependency')
            (destination / '.gitmodules').write_text('[submodule "vendor/Compositor"]\npath = vendor/Compositor\nurl = ' + str(vendee / '.git') + '\n')
            from unittest.mock import patch as _patch
            def fake_fetch(url_text, revision, destination_pack, depth=1, wanted=None):
                # Emulate fetch_package_pack's bounded shallow pack at the
                # caller's depth; index-pack runs with cwd=staging, so absolute
                # paths resolve.
                packresult = subprocess.run(['/usr/bin/git', '--git-dir=' + str(origin / '.git'), 'pack-objects', '--revs', str(packdir) + '/p'],
                                            input=sha['commit'] + '\n', env=env, capture_output=True, text=True)
                self.assertEqual(0, packresult.returncode, packresult.stderr)
                name = packresult.stdout.strip().splitlines()[-1]
                import shutil
                shutil.copyfile(packdir / ('p-' + name + '.pack'), Path(str(destination_pack)).resolve())
                shutil.copyfile(packdir / ('p-' + name + '.idx'), Path(str(destination_pack)).resolve().with_suffix('.idx'))
                return None
            def fake_refs(url_text, accept):
                result = subprocess.run(['/usr/bin/git', '--git-dir=' + str(origin / '.git'), 'for-each-ref', '--format=%(objectname) %(refname)'],
                                         env=env, capture_output=True, text=True, check=True)
                body = b''
                for line in result.stdout.splitlines():
                    body += ('%04x' % (len(line) + 5)).encode() + line.encode() + b'\n'
                head = sha['commit']
                body += ('%04x' % (len(head + ' HEAD') + 5)).encode() + (head + ' HEAD').encode() + b'\n'
                body += b'0000'
                return body, {}
            with _patch.object(r, 'fetch_package_pack', side_effect=fake_fetch), _patch.object(r, 'public_resource', side_effect=fake_refs):
                r.source_git_metadata('acme/source', sha['commit'], destination, staging)
            admin = destination / '.git'
            # git-submodule runs from a work tree whose inline .git directory is
            # the sanitized object db (the source principal sees destination/.git
            # in place, exactly as handed off).
            # The submodule's .git link is absent (it is not a checkout of the
            # dependency repository), so git reports it uninitialized '-' with
            # the exact pinned gitlink revision from the sanitized graph: the
            # recorded SHA still proves the pinned gitlink was carried.
            status = subprocess.run(['/usr/bin/git', '-C', str(destination), 'submodule', 'status'],
                                    env=dict(env, GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL='/dev/null'), capture_output=True, text=True)
            self.assertEqual(0, status.returncode, status.stderr)
            self.assertEqual(1, len(status.stdout.splitlines()), status.stdout)
            line = status.stdout.splitlines()[0]
            self.assertTrue(line.startswith('-' + dep), 'expected the exact pinned gitlink revision, got: ' + line)
            self.assertIn('vendor/Compositor', line)
            rev = subprocess.run(['/usr/bin/git', '-C', str(destination), 'rev-parse', 'HEAD:vendor/Compositor'],
                                 env=dict(env, GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL='/dev/null'), capture_output=True, text=True, check=True).stdout.strip()
            self.assertEqual(dep, rev)


    def test_source_git_metadata_reports_a_release_tag_on_a_reachable_ancestor(self):
        """Real depth-1 object sets, not pack-objects --revs full-history fakes.
        Prove parent ABSENT initially, truthful shallow boundaries before/after
        explicit deepening, annotated objects installed, and both tag forms read
        by native Git from an untagged HEAD. No network/root/second UID runs.
        """
        for history_limit in (4096, 2):
            with self.subTest(history_limit=history_limit), tempfile.TemporaryDirectory() as directory:
                root = Path(directory).resolve(); origin = root / 'origin'; origin.mkdir()
                env = {'PATH': '/usr/bin:/bin', 'HOME': str(root), 'GIT_CONFIG_NOSYSTEM': '1', 'GIT_CONFIG_GLOBAL': '/dev/null'}
                def git(*args, cwd=origin, data=None, check=True):
                    result = subprocess.run(['/usr/bin/git', *args], cwd=cwd, env=env, input=data,
                                            capture_output=True, text=True)
                    if check: self.assertEqual(0, result.returncode, result.stderr)
                    return result
                git('init')
                for value in ('root', 'release', 'head'):
                    (origin / 'file.txt').write_text(value); git('add', '.')
                    git('-c', 'user.email=t@t', '-c', 'user.name=t', 'commit', '-qm', value)
                    if value == 'release':
                        git('-c', 'user.email=t@t', '-c', 'user.name=t', 'tag', '-a', '-m', 'release', 'v3.1.0')
                        git('tag', 'light3.2.0')
                head = git('rev-parse', 'HEAD').stdout.strip()
                parent = git('rev-parse', 'HEAD^').stdout.strip()
                grandparent = git('rev-parse', 'HEAD^^').stdout.strip()
                tag_object = git('rev-parse', 'v3.1.0').stdout.strip()
                expected = git('describe', '--tags').stdout.strip()
                expected_light = git('describe', '--tags', '--match', 'light*').stdout.strip()
                # An unrelated commit's tag must be skipped BEFORE any fetch.
                tree = git('rev-parse', 'HEAD^{tree}').stdout.strip()
                foreign = git('-c', 'user.email=t@t', '-c', 'user.name=t', 'commit-tree', tree, data='unrelated\n').stdout.strip()
                git('tag', 'unrelated-tag', foreign)
                staging = root / 'staging'; staging.mkdir()
                destination = root / 'checkout'; destination.mkdir()
                (destination / 'file.txt').write_text('head')
                admin = destination / '.git'; fetched = []
                initial_parent_absent = []
                def fake_fetch(url, revision, pack, depth=1, wanted=None):
                    fetched.append((revision, depth))
                    if depth == 4096:
                        # This runs AFTER initial pack installation. It cannot
                        # pass if the first pack smuggled in connecting parents.
                        initial_parent_absent.append(git('--git-dir=' + str(admin), 'cat-file', '-e', parent,
                                                         check=False).returncode != 0)
                        self.assertTrue(initial_parent_absent[-1], 'depth-1 pack must NOT contain the parent')
                        self.assertEqual(head, (admin / 'shallow').read_text().strip())
                    # Explicit object enumeration: depth chooses the commit set
                    # BEFORE packing. pack-objects receives object IDs WITHOUT
                    # --revs, so it cannot silently traverse any parent history.
                    commits = git('rev-list', '--max-count=' + str(min(depth, history_limit)), revision + '^{commit}').stdout.splitlines()
                    ids = set(commits)
                    if git('cat-file', '-t', revision).stdout.strip() == 'tag': ids.add(revision)
                    for commit in commits:
                        tree = git('rev-parse', commit + '^{tree}').stdout.strip(); ids.add(tree)
                        for line in git('ls-tree', '-r', '-t', tree).stdout.splitlines():
                            cells = line.split()
                            if cells[1] in ('tree', 'blob'): ids.add(cells[2])
                    prefix = staging / ('fixture-' + str(len(fetched)))
                    packed = git('pack-objects', str(prefix), data='\n'.join(sorted(ids)) + '\n').stdout.strip()
                    import shutil
                    shutil.copyfile(str(prefix) + '-' + packed + '.pack', pack)
                    # Production index-pack, not the fixture, creates the index.
                def fake_refs(url, accept):
                    body = b''
                    for line in git('show-ref', '--dereference').stdout.splitlines():
                        body += ('%04x' % (len(line) + 5)).encode() + line.encode() + b'\n'
                    return body + b'0000', {}
                with patch.object(r, 'fetch_package_pack', side_effect=fake_fetch), patch.object(r, 'public_resource', side_effect=fake_refs):
                    try:
                        r.source_git_metadata('acme/source', head, destination, staging)
                    except Exception as error:
                        self.fail('valid pinned shallow Git metadata must be usable: ' + repr(error))
                self.assertEqual([True], initial_parent_absent)
                self.assertEqual([(head, 1), (head, 4096), (tag_object, 1)], fetched)
                self.assertEqual('tag', git('--git-dir=' + str(admin), 'cat-file', '-t', tag_object).stdout.strip())
                self.assertEqual(expected, git('-C', str(destination), 'describe', '--tags').stdout.strip())
                self.assertEqual(expected_light, git('-C', str(destination), 'describe', '--tags', '--match', 'light*').stdout.strip())
                self.assertEqual(head, git('-C', str(destination), 'rev-parse', 'HEAD').stdout.strip())
                self.assertEqual('', git('-C', str(destination), 'submodule', 'status').stdout.strip())
                if history_limit == 2:
                    self.assertEqual(parent, (admin / 'shallow').read_text().strip())
                    self.assertNotEqual(0, git('--git-dir=' + str(admin), 'cat-file', '-e', grandparent, check=False).returncode)
                    self.assertEqual('2', git('-C', str(destination), 'rev-list', '--count', 'HEAD').stdout.strip())
                else:
                    self.assertFalse((admin / 'shallow').exists(), 'complete connecting history must remove the old HEAD boundary')
                    self.assertEqual('3', git('-C', str(destination), 'rev-list', '--count', 'HEAD').stdout.strip())
                self.assertFalse((admin / 'hooks').exists()); self.assertFalse((admin / 'objects/info/alternates').exists())
                self.assertNotIn('*', (admin / 'config').read_text())


    def test_source_git_metadata_refuses_unreadable_or_over_budget_commit_graphs(self):
        # Unit-level command responses exercise only the bound; the preceding
        # native-Git contract proves real object membership and shallow walking.
        for bad in ('too-many', 'malformed', 'failed'):
            with self.subTest(bad=bad), tempfile.TemporaryDirectory() as directory:
                root = Path(directory); target = root / 'source'; target.mkdir()
                staging = root / 'staging'; staging.mkdir()
                def fetch(url, revision, pack, **kwargs):
                    pack.write_bytes(b'fixture'); pack.with_suffix('.idx').write_bytes(b'fixture-index')
                def command(argv, *args, **kwargs):
                    if 'rev-list' in argv:
                        return ((1, '') if bad == 'failed' else (0, 'not-a-commit') if bad == 'malformed'
                                else (0, ('1' * 40 + '\n') * 4097))
                    return 0, '1' * 40
                with patch.object(r, 'fetch_package_pack', side_effect=fetch), patch.object(r, 'command', side_effect=command), patch.object(r, 'public_resource', return_value=(b'0000', {})):
                    with self.assertRaisesRegex(ValueError, 'history is unreadable or exceeds 4096'):
                        r.source_git_metadata('acme/source', '1' * 40, target, staging)


    def test_sanitized_metadata_is_usable_by_a_different_owner_only_through_exact_safe_directory(self):
        """Model the source uid with GIT_TEST_ASSUME_DIFFERENT_OWNER=1: Git's own
        safe.directory code path, without root/chown or a real second account.
        LIMITATION (explicit): Apple Git applies the flag to rev-parse, describe
        and submodule alike, so every refusal below is real on this host. No
        safe.directory => 'dubious ownership'; a PARENT directory scope =>
        refused; '*' => must never appear in the sanitized tree; the EXACT work
        root => all three legacy read-only commands pass; the sanitized config
        holds only core (no owner config, hooks, remotes, credentials) and every
        handed-off entry stays read-only."""
        sha = {}
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            origin = root / 'origin'; origin.mkdir()
            env = {'PATH': '/usr/bin:/bin', 'HOME': str(root), 'GIT_CONFIG_NOSYSTEM': '1', 'GIT_CONFIG_GLOBAL': '/dev/null'}
            subprocess.run(['/usr/bin/git', 'init', str(origin)], env=env, capture_output=True, check=True)
            (origin / 'file.txt').write_text('pinned')
            subprocess.run(['/usr/bin/git', '-C', str(origin), 'add', '.'], env=env, capture_output=True, check=True)
            subprocess.run(['/usr/bin/git', '-C', str(origin), '-c', 'user.email=t@t', '-c', 'user.name=t', 'commit', '-qm', 'source'], env=env, capture_output=True, check=True)
            subprocess.run(['/usr/bin/git', '-C', str(origin), 'tag', 'v4.5.6'], env=env, capture_output=True, check=True)
            sha['commit'] = subprocess.run(['/usr/bin/git', '-C', str(origin), 'rev-parse', 'HEAD'], env=env, capture_output=True, text=True, check=True).stdout.strip()
            packdir = root / 'packs'; packdir.mkdir()
            staging = root / 'staging'; staging.mkdir()
            destination = root / 'checkout'; destination.mkdir()
            (destination / 'file.txt').write_text('pinned')
            from unittest.mock import patch as _patch
            def fake_fetch(url_text, revision, destination_pack, depth=1, wanted=None):
                # Emulate fetch_package_pack at the caller's depth: pack-objects
                # --revs from the named revision carries exactly the objects a
                # bounded shallow pack at that depth holds (never a full clone).
                packresult = subprocess.run(['/usr/bin/git', '--git-dir=' + str(origin / '.git'), 'pack-objects', '--revs', str(packdir) + '/p'],
                                            input=revision + '\n', env=env, capture_output=True, text=True)
                self.assertEqual(0, packresult.returncode, packresult.stderr)
                name = packresult.stdout.strip().splitlines()[-1]
                import shutil
                # index-pack runs with cwd=staging: absolute paths resolve.
                shutil.copyfile(packdir / ('p-' + name + '.pack'), Path(str(destination_pack)).resolve())
                shutil.copyfile(packdir / ('p-' + name + '.idx'), Path(str(destination_pack)).resolve().with_suffix('.idx'))
                return None
            def fake_refs(url_text, accept):
                result = subprocess.run(['/usr/bin/git', '--git-dir=' + str(origin / '.git'), 'for-each-ref', '--format=%(objectname) %(refname)'],
                                         env=env, capture_output=True, text=True, check=True)
                body = b''
                for line in result.stdout.splitlines():
                    body += ('%04x' % (len(line) + 5)).encode() + line.encode() + b'\n'
                head = subprocess.run(['/usr/bin/git', '--git-dir=' + str(origin / '.git'), 'rev-parse', 'HEAD'],
                                      env=env, capture_output=True, text=True, check=True).stdout.strip()
                body += ('%04x' % (len(head + ' HEAD') + 5)).encode() + (head + ' HEAD').encode() + b'\n'
                body += b'0000'
                return body, {}
            with _patch.object(r, 'fetch_package_pack', side_effect=fake_fetch), _patch.object(r, 'public_resource', side_effect=fake_refs):
                r.source_git_metadata('acme/source', sha['commit'], destination, staging)
            admin = destination / '.git'
            different = dict(env, GIT_TEST_ASSUME_DIFFERENT_OWNER='1')
            def scoped(value):
                return dict(different, GIT_CONFIG_COUNT='1', GIT_CONFIG_KEY_0='safe.directory', GIT_CONFIG_VALUE_0=value)
            # (a) Without safe.directory the real code path refuses each command.
            for args in (['rev-parse', 'HEAD'], ['describe', '--tags'], ['submodule', 'status']):
                refused = subprocess.run(['/usr/bin/git', '-C', str(destination), *args], env=different, capture_output=True, text=True)
                self.assertNotEqual(0, refused.returncode, 'expected refusal for ' + ' '.join(args))
                self.assertIn('dubious ownership', refused.stderr, ' '.join(args))
            # (b) A PARENT directory scope is not the exact work root: refused.
            parent = subprocess.run(['/usr/bin/git', '-C', str(destination), 'rev-parse', 'HEAD'],
                                     env=scoped(str(root)), capture_output=True, text=True)
            self.assertNotEqual(0, parent.returncode, 'parent scope must not authorize')
            self.assertIn('dubious ownership', parent.stderr)
            # (c) '*' would authorize anything: the sanitized tree must never
            # contain it, and the runtime declares the exact scope instead.
            self.assertNotIn('*', (admin / 'config').read_text())
            # (d) The EXACT work root authorizes every read-only legacy command.
            allowed = scoped(str(destination))
            probed = subprocess.run(['/usr/bin/git', '-C', str(destination), 'rev-parse', 'HEAD'],
                                    env=allowed, capture_output=True, text=True)
            self.assertEqual(0, probed.returncode, probed.stderr)
            self.assertEqual(sha['commit'], probed.stdout.strip())
            described = subprocess.run(['/usr/bin/git', '-C', str(destination), 'describe', '--tags'],
                                        env=allowed, capture_output=True, text=True)
            self.assertEqual(0, described.returncode, described.stderr)
            status = subprocess.run(['/usr/bin/git', '-C', str(destination), 'submodule', 'status'],
                                    env=allowed, capture_output=True, text=True)
            self.assertEqual(0, status.returncode, status.stderr)
            self.assertEqual('', status.stdout.strip())  # no .gitmodules in this fixture
            # (e) The sanitized tree carries only core configuration, read-only.
            config_text = (admin / 'config').read_text()
            self.assertEqual(['[core]'], [line for line in config_text.splitlines() if line.startswith('[')])
            for forbidden in ('remote', 'url', 'hook', 'credential', 'token'):
                self.assertNotIn(forbidden, config_text.lower())
            for entry in admin.rglob('*'):
                mode = entry.stat().st_mode & 0o777
                self.assertEqual(0o555 if entry.is_dir() else 0o444, mode, str(entry))
            self.assertEqual(0o555, admin.stat().st_mode & 0o777)


MUTATIONS = [
    ('mirror-exact-list', 'factory-ios-remote.py', [('json.dumps(path) +', "json.dumps('/not-listed') +")]),
    ('mirror-source-child-global', 'factory-ios-remote.py', [("'GIT_CONFIG_GLOBAL': str(principal.home / '.factory-gitconfig')", "'GIT_CONFIG_GLOBAL': '/dev/null'")]),
    ('mirror-writable-refusal', 'factory-ios-remote.py', [(' or path.stat().st_mode & 0o222', '')]),
    ('source-workflow-allowlist', 'factory-gui-source-job.py', [("value['workflow'] not in SOURCE_WORKFLOWS", 'False')]),
    ('source-timeout-bound', 'factory-gui-source-job.py', [("1 <= value['timeoutMinutes'] <= 360", "1 <= value['timeoutMinutes'] <= 9999")]),
    ('source-path-boundary', 'factory-gui-source-job.py', [(' or not pathlib.Path(p).resolve().is_relative_to(principal.root)', '')]),
    ('source-unknown-placeholder', 'factory-gui-source-job.py', [("if '${' in expanded:", 'if False:')]),
    ('source-shell-allowlist', 'factory-gui-source-job.py', [("if step.get('shell', 'implicit-bash') not in ('implicit-bash', 'bash'):", 'if False:')]),
    ('source-artifact-byte-bound', 'factory-gui-source-job.py', [('if budget[0] > 100 * 1024 * 1024:', 'if False:')]),
    ('source-failure-outcome', 'factory-gui-source-job.py', [('succeeded = succeeded and code == 0', 'succeeded = True')]),
    ('source-pipefail-argv', 'factory-gui-source-job.py', [("['/bin/bash', '--noprofile', '--norc', '-e', '-o', 'pipefail', str(script)]", "['/bin/bash', '--noprofile', '--norc', str(script)]")]),
    ('source-git-repository-skeleton', 'factory-gui-source-job.py', [(
        "    code, _ = command(['/usr/bin/git', 'init', '--bare', str(admin)], str(staging), env=env)\n    if code:\n        raise ValueError('source metadata repository could not be initialized')",
        '    pass')]),
    ('source-git-pack-install', 'factory-gui-source-job.py', [(
        "        pack.rename(pack_store / ('pack-' + pack_hash + '.pack'))",
        '        pass')]),
    ('source-git-connecting-depth', 'factory-gui-source-job.py', [(
        "    fetch(sha, 'history', 4096)",
        '    pass')]),
    ('source-git-ancestor-direction', 'factory-gui-source-job.py', [(
        '        if ancestor != 0:',
        '        if ancestor == 0:')]),
    ('source-git-tag-install', 'factory-gui-source-job.py', [(
        '                install(pack)\n            except Exception:',
        '                pass\n            except Exception:')]),
    ('source-git-history-budget', 'factory-gui-source-job.py', [('len(lines) > 4096', 'False')]),
    ('source-git-truthful-shallow-boundary', 'factory-gui-source-job.py', [("            boundary.write_text(''.join(root + '\\n' for root in roots))", '            pass')]),
    ('source-git-clear-old-shallow-boundary', 'factory-gui-source-job.py', [('        boundary.unlink(missing_ok=True)', '        pass')]),
    ('source-git-alternates-refusal', 'factory-gui-source-job.py', [("""for emptyable in ('hooks', 'logs'):
        directory = admin / emptyable
        if directory.exists():
            import shutil
            shutil.rmtree(directory)""", "pass")]),
    ('source-ignore-condition', 'factory-gui-source-job.py', [("if (step['condition'] == 'success()' and not succeeded) or (step['condition'] == \"env.LW_INFERENCE_REAL == '1'\" and (not succeeded or environment.get('LW_INFERENCE_REAL') != '1')):", 'if False:')]),
    ('source-missing-cleanup-journal', 'factory-gui-remote.py', [("if self.root is None or self.cleaned:", 'if True:')]),
    ('source-stale-export-reconciliation', 'factory-gui-source-job.py', [('if metadata.st_mtime < time.time() - 21600:', 'if False:')]),
    ('source-exact-submodule-pin', 'factory-gui-source-job.py', [("return node['sha']", "return '4' * 40")]),
    ('source-artifact-link-boundary', 'factory-gui-source-job.py', [('if not stat.S_ISREG(metadata.st_mode):', 'if False:'), ('os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK', 'os.O_RDONLY | os.O_NONBLOCK')]),
    ('probe-interrupted-auto-recovery', 'factory-gui-remote.py', [("            reconcile_probe_fixture({'collectorUid': os.geteuid(), **self.config})", '            pass')]),
    ('probe-own-fixture-only', 'factory-gui-remote.py', [(' or probe.read_text() != PROBE_UNCERTAINTY_BODY', '')]),
]


def mutations():
    global r
    executed = failures = errors = 0
    for name, path, replacements in MUTATIONS:
        try:
            r = load_source((path, replacements))
            red = unittest.TextTestRunner(stream=io.StringIO()).run(unittest.defaultTestLoader.loadTestsFromTestCase(Contracts))
            r = load_source()
            green = unittest.TextTestRunner(stream=io.StringIO()).run(unittest.defaultTestLoader.loadTestsFromTestCase(Contracts))
            executed += 1
            if not red.failures or not green.wasSuccessful():
                failures += 1
                print(name + ': FAILED missing assertion RED or restored GREEN')
            else:
                print(name + ': assertion RED, restored GREEN')
        except Exception as error:
            errors += 1
            print(name + ': ERROR ' + type(error).__name__)
        finally:
            r = load_source()
    print('GUI_SOURCE_MUTATION_RESULT ' + json.dumps(dict(executed=executed, skipped=0, failures=failures, errors=errors)))
    return 0 if executed == len(MUTATIONS) and not failures and not errors else 1


if __name__ == '__main__':
    if '--mutations' in sys.argv:
        sys.exit(mutations())
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(Contracts)
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    print('GUI_SOURCE_CONTRACT_RESULT ' + json.dumps({'executed': result.testsRun, 'skipped': len(result.skipped), 'failures': len(result.failures), 'errors': len(result.errors)}))
    sys.exit(0 if result.wasSuccessful() else 1)
