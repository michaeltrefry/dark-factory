using System.Diagnostics;
using DarkFactory.Orchestrator.Gate;

namespace DarkFactory.Orchestrator.Tests;

public sealed class RemoteIosExecutionTests
{
    [Fact]
    public void The_sealed_workflow_forwards_an_optional_execution_without_interpolation_into_code()
    {
        Assert.Contains("      execution:\n        default: ''\n        type: string", RemoteIosWorkflow.Text);
        Assert.Contains("EXECUTION: ${{ inputs.execution }}", RemoteIosWorkflow.Text);
        Assert.DoesNotContain("${{", RemoteIosWorkflow.Runner);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task General_execution_fixtures_validate_descriptors_overlay_and_collect_raw_results_without_running_repository_code()
    {
        var directory = Directory.CreateTempSubdirectory("df-ios-execution-tests-").FullName;
        try
        {
            var helper = Path.Combine(directory, "runner.py");
            await File.WriteAllTextAsync(helper, RemoteIosWorkflow.Runner);
            var workflow = Path.Combine(directory, "workflow.yml");
            await File.WriteAllTextAsync(workflow, RemoteIosWorkflow.Text);
            using var process = Process.Start(new ProcessStartInfo("/usr/bin/perl", ["-e", "setpgrp(0,0); exec @ARGV or die $!;", "python3", "-I", "-c", Fixtures, helper, workflow])
            {
                WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
            })!;
            process.StandardInput.Close();
            var owned = OwnProcess.Of(process);
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(8));
                var output = await stdout + await stderr;
                Assert.True(process.ExitCode == 0, output);
                Assert.Contains("OK", output);
            }
            finally
            {
                owned.KillGroupIfStillLeading();
                owned.KillIfStillRunning();
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private const string Fixtures = """"
import copy, hashlib, importlib.util, io, json, os, pathlib, stat, sys, tarfile, tempfile, unittest, zipfile
from unittest.mock import patch
spec = importlib.util.spec_from_file_location('runner', sys.argv[1])
r = importlib.util.module_from_spec(spec)
spec.loader.exec_module(r)
def forbidden_admin(*args,**kwargs):raise AssertionError('native host administration forbidden in fixtures')
r.admin=forbidden_admin
signal_guard=patch.object(r.os,'killpg',side_effect=AssertionError('native signals forbidden; only OwnProcess host may signal'))
signal_guard.start()
def host_safety_audit(event,args):
    if event=='subprocess.Popen' and pathlib.Path(str(args[0])).name=='sudo':raise AssertionError('native sudo forbidden before process creation')
    if event=='open' and isinstance(args[0],(str,bytes)) and os.fsdecode(args[0]).startswith(('/private/etc/hosts','/etc/hosts')):raise AssertionError('real host mapping file forbidden in fixtures')
sys.addaudithook(host_safety_audit)
WORKFLOW = pathlib.Path(sys.argv[2]).read_text()
sys.argv = [sys.argv[0]]
BASE, HEAD = '1' * 40, '2' * 40
TESTS = ['AppTests/Nested.Suite/parameterized(_:)', 'AppUITests/ScreenTests/testLaunch']
REQUEST = dict(NONCE='a' * 32, BASE=BASE, HEAD=HEAD, SIDE='base', TESTS='[]')
def descriptor():
    return dict(side='base', commit=BASE, overlayFrom=HEAD,
        overlayPaths=['Tests/New.swift'], deletePaths=['Tests/Old.swift'],
        replacements={'App.xcodeproj/project.pbxproj': 'generated graph'},
        builds=[dict(flag='-project', container='App.xcodeproj', scheme='App', tests=TESTS)])
def prepare():
    return dict(artifacts=[dict(name='sdk',url='https://public.example/sdk',sha256=hashlib.sha256(b'payload').hexdigest(),
        maxBytes=100,unpack='none',maxUnpackedBytes=100)],
        steps=[dict(name='generate',argv=['${worktree}/tools/gen','--sdk','${artifact:sdk}'],
        workingDirectory='.',environment={'SDK':'${artifact:sdk}'},timeoutSeconds=60)])
class FixtureRegistryHosts:
    # Tests NEVER edit the host's hosts file or run sudo. Native protocol proof
    # uses standard already-resolving loopback names, independent of VM mapping.
    def __init__(self,root):
        self.root=root;self.nonce=hashlib.sha256(str(root).encode()).hexdigest()[:32];self.names=[];self.removed=False
    def original_hosts(self,urls):return r.RegistryHosts.original_hosts(self,urls)
    def install(self,names):self.names=names
    def native_host(self,name):return ['127.0.0.1','localhost','localhost.'][self.names.index(name)]
    def remove(self):self.removed=True
class NativePrincipal:
    def __init__(self,root,worktree,filesystem=True):
        self.filesystem=filesystem
        self.root,self.worktree,self.home=root,worktree,root/'native-home'
        self.home.mkdir();(self.home/'tmp').mkdir();self.profile=root/'native.sb'
        self.registry_endpoints([])
    def registry_endpoints(self,endpoints):
        self.profile.write_text(r.sandbox_profile(self.root,[self.home,self.worktree],endpoints)+('' if self.filesystem else '\n(allow file-read* file-write*)\n'))
    def run(self,args,cwd,**kwargs):
        developer='/Applications/Xcode.app/Contents/Developer'
        tools={'/usr/bin/python3':developer+'/usr/bin/python3','/usr/bin/swift':developer+'/Toolchains/XcodeDefault.xctoolchain/usr/bin/swift'}
        args=[tools.get(args[0],args[0]),*args[1:]]
        result=r.subprocess.run(['/usr/bin/sandbox-exec','-f',str(self.profile),*args],cwd=cwd,env={'DEVELOPER_DIR':developer,'PATH':'/usr/bin:/bin:/usr/sbin:/sbin','HOME':str(self.home),'CFFIXED_USER_HOME':str(self.home),'TMPDIR':str(self.home/'tmp'),'SWIFTPM_MODULECACHE_OVERRIDE':str(self.home/'module-cache'),'xcrun_nocache':'1'},stdin=r.subprocess.DEVNULL,capture_output=True)
        return result.returncode,result.stdout.decode()+result.stderr.decode()
class Fixtures(unittest.TestCase):
    def parse(self, value):
        return r.parse_execution(json.dumps(value), REQUEST)
    def test_test_environment_validation_and_job_local_resolution(self):
        for key, value in [('HOME','x'),('DYLD_INSERT_LIBRARIES','x'),('LD_PRELOAD','x'),('REPO_TOKEN','x'),('TEST_RUNNER_HOME','x'),('HELPER','/Users/owner/helper'),('HELPER','${worktree}/../helper'),('HELPER','${artifact:sdk}/helper'),('MODE','__CURRENT_VALUE__'),('MODE','a\n')]:
            with self.subTest(key=key,value=value),self.assertRaisesRegex(ValueError,key):
                r.validate_test_environment({key:value})
        for values in [{str(i):'x' for i in range(33)}, {'MODE':'x'*1025}, {'V'+str(i):'x'*1024 for i in range(9)}]:
            with self.assertRaises(ValueError):r.validate_test_environment(values)
        with tempfile.TemporaryDirectory() as d:
            root=pathlib.Path(d);worktree=root/'base';worktree.mkdir()
            values={'REPO_HELPER':'${worktree}/helper','REPO_MODE':'fixture'}
            resolved=r.resolve_test_environment(values,worktree)
            self.assertEqual(str(worktree.resolve()/'helper'),resolved['TEST_RUNNER_REPO_HELPER'])
            (worktree/'helper').symlink_to(root/'owner')
            with self.assertRaisesRegex(ValueError,'REPO_HELPER'):r.resolve_test_environment(values,worktree)

    def test_general_identifiers_and_workspace(self):
        value = descriptor()
        value['builds'][0].update(flag='-workspace', container='App.xcworkspace')
        self.assertEqual(value, self.parse(value))
    def test_top_level_swift_testing_test_is_supported(self):
        value = descriptor()
        value['builds'][0]['tests'] = ['AppTests/topLevel()']
        self.assertEqual(value, self.parse(value))
    def test_bad_paths(self):
        for path in ['../escape', '/abs', 'a/../b', 'a//b', './b', 'a\\b', '.factory/recipe', '.factory-test-results/x', 'a/\x00b']:
            for field in ['overlayPaths', 'deletePaths', 'replacements']:
                value = descriptor()
                value[field] = {path: 'body'} if field == 'replacements' else [path]
                with self.subTest(path=path, field=field), self.assertRaises(ValueError): self.parse(value)
    def test_identity_and_shape(self):
        for field, bad in [('commit',HEAD), ('side','third'), ('overlayFrom','3'*40), ('builds',[]),
            ('overlayPaths','path'), ('replacements',{'Tests/New.swift':42}), ('extra','ignored')]:
            value = descriptor(); value[field] = bad
            with self.subTest(field=field), self.assertRaises(ValueError): self.parse(value)
    def test_command_injection_is_never_an_argument_vector(self):
        for field, bad in [('flag','-configuration'), ('container','-project'), ('scheme','-scheme'), ('tests',['-only-testing:other'])]:
            value = descriptor(); value['builds'][0][field] = bad
            with self.subTest(field=field), self.assertRaises(ValueError): self.parse(value)
    def test_duplicate_tests_and_unknown_build_arguments(self):
        for mutate in [lambda d:d['builds'].append(copy.deepcopy(d['builds'][0])),
                       lambda d:d['builds'][0].update(arguments=['/bin/sh']),
                       lambda d:d['builds'][0].update(tests=[TESTS[0],TESTS[0]])]:
            value=descriptor(); mutate(value)
            with self.assertRaises(ValueError): self.parse(value)
    def test_bounded_descriptor_and_duplicate_json_keys(self):
        with self.assertRaises(ValueError): r.parse_execution(' ' * (48*1024+1), REQUEST)
        with self.assertRaises(ValueError): r.parse_execution('{"side":"base","side":"head"}', REQUEST)
    def test_overlay_requires_revision(self):
        value=descriptor(); value['overlayFrom']=None
        with self.assertRaises(ValueError): self.parse(value)
    def test_overlay_uses_requested_commits_and_generated_replacements(self):
        with tempfile.TemporaryDirectory() as d:
            root=pathlib.Path(d)
            for side in ['base','head']:
                (root/side/'Tests').mkdir(parents=True)
            (root/'base/Tests/Old.swift').write_text('old')
            (root/'head/Tests/New.swift').write_text('test body')
            r.apply_execution(root, self.parse(descriptor()))
            self.assertEqual('test body',(root/'base/Tests/New.swift').read_text())
            self.assertFalse((root/'base/Tests/Old.swift').exists())
            self.assertEqual('generated graph',(root/'base/App.xcodeproj/project.pbxproj').read_text())
    def test_symlink_overlay_is_refused(self):
        with tempfile.TemporaryDirectory() as d:
            root=pathlib.Path(d); (root/'base').mkdir(); (root/'head').mkdir()
            (root/'head/Tests').symlink_to(root/'base',target_is_directory=True)
            with self.assertRaises(ValueError): r.apply_execution(root,self.parse(descriptor()))
    def run_main(self, side='base', build_code=0, report_code=0, execution_side='base', close_failure=False, multiple_builds=False, prepared=False, prepare_code=0, hydrated=False):
        value=descriptor()
        if execution_side=='head':
            value.update(side='head',commit=HEAD,overlayFrom=None,overlayPaths=[],deletePaths=[])
        if multiple_builds:
            value['builds'][0]['tests']=[TESTS[0]]
            value['builds'].append(dict(flag='-workspace',container='Other.xcworkspace',scheme='Other',tests=[TESTS[1]]))
        if prepared:value['prepare']=prepare()
        value['testEnvironment']={'REPO_MODE':side,'REPO_HELPER':'${worktree}/tools/helper'}
        raw=json.dumps(value)
        calls=[]; closed=[];workspaces=[]
        class Principal:
            def __init__(self,root):
                self.root=root;workspaces.append(root)
                if prepared:
                    assert (root/'artifacts/sdk').read_bytes()==b'payload'
                    assert stat.S_IMODE((root/'artifacts/sdk').stat().st_mode)==0o444
            def share(self,path): pass
            def close(self):
                closed.append(True)
                if close_failure: raise ValueError('descendants survived')
                r.tempfile.TemporaryDirectory._rmtree(self.root)
            def run(self,args,cwd,extra_environment=None,timeout=r.COMMAND_TIMEOUT):
                calls.append((args,cwd))
                if 'test-without-building' in args:
                    assert extra_environment=={'TEST_RUNNER_REPO_MODE':side,'TEST_RUNNER_REPO_HELPER':str(self.root.resolve()/side/'tools/helper')}
                elif not args[0].endswith('/tools/gen'):
                    assert not extra_environment, 'test inputs reached build, toolchain or collector'
                if args[0].endswith('/tools/gen'):
                    (pathlib.Path(cwd)/'Package.resolved').write_text('{"version":2,"pins":[]}')
                    assert extra_environment['SDK']==str(self.root/'artifacts/sdk')
                    assert timeout==60
                    return prepare_code,'prepared' if not prepare_code else 'Tests/New.swift:3:5: error: generator failed'
                if '-version' in args:return 0,'Xcode 16.4\nBuild version 16F6'
                if 'list' in args:return 0,json.dumps({'runtimes':[{'identifier':r.ENVIRONMENT['runtime'],'isAvailable':True}], 'devicetypes':[{'identifier':r.ENVIRONMENT['deviceType']}]})
                if 'create' in args:return 0,'AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE'
                if 'xcresulttool' in args:return report_code,'{"testNodes":[]}'
                if '-resolvePackageDependencies' in args:return 0,'resolved offline'
                if 'build-for-testing' in args:
                    self.assert_overlay(cwd)
                    return build_code,str(cwd)+'/Tests/New.swift:3:5: error: missing symbol' if build_code else 'build passed'
                return 65,'test output'
            def assert_overlay(self,cwd):
                assert (cwd/'Tests/New.swift').read_text()=='test body'
                assert (cwd/'App.xcodeproj/project.pbxproj').read_text()=='generated graph'
        def snapshot(principal,worktree):
            if prepared:
                assert (worktree/'Package.resolved').exists(), 'prepare-generated declarations must be read after prepare'
                return {'Package.resolved':(worktree/'Package.resolved').read_text()}
            return {}
        def hydrate(root,worktree,data):
            if prepared:assert data.get('Package.resolved')=='{"version":2,"pins":[]}', 'generated declarations must reach hydration'
            return [{'original':'https://public.example/repo','mirror':'file:///sealed'}] if hydrated else []
        with tempfile.TemporaryDirectory() as d:
            old=os.getcwd(); os.chdir(d)
            try:
                pathlib.Path('sources').mkdir()
                for name in ['base','head']:
                    with zipfile.ZipFile('sources/'+name+'.zip','w') as z:
                        z.writestr('repo/Tests/'+('New' if name=='head' else 'Old')+'.swift','test body')
                env=dict(REQUEST,SIDE=side,EXECUTION=raw,EXECUTION_BUDGET_SECONDS='1200',EXECUTION_DEADLINE_UNIX_SECONDS=str(r.time.time()+1200))
                with patch.dict(os.environ,env), patch.object(r,'Principal',Principal), patch.object(r,'command',return_value=(report_code,'{"testNodes":[]}')), patch.object(r,'download_artifact',side_effect=lambda a,p:p.write_bytes(b'payload')), patch.object(r,'hydrate_packages',side_effect=hydrate), patch.object(r,'install_package_mirrors',side_effect=lambda p,m:calls.append((['install-mirrors'],p.root))), patch.object(r,'package_snapshot',side_effect=snapshot), patch.object(r,'hydrate_binary_packages',return_value=False):
                    r.main()
                payload=json.loads(pathlib.Path('result.json').read_text())
                self.assertEqual(hashlib.sha256(raw.encode()).hexdigest(),payload['executionHash'])
                self.assertEqual(side,payload['side'])
                return payload,calls,closed
            finally:
                os.chdir(old)
                # Fake principals wrote only this test's known files and ran no
                # real source process; test cleanup is deliberately explicit.
                for path in workspaces:
                    if path.exists():r.tempfile.TemporaryDirectory._rmtree(path)
    def test_raw_nested_parameterized_and_ui_results_match_csharp_names(self):
        payload,calls,closed=self.run_main()
        execution=payload['execution']
        self.assertEqual('ran',execution['status']); self.assertTrue(closed)
        self.assertEqual(BASE,execution['commit'])
        self.assertEqual(4,len(execution['files']))
        for test in TESTS:
            prefix='xcode-test-'+hashlib.sha256(test.encode()).hexdigest()[:16]
            records={f['name']:f for f in execution['files']}
            self.assertEqual(65,records[prefix+'.out']['exitCode'])
            self.assertEqual(0,records[prefix+'.json']['exitCode'])
            self.assertIn('-only-testing:'+test,str(calls))
        self.assertEqual(1,sum('build-for-testing' in a for a,c in calls))
        self.assertEqual(2,sum('xcresulttool' in a for a,c in calls))
    def test_build_failure_returns_complete_log_for_csharp_attribution(self):
        payload,calls,closed=self.run_main(build_code=65)
        self.assertEqual('build-failed',payload['execution']['status'])
        self.assertIn('/Tests/New.swift:3:5: error:',payload['execution']['log'])
        self.assertEqual([],payload['execution']['files']); self.assertTrue(closed)
    def test_infrastructure_build_exit_is_error(self):
        payload,_,_=self.run_main(build_code=70)
        self.assertEqual('error',payload['execution']['status'])
    def test_unused_side_executes_nothing(self):
        payload,calls,closed=self.run_main(side='head')
        self.assertEqual('unused',payload['execution']['status'])
        self.assertEqual([],calls); self.assertEqual([],closed)
    def test_xcresult_exit_status_is_preserved_not_judged_by_fixture_parser(self):
        payload,_,_=self.run_main(report_code=1)
        self.assertTrue(all(f['exitCode']==1 for f in payload['execution']['files'] if f['name'].endswith('.json')))
    def test_preparation_fetches_before_source_and_runs_under_principal_before_build(self):
        payload,calls,closed=self.run_main(prepared=True)
        self.assertEqual('ran',payload['execution']['status']);self.assertTrue(closed)
        self.assertTrue(calls[0][0][0].endswith('/tools/gen'))
        self.assertNotIn('prepared',payload['execution']['log'])
        profile=r.sandbox_profile(pathlib.Path('/tmp/work'),[pathlib.Path('/tmp/work/base')])
        self.assertIn('(subpath "/private/tmp/work/artifacts")' if pathlib.Path('/tmp').resolve()!=pathlib.Path('/tmp') else '(subpath "/tmp/work/artifacts")',profile)
        self.assertNotIn('artifacts',profile.split('(allow file-write*')[1])
    def test_failed_prepare_is_error_never_compiler_evidence_and_still_quiesces(self):
        payload,calls,closed=self.run_main(prepared=True,prepare_code=65)
        self.assertTrue(closed); self.assertEqual('prepare-failed',payload['execution']['status'])
        self.assertEqual('',payload['execution']['log'])
        self.assertEqual({'code':'prepare-step-failed','step':0},payload['execution']['failure'])
        self.assertFalse(any('build-for-testing' in args for args,cwd in calls))
    def test_head_runs_without_base_overlay(self):
        payload,calls,closed=self.run_main(side='head',execution_side='head')
        self.assertEqual('ran',payload['execution']['status'])
        self.assertEqual(HEAD,payload['execution']['commit']); self.assertTrue(closed)
    def test_every_build_finishes_before_any_test_like_the_local_runner(self):
        payload,calls,_=self.run_main(multiple_builds=True)
        self.assertEqual(2,sum('build-for-testing' in a for a,c in calls))
        self.assertEqual(4,len(payload['execution']['files']))
        build_indices=[i for i,(a,c) in enumerate(calls) if 'build-for-testing' in a]
        test_indices=[i for i,(a,c) in enumerate(calls) if 'test-without-building' in a]
        self.assertLess(max(build_indices),min(test_indices))
    def test_quiescence_failure_publishes_no_success(self):
        with self.assertRaises(ValueError): self.run_main(close_failure=True)
    def test_prepare_validated_without_recipe_read_or_owner_paths(self):
        value=descriptor(); value['prepare']=prepare()
        self.assertEqual(value,self.parse(value))
        for mutate in [lambda p:p['steps'][0]['environment'].update(PATH='/evil'),
                       lambda p:p['steps'][0].update(workingDirectory='../outside'),
                       lambda p:p['steps'][0].update(argv=['../tool']),
                       lambda p:p['steps'][0].update(argv=['${artifact:unknown}/tool']),
                       lambda p:p['steps'][0].update(timeoutSeconds=7201),
                       lambda p:p['artifacts'][0].update(url='https://127.0.0.1/file'),
                       lambda p:p['artifacts'][0].update(maxBytes=4*1024**3+1),
                       lambda p:p['artifacts'][0].update(sha256='not-a-hash'),
                       lambda p:p['artifacts'][0].update(unpack='shell')]:
            value=descriptor(); value['prepare']=prepare(); mutate(value['prepare'])
            with self.assertRaises(ValueError): self.parse(value)
    def test_public_address_rules_include_embedded_ipv4_and_all_dns_answers(self):
        for address in ['127.0.0.1','10.0.0.1','100.64.0.1','169.254.169.254','192.0.2.1','224.0.0.1',
                        '::1','fc00::1','2001:db8::1','::ffff:127.0.0.1','64:ff9b::a00:1','2002:7f00:1::1','64:ff9b:1::808:808']:
            with self.subTest(address=address), self.assertRaises(ValueError): r.public_address(address)
        self.assertIsNotNone(r.public_address('8.8.8.8'))
        with patch.object(r.socket,'getaddrinfo',return_value=[(2,1,6,'',('8.8.8.8',443)),(2,1,6,'',('127.0.0.1',443))]):
            with self.assertRaises(ValueError): r.artifact_addresses('public.example')
    def test_artifact_urls_refuse_credentials_ports_fragments_and_local_names(self):
        for url in ['http://public.example/a','https://u:p@public.example/a','https://public.example:444/a',
                    'https://public.example/a#fragment','https://x.localhost/a','https://[::1]/a']:
            with self.assertRaises(ValueError): r.artifact_url(url)
    def test_download_hash_and_streaming_limit(self):
        class Response:
            status=200
            def __init__(self):self.parts=[b'payload',b'']
            def read(self,n):return self.parts.pop(0)
        class Connection:
            def request(self,*args,**kwargs):pass
            def getresponse(self):return Response()
            def close(self):pass
        with tempfile.TemporaryDirectory() as d, patch.object(r,'artifact_connection',return_value=Connection()):
            path=pathlib.Path(d)/'blob'
            artifact=prepare()['artifacts'][0]
            r.download_artifact(artifact,path)
            self.assertEqual(b'payload',path.read_bytes())
            for bad in [dict(artifact,sha256='0'*64),dict(artifact,maxBytes=2)]:
                path.unlink()
                with self.assertRaises(ValueError):r.download_artifact(bad,path)
                if not path.exists():path.write_bytes(b'')
    def test_safe_artifact_unpack_refuses_links_traversal_and_bombs(self):
        with tempfile.TemporaryDirectory() as d:
            root=pathlib.Path(d)
            for name,size in [('../outside',10),('ok',2)]:
                archive=root/'blob.zip'
                with zipfile.ZipFile(archive,'w') as z:z.writestr(name,'hello')
                destination=root/('unpacked'+str(size)); destination.mkdir()
                with self.assertRaises(ValueError):r.unpack_artifact(archive,destination,'zip',size)
    def test_download_redirects_revalidate_url_and_have_a_hard_hop_limit(self):
        class Response:
            status=302
            def __init__(self,location):self.location=location
            def getheader(self,key):return self.location
        class Connection:
            def __init__(self,location):self.location=location
            def request(self,*args,**kwargs):pass
            def getresponse(self):return Response(self.location)
            def close(self):pass
        with tempfile.TemporaryDirectory() as d:
            for location,count in [('https://169.254.169.254/latest',1),('https://public.example/again',6)]:
                with patch.object(r,'artifact_connection',return_value=Connection(location)) as connect:
                    with self.assertRaises(ValueError):r.download_artifact(prepare()['artifacts'][0],pathlib.Path(d)/'blob')
                    self.assertEqual(count,connect.call_count)
    def test_tls_connection_pins_checked_ip_but_validates_original_hostname(self):
        class Socket:
            def settimeout(self,value):pass
            def connect(self,address):self.address=address
            def close(self):pass
        class Context:
            verify_mode=r.ssl.CERT_REQUIRED
            check_hostname=True
            post_handshake_auth=True
            def wrap_socket(self,sock,server_hostname):self.host=server_hostname;return sock
        sock,context=Socket(),Context()
        with patch.object(r.socket,'socket',return_value=sock), patch.object(r.ssl,'create_default_context',return_value=context), patch.object(r,'artifact_addresses',return_value=[r.ipaddress.ip_address('8.8.8.8')]):
            connection=r.artifact_connection(r.artifact_url('https://public.example/path'))
            connection.connect()
            self.assertEqual(('8.8.8.8',443),sock.address)
            self.assertEqual('public.example',context.host)
    def test_tar_and_zip_links_are_refused_and_safe_executables_are_read_only(self):
        with tempfile.TemporaryDirectory() as d:
            root=pathlib.Path(d)
            for kind in ['zip','tar','tar.gz']:
                archive=root/('blob.'+kind); destination=root/('out-'+kind);destination.mkdir()
                if kind=='zip':
                    with zipfile.ZipFile(archive,'w') as z:
                        node=zipfile.ZipInfo('link');node.external_attr=(stat.S_IFLNK|0o777)<<16;z.writestr(node,'/outside')
                else:
                    with tarfile.open(archive,'w:gz' if kind=='tar.gz' else 'w') as z:
                        node=tarfile.TarInfo('link');node.type=tarfile.SYMTYPE;node.linkname='/outside';z.addfile(node)
                with self.assertRaises(ValueError):r.unpack_artifact(archive,destination,kind,100)
            archive=root/'safe.zip';destination=root/'safe';destination.mkdir()
            with zipfile.ZipFile(archive,'w') as z:
                node=zipfile.ZipInfo('bin/tool');node.external_attr=(stat.S_IFREG|0o755)<<16;z.writestr(node,'tool')
            r.unpack_artifact(archive,destination,'zip',100)
            self.assertEqual(0o555,stat.S_IMODE((destination/'bin/tool').stat().st_mode))
            self.assertEqual(0o555,stat.S_IMODE((destination/'bin').stat().st_mode))
    def test_prepare_steps_run_only_as_principal_with_expanded_declared_cache(self):
        with tempfile.TemporaryDirectory() as d:
            root=pathlib.Path(d); (root/'base').mkdir(); (root/'artifacts').mkdir()
            class Principal:
                def run(self,args,cwd,extra_environment=None,timeout=r.COMMAND_TIMEOUT):
                    self.args,self.cwd,self.env,self.timeout=args,cwd,extra_environment,timeout
                    return 0,'prepared'
            principal=Principal(); recipe=prepare()
            log=r.run_prepare(principal,root/'base',recipe,{'sdk':str(root/'artifacts/sdk')})
            self.assertEqual(str(root/'base/tools/gen'),principal.args[0])
            self.assertEqual(str(root/'artifacts/sdk'),principal.env['SDK'])
            self.assertEqual(60,principal.timeout); self.assertIn('prepared',log)
    def test_workflow_schedules_only_requested_side_but_legacy_both(self):
        import textwrap
        script=WORKFLOW.split('      - name: Select requested execution side\n',1)[1].split("          python3 -I - <<'PY'\n",1)[1].split('\n          PY',1)[0]
        script=textwrap.dedent(script)
        with tempfile.TemporaryDirectory() as d:
            for execution,expected in [('', ['base','head']),(json.dumps({'side':'base'}),['base']),(json.dumps({'side':'head'}),['head'])]:
                output=pathlib.Path(d)/'output';output.write_text('')
                r.subprocess.run([sys.executable,'-I','-c',script],env={'EXECUTION':execution,'GITHUB_OUTPUT':str(output)},check=True,stdin=r.subprocess.DEVNULL,capture_output=True)
                self.assertEqual(expected,json.loads(output.read_text().split('=',1)[1]))
        self.assertTrue('side: ${{ fromJSON(needs.sources.outputs.sides) }}' in WORKFLOW and 'factory-ios-result-${{ inputs.nonce }}-${{ matrix.side }}' in WORKFLOW and 'EXECUTION_BUDGET_SECONDS: ${{ inputs.execution_budget_seconds }}' in WORKFLOW and 'EXECUTION_DEADLINE_UNIX_SECONDS: ${{ inputs.execution_deadline_unix_seconds }}' in WORKFLOW)
    def test_offline_resolution_is_source_only_after_mirror_install_before_build(self):
        payload,calls,_=self.run_main(hydrated=True)
        resolve=next(i for i,(args,cwd) in enumerate(calls) if '-resolvePackageDependencies' in args)
        build=next(i for i,(args,cwd) in enumerate(calls) if 'build-for-testing' in args)
        self.assertEqual(('ran',True,True,True,False),(payload['execution']['status'],calls[0][0]==['install-mirrors'],resolve<build,'-onlyUsePackageVersionsFromResolvedFile' in calls[resolve][0],'-disableAutomaticPackageResolution' in calls[resolve][0]))
    def test_cancellation_unwinds_cleanup_and_masks_repeat_signals(self):
        with patch.object(r.signal,'signal') as signal:
            with self.assertRaises(KeyboardInterrupt):r.interrupt(r.signal.SIGTERM,None)
            self.assertEqual([(r.signal.SIGINT,r.signal.SIG_IGN),(r.signal.SIGTERM,r.signal.SIG_IGN)],[call.args for call in signal.call_args_list])
    def test_transported_budget_consumes_queue_and_never_defaults_to_600(self):
        with patch.object(r.time,'time',return_value=1000), patch.object(r.time,'monotonic',return_value=20):
            self.assertEqual((1520,25,23),(r.execution_budget('1500','3000'),r.execution_budget('1500','1005'),r.execution_budget('3','3000')))
    def test_missing_invalid_or_expired_budget_is_refused(self):
        for seconds,expires in [('', '3000'),('nan','3000'),('0','3000'),('21601','3000'),('1',''),('1','999')]:
            with patch.object(r.time,'time',return_value=1000), self.assertRaises(ValueError):r.execution_budget(seconds,expires)
    def test_requested_validation_flags_and_offline_build_flags_are_preserved(self):
        _,calls,_=self.run_main()
        builds=[args for args,cwd in calls if 'build-for-testing' in args]
        self.assertTrue(all(flag in builds[0] for flag in ['-skipMacroValidation','-skipPackagePluginValidation','-disableAutomaticPackageResolution','-skipPackageUpdates']))
    def test_remaining_budget_bounds_each_command_and_prepare_timeout(self):
        with patch.object(r,'EXECUTION_DEADLINE',125), patch.object(r.time,'monotonic',return_value=100):
            self.assertEqual((25,4,25),(r.remaining(),r.remaining(4),r.remaining(900)))
    def test_public_package_pins_all_supported_lockfile_versions(self):
        with tempfile.TemporaryDirectory() as d:
            root=pathlib.Path(d); url='https://public.example/dependency.git'; state={'revision':BASE}
            for value in [dict(version=1,object={'pins':[{'repositoryURL':url,'state':state}]}),dict(version=2,pins=[{'location':url,'kind':'remoteSourceControl','state':state}]),dict(version=3,pins=[{'location':url,'kind':'remoteSourceControl','state':state}])]:
                (root/'Package.resolved').write_text(json.dumps(value))
                self.assertEqual({url:BASE},r.package_pins(root))
    def test_unpinned_private_or_registry_package_is_explicitly_refused(self):
        with tempfile.TemporaryDirectory() as d:
            root=pathlib.Path(d)
            for url,revision in [('git@public.example:repo',BASE),('https://u:p@public.example/repo',BASE),('https://127.0.0.1/repo',BASE),('https://public.example/repo','main'),('https://public.example/repo',None)]:
                (root/'Package.resolved').write_text(json.dumps(dict(version=3,pins=[{'location':url,'state':{'revision':revision}}])))
                with self.assertRaises(ValueError):r.package_pins(root)
    def test_package_transport_uses_checked_https_and_exact_revision_without_credentials(self):
        content=b'PACKsealed'; received=[]
        class Response:
            status=200
            def __init__(self):self.stream=io.BytesIO(r.packet(('shallow '+BASE+'\n').encode())+b'0000'+r.packet(b'NAK\n')+r.packet(b'\x01'+content)+b'0000')
            def getheader(self,key,default=''):return 'application/x-git-upload-pack-result'
            def read(self,n):return self.stream.read(n)
        class Connection:
            def request(self,*args,**kwargs):received.append((args,kwargs))
            def getresponse(self):return Response()
            def close(self):pass
        with tempfile.TemporaryDirectory() as d, patch.object(r,'artifact_connection',return_value=Connection()):
            target=pathlib.Path(d)/'objects.pack';r.fetch_package('https://public.example/repo.git',BASE,target)
            self.assertEqual((content,'POST','/repo.git/git-upload-pack',True,False),(target.read_bytes(),received[0][0][0],received[0][0][1],BASE.encode() in received[0][1]['body'],'Authorization' in received[0][1]['headers']))
    def test_package_auth_and_missing_repositories_have_safe_explicit_failure_codes(self):
        class Connection:
            def __init__(self,status):self.status=status
            def request(self,*args,**kwargs):pass
            def getresponse(self):return self
            def close(self):pass
        for status,expected in [(401,'package-auth-required'),(403,'package-auth-required'),(404,'package-unavailable')]:
            with patch.object(r,'artifact_connection',return_value=Connection(status)):
                try:r.fetch_package('https://public.example/repo',BASE,pathlib.Path('/unused'))
                except r.PrepareFailure as failure:actual=failure.code
                else:actual='no refusal'
                self.assertEqual(expected,actual)
    def test_hydrated_local_mirror_contains_real_pinned_objects_without_evaluating_manifest(self):
        with tempfile.TemporaryDirectory() as d:
            root=pathlib.Path(d); source=root/'source';source.mkdir();worktree=root/'worktree';worktree.mkdir()
            def git(args,cwd=source):
                result=r.subprocess.run(['/usr/bin/git','-c','core.hooksPath=/dev/null',*args],cwd=cwd,env={'PATH':'/usr/bin:/bin','HOME':str(root),'GIT_CONFIG_NOSYSTEM':'1','GIT_CONFIG_GLOBAL':'/dev/null'},stdin=r.subprocess.DEVNULL,capture_output=True,check=True)
                return result.stdout
            git(['init']);(source/'Package.swift').write_text('// swift-tools-version: 6.0\nimport PackageDescription\nlet package = Package(name: "Pinned", products: [.library(name: "Pinned", targets: ["Pinned"])], targets: [.target(name: "Pinned")])')
            (source/'Sources/Pinned').mkdir(parents=True);(source/'Sources/Pinned/Pinned.swift').write_text('public let pinned = 1')
            git(['add','.']);git(['-c','user.name=Fixture','-c','user.email=fixture@example.invalid','commit','-m','pinned'])
            revision=git(['rev-parse','HEAD']).decode().strip()
            pack=git(['pack-objects','--all','--stdout'])
            url='https://public.example/package.git'
            (worktree/'Package.resolved').write_text(json.dumps(dict(version=3,pins=[dict(location=url,state={'revision':revision,'version':'1.2.3'})])))
            def local(args,cwd,env=None,**kwargs):
                result=r.subprocess.run(args,cwd=cwd,env=env,stdin=r.subprocess.DEVNULL,capture_output=True)
                return result.returncode,result.stdout.decode()+result.stderr.decode()
            with patch.object(r,'fetch_package',side_effect=lambda u,v,p:p.write_bytes(pack)),patch.object(r,'command',side_effect=local):
                mirrors=r.hydrate_packages(root,worktree)
            mirror=pathlib.Path(r.urllib.parse.urlsplit(mirrors[0]['mirror']).path)
            actual=git(['--git-dir='+str(mirror),'rev-parse','refs/heads/factory-pin']).decode().strip()
            self.assertEqual((revision,url,0),(actual,mirrors[0]['original'],mirror.stat().st_mode&0o222))
            if sys.platform=='darwin' and pathlib.Path('/usr/bin/swift').exists():
                home=root/'isolated-home';home.mkdir()
                class Principal:
                    def __init__(self):self.home=home;self.env={}
                    def share(self,path):pass
                    def run(self,args,cwd,**kwargs):
                        result=r.subprocess.run(args,cwd=cwd,env={'PATH':'/usr/bin:/bin','HOME':str(home),'CFFIXED_USER_HOME':str(home)},stdin=r.subprocess.DEVNULL,capture_output=True)
                        return result.returncode,result.stdout.decode()+result.stderr.decode()
                r.install_package_mirrors(Principal(),mirrors)
                consumer=root/'consumer';consumer.mkdir();(consumer/'Sources/App').mkdir(parents=True)
                (consumer/'Sources/App/App.swift').write_text('import Pinned\nprint(pinned)')
                (consumer/'Package.swift').write_text('// swift-tools-version: 6.0\nimport PackageDescription\nlet package = Package(name: "App", dependencies: [.package(url: "'+url+'", exact: "1.2.3")], targets: [.executableTarget(name: "App", dependencies: [.product(name: "Pinned", package: "package")])])')
                (consumer/'Package.resolved').write_text(json.dumps(dict(version=2,pins=[dict(identity='package',kind='remoteSourceControl',location=url,state={'revision':revision,'version':'1.2.3'})])))
                profile=root/'offline.sb';profile.write_text('(version 1)(allow default)(deny network*)')
                result=r.subprocess.run(['/usr/bin/sandbox-exec','-f',str(profile),'/usr/bin/swift','package','--package-path',str(consumer),'--disable-sandbox','--cache-path',str(home/'cache'),'--scratch-path',str(home/'build'),'resolve','--force-resolved-versions'],env={'PATH':'/usr/bin:/bin:/usr/sbin:/sbin','HOME':str(home),'CFFIXED_USER_HOME':str(home)},stdin=r.subprocess.DEVNULL,capture_output=True)
                self.assertEqual(0,result.returncode,result.stdout.decode()+result.stderr.decode())
            # TemporaryDirectory cleanup needs write permission restored to our
            # own fixture tree, not a production workspace or helper.
            for directory,_,files in os.walk(root/'packages'):
                pathlib.Path(directory).chmod(0o755)
                for name in files:(pathlib.Path(directory)/name).chmod(0o644)
    def test_command_teardown_never_signals_reaped_pid_but_kills_unreaped_timeout_even_if_sweep_errors(self):
        from unittest.mock import MagicMock
        proc=MagicMock();proc.pid=43210;proc.stdout=io.BytesIO();proc.poll.return_value=0
        with patch.object(r.subprocess,'Popen',return_value=proc),patch.object(r.os,'killpg') as terminate,patch.object(r.selectors,'DefaultSelector') as selector:
            selector.return_value.__enter__.return_value.get_map.return_value={}
            r.command(['/trusted/tool'],'/')
            self.assertEqual([],[call.args for call in terminate.call_args_list])
        proc.wait.side_effect=[r.subprocess.TimeoutExpired('tool',1),0]
        with patch.object(r.subprocess,'Popen',return_value=proc),patch.object(r.os,'killpg') as terminate,patch.object(r.selectors,'DefaultSelector') as selector:
            selector.return_value.__enter__.return_value.get_map.return_value={}
            with self.assertRaisesRegex(ValueError,'sweep failed'):r.command(['/trusted/tool'],'/',stop=lambda:(_ for _ in ()).throw(ValueError('sweep failed')))
            self.assertEqual([(43210,r.signal.SIGKILL)],[call.args for call in terminate.call_args_list])
    def test_generic_git_transport_preserves_an_older_pin_with_default_upload_pack_policy(self,force_refusal=False):
        with tempfile.TemporaryDirectory() as d:
            root=pathlib.Path(d);source=root/'source';source.mkdir();worktree=root/'base';worktree.mkdir()
            env={'PATH':'/usr/bin:/bin','HOME':str(root),'GIT_CONFIG_NOSYSTEM':'1','GIT_CONFIG_GLOBAL':'/dev/null'}
            def git(args,cwd=source):return r.subprocess.run(['/usr/bin/git',*args],cwd=cwd,env=env,stdin=r.subprocess.DEVNULL,capture_output=True,check=True).stdout
            git(['init']);(source/'Package.swift').write_text('pinned original')
            git(['add','.']);git(['-c','user.name=Fixture','-c','user.email=fixture@example.invalid','commit','-m','pinned'])
            revision=git(['rev-parse','HEAD']).decode().strip()
            (source/'Package.swift').write_text('later head');git(['add','.']);git(['-c','user.name=Fixture','-c','user.email=fixture@example.invalid','commit','-m','later'])
            bare=root/'server.git';git(['clone','--bare',str(source),str(bare)])
            advertised=git(['upload-pack','--stateless-rpc','--advertise-refs',str(bare)])
            requests=[]
            class Response:
                status=200
                def __init__(self,data):self.data=io.BytesIO(data)
                def read(self,n):return self.data.read(n)
                def getheader(self,key,default=''):return 'application/x-git-upload-pack-result'
            class Connection:
                def request(self,*args,**kwargs):self.body=kwargs['body'];requests.append(self.body)
                def getresponse(self):
                    if force_refusal and len(requests)==1:return Response(r.packet(b'ERR upload-pack: not our ref\n'))
                    response=r.subprocess.run(['/usr/bin/git','upload-pack','--stateless-rpc',str(bare)],input=self.body,capture_output=True,env=env)
                    return Response(response.stdout)
                def close(self):pass
            def local(args,cwd,env=None,**kwargs):
                result=r.subprocess.run(args,cwd=cwd,env=env,stdin=r.subprocess.DEVNULL,capture_output=True)
                return result.returncode,result.stdout.decode()+result.stderr.decode()
            (worktree/'Package.resolved').write_text(json.dumps({'version':2,'pins':[{'location':'https://public.example/generic.git','state':{'revision':revision}}]}))
            try:
                with patch.object(r,'artifact_connection',side_effect=lambda u:Connection()),patch.object(r,'public_resource',return_value=(advertised,{})),patch.object(r,'command',side_effect=local):
                    mirrors=r.hydrate_packages(root,worktree)
                mirror=pathlib.Path(r.urllib.parse.urlsplit(mirrors[0]['mirror']).path)
                actual=git(['--git-dir='+str(mirror),'rev-parse','refs/heads/factory-pin']).decode().strip()
            except ValueError:actual='refused'
            self.assertEqual(revision,actual)
            for directory,_,files in os.walk(root/'packages'):
                pathlib.Path(directory).chmod(0o755)
                for name in files:(pathlib.Path(directory)/name).chmod(0o644)
    def test_generic_git_refusal_negotiates_advertised_history_without_changing_the_pin(self):
        self.test_generic_git_transport_preserves_an_older_pin_with_default_upload_pack_policy(force_refusal=True)
    def test_pinned_source_tools_avoid_shim_access_to_owner_darwin_cache(self):
        principal=r.Principal.__new__(r.Principal);developer='/Applications/Xcode_16.4.app/Contents/Developer'
        principal.env={'HOME':'/source/home','PATH':'/usr/bin:/bin','DEVELOPER_DIR':developer};principal.profile=pathlib.Path('/sealed/build.sb');principal.sweep=lambda:None
        calls=[]
        with patch.object(r,'command',side_effect=lambda args,cwd,**kwargs:(calls.append(args) or (0,''))):
            for tool in ['python3','swift','xcodebuild','xcrun']:principal.run(['/usr/bin/'+tool],'/')
        vectors=[args[args.index('-f')+2:] for args in calls]
        self.assertEqual([[developer+'/usr/bin/python3'],[developer+'/Toolchains/XcodeDefault.xctoolchain/usr/bin/swift'],[developer+'/usr/bin/xcodebuild'],['/usr/bin/xcrun','--no-cache']],vectors)
    def test_root_mapping_backend_never_reaches_native_administration_in_fixtures(self):
        with tempfile.TemporaryDirectory() as d:
            backend=r.RegistryHosts(pathlib.Path(d))
            name='factory-ios-'+backend.nonce+'-'+'1'*16+'.invalid'
            with self.assertRaisesRegex(AssertionError,'native host administration forbidden'):backend.install([name])
    def test_root_mapping_script_changes_only_its_generated_marked_entries(self):
        namespace={'__name__':'sealed_hosts_test'};exec(r.HOSTS_SCRIPT,namespace);edit=namespace['edit_hosts']
        with tempfile.TemporaryDirectory() as d:
            path=pathlib.Path(d)/'hosts';path.write_bytes(b'127.0.0.1 localhost\n::1 localhost\n');path.chmod(0o600)
            nonce='a'*32;other='b'*32
            names=['factory-ios-'+nonce+'-'+format(i,'016x')+'.invalid' for i in range(6)]
            edit(path,'install',other,['factory-ios-'+other+'-'+'f'*16+'.invalid'],os.getuid())
            original=path.read_bytes();edit(path,'install',nonce,names,os.getuid());installed=path.read_bytes()
            edit(path,'install',nonce,names,os.getuid())
            self.assertEqual((installed,12),(path.read_bytes(),sum(nonce.encode() in line for line in path.read_bytes().splitlines())))
            edit(path,'remove',nonce,names,os.getuid())
            self.assertEqual(original,path.read_bytes())
            target=pathlib.Path(d)/'other';target.write_bytes(original);target.chmod(0o600)
            for kind in ['symlink','fifo','world-write','wrong-owner','foreign-marker']:
                path.unlink()
                if kind=='symlink':path.symlink_to(target)
                elif kind=='fifo':os.mkfifo(path)
                else:path.write_bytes(original if kind!='foreign-marker' else ('127.0.0.1 unfamiliar # factory-ios:'+nonce+'\n').encode());path.chmod(0o666 if kind=='world-write' else 0o600)
                native_fstat=os.fstat;calls=[]
                def inspect(fd):
                    node=native_fstat(fd);calls.append(True)
                    if kind=='wrong-owner' and len(calls)==2:
                        fields=list(node);fields[4]=os.getuid()+1;return os.stat_result(fields)
                    return node
                with self.subTest(kind=kind),patch.object(namespace['os'],'fstat',side_effect=inspect),self.assertRaises((ValueError,OSError)):edit(path,'install',nonce,names,os.getuid())
    def test_source_workspace_refused_cleanup_never_falls_back_to_owner_tree_walk(self):
        root=None
        try:
            with patch.object(r.shutil,'rmtree') as owner_walk:
                try:
                    with r.RemoteWorkspace() as workspace:
                        root=workspace.root;workspace.handoff()
                        raise ValueError('principal cleanup refused')
                except ValueError:pass
                self.assertEqual((True,False),(root.exists(),owner_walk.called))
        finally:
            if root is not None and root.exists():r.tempfile.TemporaryDirectory._rmtree(root)
    def test_registry_constructor_cleanup_failure_is_not_a_publishable_prepare_refusal(self):
        with tempfile.TemporaryDirectory() as d:
            root=pathlib.Path(d);(root/'packages').mkdir();events=[]
            (root/'packages/registry-plan.json').write_text(json.dumps({'urls':{'a':'https://registry.example'},'security':{},'routes':[]}))
            class Hosts:
                nonce='a'*32
                def original_hosts(self,urls):return {url:'registry.example' for url in urls}
                def install(self,names):events.append('install');raise OSError('install failed')
                def remove(self):events.append('remove');raise ValueError('cleanup refused')
            try:r.RegistryMirror(root,Hosts())
            except BaseException as error:failure=type(error)
            else:failure=None
            self.assertEqual((r.CleanupFailure,['install','remove']),(failure,events))
    def test_storage_bootstrap_refusal_sweeps_and_deletes_fresh_role_before_reporting(self):
        principal=r.Principal.__new__(r.Principal);events=[]
        principal.darwin_directories=lambda:(_ for _ in ()).throw(r.PrepareFailure('package-storage-unavailable'))
        principal.sweep=lambda:events.append('sweep')
        with patch.object(r,'admin',side_effect=lambda args,**kwargs:events.append((args,kwargs))):
            with self.assertRaisesRegex(r.PrepareFailure,'package-storage-unavailable'):principal.bootstrap_storage()
        self.assertEqual(['sweep',(['/usr/bin/dscl','.','-delete','/Users/_factory_ios_build'],{'cleanup':True})],events)
        with patch.object(r,'admin',side_effect=ValueError('cleanup refused')):
            with self.assertRaisesRegex(ValueError,'cleanup refused'):principal.bootstrap_storage()
    def test_post_prepare_package_snapshot_refuses_symlinks_and_fifos_without_privileged_reads(self):
        with tempfile.TemporaryDirectory() as d:
            root=pathlib.Path(d);worktree=root/'base';worktree.mkdir();secret=root/'fixture-secret';secret.write_text('{"fixture":"not a real secret"}')
            class Principal:
                def run(self,args,cwd,**kwargs):
                    result=r.subprocess.run([sys.executable,*args[1:]],cwd=cwd,stdin=r.subprocess.DEVNULL,capture_output=True)
                    return result.returncode,result.stdout.decode()+result.stderr.decode()
            path=worktree/'Package.resolved'
            for kind in ['symlink','fifo']:
                if kind=='symlink':path.symlink_to(secret)
                else:os.mkfifo(path)
                try:
                    with self.subTest(kind=kind),self.assertRaises(ValueError):r.package_snapshot(Principal(),worktree)
                finally:path.unlink()
    def test_profile_rechecks_source_replaced_darwin_cache_before_expanding_network(self):
        with tempfile.TemporaryDirectory() as d:
            root=pathlib.Path(d);principal=r.Principal.__new__(r.Principal)
            principal.root=root;principal.home=root/'home';principal.uid=550;principal.collector_uid=501
            principal.platform_directories=[pathlib.Path('/private/var/folders/xx/fresh/T')];principal.profile=root/'build.sb';principal.profile.write_text('original confined profile')
            with patch.dict(os.environ,SIDE='base'),patch.object(r,'darwin_directory',side_effect=ValueError('source replaced cache with symlink')):
                with self.assertRaisesRegex(r.PrepareFailure,'package-storage-unavailable'):principal.registry_endpoints([('127.0.0.1',1234)])
            self.assertEqual('original confined profile',principal.profile.read_text())
    def test_darwin_runtime_directories_require_fresh_uid_real_ancestors_and_closed_modes(self):
        from types import SimpleNamespace
        path=pathlib.Path('/private/var/folders/xx/fresh/T')
        def node(mode=stat.S_IFDIR|0o700,owner=550):return SimpleNamespace(st_mode=mode,st_uid=owner)
        self.assertEqual(path,r.darwin_directory(str(path),550,501,lambda p:node()))
        for bad in ['same-owner','owner-leaf','root-leaf','symlink-ancestor','non-dir','world-write','outside','traversal']:
            def inspect(p):
                if bad=='owner-leaf' and p==path:return node(owner=501)
                if bad=='root-leaf' and p==path:return node(owner=0)
                if bad=='non-dir' and p.name=='fresh':return node(stat.S_IFREG|0o700)
                if bad=='symlink-ancestor' and p.name=='fresh':return node(stat.S_IFLNK|0o700)
                if bad=='world-write' and p==path:return node(stat.S_IFDIR|0o777)
                return node()
            value='/tmp/T' if bad=='outside' else str(path.parent/'..'/'T') if bad=='traversal' else str(path)
            with self.subTest(bad=bad),self.assertRaises(ValueError):r.darwin_directory(value,550,550 if bad=='same-owner' else 501,inspect)
    @unittest.skipUnless(sys.platform=='darwin','native offline package fixture requires macOS')
    def test_native_binary_zip_is_hydrated_before_offline_resolution(self,indexed=False):
        import plistlib
        with tempfile.TemporaryDirectory() as d:
            root=pathlib.Path(d);worktree=root/'base';worktree.mkdir();(root/'packages').mkdir()
            archive=root/'binary.zip'
            with zipfile.ZipFile(archive,'w') as z:
                z.writestr('Remote.xcframework/Info.plist',plistlib.dumps({'CFBundlePackageType':'XFWK','XCFrameworkFormatVersion':'1.0','AvailableLibraries':[{'LibraryIdentifier':'macos-arm64','LibraryPath':'Remote.framework','SupportedArchitectures':['arm64'],'SupportedPlatform':'macos'}]}))
                z.writestr('Remote.xcframework/macos-arm64/Remote.framework/Remote',b'fixture binary, never executed')
            body=archive.read_bytes();checksum=hashlib.sha256(body).hexdigest();url='https://public.example/Remote.zip'
            documents={url:body}
            if indexed:
                url='https://public.example/Remote.artifactbundleindex'
                documents[url]=json.dumps({'schemaVersion':'1.0','archives':[{'fileName':'Remote.zip','checksum':checksum,'supportedTriples':['arm64-apple-macosx','x86_64-apple-macosx']}]}).encode()
                checksum=hashlib.sha256(documents[url]).hexdigest()
            (worktree/'Package.swift').write_text('// swift-tools-version: 6.0\nimport PackageDescription\nlet package = Package(name:"Wrapper",products:[.library(name:"Wrapper",targets:["Remote"])],targets:[.binaryTarget(name:"Remote",url:"'+url+'",checksum:"'+checksum+'")])')
            principal=NativePrincipal(root,worktree)
            with patch.object(r,'download_artifact',side_effect=lambda a,p:p.write_bytes(documents[a['url']])):
                r.hydrate_binary_packages(principal,root,worktree,[],principal.home/'build')
            code,output=principal.run(['/usr/bin/swift','package','--package-path',str(worktree),'--disable-sandbox','--scratch-path',str(principal.home/'build'),'resolve','--force-resolved-versions'],worktree)
            self.assertEqual(0,code,output)
            for directory,_,files in os.walk(root/'packages'):
                pathlib.Path(directory).chmod(0o755)
                for name in files:(pathlib.Path(directory)/name).chmod(0o644)
    @unittest.skipUnless(sys.platform=='darwin','native offline package fixture requires macOS')
    def test_native_binary_artifact_index_preserves_offline_pinned_archive(self):
        self.test_native_binary_zip_is_hydrated_before_offline_resolution(indexed=True)
    @unittest.skipUnless(sys.platform=='darwin','native registry fixture requires macOS')
    def test_native_registry_mirror_preserves_two_host_trust_policies_and_denies_other_network(self,signed=False,confinement_only=False):
        import urllib.parse
        with tempfile.TemporaryDirectory() as d:
            root=pathlib.Path(d);worktree=root/'base';worktree.mkdir();cache=root/'packages';cache.mkdir()
            configuration=worktree/'.swiftpm/configuration';configuration.mkdir(parents=True)
            security={'registryOverrides':{'registry-a.example':{'signing':{'onUnsigned':'warn'}},'xn--bcher-kva.example':{'signing':{'onUnsigned':'error'}}}}
            security['registryOverrides']['registry-c.example']={'signing':{'onUnsigned':'silentAllow'}}
            if signed:security['registryOverrides']['xn--bcher-kva.example']['signing']={'onUnsigned':'warn','onUntrustedCertificate':'error'}
            (configuration/'registries.json').write_text(json.dumps({'version':1,'registries':{'a':{'url':'https://registry-a.example'},'b':{'url':'https://BÜCHER.example'},'c':{'url':'https://registry-c.example'}},'security':security}))
            resources={};archives={}
            for scope,name in [('a','alpha'),('b','beta'),('c','gamma')]:
                manifest='// swift-tools-version: 6.0\nimport PackageDescription\nlet package = Package(name:"'+name+'",products:[.library(name:"'+name+'",targets:["'+name+'Building"])],targets:[.target(name:"'+name+'Building")])'
                stream=io.BytesIO()
                with zipfile.ZipFile(stream,'w') as z:
                    z.writestr(name+'/Package.swift',manifest);z.writestr(name+'/Sources/'+name+'Building/Value.swift','public let value = 1')
                body=stream.getvalue();url='https://registry-'+scope+'.example/'+scope+'/'+name+'/1.0.0'
                resources[url]=(json.dumps({'id':scope+'.'+name,'version':'1.0.0','resources':[{'name':'source-archive','type':'application/zip','checksum':hashlib.sha256(body).hexdigest()}]}).encode(),{'Content-Type':'application/json','Content-Version':'1'})
                resources[url+'/Package.swift']=(manifest.encode(),{'Content-Type':'text/x-swift','Content-Version':'1'})
                if signed and scope=='b':
                    metadata=json.loads(resources[url][0]);metadata['resources'][0]['signing']={'signatureBase64Encoded':'bm90IGEgQ01TIHNpZ25hdHVyZQ==','signatureFormat':'cms-1.0.0'}
                    resources[url]=(json.dumps(metadata).encode(),resources[url][1])
                archives[url+'.zip']=body
            normalize=lambda url:url.replace('https://BÜCHER.example','https://registry-b.example')
            with patch.object(r,'public_resource',side_effect=lambda u,a:resources[normalize(u)]),patch.object(r,'download_artifact',side_effect=lambda a,p:p.write_bytes(archives[normalize(a['url'])])):
                r.hydrate_registry_packages(root,worktree,[('a.alpha','1.0.0'),('b.beta','1.0.0'),('c.gamma','1.0.0')],cache)
            principal=NativePrincipal(root,worktree,filesystem=confinement_only)
            backend=FixtureRegistryHosts(root)
            if confinement_only:backend.original_hosts=lambda urls:{url:r.urllib.parse.urlsplit(url).hostname for url in urls}
            with patch.object(r,'command',side_effect=lambda args,cwd,**kwargs:principal.run(['/usr/bin/swift',*args[1:]],root)):
                mirror=r.RegistryMirror(root,backend)
            try:
                principal.registry_endpoints(mirror.endpoints);r.install_package_mirrors(principal,[],mirror)
                if not confinement_only:
                    def consumer(directory,dependencies):
                        directory.mkdir(exist_ok=True);(directory/'Sources/App').mkdir(parents=True,exist_ok=True)
                        (directory/'Sources/App/App.swift').write_text('public let app = 1')
                        (directory/'Package.swift').write_text('// swift-tools-version: 6.0\nimport PackageDescription\nlet package = Package(name:"App",dependencies:['+','.join('.package(id:"'+x+'",exact:"1.0.0")' for x in dependencies)+'],targets:[.target(name:"App")])')
                        (directory/'Package.resolved').write_text(json.dumps({'version':2,'pins':[{'identity':x,'kind':'registry','location':'','state':{'version':'1.0.0'}} for x in dependencies]}))
                    consumer(worktree,['a.alpha'])
                    allowed,output=principal.run(['/usr/bin/swift','package','--package-path',str(worktree),'--disable-sandbox','--scratch-path',str(principal.home/'build-allowed'),'resolve','--force-resolved-versions'],worktree)
                    self.assertEqual((0,True),(allowed,'not signed' in output),output)
                    consumer(worktree,['a.alpha','b.beta'])
                    refused,output=principal.run(['/usr/bin/swift','package','--package-path',str(worktree),'--disable-sandbox','--scratch-path',str(principal.home/'build-refused'),'resolve','--force-resolved-versions'],worktree)
                    self.assertEqual((True,'package-signature-refused'),(refused!=0,r.package_resolution_failure(output)),output)
                    consumer(worktree,['c.gamma'])
                    silent,output=principal.run(['/usr/bin/swift','package','--package-path',str(worktree),'--disable-sandbox','--scratch-path',str(principal.home/'build-silent'),'resolve','--force-resolved-versions'],worktree)
                    self.assertEqual((0,False),(silent,'not signed' in output),output)
                # Native URLSession above uses the owner's normal filesystem
                # only for harmless test-authored packages. Reapply the FULL
                # production filesystem jail before all confinement probes.
                principal.filesystem=True;principal.registry_endpoints(mirror.endpoints)
                # Live alternate listener distinguishes sandbox denial from a
                # merely unavailable socket; no real credentials or Internet IO.
                class Handler(r.http.server.BaseHTTPRequestHandler):
                    def do_GET(self):self.send_response(200);self.end_headers()
                    def log_message(self,*args):pass
                other=r.http.server.ThreadingHTTPServer(('127.0.0.1',0),Handler)
                thread=r.threading.Thread(target=other.serve_forever,daemon=True);thread.start()
                try:
                    probe='import socket,sys; s=socket.socket(); s.settimeout(1); s.connect((sys.argv[1],int(sys.argv[2])))'
                    denied,output=principal.run(['/usr/bin/python3','-I','-c',probe,'127.0.0.1',str(other.server_port)],root)
                    self.assertTrue(denied!=0 and 'Operation not permitted' in output,output)
                    udp=r.socket.socket(r.socket.AF_INET,r.socket.SOCK_DGRAM);udp.bind(('127.0.0.1',mirror.endpoints[0][1]))
                    try:
                        denied,output=principal.run(['/usr/bin/python3','-I','-c','import socket,sys; socket.socket(socket.AF_INET,socket.SOCK_DGRAM).sendto(b"fixture",("127.0.0.1",int(sys.argv[1])))',str(mirror.endpoints[0][1])],root)
                        self.assertTrue(denied!=0 and 'Operation not permitted' in output,output)
                    finally:udp.close()
                    denied,output=principal.run(['/usr/bin/python3','-I','-c',probe,'192.0.2.1','443'],root)
                    self.assertTrue(denied!=0 and 'Operation not permitted' in output,output)
                    secret=root/'owner-fixture-secret';secret.write_text('fixture only, never real credentials')
                    denied,output=principal.run(['/usr/bin/python3','-I','-c','import pathlib,sys; print(pathlib.Path(sys.argv[1]).read_text())',str(secret)],root)
                    self.assertTrue(denied!=0 and 'Operation not permitted' in output and 'fixture only' not in output,output)
                finally:other.shutdown();other.server_close();thread.join()
            finally:mirror.close()
            connections=[]
            for host,port in mirror.endpoints:
                sock=r.socket.socket(r.socket.AF_INET6 if host=='::1' else r.socket.AF_INET);sock.settimeout(0.2)
                connections.append(sock.connect_ex((host,port)));sock.close()
            self.assertTrue(all(code!=0 for code in connections) and mirror.hosts.removed)
    @unittest.skipUnless(sys.platform=='darwin','native registry fixture requires macOS')
    def test_registry_ipc_and_filesystem_confinement_without_native_resolver_warmup(self):
        self.test_native_registry_mirror_preserves_two_host_trust_policies_and_denies_other_network(confinement_only=True)
    @unittest.skipUnless(sys.platform=='darwin','native registry fixture requires macOS')
    def test_native_registry_rejects_malformed_signed_archive_without_weakening_unsigned_policy(self):
        self.test_native_registry_mirror_preserves_two_host_trust_policies_and_denies_other_network(signed=True)
    def test_combined_result_is_bounded_without_clipping_evidence(self):
        with self.assertRaises(ValueError): r.write_execution_result({'execution':{'log':'a'*(2*1024*1024)}})
unittest.main()
"""";
}
