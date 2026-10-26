#!/usr/bin/python3 -I
"""Trusted-default Ubuntu bridge; NEVER checks out, imports, or executes source-repository code.
The App key is a protected-environment secret, never a source/collector runtime variable.
"""
import base64
import hashlib
import io
import json
import os
import pathlib
import re
import secrets
import signal
import stat
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile

API = 'https://api.github.com/'
ENVIRONMENT = 'factory-gui-dispatch'
WORKFLOW = '.github/workflows/factory-gui-source-job.yml'
MAX_ZIP_BYTES = 128 * 1024 * 1024
MAX_UNPACKED_BYTES = 100 * 1024 * 1024
MAX_ENTRIES = 100000


class Refused(Exception):
    pass


def require(value, reason):
    if not value:
        raise Refused(reason)


def b64(value):
    return base64.urlsafe_b64encode(value).rstrip(b'=')


def app_jwt(app_id, private_key, now):
    require(re.fullmatch(r'[0-9]{1,30}', app_id), 'invalid App identity')
    payload = b64(b'{"alg":"RS256","typ":"JWT"}') + b'.' + b64(json.dumps({'iat': now - 60, 'exp': now + 540, 'iss': app_id}, separators=(',', ':')).encode())
    require(len(private_key.encode()) <= 4096, 'App signing material exceeds the pipe bound')
    read_fd, write_fd = os.pipe()
    try:
        # The private key is neither an argument nor a file. OpenSSL sees one inherited read-only pipe.
        os.write(write_fd, private_key.encode())
        os.close(write_fd)
        write_fd = -1
        signed = subprocess.run(['/usr/bin/openssl', 'dgst', '-sha256', '-sign', '/dev/fd/' + str(read_fd)],
                                input=payload, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                pass_fds=(read_fd,), env={'PATH': '/usr/bin:/bin'}, cwd='/', timeout=15)
        require(signed.returncode == 0, 'App signing failed')
        return (payload + b'.' + b64(signed.stdout)).decode()
    finally:
        os.close(read_fd)
        if write_fd >= 0:
            os.close(write_fd)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class GitHub:
    def __init__(self):
        self.opener = urllib.request.build_opener(NoRedirect())

    def call(self, method, path, token, body=None):
        require(not path.startswith('/') and '..' not in path.split('/'), 'invalid API route')
        data = None if body is None else json.dumps(body, separators=(',', ':')).encode()
        request = urllib.request.Request(API + path, data=data, method=method,
                                        headers={'Authorization': 'Bearer ' + token, 'Accept': 'application/vnd.github+json',
                                                 'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'dark-factory-gui-dispatch'})
        try:
            with self.opener.open(request, timeout=30) as response:
                result = response.read(1024 * 1024 + 1)
                require(len(result) <= 1024 * 1024, 'API answer exceeds its bound')
                return json.loads(result) if result else {}
        except urllib.error.HTTPError as error:
            raise Refused('GitHub ' + method + ' refused (' + str(error.code) + '); response body not retained') from None

    def archive(self, path, token):
        # Follow GitHub's signed storage redirect WITHOUT forwarding the installation token.
        url = API + path
        for redirect in range(6):
            parsed = urllib.parse.urlparse(url)
            host = parsed.hostname or ''
            require(parsed.scheme == 'https' and not parsed.username and not parsed.password
                    and (host == 'api.github.com' or host.endswith('.githubusercontent.com')
                         or host.endswith('.blob.core.windows.net') or host.endswith('.actions.githubusercontent.com')),
                    'artifact redirect is not trusted HTTPS storage')
            headers = {'User-Agent': 'dark-factory-gui-dispatch'}
            if host == 'api.github.com':
                headers['Authorization'] = 'Bearer ' + token
            request = urllib.request.Request(url, headers=headers)
            try:
                with self.opener.open(request, timeout=60) as response:
                    require(response.status == 200, 'artifact read refused')
                    length = response.headers.get('Content-Length')
                    require(length is None or int(length) <= MAX_ZIP_BYTES, 'artifact archive exceeds its bound')
                    output = io.BytesIO()
                    while True:
                        chunk = response.read(1024 * 1024)
                        if not chunk:
                            break
                        require(output.tell() + len(chunk) <= MAX_ZIP_BYTES, 'artifact archive exceeds its bound')
                        output.write(chunk)
                    return output.getvalue()
            except urllib.error.HTTPError as error:
                require(error.code in (301, 302, 303, 307, 308) and redirect < 5, 'artifact HTTP read refused')
                url = urllib.parse.urljoin(url, error.headers['Location'])
        raise Refused('artifact redirect bound exceeded')


def immutable_source(environ, event):
    repo = environ['GITHUB_REPOSITORY']
    require(repo.lower() == 'michaeltrefry/lensworks', 'source repository is not allowlisted')
    require(environ['GITHUB_REF'] == 'refs/heads/' + environ['FACTORY_GUI_DEFAULT_BRANCH'], 'dispatcher must run from the protected default branch')
    if environ['GITHUB_EVENT_NAME'] == 'pull_request_target':
        require(event['pull_request']['base']['repo']['full_name'].lower() == repo.lower()
                and event['pull_request']['base']['ref'] == environ['FACTORY_GUI_DEFAULT_BRANCH']
                and event['pull_request']['head']['repo']['full_name'].lower() == repo.lower(), 'PR repository or protected base mismatch; forks are not dispatched')
        sha = event['pull_request']['head']['sha']
    else:
        require(environ['GITHUB_EVENT_NAME'] == 'push', 'untrusted dispatcher event')
        sha = environ['GITHUB_SHA']
        require(event['after'] == sha and event['ref'] == environ['GITHUB_REF']
                and event['repository']['full_name'].lower() == repo.lower(), 'push head/ref/repository identity mismatch')
    require(re.fullmatch('[0-9a-f]{40}', sha), 'source head is not immutable')
    return repo, sha


def extract_artifacts(raw, root, count):
    require(not root.exists(), 'artifact export path already exists')
    root.mkdir(mode=0o700)
    names = set()
    total = 0
    with zipfile.ZipFile(io.BytesIO(raw)) as archive:
        require(len(archive.infolist()) <= MAX_ENTRIES, 'too many artifact entries')
        for entry in archive.infolist():
            raw_name = entry.filename[:-1] if entry.is_dir() else entry.filename
            require(raw_name and all(part not in ('', '.', '..') for part in raw_name.split('/')), 'artifact raw path is not canonical')
            path = pathlib.PurePosixPath(entry.filename)
            mode = entry.external_attr >> 16
            diagnostic = len(path.parts) == 1 and (entry.filename == 'receipt.json' or re.fullmatch(r'source-step-(?:[0-9]|[1-4][0-9])\.log', entry.filename))
            indexed = path.parts and path.parts[0].isdigit() and int(path.parts[0]) < count
            require(not path.is_absolute() and path.parts and all(part not in ('', '.', '..') for part in path.parts)
                    and '\\' not in entry.filename and entry.filename not in names
                    and (indexed or (diagnostic and not entry.is_dir()))
                    and not stat.S_ISLNK(mode) and (stat.S_IFMT(mode) in (0, stat.S_IFREG, stat.S_IFDIR)),
                    'artifact entry is not bounded regular source evidence')
            names.add(entry.filename)
            total += entry.file_size
            require(total <= MAX_UNPACKED_BYTES, 'artifact expansion exceeds its bound')
            target = root.joinpath(*path.parts)
            if entry.is_dir():
                target.mkdir(mode=0o700, parents=True, exist_ok=True)
            else:
                target.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
                with archive.open(entry) as source, open(target, 'xb') as output:
                    copied = 0
                    while True:
                        chunk = source.read(1024 * 1024)
                        if not chunk:
                            break
                        copied += len(chunk)
                        require(copied <= entry.file_size, 'artifact entry exceeds declared size')
                        output.write(chunk)
                    require(copied == entry.file_size, 'truncated artifact entry')


def run(environ, event, github, signer=app_jwt, sleep=time.sleep, clock=time.time):
    try:
        return execute(environ, event, github, signer, sleep, clock)
    except Exception:
        # A permission or workflow-hash failure before dispatch still reports an error on the actual PR head.
        # Never substitute the pull_request_target workflow's intrinsic default-branch check.
        reported = False
        try:
            repo, sha = immutable_source(environ, event)
            github.call('POST', 'repos/' + repo + '/statuses/' + sha, environ['FACTORY_GUI_SOURCE_TOKEN'],
                        {'state': 'error', 'context': environ['FACTORY_GUI_SOURCE_JOB'], 'description': 'Trusted GUI dispatch failed; no source result accepted'})
            reported = True
        except Exception:
            pass
        if reported and environ['GITHUB_EVENT_NAME'] in ('pull_request_target', 'workflow_run'):
            return 0
        raise


def execute(environ, event, github, signer, sleep, clock):
    repo, sha = immutable_source(environ, event)
    collector = environ['FACTORY_GUI_COLLECTOR_REPOSITORY']
    require(re.fullmatch(r'[A-Za-z0-9_-][A-Za-z0-9_.-]*/[A-Za-z0-9_-][A-Za-z0-9_.-]*', collector)
            and collector.lower() != repo.lower(), 'collector is not a distinct configured repository')
    plan_raw = base64.b64decode(environ['FACTORY_GUI_SOURCE_PLAN'], validate=True)
    require(len(plan_raw) <= 100000, 'source plan exceeds its bound')
    plan = json.loads(plan_raw)
    require(plan['version'] == 1 and plan['workflow'] == environ['FACTORY_GUI_SOURCE_WORKFLOW']
            and plan['jobId'] == environ['FACTORY_GUI_SOURCE_JOB'] and 1 <= plan['timeoutMinutes'] <= 180,
            'source plan identity mismatch')
    context = plan['jobId']
    source_token = environ['FACTORY_GUI_SOURCE_TOKEN']
    key = environ.pop('FACTORY_GUI_APP_PRIVATE_KEY')
    app_id = environ['FACTORY_GUI_APP_ID']
    require(1000 <= len(key) <= 16384, 'invalid App signing material')
    now = int(clock())
    jwt = signer(app_id, key, now)
    # Keep only this App key in trusted dispatcher memory so a >1-hour source job can renew its narrowly scoped token.
    # It is absent from the process environment and never forwarded to the collector or source account.
    app = github.call('GET', 'app', jwt)
    require(app.get('permissions', {}).get('actions') == 'write', 'App metadata lacks Actions write')
    installation = github.call('GET', 'repos/' + collector + '/installation', jwt)
    require(installation.get('permissions', {}).get('actions') == 'write', 'collector installation has not approved Actions write')
    minted = github.call('POST', 'app/installations/' + str(installation['id']) + '/access_tokens', jwt,
                         {'repositories': [collector.split('/')[1]], 'permissions': {'actions': 'write', 'contents': 'read'}})
    permissions = minted.get('permissions', {})
    require(permissions.get('actions') == 'write' and permissions.get('contents') == 'read'
            and all(name in ('actions', 'contents', 'metadata') and (name != 'metadata' or value == 'read') for name, value in permissions.items()),
            'installation token permissions are not minimally scoped')
    repositories = minted.get('repositories', [])
    require(len(repositories) == 1 and repositories[0].get('full_name', '').lower() == collector.lower(),
            'installation token does not name only the exact collector repository')
    token = minted['token']
    jwt = None
    # GitHub's fixed UTC timestamp format; reject expired or overlong installation tokens.
    expiry = int(__import__('datetime').datetime.fromisoformat(minted['expires_at'].replace('Z', '+00:00')).timestamp())
    require(now < expiry <= now + 3660, 'invalid scoped installation-token lifetime')
    metadata = github.call('GET', 'repos/' + collector, token)
    require(metadata.get('private') is True and metadata.get('fork', False) is False
            and metadata.get('full_name', '').lower() == collector.lower(), 'collector must be the exact private non-fork control repository')
    collector = metadata['full_name']  # Use GitHub's canonical spelling after proving exact case-insensitive identity.
    branch = metadata['default_branch']
    tip = github.call('GET', 'repos/' + collector + '/commits/' + urllib.parse.quote(branch, safe=''), token)['sha']
    require(re.fullmatch('[0-9a-f]{40}', tip), 'collector default head is not immutable')
    workflow = github.call('GET', 'repos/' + collector + '/contents/' + WORKFLOW + '?ref=' + tip, token)
    require(workflow['type'] == 'file' and workflow['encoding'] == 'base64'
            and hashlib.sha256(base64.b64decode(workflow['content'])).hexdigest() == environ['FACTORY_GUI_COLLECTOR_WORKFLOW_SHA256'],
            'collector workflow differs from the reviewed sealed protocol')
    nonce = secrets.token_hex(16)
    title = 'factory-gui-source-' + nonce
    run_id = None
    finished = False
    status_route = 'repos/' + repo + '/statuses/' + sha

    def status(state, description, url=None):
        body = {'state': state, 'context': context, 'description': description}
        if url:
            body['target_url'] = url
        github.call('POST', status_route, source_token, body)

    status('pending', 'Source job queued on the isolated trusted GUI collector')
    try:
        github.call('POST', 'repos/' + collector + '/actions/workflows/' + WORKFLOW.split('/')[-1] + '/dispatches', token,
                    {'ref': branch, 'inputs': {'source_repository': repo, 'source_sha': sha, 'nonce': nonce,
                     'plan': environ['FACTORY_GUI_SOURCE_PLAN'], 'source_run_id': environ['GITHUB_RUN_ID'],
                     'source_run_attempt': environ['GITHUB_RUN_ATTEMPT'],
                     'lw_require_real': environ.get('FACTORY_GUI_VAR_LW_REQUIRE_REAL', '')}})
        deadline = clock() + (plan['timeoutMinutes'] + 5) * 60
        while clock() < deadline:
            if clock() >= expiry - 120:
                renewal_now = int(clock())
                renewal_jwt = signer(app_id, key, renewal_now)
                renewed = github.call('POST', 'app/installations/' + str(installation['id']) + '/access_tokens', renewal_jwt,
                                      {'repositories': [collector.split('/')[1]], 'permissions': {'actions': 'write', 'contents': 'read'}})
                renewal_jwt = None
                renewal_repos = renewed.get('repositories', [])
                renewal_expiry = int(__import__('datetime').datetime.fromisoformat(renewed['expires_at'].replace('Z', '+00:00')).timestamp())
                require(renewed.get('permissions') == permissions and len(renewal_repos) == 1
                        and renewal_repos[0].get('full_name', '').lower() == collector.lower()
                        and renewal_now < renewal_expiry <= renewal_now + 3660, 'renewed installation token changed scope or lifetime')
                token, expiry = renewed['token'], renewal_expiry
            matches = []
            for page in range(1, 11):
                rows = github.call('GET', 'repos/' + collector + '/actions/workflows/' + WORKFLOW.split('/')[-1]
                                   + '/runs?event=workflow_dispatch&per_page=100&page=' + str(page), token)['workflow_runs']
                matches.extend(row for row in rows if row.get('display_title') == title)
                if len(rows) < 100:
                    break
            require(len(matches) <= 1, 'ambiguous collector run identity')
            if matches:
                result = matches[0]
                require(result['event'] == 'workflow_dispatch' and result['head_sha'] == tip and result['path'] == WORKFLOW
                        and result['run_attempt'] == 1, 'collector run identity does not match the pinned dispatch')
                run_id = result['id']
                if result['status'] == 'completed':
                    exports = github.call('GET', 'repos/' + collector + '/actions/runs/' + str(run_id) + '/artifacts?per_page=100', token)
                    artifacts = exports['artifacts']
                    require(exports['total_count'] == len(artifacts) and len(artifacts) <= 1, 'artifact inventory is incomplete or ambiguous')
                    root = pathlib.Path(tempfile.mkdtemp(prefix='factory-gui-source-', dir=environ['RUNNER_TEMP'])) / 'exports'
                    if artifacts:
                        artifact = artifacts[0]
                        require(artifact['name'] == title and not artifact['expired'] and artifact['size_in_bytes'] <= MAX_ZIP_BYTES
                                and artifact.get('workflow_run', {}).get('id', run_id) == run_id, 'artifact belongs to another execution')
                        raw = github.archive('repos/' + collector + '/actions/artifacts/' + str(artifact['id']) + '/zip', token)
                        extract_artifacts(raw, root, len(plan['artifacts']))
                    else:
                        require(not any(a['ifNoFilesFound'] == 'error' for a in plan['artifacts']), 'required source evidence was not collected')
                        root.mkdir(mode=0o700)
                    passed = result['conclusion'] == 'success'
                    receipt_path = root / 'receipt.json'
                    if receipt_path.exists():
                        require(receipt_path.stat().st_size <= 16384, 'source receipt exceeds its bound')
                        receipt = json.loads(receipt_path.read_text())
                        configuration = environ['FACTORY_GUI_CONFIGURATION_SHA256']
                        require(re.fullmatch('[0-9a-f]{64}', configuration) and receipt.get('version') == 1
                                and receipt.get('repository', '').lower() == repo.lower() and receipt.get('sha') == sha
                                and receipt.get('workflow') == plan['workflow'] and receipt.get('jobId') == plan['jobId']
                                and receipt.get('configurationSha256') == configuration and type(receipt.get('succeeded')) is bool
                                and (not passed or receipt['succeeded']), 'source receipt identity or installed configuration mismatch')
                    else:
                        require(not passed, 'successful collector run has no verified source execution receipt')
                    with open(environ['GITHUB_OUTPUT'], 'a') as output:
                        output.write('artifact_root=' + str(root) + '\n')
                        output.write('source_conclusion=' + ('success' if passed else 'failure') + '\n')
                    url = result['html_url']
                    require(url == 'https://github.com/' + collector + '/actions/runs/' + str(run_id), 'collector result link mismatch')
                    with open(environ['GITHUB_OUTPUT'], 'a') as output:
                        output.write('source_target_url=' + url + '\n')
                    passed = result['conclusion'] == 'success'
                    if not passed or not plan['artifacts']:
                        status('success' if passed else 'failure', 'Original source job passed' if passed else 'Original source job did not pass', url)
                    # With original source artifacts, keep the head PENDING until the hosted uploads finish. A failed
                    # re-upload must never race a previously posted success or poison the PR's default-branch controller.
                    finished = True
                    # A pull_request_target controller check belongs to the BASE commit. The actual source failure
                    # is the explicit HEAD status above; do not poison main-red with a failed PR's controller job.
                    return 0 if passed or environ['GITHUB_EVENT_NAME'] in ('pull_request_target', 'workflow_run') else 1
            sleep(5)
        raise Refused('collector execution exceeded its deadline')
    finally:
        if not finished:
            status('error', 'Trusted collector execution was not accepted; no source pass')
            if run_id is not None:
                try:
                    github.call('POST', 'repos/' + collector + '/actions/runs/' + str(run_id) + '/cancel', token)
                except Exception:
                    pass


def artifact_delivery(environ, event, github):
    repo, sha = immutable_source(environ, event)
    conclusion = environ.get('FACTORY_GUI_SOURCE_CONCLUSION', '')
    failed = environ.get('FACTORY_GUI_ARTIFACT_DELIVERY_FAILED', '')
    require(conclusion in ('success', 'failure', '') and failed in ('true', 'false'), 'invalid source delivery outcome')
    state = 'error' if failed == 'true' or not conclusion else conclusion
    body = {'state': state, 'context': environ['FACTORY_GUI_SOURCE_JOB'],
            'description': 'Original source job and evidence delivered' if state == 'success' else 'Source job/evidence did not pass; no source pass'}
    url = environ.get('FACTORY_GUI_SOURCE_TARGET_URL', '')
    if url:
        collector = environ['FACTORY_GUI_COLLECTOR_REPOSITORY']
        require(re.fullmatch(r'[A-Za-z0-9_-][A-Za-z0-9_.-]*/[A-Za-z0-9_-][A-Za-z0-9_.-]*', collector)
                and re.fullmatch(r'https://github\.com/' + re.escape(collector) + r'/actions/runs/[0-9]+', url, flags=re.IGNORECASE), 'source result link mismatch')
        body['target_url'] = url
    github.call('POST', 'repos/' + repo + '/statuses/' + sha, environ['FACTORY_GUI_SOURCE_TOKEN'], body)
    return 0 if state == 'success' or environ['GITHUB_EVENT_NAME'] in ('pull_request_target', 'workflow_run') else 1


def artifact_delivery_main():
    try:
        path = pathlib.Path(os.environ['GITHUB_EVENT_PATH'])
        require(path.stat().st_size <= 4 * 1024 * 1024, 'event exceeds its bound')
        return artifact_delivery(os.environ, json.loads(path.read_text()), GitHub())
    except Exception as error:
        print('factory GUI source evidence delivery refused (' + type(error).__name__ + '); no source pass', file=sys.stderr)
        return 1


def cancelled(signum, frame):
    raise Refused('trusted dispatcher cancelled')


def main():
    signal.signal(signal.SIGTERM, cancelled)
    signal.signal(signal.SIGINT, cancelled)
    try:
        # The protected default workflow runs this compiled-in script, not a script fetched from a PR.
        require(os.environ.get('FACTORY_GUI_SOURCE_PROTOCOL') == 'factory-gui-source-v1', 'source protocol mismatch')
        event_path = pathlib.Path(os.environ['GITHUB_EVENT_PATH'])
        require(event_path.stat().st_size <= 4 * 1024 * 1024, 'event exceeds its bound')
        event = json.loads(event_path.read_text())
        return run(os.environ, event, GitHub())
    except Exception as error:
        # API bodies, signing material, tokens, URLs and source text never appear in diagnostics.
        print('factory GUI source dispatch refused (' + type(error).__name__ + '); no source result accepted', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
