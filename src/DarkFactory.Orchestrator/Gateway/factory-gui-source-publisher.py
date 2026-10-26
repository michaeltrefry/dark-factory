"""Appended to the sealed dispatcher; no source code, artifacts or request plans are loaded here."""
_SOURCE_REPOSITORY = 'michaeltrefry/LensWorks'
_SOURCE_JOBS = {
    '.github/workflows/inference-adapter.yml': 'inference',
    '.github/workflows/live-raw-acceptance.yml': 'live-raw',
    '.github/workflows/rust-catalog-tests.yml': 'cargo-test',
    '.github/workflows/s47-procedural-ui-tour.yml': 'deterministic-fixtures-and-tour',
}
_PUBLISHER_PROOF = object()
_original_immutable_source = immutable_source


def immutable_source(environ, event):
    if environ.get('GITHUB_EVENT_NAME') == 'workflow_run' and event.get('_publisher_proof') is _PUBLISHER_PROOF:
        require(environ.get('GITHUB_REPOSITORY', '').lower() == _SOURCE_REPOSITORY.lower(), 'publisher source repository mismatch')
        require(environ.get('GITHUB_REF') == 'refs/heads/' + environ['FACTORY_GUI_DEFAULT_BRANCH'], 'publisher must run from the protected default branch')
        require(event.get('_repository') == _SOURCE_REPOSITORY and re.fullmatch('[0-9a-f]{40}', event.get('_head', '')), 'publisher source identity mismatch')
        return _SOURCE_REPOSITORY, event['_head']
    return _original_immutable_source(environ, event)


def _source_identity(pr):
    require(pr['head']['repo']['full_name'].lower() == _SOURCE_REPOSITORY.lower()
            and pr['base']['repo']['full_name'].lower() == _SOURCE_REPOSITORY.lower(), 'source PR repository mismatch; forks refused')
    head = pr['head']['sha']
    require(isinstance(head, str) and re.fullmatch('[0-9a-f]{40}', head), 'source PR head is not immutable')
    return head


def _positive_id(value):
    require(type(value) is int and value > 0, 'API identity is not a positive integer')
    return str(value)


def _source_status(github, environ, head, job, state):
    github.call('POST', 'repos/' + _SOURCE_REPOSITORY + '/statuses/' + head,
                environ['FACTORY_GUI_SOURCE_TOKEN'], {'state': state, 'context': job,
                'description': 'Awaiting trusted GUI publisher' if state == 'pending' else 'Trusted source request refused; no source pass'})


def request_run(environ, event, github):
    # No App secret, plan, artifact or source checkout is consulted by this unprivileged hop.
    require(environ.get('GITHUB_EVENT_NAME') == 'pull_request', 'request requires pull_request')
    require(environ.get('GITHUB_REPOSITORY', '').lower() == _SOURCE_REPOSITORY.lower()
            and event['repository']['full_name'].lower() == _SOURCE_REPOSITORY.lower(), 'request repository mismatch')
    job = environ['FACTORY_GUI_SOURCE_JOB']
    require(job in _SOURCE_JOBS.values(), 'request job is not inventoried')
    head = _source_identity(event['pull_request'])
    default = environ['FACTORY_GUI_DEFAULT_BRANCH']
    require(default and event['pull_request']['base']['ref'] != default, 'request only supports nondefault PR bases')
    _source_status(github, environ, head, job, 'pending')
    return 0


def _publisher_plans(environ):
    encoded = environ['FACTORY_GUI_PUBLISHER_PLANS']
    require(len(encoded) <= 600000, 'publisher map exceeds its bound')
    rows = json.loads(base64.b64decode(encoded, validate=True).decode('utf-8'))
    require(type(rows) is list and 1 <= len(rows) <= 4, 'publisher map is not bounded')
    paths, names = set(), set()
    for row in rows:
        require(type(row) is dict and set(row) == {'workflowName', 'path', 'jobId', 'plan', 'sha256'}, 'publisher map schema mismatch')
        require(row['path'] in _SOURCE_JOBS and row['jobId'] == _SOURCE_JOBS[row['path']], 'publisher map job mismatch')
        require(isinstance(row['workflowName'], str) and 0 < len(row['workflowName']) <= 200
                and row['path'] not in paths and row['workflowName'] not in names, 'publisher map identities ambiguous')
        require(isinstance(row['sha256'], str) and re.fullmatch('[0-9a-f]{64}', row['sha256']), 'publisher workflow digest invalid')
        raw = base64.b64decode(row['plan'], validate=True)
        require(len(raw) <= 100000, 'publisher plan exceeds its bound')
        plan = json.loads(raw.decode('utf-8'))
        require(plan['version'] == 1 and plan['workflow'] == row['path'] and plan['jobId'] == row['jobId'], 'publisher plan identity mismatch')
        artifacts = plan['artifacts']
        require(type(artifacts) is list and len(artifacts) <= 10, 'publisher artifact inventory exceeds its bound')
        artifact_names = set()
        for artifact in artifacts:
            require(isinstance(artifact['name'], str) and re.fullmatch('[a-zA-Z0-9_-]{1,100}', artifact['name'])
                    and artifact['name'] not in artifact_names, 'publisher artifact name is invalid or duplicated')
            require(type(artifact['retentionDays']) is int and 1 <= artifact['retentionDays'] <= 30
                    and artifact['ifNoFilesFound'] in ('ignore', 'warn', 'error'), 'publisher artifact upload policy invalid')
            artifact_names.add(artifact['name'])
        paths.add(row['path'])
        names.add(row['workflowName'])
    return rows


def _workflow_hash(github, token, path, ref):
    answer = github.call('GET', 'repos/' + _SOURCE_REPOSITORY + '/contents/' + path + '?ref=' + urllib.parse.quote(ref, safe=''), token)
    require(answer['type'] == 'file' and answer['encoding'] == 'base64', 'source workflow is not a regular encoded file')
    raw = base64.b64decode(answer['content'].replace('\n', ''), validate=True)
    require(len(raw) <= 1024 * 1024, 'source workflow exceeds its bound')
    return hashlib.sha256(raw).hexdigest()


def publisher_run(environ, event, github, signer=app_jwt, sleep=time.sleep, clock=time.time):
    return _publisher_run(environ, event, github, signer, sleep, clock)


def _publisher_run(environ, event, github, signer, sleep, clock, deliver=False):
    error_head = error_job = None
    try:
        require(environ.get('GITHUB_EVENT_NAME') == 'workflow_run', 'publisher requires workflow_run')
        require(environ.get('GITHUB_REPOSITORY', '').lower() == _SOURCE_REPOSITORY.lower(), 'publisher repository mismatch')
        require(environ.get('GITHUB_REF') == 'refs/heads/' + environ['FACTORY_GUI_DEFAULT_BRANCH'], 'publisher must run from the protected default branch')
        token = environ['FACTORY_GUI_SOURCE_TOKEN']
        rows = _publisher_plans(environ)
        run_id = _positive_id(event['workflow_run']['id'])
        prefix = 'repos/' + _SOURCE_REPOSITORY
        actual = github.call('GET', prefix + '/actions/runs/' + run_id, token)
        require(str(actual['id']) == run_id and actual['repository']['full_name'].lower() == _SOURCE_REPOSITORY.lower()
                and actual['head_repository']['full_name'].lower() == _SOURCE_REPOSITORY.lower(), 'request run repository mismatch')
        workflow_id = _positive_id(actual['workflow_id'])
        workflow = github.call('GET', prefix + '/actions/workflows/' + workflow_id, token)
        require(str(workflow['id']) == workflow_id, 'request workflow API identity mismatch')
        matches = [row for row in rows if row['path'] == workflow['path'] and row['workflowName'] == workflow['name']]
        require(len(matches) == 1, 'request workflow is not compile-pinned')
        row = matches[0]
        require(actual['path'] == row['path'] and actual['name'] == row['workflowName'], 'request run workflow mismatch')
        prs = actual['pull_requests']
        require(type(prs) is list and len(prs) == 1, 'request run must name exactly one PR')
        number = _positive_id(prs[0]['number'])
        pr = github.call('GET', prefix + '/pulls/' + number, token)
        require(str(pr['number']) == number, 'request PR API identity mismatch')
        head = _source_identity(pr)
        # Only authenticated source metadata and a fixed job can authorize an error status.
        recorded_head = prs[0]['head']['sha']
        require(isinstance(recorded_head, str) and re.fullmatch('[0-9a-f]{40}', recorded_head), 'recorded PR head is not immutable')
        require(prs[0]['head']['repo']['url'] == API.rstrip('/') + '/repos/' + _SOURCE_REPOSITORY
                and prs[0]['base']['repo']['url'] == API.rstrip('/') + '/repos/' + _SOURCE_REPOSITORY, 'recorded PR repository mismatch')
        error_head, error_job = recorded_head, row['jobId']
        require(actual['event'] == 'pull_request' and event['workflow_run']['event'] == 'pull_request', 'request run event mismatch')
        require(actual['status'] == 'completed' and actual['conclusion'] == 'success', 'request run did not complete successfully')
        attempt = _positive_id(actual['run_attempt'])
        require(type(event['workflow_run']['run_attempt']) is int and actual['run_attempt'] == event['workflow_run']['run_attempt'], 'request attempt is stale')
        require(pr.get('state') == 'open' and pr['base']['ref'] != environ['FACTORY_GUI_DEFAULT_BRANCH'], 'request PR is closed or targets default')
        require(isinstance(actual['head_sha'], str) and re.fullmatch('[0-9a-f]{40}', actual['head_sha'])
                and recorded_head == head and actual['head_sha'] in (head, pr.get('merge_commit_sha')), 'request PR head moved or run commit mismatch')
        metadata = github.call('GET', prefix, token)
        require(metadata['full_name'].lower() == _SOURCE_REPOSITORY.lower()
                and metadata['default_branch'] == environ['FACTORY_GUI_DEFAULT_BRANCH'], 'source default branch policy mismatch')
        require(_workflow_hash(github, token, row['path'], head) == row['sha256'], 'request PR modified the migrated workflow')
        require(_workflow_hash(github, token, row['path'], environ['FACTORY_GUI_DEFAULT_BRANCH']) == row['sha256'], 'default source workflow is not the pinned migration')
        statuses = github.call('GET', prefix + '/commits/' + head + '/statuses?per_page=100', token)
        require(type(statuses) is list, 'request pending inventory unreadable')
        latest = next((status for status in statuses if status.get('context') == row['jobId']), None)
        require(latest is not None and (latest.get('state') == 'pending'
                or deliver and latest.get('state') == 'failure' and environ.get('FACTORY_GUI_SOURCE_CONCLUSION') == 'failure'
                or deliver and latest.get('state') == 'error' and not environ.get('FACTORY_GUI_SOURCE_CONCLUSION')), 'request did not publish its actual-head pending status')
        trusted = dict(environ)
        trusted.update(FACTORY_GUI_SOURCE_PLAN=row['plan'], FACTORY_GUI_SOURCE_JOB=row['jobId'],
                       FACTORY_GUI_SOURCE_WORKFLOW=row['path'], GITHUB_RUN_ID=run_id, GITHUB_RUN_ATTEMPT=attempt)
        context = {'_publisher_proof': _PUBLISHER_PROOF, '_repository': _SOURCE_REPOSITORY, '_head': head}
        if deliver:
            artifact_delivery(trusted, context, github)
            # artifact_delivery has posted the explicit head outcome before returning.
            return 0
        plan = json.loads(base64.b64decode(row['plan'], validate=True).decode('utf-8'))
        # Only the compile-pinned upload inventory reaches Actions outputs. Names are one safe line,
        # retentions are bounded integers, and source workflow/job selectors are fixed above.
        with open(environ['GITHUB_OUTPUT'], 'a') as output:
            output.write('source_workflow=' + row['path'] + '\n')
            output.write('artifact_count=' + str(len(plan['artifacts'])) + '\n')
            for index, artifact in enumerate(plan['artifacts']):
                output.write('artifact_name_' + str(index) + '=' + artifact['name'] + '\n')
                output.write('artifact_retention_' + str(index) + '=' + str(artifact['retentionDays']) + '\n')
                output.write('artifact_missing_' + str(index) + '=' + artifact['ifNoFilesFound'] + '\n')
        return run(trusted, context, github, signer=signer, sleep=sleep, clock=clock)
    except Exception:
        if error_head is not None:
            # Returning success means the controller accepted an explicit HEAD error, not source success.
            _source_status(github, environ, error_head, error_job, 'error')
            return 0
        raise


def _source_entrypoint(function):
    signal.signal(signal.SIGTERM, cancelled)
    signal.signal(signal.SIGINT, cancelled)
    try:
        require(os.environ.get('FACTORY_GUI_SOURCE_PROTOCOL') == 'factory-gui-source-v1', 'source protocol mismatch')
        path = pathlib.Path(os.environ['GITHUB_EVENT_PATH'])
        require(path.stat().st_size <= 4 * 1024 * 1024, 'event exceeds its bound')
        return function(os.environ, json.loads(path.read_text()), GitHub())
    except Exception as error:
        print('factory GUI source request/publisher refused (' + type(error).__name__ + '); no source pass', file=sys.stderr)
        return 1


def request_main():
    return _source_entrypoint(request_run)


def publisher_main():
    return _source_entrypoint(publisher_run)


def publisher_delivery_run(environ, event, github):
    # A separate always() step re-proves identity; a JSON event never carries the capability across steps.
    return _publisher_run(environ, event, github, app_jwt, time.sleep, time.time, deliver=True)


def publisher_delivery_main():
    return _source_entrypoint(publisher_delivery_run)
