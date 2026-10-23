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
    public async Task General_execution_fixtures_validate_descriptors_overlay_and_collect_raw_results_without_running_repository_code()
    {
        var directory = Directory.CreateTempSubdirectory("df-ios-execution-tests-").FullName;
        try
        {
            var helper = Path.Combine(directory, "runner.py");
            await File.WriteAllTextAsync(helper, RemoteIosWorkflow.Runner);
            using var process = Process.Start(new ProcessStartInfo("python3", ["-I", "-c", Fixtures, helper])
            {
                WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true
            })!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var output = await stdout + await stderr;
            Assert.True(process.ExitCode == 0, output);
            Assert.Contains("OK", output);
        }
        finally { Directory.Delete(directory, true); }
    }

    private const string Fixtures = """"
import copy, hashlib, importlib.util, io, json, os, pathlib, stat, sys, tarfile, tempfile, unittest, zipfile
from unittest.mock import patch
spec = importlib.util.spec_from_file_location('runner', sys.argv[1])
r = importlib.util.module_from_spec(spec)
spec.loader.exec_module(r)
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
class Fixtures(unittest.TestCase):
    def parse(self, value):
        return r.parse_execution(json.dumps(value), REQUEST)
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
    def run_main(self, side='base', build_code=0, report_code=0, execution_side='base', close_failure=False, multiple_builds=False, prepared=False, prepare_code=0):
        value=descriptor()
        if execution_side=='head':
            value.update(side='head',commit=HEAD,overlayFrom=None,overlayPaths=[],deletePaths=[])
        if multiple_builds:
            value['builds'][0]['tests']=[TESTS[0]]
            value['builds'].append(dict(flag='-workspace',container='Other.xcworkspace',scheme='Other',tests=[TESTS[1]]))
        if prepared:value['prepare']=prepare()
        raw=json.dumps(value)
        calls=[]; closed=[]
        class Principal:
            def __init__(self,root):
                self.root=root
                if prepared:
                    assert (root/'artifacts/sdk').read_bytes()==b'payload'
                    assert stat.S_IMODE((root/'artifacts/sdk').stat().st_mode)==0o444
            def share(self,path): pass
            def close(self):
                closed.append(True)
                if close_failure: raise ValueError('descendants survived')
            def run(self,args,cwd,extra_environment=None,timeout=r.COMMAND_TIMEOUT):
                calls.append((args,cwd))
                if args[0].endswith('/tools/gen'):
                    assert extra_environment['SDK']==str(self.root/'artifacts/sdk')
                    assert timeout==60
                    return prepare_code,'prepared' if not prepare_code else 'Tests/New.swift:3:5: error: generator failed'
                if '-version' in args:return 0,'Xcode 16.4\nBuild version 16F6'
                if 'list' in args:return 0,json.dumps({'runtimes':[{'identifier':r.ENVIRONMENT['runtime'],'isAvailable':True}], 'devicetypes':[{'identifier':r.ENVIRONMENT['deviceType']}]})
                if 'create' in args:return 0,'AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE'
                if 'build-for-testing' in args:
                    self.assert_overlay(cwd)
                    return build_code,str(cwd)+'/Tests/New.swift:3:5: error: missing symbol' if build_code else 'build passed'
                return 65,'test output'
            def assert_overlay(self,cwd):
                assert (cwd/'Tests/New.swift').read_text()=='test body'
                assert (cwd/'App.xcodeproj/project.pbxproj').read_text()=='generated graph'
        with tempfile.TemporaryDirectory() as d:
            old=os.getcwd(); os.chdir(d)
            try:
                pathlib.Path('sources').mkdir()
                for name in ['base','head']:
                    with zipfile.ZipFile('sources/'+name+'.zip','w') as z:
                        z.writestr('repo/Tests/'+('New' if name=='head' else 'Old')+'.swift','test body')
                env=dict(REQUEST,SIDE=side,EXECUTION=raw)
                with patch.dict(os.environ,env), patch.object(r,'Principal',Principal), patch.object(r,'command',return_value=(report_code,'{"testNodes":[]}')), patch.object(r,'download_artifact',side_effect=lambda a,p:p.write_bytes(b'payload')):
                    r.main()
                payload=json.loads(pathlib.Path('result.json').read_text())
                self.assertEqual(hashlib.sha256(raw.encode()).hexdigest(),payload['executionHash'])
                self.assertEqual(side,payload['side'])
                return payload,calls,closed
            finally: os.chdir(old)
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
        self.assertTrue(closed); self.assertEqual('error',payload['execution']['status'])
        self.assertEqual('',payload['execution']['log'])
        self.assertIn('generator failed',payload['execution']['prepareLog'])
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
    def test_combined_result_is_bounded_without_clipping_evidence(self):
        with self.assertRaises(ValueError): r.write_execution_result({'execution':{'log':'a'*(2*1024*1024)}})
unittest.main()
"""";
}
